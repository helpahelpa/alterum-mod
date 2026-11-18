using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Animations.ExtendedAnimancer.PersonAnimancer;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Stage 3A: an emote played by player A appears on A's remote body for player B.
//
// What crosses the wire is an id, a sequence number and a start timestamp - never a clip, a pose, a
// bone name, a duration or an index. An accepted id is one item of EmoteCatalog ("Twerk", "Salute");
// anything else - a removed clip, a "native:<Name>" id, a stale number - is unsupported here: it is
// logged and ignored, never mapped onto a survivor. The id is resolved on the OBSERVER from its own
// allowlisted pack, so a peer that knows the id can play it and a peer that does not simply does not
// (two states only, no negotiation, no catalogue intersection).
//
// Presentation is a per-actor PosePlayer: the same CPU pose player the local emote uses, bound to
// the REMOTE player's own PlayerModelReferences/animancer controller. That means the remote body
// gets exactly the local playback machinery - the same lease, the same write phase, the same
// fade-out hand-back, the same late-writer guard - and the mod's own remote body (rigged retarget
// or rigid pieces) follows it, because both are driven from the game skeleton it writes.
//
// Nothing here initiates gameplay: the lease is DoingSomethingHandler (a local
// SettableSafeVariableWrapper<bool> modificator-key gate, no netcode member) plus
// ModelPersonAnimancerController.DisableAnimancerAndAnimator (a local component switch). No input,
// no RPC, no NetworkVariable and no hitbox/stat is touched on the remote actor; the audit line for
// that is logged once per bind.
internal static class RemoteEmotes
{
    private const string Tag = "[swap:emote]";

    // A remote emote is a presentation lease, not a gameplay state. These are the observer's own
    // give-up rules; the wire only says start and stop.
    private const float MovementCancelMeters = 0.12f;
    private const float MovementSampleSeconds = 0.25f;
    private const float MaxAgeSeconds = 900f;          // a start older than this is stale/dropped
    private const float RebindRetrySeconds = 1.0f;
    // A start that arrives before its remote body exists waits in the explicit Pending state. It is
    // retried until it binds, and dropped loudly (never played later, never replayed) after this.
    private const float PendingGiveUpSeconds = 10f;
    private const float PendingBindRetrySeconds = 0.25f;

    // ------------------------------------------------------------------ the run state model
    //
    // Per actor there is at most one "intent", and it is exactly one of:
    //
    //   idle      ClipId == ""              Pending == false   Playing == false
    //   pending   ClipId == <accepted id>   Pending == true    Playing == false   (no run yet)
    //   playing   ClipId == <running id>    Pending == false   Playing == true
    //
    // Pending is an explicit flag, NOT "Playing == false && ClipId != "": only Pending is retried,
    // so a completed or stopped clip cannot be mistaken for a request to play. Every end of an
    // intent - natural completion, cancellation, peer stop, drop, give-up - goes through Consume(),
    // which leaves ClipId == "" and Pending == false. Play() is reached only from a new Start()
    // message or from the Pending branch of TickActor, so no completed clip can revive itself.
    //
    // A pose player may still be writing after Playing becomes false: PosePlayer.Stop starts a
    // fade-out ("handing the pose back") that keeps writing for FadeOutSeconds. That is a property
    // of the player, not of the intent - see Writing() and LateTick.
    private sealed class Actor
    {
        public ulong ClientId;
        public PosePlayer Player;
        public Player Owner;
        public PlayerModelReferences Refs;
        public ModelPersonAnimancerController Controller;
        public PersonAnimancerComponent Person;
        public IntPtr RefsPtr, ControllerPtr, PersonPtr;

        // The id this actor owns: the clip it is playing, or - while Pending is set - the start it
        // has accepted but not played yet. "" means no intent at all (idle, completed, stopped).
        public string ClipId = "";
        public uint Seq;
        // The player is writing clip frames right now.
        public bool Playing;
        // An accepted start with no run yet: the remote body/animancer was not resolvable when it
        // arrived. This flag is what makes a start retryable (see TickActor), and it is the only
        // thing that can resurrect an id - so it is cleared whenever the id is consumed.
        public bool Pending;
        // A pending start's phase: its wire age at arrival plus the time it has waited here, so the
        // run joins the clip in phase however long the body took to appear.
        public float PendingAge;
        public float PendingWait;
        public float LastCheck;
        public Vector3 LastPos;
        public bool HavePos;
        public float NextBind;
        public int Starts, Stops, Seeks, Failures;
        public string Print = "";
    }

    private static readonly Dictionary<ulong, Actor> _actors = new();
    private static readonly List<ulong> _stale = new(4);
    private static EmotePack _pack;
    private static bool _packTried;
    private static IHostContext _log;

    internal static int ActiveCount
    {
        get { int n = 0; foreach (var a in _actors.Values) if (a.Playing) n++; return n; }
    }

    // The id a remote body is actually PLAYING. A pending start is not playing: it is reported by
    // Dump as pending=, never as the actor's emote.
    internal static string PlayingId(ulong clientId) =>
        _actors.TryGetValue(clientId, out var a) ? (a.Playing ? a.ClipId : "") : "";

    internal static void Tick(IHostContext log, float dt)
    {
        _log = log;
        if (!_packTried) { _packTried = true; _pack = EmotePack.TryLoad(log); }
        if (_actors.Count == 0) return;
        _stale.Clear();
        foreach (var kv in _actors)
        {
            var a = kv.Value;
            try { TickActor(log, a, dt); }
            catch (Exception e) { log.LogWarning($"{Tag} client={a.ClientId} tick failed: {e.Message}"); }
            // An actor with nothing to do and nothing bound is released. A PENDING actor has no
            // player by definition - it must survive this sweep so its start can still be retried.
            if (!a.Playing && !a.Pending && a.Player == null) _stale.Add(kv.Key);
        }
        foreach (var id in _stale) { Release(log, id, "idle"); }
    }

    // ================================================================== inbound
    //
    // Authority note: both entry points below are reached only from RemoteModels.HandleFrame, after
    // its Vouched() check proved that the frame's claimed origin is the peer that actually sent it
    // (server: sender == origin; client: the frame came from our matched direct peer, the server).
    // Nothing in here can tell a claimed origin from a real one, and nothing in here should try: an
    // unproven origin is dropped at the frame boundary, so a mismatched frame never starts or stops
    // a run - it never reaches these two methods at all.

    internal static void Start(IHostContext log, ulong clientId, string clipId, uint seq, float startAge)
    {
        _log = log;
        if (string.IsNullOrEmpty(clipId)) return;
        if (clipId.Length > 48) { log.LogWarning($"{Tag} client={clientId} id too long ({clipId.Length})"); return; }
        // Acceptance is allowlist-only. The received spelling may be either survivor's casing, so the
        // canonical id is what gets played and remembered.
        string canonical = EmoteCatalog.Canonical(clipId);
        if (canonical == null)
        {
            log.LogWarning($"{Tag} client={clientId} id='{clipId}' is not in this build's emote allowlist " +
                           $"({EmoteCatalog.List()}); unsupported start ignored (no substitute clip is played)");
            return;
        }
        clipId = canonical;
        if (_pack == null) { log.LogWarning($"{Tag} client={clientId} no emote pack in THIS build; id='{clipId}' cannot be played"); return; }
        if (startAge > MaxAgeSeconds) { log.LogInfo($"{Tag} client={clientId} id='{clipId}' start age={startAge:F1}s is stale; ignored"); return; }

        var a = Ensure(clientId, out bool created);
        // Duplicate relay of a start this actor is already acting on - playing it, or still waiting
        // for its body: keep the original phase and the original wait. Anything else (a new seq, a
        // different id, or any start on an idle actor) is a new request and is played.
        if (!created && seq <= a.Seq && (a.Playing || a.Pending) &&
            string.Equals(a.ClipId, clipId, StringComparison.Ordinal))
        {
            return;
        }
        a.Seq = seq;

        if (!Bind(log, a, force: created))
        {
            // No animation binding yet (the remote model is not up). This is an EXPLICIT pending
            // start: TickActor retries the bind, keeps the start in phase and gives up loudly after
            // PendingGiveUpSeconds. Nothing is played, and nothing is dropped silently.
            a.Pending = true;
            a.Playing = false;
            a.ClipId = clipId;
            a.PendingAge = startAge;
            a.PendingWait = 0f;
            a.NextBind = 0f;                       // retry on the very next tick
            if (a.Print != "pending:" + clipId)
            {
                a.Print = "pending:" + clipId;
                log.LogInfo($"{Tag} client={clientId} id='{clipId}' seq={seq} start age={startAge:F3}s: " +
                            $"model/animancer not resolved yet, start is PENDING (seq={seq}) and will be retried");
            }
            return;
        }

        // A start this build cannot actually play is consumed, not parked: there is nothing to
        // retry, and leaving the id behind would be exactly the stale-intent bug.
        if (!Play(log, a, clipId, seq, startAge, "start")) Consume(a);
    }

    internal static void Stop(IHostContext log, ulong clientId, string clipId, uint seq)
    {
        _log = log;
        if (!_actors.TryGetValue(clientId, out var a)) return;
        // Empty id is the established "stop whatever this actor is playing" protocol, preserved
        // exactly. A non-empty id has to be in the allowlist AND name the clip the actor is actually
        // running: a stop is never a way to reach, or to end, a different clip.
        string stopId = "";
        if (!string.IsNullOrEmpty(clipId))
        {
            stopId = EmoteCatalog.Canonical(clipId);
            if (stopId == null)
            {
                log.LogWarning($"{Tag} client={clientId} stop id='{clipId}' is not in this build's emote " +
                               $"allowlist ({EmoteCatalog.List()}); not applied");
                return;
            }
        }
        if (seq < a.Seq && (a.Playing || a.Pending)) return;   // an older stop cannot undo a newer start
        if (!a.Playing && !a.Pending)
        {
            // Nothing is running and nothing is waiting: a stop has nothing to end and must never
            // revive (or "clean up") an intent that is already consumed.
            return;
        }
        if (stopId.Length > 0 && !string.Equals(a.ClipId, stopId, StringComparison.Ordinal))
        {
            log.LogWarning($"{Tag} client={clientId} stop id='{stopId}' does not name the " +
                           $"{(a.Playing ? "playing" : "pending")} id='{a.ClipId}'; " +
                           "ignored (a stop cannot end a clip it does not name)");
            return;
        }
        a.Seq = seq;
        StopActor(log, a, "peer-stop");
    }

    // A remote body was torn down / rebuilt, the peer left, or the scene changed: the lease cannot
    // survive that (the transforms it drives are gone), so the actor is released rather than left
    // bound to a dead skeleton - the next start binds the new body from scratch.
    internal static void Drop(IHostContext log, ulong clientId, string why)
    {
        _log = log;
        Release(log, clientId, why);
    }

    internal static void StopAll(IHostContext log, string why)
    {
        _log = log;
        foreach (var a in _actors.Values) StopActor(log, a, why);
    }

    internal static void Dispose(IHostContext log)
    {
        _log = log;
        foreach (var a in _actors.Values) Release(log, a.ClientId, "module-dispose");
        _actors.Clear();
        _pack = null;
        _packTried = false;
    }

    internal static void Dump(IHostContext log)
    {
        if (_actors.Count == 0) { log.LogInfo($"{Tag} actors=0"); return; }
        foreach (var a in _actors.Values)
            log.LogInfo($"{Tag} client={a.ClientId} playing={a.Playing} id='{a.ClipId}' seq={a.Seq} " +
                        $"pending={a.Pending} wait={a.PendingWait:F2}s age={a.PendingAge:F2}s " +
                        $"starts={a.Starts} stops={a.Stops} seeks={a.Seeks} failures={a.Failures} " +
                        $"elapsed={(a.Player != null ? a.Player.Elapsed : 0f):F3}s " +
                        $"dur={(a.Player != null ? a.Player.Duration : 0f):F3}s lease={(a.Player != null && a.Player.LeaseHeld)}");
    }

    // ================================================================== play

    private static Actor Ensure(ulong clientId, out bool created)
    {
        created = false;
        if (_actors.TryGetValue(clientId, out var a)) return a;
        created = true;
        a = new Actor { ClientId = clientId };
        _actors[clientId] = a;
        return a;
    }

    // The only playback path left: an allowlisted pack clip. Start() already refused everything
    // else, so this check is the frame-boundary backstop (a pending request can outlive a reload).
    // Returns true only when the run actually started; a refusal leaves NO intent behind (the caller
    // Consume()s), because a clip this build cannot play is not something to retry.
    private static bool Play(IHostContext log, Actor a, string clipId, uint seq, float startAge, string why)
    {
        var player = a.Player;
        if (player == null)
        {
            a.Failures++;
            log.LogWarning($"{Tag} client={a.ClientId} id='{clipId}' cannot start: no pose player is bound");
            return false;
        }
        if (!EmoteCatalog.Contains(clipId))
        {
            a.Failures++;
            log.LogWarning($"{Tag} client={a.ClientId} id='{clipId}' is not in this build's emote allowlist " +
                           $"({EmoteCatalog.List()}); the remote emote stays unplayed (no fallback clip is invented)");
            return false;
        }
        if (_pack == null || _pack.ByName(clipId) == null)
        {
            a.Failures++;
            log.LogWarning($"{Tag} client={a.ClientId} id='{clipId}' is not in this build's allowlisted pack; skipped");
            return false;
        }
        // A previous run must hand its pelvis offset back before the entry pose is captured.
        if (player.Active || player.LeaseHeld) player.Stop("restart");

        player.Start(clipId);
        bool ok = player.Active;
        if (!ok)
        {
            a.Failures++;
            log.LogWarning($"{Tag} client={a.ClientId} id='{clipId}' did not start (see the emote warning above)");
            return false;
        }

        a.ClipId = clipId;
        a.Seq = seq;
        a.Playing = true;
        a.Pending = false;                       // the pending start is now a run
        a.PendingAge = 0f;
        a.PendingWait = 0f;
        a.Starts++;
        a.LastPos = SafePos(a.Owner);
        a.HavePos = true;
        a.LastCheck = 0f;
        a.Print = "playing:" + clipId;

        // Late arrival: seek to where the wearer's clip is by now. startAge is the time between the
        // wearer's start timestamp and this frame on a shared clock (game server time when the
        // session exposes it) plus whatever this start waited for its body.
        float seek = startAge;
        if (seek > 0.05f && player.Seek(seek)) a.Seeks++;

        log.LogInfo($"{Tag} START client={a.ClientId} id='{clipId}' seq={seq} age={startAge:F3}s " +
                    $"seek={(seek > 0.05f ? $"{seek:F3}s" : "none")} dur={player.Duration:F3}s " +
                    $"native={(player.NativeMode ? 1 : 0)} starts={a.Starts} why={why} " +
                    $"body='{(a.Owner == null ? "?" : a.Owner.name)}'");
        return true;
    }

    // Ends the run this actor has, if any, and consumes its intent either way. After this the actor
    // is idle - no clip id, no pending flag - so there is nothing a later tick could replay.
    private static void StopActor(IHostContext log, Actor a, string why)
    {
        string id = a.ClipId;
        uint seq = a.Seq;
        var player = a.Player;
        bool running = a.Playing || (player != null && (player.Active || player.LeaseHeld));
        if (running && player != null)
        {
            try { player.Stop(why); } catch (Exception e) { log.LogWarning($"{Tag} client={a.ClientId} stop failed: {e.Message}"); }
            a.Stops++;
            // The player keeps writing for FadeOutSeconds from here: LateTick must keep driving it.
            log.LogInfo($"{Tag} STOP client={a.ClientId} id='{id}' seq={seq} why={why} " +
                        $"stops={a.Stops} lease={(player.LeaseHeld)} handback=fade-out " +
                        "(the game's own animation resumes, the pose was handed back)");
        }
        else if (a.Pending)
        {
            log.LogInfo($"{Tag} CANCEL client={a.ClientId} id='{id}' seq={seq} why={why} " +
                        "(the start was still pending a remote body; nothing was played, nothing is retried)");
        }
        a.Playing = false;
        Consume(a);
    }

    // The single way an intent ends. A completed, cancelled or stopped emote leaves no clip id and
    // no pending flag, and Pending is the only state TickActor retries - so an id that reached here
    // can only ever be played again by a NEW Start() message.
    private static void Consume(Actor a)
    {
        a.ClipId = "";
        a.Pending = false;
        a.PendingAge = 0f;
        a.PendingWait = 0f;
        a.Print = "";
    }

    // A pose player is still writing while it plays (Active) AND while it hands the pose back: the
    // fade-out that PosePlayer.Stop starts after a natural end or a peer stop. "Playing == false"
    // must not stop the fade from being driven (it did: the remote skeleton stayed on the last pose).
    private static bool Writing(PosePlayer player)
    {
        if (player == null) return false;
        try { return player.Writing; } catch { return player.Active; }
    }

    private static void Release(IHostContext log, ulong clientId, string why)
    {
        if (!_actors.TryGetValue(clientId, out var a)) return;
        StopActor(log, a, why);
        try { a.Player?.Unbind(); } catch { }
        try { a.Player?.Dispose(); } catch { }
        a.Player = null;
        a.Refs = null; a.Controller = null; a.Person = null;
        _actors.Remove(clientId);
    }

    // ================================================================== binding

    private static bool Bind(IHostContext log, Actor a, bool force)
    {
        float now = Time.unscaledTime;
        if (!force && now < a.NextBind) return a.Player != null && a.Player.Bound;
        a.NextBind = now + RebindRetrySeconds;

        if (_pack == null) return false;
        var owner = FindRemotePlayer(a.ClientId);
        if (owner == null) return false;
        var refs = owner.GetRefs<PlayerModelReferences>();
        if (refs == null) return false;
        var controller = refs.ThirdPlayerModelPersonAnimancerController;
        if (controller == null) return false;
        var person = controller.CurrentPersonAnimancerComponent;
        if (person == null) return false;
        if (person.Animator == null) return false;

        bool same = a.Player != null && a.Player.Bound &&
                    SamePointer(a.Refs, refs) && SamePointer(a.Controller, controller) && SamePointer(a.Person, person);
        if (same) return true;

        if (a.Player != null && (a.Player.Active || a.Player.LeaseHeld)) a.Player.Stop("rebind");
        var player = a.Player ?? new PosePlayer(log, _pack);
        if (!player.Bind(refs, controller, person))
        {
            a.Failures++;
            log.LogWarning($"{Tag} client={a.ClientId}: pose player could not bind to the remote animancer; will retry");
            try { player.Unbind(); } catch { }
            return false;
        }
        a.Player = player;
        a.Owner = owner;
        a.Refs = refs; a.Controller = controller; a.Person = person;
        a.RefsPtr = Ptr(refs); a.ControllerPtr = Ptr(controller); a.PersonPtr = Ptr(person);
        a.LastPos = SafePos(owner); a.HavePos = true;

        // Audit for the brief's "no authoritative gameplay change from a remote emote": the only
        // native calls the lease makes are named here, and they are the two local, non-networked
        // gates. Logged once per binding.
        log.LogInfo($"{Tag} bound client={a.ClientId} player='{owner.name}' animEnabled={controller.IsAnimancerAndAnimatorEnabled} " +
                    $"animator='{person.Animator.name}' layers={Count(person)} " +
                    $"lease=DoingSomethingHandler.StartDoingSomething(key)+ModelPersonAnimancerController.DisableAnimancerAndAnimator(key) " +
                    "audit=local-only (SettableSafeVariableWrapper<bool> modificator gate + component switch; no RPC/NetworkVariable/input)");
        return true;
    }

    private static int Count(PersonAnimancerComponent person)
    {
        try { return person.Layers == null ? 0 : person.Layers.Count; } catch { return 0; }
    }

    private static void TickActor(IHostContext log, Actor a, float dt)
    {
        if (a.Playing)
        {
            var player = a.Player;
            if (player == null || !player.Active)
            {
                // End of a non-looping clip, a write abort or an ownership loss: the run is over and
                // its intent is consumed HERE. A finished clip must leave no id behind that a later
                // idle pass could read as "start this clip" - that was the remote-Salute replay.
                // The player is still faded out: it is still writing (Writing() == true), and
                // LateTick drives it until the hand-back completes.
                string id = a.ClipId;
                string why = player == null ? "player-gone" : "ended";
                bool fading = Writing(player);
                a.Playing = false;
                a.Stops++;
                Consume(a);
                log.LogInfo($"{Tag} STOP client={a.ClientId} id='{id}' why={why} " +
                            $"(end/abort; intent consumed, no replay; lease={(player != null && player.LeaseHeld)} " +
                            $"handback={(fading ? "fade-out running" : "none")})");
                return;
            }
            // The observer's own give-up rules: the game took the animancer back (death, ragdoll,
            // a new action) or the actor moved.
            bool reenabled = false;
            try { reenabled = a.Controller != null && a.Controller.IsAnimancerAndAnimatorEnabled; } catch { }
            if (reenabled)
            {
                StopActor(log, a, "game-took-animancer");
                return;
            }
            // NOTE: do NOT read Animator.enabled here: the lease itself disables it
            // (DisableAnimancerAndAnimator), so "disabled" is the healthy state. The controller's
            // own IsAnimancerAndAnimatorEnabled is the ownership signal, and it is checked above.
            float now = Time.unscaledTime;
            if (now - a.LastCheck >= MovementSampleSeconds)
            {
                a.LastCheck = now;
                var p = SafePos(a.Owner);
                if (a.HavePos && (p - a.LastPos).sqrMagnitude > MovementCancelMeters * MovementCancelMeters)
                {
                    StopActor(log, a, $"moved({Vector3.Distance(p, a.LastPos):F2}m)");
                    return;
                }
                a.LastPos = p; a.HavePos = true;
            }
            return;
        }

        // ===== pending: a start that arrived before its remote body existed =====
        if (!a.Pending) return;                  // idle (completed/stopped): nothing is retried

        a.PendingWait += dt;                     // the start's age keeps advancing while it waits
        if (a.PendingWait > PendingGiveUpSeconds)
        {
            log.LogWarning($"{Tag} PENDING GIVE-UP client={a.ClientId} id='{a.ClipId}' seq={a.Seq} " +
                           $"waited={a.PendingWait:F2}s: the remote animancer never became bindable; " +
                           "the start is dropped (never played later, never replayed)");
            a.Failures++;
            Consume(a);
            return;
        }
        if (Time.unscaledTime < a.NextBind) return;    // Bind() throttles itself; the wait goes on
        // Retried with no player required: that precondition (a.Player != null && Bound) is exactly
        // what made this recovery unreachable for a start that arrived before the body did.
        if (!Bind(log, a, force: false))
        {
            // A waiting start retries faster than the ordinary rebind path.
            a.NextBind = Mathf.Min(a.NextBind, Time.unscaledTime + PendingBindRetrySeconds);
            return;
        }
        float age = a.PendingAge + a.PendingWait;
        // Refused (not playable in this build): consume rather than retry a clip that cannot start.
        if (!Play(log, a, a.ClipId, a.Seq, age, "late-body")) Consume(a);
    }

    // ================================================================== per-frame write

    // Same phase as the local emote: after every other skeleton writer, before the mirror renders.
    internal static void LateTick(IHostContext log, float dt)
    {
        if (_actors.Count == 0) return;
        foreach (var a in _actors.Values)
        {
            var player = a.Player;
            // Drive the player for as long as it is writing: a running clip AND the fade-out that
            // PosePlayer.Stop starts (natural end, peer stop, restart). Gating on a.Playing here left
            // the hand-back unticked, so the remote skeleton kept the last emote pose.
            if (!Writing(player)) continue;
            try { player.PostAnimationUpdate(dt); }
            catch (Exception e) { log.LogWarning($"{Tag} client={a.ClientId} write failed: {e.Message}"); }
        }
    }

    // ================================================================== resolve / clips

    private static Player FindRemotePlayer(ulong clientId)
    {
        try
        {
            var players = SceneCache.Scan(Il2CppType.Of<Player>());
            for (int i = 0; players != null && i < players.Length; i++)
            {
                var p = players[i].TryCast<Player>();
                if (p == null || p.transform == null) continue;
                var go = p.gameObject;
                if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
                if (p.OwnerClientId != clientId) continue;
                return p;
            }
        }
        catch { }
        return null;
    }

    private static Vector3 SafePos(Player owner)
    {
        try { return owner == null || owner.transform == null ? Vector3.zero : owner.transform.position; }
        catch { return Vector3.zero; }
    }

    private static IntPtr Ptr(Il2CppObjectBase o)
    {
        try { return o == null ? IntPtr.Zero : o.Pointer; } catch { return IntPtr.Zero; }
    }

    private static bool SamePointer(Il2CppObjectBase x, Il2CppObjectBase y)
    {
        if (x == null || y == null) return x == null && y == null;
        try { return x.Pointer == y.Pointer; } catch { return false; }
    }
}
