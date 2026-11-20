using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// Stage-2 model sync: player A's custom wardrobe entry appears on player B.
//
// The contract is deliberately one bit wide (see the stage-2 brief): a peer either has this exact
// mod (host + assets + code) or it does not. No catalogue, no content hashes, no per-asset
// negotiation - only a stable slug ever crosses the wire, never a mesh, texture or bone table.
//
// Wire (inside a NetKind.App payload; maximum 1024 B, so this is never near the ceiling):
//   hello : [1][proto:u8][caps:u8]                                  3 bytes
//   state : [2][originClientId:u64 LE][flags:u8][slugLen:u8][slug]
//              [cosmeticCount:u8]([idLen:u8][id] * count)          12 + len + sum(1+idLen)
//   emote : [3][originClientId:u64 LE][kind:u8][seq:u32 LE][t:double LE][idLen:u8][id]
//
// Stage 3B keeps the same envelope and the same three frames; proto 3 adds the Reel of Fortune's
// authority frames (4..9, see ReelNet.cs), which this class dispatches and never interprets.
//
// `flags` carries the wearer's two enhancement toggles (bit0 chest, bit1 hips). The cosmetic list is
// the wearer's EQUIPPED SET (bounded: 8 ids, 48 bytes each), because the cosmetics runtime stores a
// set, not a single hat. Only slugs/ids travel; the observer places each cosmetic with ITS OWN
// measured per-model anchor (ModelRoster.AnchorFor) and never accepts a transform from the wire.
//
// `emote` is an event, not state: kind 0 = stop, 1 = start; `seq` orders starts/stops per origin and
// `t` is the start instant on the game's own server clock when the session exposes one (see Clock()).
// `originClientId` is in the payload, not taken from the frame's sender, because the host relays:
// a client can only address the server (NetBridge refuses everything else), so peer states travel
// client -> server -> clients and the sender of a relayed frame is always the server.
//
// Roles:
//   server/host : sends hello to every connected peer, keeps a per-client handshake verdict and a
//                 per-client slug cache, relays a client's state to the other clients that answered.
//   client      : sends hello and its own state to the server only.
// A peer that never answers the handshake is "vanilla": no state frame is ever sent to it, it is
// logged once, and nothing else happens - its native body stays untouched on our side too.
//
// Presentation on the observing side reuses the two existing second-instance paths, exactly as the
// wardrobe preview does:
//   rigged bake -> SahurRig.AttachExternal / PoseExternal / DetachExternal (own skeleton, retarget)
//   rigid bake  -> SahurModel.BuildPreviewBody / DestroyPreviewBody (bindpose from the native mesh)
// The remote player's own renderers are leased (disabled, restored on teardown) and every body is
// keyed by client id, so a peer changing model, leaving, a scene change, a respawn or a module
// reload disposes exactly one body.
internal static class RemoteModels
{
    private const string Tag = "[swap:net]";
    private const string RemoteTag = "[swap:remote]";

    private const byte MsgHello = 1;
    private const byte MsgState = 2;
    private const byte MsgEmote = 3;
    // Proto 3: reel authority (placements, requests, spins, settles) lives in ReelNet and rides the
    // same App payload. A proto-2 peer is the existing vanilla verdict (two states, no negotiation).
    private const byte ProtoVersion = 3;
    private const byte CapHasMod = 1;
    private const byte CapCosmetics = 2;
    private const byte CapEnhancements = 4;
    private const byte CapsAll = CapHasMod | CapCosmetics | CapEnhancements;

    private const byte FlagChest = 1;
    private const byte FlagHips = 2;

    private const int MaxCosmetics = 8;
    private const int MaxIdBytes = 48;
    private const int MaxEmoteIdBytes = 64;

    private const byte EmoteStop = 0;
    private const byte EmoteStart = 1;

    // Coalescing + rate limit: a burst of wardrobe clicks collapses to the latest slug, sent at most
    // four times a second. Independent of that, the current state is re-sent on a heartbeat.
    private const float StateMinInterval = 0.25f;
    private const float HeartbeatSeconds = 5f;
    private const float HelloRetrySeconds = 2f;
    private const int HelloAttempts = 3;
    private const float HandshakeVerdictSeconds = 3f;
    private const float ModelGoneSeconds = 1.5f;
    private const float ResolveSeconds = 1.0f;
    private const float PollSeconds = 0.25f;
    private const int MaxFramesPerTick = 16;
    // A failed per-body mesh clone is retried with a doubling delay instead of blacklisting the
    // model for the session: 5 s, 10 s, 20 s, 40 s, then once a minute. A truly deterministic failure
    // stays quiet (one attempt per minute) and a transient one is back on screen within seconds.
    private const float CloneRetryBaseSeconds = 5f;
    private const float CloneRetryMaxSeconds = 60f;

    private const string RequestFileName = "remote.request";
    private const float RequestPollSeconds = 0.25f;

    private static IHostContext _log;

    // ---------------------------------------------------------------- session
    private static bool _sessionLive;
    private static uint _epoch;
    private static uint _gen;
    private static ulong _localId;
    private static bool _isServer;
    // The server's client id (NetBridgeStatus.ServerClientId). A client accepts a peer frame only
    // from this address - see Vouched(). Read every poll for the same reason the role is.
    private static ulong _serverId;
    private static string _scene = "";

    // ---------------------------------------------------------------- local state
    private static string _localSlug = "";       // the applied entry's slug; "" = a native model
    private static string _localSig = "";        // slug + toggles + equipped set, change detection
    private static bool _localDirty;
    private static string _localEmoteId = "";
    private static uint _emoteSeq;
    private static float _nextStateSend;
    private static float _nextHeartbeat;
    private static bool _localLogged;

    // ---------------------------------------------------------------- peers
    private enum Handshake { Pending = 0, Mod = 1, Vanilla = 2 }

    private sealed class Peer
    {
        public ulong Id;
        public Handshake Handshake;
        public float FirstSeen;
        public float NextHelloAt;
        public int HellosSent;
        // Rate limit for re-acknowledging a matching hello this peer sends after it already was
        // `mod` (a reloaded peer starts with an empty peer table and needs the answer again).
        public float NextAckAt;
        public float LastSeen;
        public string CachedSlug;
        public byte[] CachedState;          // the exact state payload this origin sent, for late joins
        public bool HasState;
        public string SentSlug;              // the slug this peer last received from us
        public string Print;                 // last print of this peer (change only)
        public float NextPrintAt;
    }

    private static readonly Dictionary<ulong, Peer> _peers = new();
    private static readonly List<ulong> _peerScan = new(8);

    // ---------------------------------------------------------------- remotes
    private sealed class NativeRenderer
    {
        public Renderer R;
        public bool HadEnabled;
        public bool Hidden;
    }

    private sealed class Remote
    {
        public ulong ClientId;
        public string Slug = "";
        public bool HasState;
        public float LastStateAt;

        public ThirdViewPlayerModel Model;
        public Transform Root;
        public SahurRig.RigLease Rig;
        public GameObject Rigid;
        public Mesh RigidMesh;
        public GameObject BodyGo;             // the object this lease actually built (rigid body / rigged body)
        public string BuiltSlug = "";
        public int BuiltKind;                 // 0 none, 1 rigid, 2 rigged
        public readonly List<NativeRenderer> HiddenRenderers = new();

        public float NextResolve;
        public float GoneSince;
        public bool LoggedUnresolved, LoggedUnknown;
        public string Print;
        public int Builds, Disposes, Poses;

        // ---- stage 3A: the wearer's state on this body
        public readonly List<string> Hats = new();
        public int HatVersion = -1;
        public IntPtr HatHeadPtr;
        public CosmeticsRuntime.RemoteHats Attached;
        public int HatAttaches, HatDetaches;
        public bool Chest, Hips;
        public bool EnhApplied;
        public bool EnhChest, EnhHips;
        public int EnhApplies, EnhReleases;

        // The slug whose per-body mesh clone failed. Scoped, not permanent: cleared when the wearer
        // advertises a different model and on a scene change, and re-attempted after a doubling
        // backoff (`CloneRetryBaseSeconds`), so one transient failure cannot hide a peer's model for
        // the rest of the session. See Build().
        public string CloneBlockedSlug = "";
        public int CloneBlockFails;
        public float NextCloneRetryAt;
    }

    private static readonly Dictionary<ulong, Remote> _remotes = new();
    private static readonly List<ulong> _stale = new(4);

    // ---------------------------------------------------------------- timers
    private static float _nextPoll;
    private static float _nextRequestPoll;
    // The wall time of the previous RemoteEmotes.Tick. The emote runtime is driven on the state-poll
    // clock (every PollSeconds), not once per frame, so it must be handed the time that actually
    // elapsed since that previous tick. Handing it one frame's `dt` made PendingWait accumulate at
    // dt/PollSeconds of real time: at 15 fps (dt=0.0667 s) the nominal 10 s pending give-up took
    // ~37.5 s, and the late-start seek was ~4x younger than the start really was.
    private static float _lastEmoteTick;

    // ---------------------------------------------------------------- pending emote event
    private static string _pendingEmoteId;
    private static byte _pendingEmoteKind;
    private static uint _pendingEmoteSeq;
    private static double _pendingEmoteAt;
    private static bool _pendingEmoteClock;
    private static float _nextEmoteSend;

    // The game's own server clock when Unity Netcode is listening (all peers in a session share it),
    // otherwise this process's monotonic unscaled time. The receiver can only seek a late start when
    // both ends read the same clock, so the choice is logged and carried with each event.
    // Bumped whenever an accepted peer state or a built/removed remote body can have changed what
    // the display layer should show. ModelPresentation refreshes on this edge instead of scanning
    // the scene every frame. Only slugs/ids are in play: NO database index is ever sent or stored.
    private static int _presentationRevision;
    internal static int PresentationRevision => _presentationRevision;
    internal static void TouchPresentation() => _presentationRevision++;

    // Read-only views for the identity resolver / wardrobe occupancy correction. The body slug is
    // what this instance actually BUILT for the peer, which is the only honest "bodySlug" value.
    internal static void CollectRemoteIds(List<ulong> into)
    {
        into.Clear();
        foreach (var kv in _remotes) into.Add(kv.Key);
    }

    // Is this client id an actor this instance holds an ACCEPTED state for? ResolveRemote and every
    // other view below read this same table, so an id it has never seen would otherwise resolve to a
    // fabricated "native remote". The console checks this before it reports an identity.
    internal static bool HasRemote(ulong clientId) => _remotes.ContainsKey(clientId);

    internal static string RemoteSlug(ulong clientId)
        => _remotes.TryGetValue(clientId, out var r) ? (r.Slug ?? "") : "";

    internal static string RemoteBodySlug(ulong clientId)
        => _remotes.TryGetValue(clientId, out var r) && r.BuiltKind != 0 ? (r.BuiltSlug ?? "") : "";

    // The slug this client last published. Cleared only in Dispose, so it survives scene changes -
    // the Results scene must keep knowing what the local player wore after the map scene is gone
    // (ResultsBodies reads it; SahurWardrobe.Snapshot degrades there).
    internal static string LocalPublishedSlug => _localSlug ?? "";

    internal static Transform RemoteRoot(ulong clientId)
    {
        if (!_remotes.TryGetValue(clientId, out var r)) return null;
        try
        {
            if (r.Root != null) return r.Root;
            if (r.Model != null && r.Model.gameObject != null) return r.Model.transform;
        }
        catch { }
        return null;
    }

    internal static double Clock()
    {
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
            {
                double t = nm.ServerTime.Time;
                if (t > 0.0) { ClockIsServer = true; return t; }
            }
        }
        catch { }
        ClockIsServer = false;
        try { return Time.unscaledTime; } catch { return 0.0; }
    }

    // True when Clock() returned the game's own NetworkManager server time.
    internal static bool ClockIsServer;

    // ---------------------------------------------------------------- capture (evidence)
    // The remote-shot path writes the pose, renders one frame into a private RenderTexture, and
    // restores pose/target/active in finally blocks. There is no persistent Camera.onPreCull hook to
    // arm, disarm or leave behind.

    // ================================================================== public surface

    // The local player's chosen entry, from the wardrobe funnel (manual pick and the random-on-join
    // roll both land in SahurWardrobe.SyncSelection). Idempotent: only a real change queues a send.
    internal static void PublishLocal(IHostContext log, string slug)
    {
        _log = log;
        slug ??= "";
        if (slug == _localSlug) return;
        _localSlug = slug;
        _localSig = "";
        _localDirty = true;
        _nextStateSend = 0f;
        try
        {
            log?.LogInfo($"{Tag} local model -> '{(slug.Length == 0 ? "<native>" : slug)}' (queued for peers)");
        }
        catch { }
    }

    // Called once per frame with whatever the local emote runtime is playing ("" when idle). The id
    // is an allowlisted catalogue id (EmoteCatalog), so the observer resolves it from its own pack.
    // A transition (idle -> id, id -> other id, id -> idle) emits exactly one event; nothing is sent
    // while the same clip keeps playing. An id outside the allowlist is published as idle: this
    // build never announces a clip it cannot play itself.
    internal static void PublishLocalEmote(IHostContext log, string id)
    {
        if (log != null) _log = log;
        id ??= "";
        if (id.Length > 0 && !EmoteCatalog.Contains(id))
        {
            log?.LogWarning($"{Tag} local emote id '{id}' is not in the emote allowlist ({EmoteCatalog.List()}); " +
                            "publishing <idle> instead (the local playback path is unchanged)");
            id = "";
        }
        if (id == _localEmoteId) return;
        string from = _localEmoteId;
        _localEmoteId = id;
        _pendingEmoteId = id;
        _pendingEmoteKind = id.Length == 0 ? EmoteStop : EmoteStart;
        _pendingEmoteSeq = ++_emoteSeq;
        _pendingEmoteAt = Clock();
        _pendingEmoteClock = ClockIsServer;
        if (id.Length == 0 && from.Length == 0) return;
        try
        {
            log?.LogInfo($"{Tag} local emote '{(from.Length == 0 ? "<idle>" : from)}' -> " +
                         $"'{(_pendingEmoteId.Length == 0 ? "<idle>" : _pendingEmoteId)}' " +
                         $"seq={_pendingEmoteSeq} clock={(ClockIsServer ? "server" : "local")} t={_pendingEmoteAt:F3} (queued)");
        }
        catch { }
    }

    // `dt` (the frame delta) is deliberately NOT what the emote runtime is handed: that runtime runs
    // on the state-poll clock below, so it gets the time that actually elapsed since its last tick.
    internal static void Tick(IHostContext log, float dt)
    {
        _log = log;
        try { PollRequest(log); } catch (Exception e) { log.LogWarning($"{RemoteTag} request poll failed: {e.Message}"); }
        var net = log?.Net;
        if (net == null) return;

        NetBridgeStatus st;
        try { st = net.Status; } catch { return; }

        if (st.State != NetState.Registered)
        {
            if (_sessionLive) EndSession("bridge-not-registered");
            return;
        }

        float now = 0f;
        try { now = Time.unscaledTime; } catch { }

        if (!_sessionLive || st.SessionEpoch != _epoch || st.Generation != _gen)
            BeginSession(log, net, st, now);
        // The local client id and the role are not final when the handler registers (they are set
        // the moment Netcode assigns them), so they are re-read every poll. A stale local id makes the
        // sender mistake its own state for a peer's and vice versa.
        try
        {
            if (st.LocalClientId != _localId || st.IsServer != _isServer)
            {
                log.LogInfo($"{Tag} role change local={_localId}->{st.LocalClientId} " +
                            $"server={_isServer}->{st.IsServer} epoch={_epoch}");
                _localId = st.LocalClientId;
                _isServer = st.IsServer;
                _serverId = st.ServerClientId;
                _localDirty = true;
                _nextStateSend = 0f;
            }
        }
        catch { }

        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;

        // The reel authority re-reads the role every poll for the same reason the model sync does.
        try { ReelNet.Bind(log, net, _sessionLive, _isServer, _localId, st.ServerClientId); }
        catch (Exception e) { log.LogWarning($"{Tag} reel bind failed: {e.Message}"); }
        try { LobbyMusicNet.Bind(net, _sessionLive, _isServer, _localId, st.ServerClientId, _epoch); }
        catch (Exception e) { log.LogWarning($"{Tag} music bind failed: {e.Message}"); }

        try { DrainInbound(log, net, now); }
        catch (Exception e) { log.LogWarning($"{Tag} drain failed: {e.Message}"); }
        try { CheckScene(log); } catch { }
        try { ScanPeers(net, now); } catch (Exception e) { log.LogWarning($"{Tag} peer scan failed: {e.Message}"); }
        try { HandshakeTick(log, net, now); } catch (Exception e) { log.LogWarning($"{Tag} handshake failed: {e.Message}"); }
        try { RefreshLocalSignature(log); } catch (Exception e) { log.LogWarning($"{Tag} signature failed: {e.Message}"); }
        try { SendLocalTick(log, net, now); } catch (Exception e) { log.LogWarning($"{Tag} state send failed: {e.Message}"); }
        try { SendEmoteTick(log, net); } catch (Exception e) { log.LogWarning($"{Tag} emote send failed: {e.Message}"); }
        try { RemoteTick(log, now); } catch (Exception e) { log.LogWarning($"{RemoteTag} tick failed: {e.Message}"); }
        try { RuleTick(log, now); } catch (Exception e) { log.LogWarning($"{RemoteTag} rules failed: {e.Message}"); }
        // Real elapsed time since the previous emote tick, so PendingWait and the late-start seek
        // phase are on the wall clock regardless of the frame rate.
        float emoteDt = _lastEmoteTick <= 0f ? 0f : Math.Max(0f, now - _lastEmoteTick);
        _lastEmoteTick = now;
        try { RemoteEmotes.Tick(log, emoteDt); } catch (Exception e) { log.LogWarning($"{Tag} tick failed: {e.Message}"); }
    }

    // Rigged bodies need the game bones *after* every skeleton writer, exactly like the local
    // retarget and the preview (same ordering rule: after writers, before the mirror renders).
    internal static void LateTick(IHostContext log)
    {
        _log = log;
        if (_remotes.Count == 0) return;
        foreach (var r in _remotes.Values)
        {
            if (r.BuiltKind != 2 || r.Rig == null) continue;
            try { SahurRig.PoseExternal(log, r.Rig); r.Poses++; }
            catch (Exception e) { log.LogWarning($"{RemoteTag} pose client={r.ClientId} failed: {e.Message}"); }
        }
        // Remote emote pose: same phase as the local one (after every other skeleton writer, so the
        // remote rigged retarget above reads the emote pose this frame).
        try { RemoteEmotes.LateTick(log, Time.deltaTime); }
        catch (Exception e) { log.LogWarning($"{Tag} late failed: {e.Message}"); }
    }

    internal static void Dispose(IHostContext log)
    {
        _log = log;
        try { RemoteEmotes.Dispose(log); } catch { }
        TeardownAll(log, "module-dispose");
        try
        {
            var sb = new StringBuilder();
            foreach (var r in _remotes.Values)
                sb.Append($"client={r.ClientId} slug='{r.Slug}' builds={r.Builds} disposes={r.Disposes}; ");
            log?.LogInfo($"{RemoteTag} disposed session={_sessionLive} remotes={_remotes.Count} [{sb}]");
        }
        catch { }
        try { CustomEnhancements.ReleaseAllExternal(log, "module-dispose"); } catch { }
        _remotes.Clear();
        _peers.Clear();
        _sessionLive = false;
        _localSlug = "";
        _localSig = "";
        _localDirty = false;
        _localLogged = false;
        _localEmoteId = "";
        _pendingEmoteId = null;
        _pendingEmoteSeq = 0;
        _emoteSeq = 0;
        _epoch = 0;
        _gen = 0;
        _scene = "";
    }

    // ================================================================== session

    private static void BeginSession(IHostContext log, INetBridge net, in NetBridgeStatus st, float now)
    {
        if (_sessionLive) TeardownAll(log, "session-restart");
        _sessionLive = true;
        _epoch = st.SessionEpoch;
        _gen = st.Generation;
        _localId = st.LocalClientId;
        _isServer = st.IsServer;
        _serverId = st.ServerClientId;
        _localDirty = true;
        _localLogged = false;
        _nextStateSend = 0f;
        _nextHeartbeat = 0f;
        _nextPoll = 0f;
        _lastEmoteTick = 0f;              // first emote tick of the session gets dt=0, never a gap
        _peers.Clear();
        _scene = SafeScene();
        log?.LogInfo($"{Tag} session up epoch={_epoch} gen={_gen} local={_localId} server={_localId == st.ServerClientId} " +
                     $"role={(_isServer ? "server" : "client")} proto={ProtoVersion} mod=yes caps=0x{CapsAll:x2} " +
                     $"clock={(Clock() >= 0 ? (ClockIsServer ? "server" : "local") : "?")} " +
                     $"localModel='{(_localSlug.Length == 0 ? "<native>" : _localSlug)}' " +
                     $"cosmetics={HostedCosmeticCount()} chest={EnhancementSettings.OnOff(EnhancementSettings.ChestEnabled)} " +
                     $"hips={EnhancementSettings.OnOff(EnhancementSettings.HipsEnabled)}");
        ScanPeers(net, now);
        // Server: tell every connected peer the mod is here. Client: ask the server the same. Both
        // are a single small frame; a peer that does not answer is vanilla and gets nothing after
        // the retries are exhausted.
        foreach (var p in _peers.Values) SendHello(log, net, p, now, "session-start");
    }

    private static void EndSession(string why)
    {
        _sessionLive = false;
        TeardownAll(_log, why);
        _peers.Clear();
        try { ReelNet.Bind(_log, null, false, false, 0, 0); } catch { }
        try { LobbyMusicNet.Bind(null, false, false, 0, 0, _epoch); } catch { }
        _log?.LogInfo($"{Tag} session ended ({why})");
    }

    private static void CheckScene(IHostContext log)
    {
        string scene = SafeScene();
        if (scene == _scene) return;
        string from = _scene;
        _scene = scene;
        log?.LogInfo($"{Tag} scene '{from}' -> '{scene}': dropping every remote body, states are kept");
        int cleared = 0;
        foreach (var r in _remotes.Values)
        {
            // A clone block does not survive the scene it was learned in: a new scene is a new body,
            // a new head and a fresh chance, and a transient failure must not outlive it.
            if (r.CloneBlockedSlug.Length > 0) { ClearCloneBlock(r); cleared++; }
            DisposeBody(log, r, "scene-change");
        }
        if (cleared > 0)
            log?.LogInfo($"{Tag} scene change cleared {cleared} mesh-clone block(s) (a scene is not a permanent verdict)");
    }

    private static string SafeScene()
    {
        try { return SceneManager.GetActiveScene().name ?? ""; } catch { return ""; }
    }

    // ================================================================== peers / handshake

    private static Peer EnsurePeer(ulong id, float now)
    {
        if (_peers.TryGetValue(id, out var p)) return p;
        p = new Peer { Id = id, Handshake = Handshake.Pending, FirstSeen = now, NextHelloAt = 0f, LastSeen = now };
        _peers[id] = p;
        return p;
    }

    private static void ScanPeers(INetBridge net, float now)
    {
        _peerScan.Clear();
        int n = 0;
        try { n = net.Peers(_peerScan); } catch { n = 0; }
        // Client: the server is the only address a client may send to, so it is the only peer that
        // matters for the outbound side. It is still in ConnectedClientsIds on a client, so this is
        // normally the same list.
        for (int i = 0; i < n; i++)
        {
            if (_peerScan[i] == _localId) continue;
            var p = EnsurePeer(_peerScan[i], now);
            p.LastSeen = now;
        }
        _stale.Clear();
        foreach (var p in _peers.Values) if (now - p.LastSeen > 1.0f) _stale.Add(p.Id);
        foreach (var id in _stale)
        {
            _peers.Remove(id);
            _log?.LogInfo($"{Tag} peer gone client={id} (handshake={HandshakeWord(id)})");
            if (_remotes.TryGetValue(id, out var r)) { DisposeBody(_log, r, "peer-left"); _remotes.Remove(id); }
        }
    }

    private static string HandshakeWord(ulong id) =>
        _peers.TryGetValue(id, out var p) ? Word(p.Handshake) : "gone";

    private static string Word(Handshake h) => h == Handshake.Mod ? "mod" : h == Handshake.Vanilla ? "vanilla" : "pending";

    // ------------------------------------------------------------------ authority
    //
    // A state or emote frame NAMES its origin, but the only field the transport vouches for is the
    // sender. Presentation is a per-actor assertion, so the two have to agree before anything is
    // mutated: a frame whose claimed origin is not the peer that actually sent it would otherwise
    // let one client assert another actor's wardrobe/emote, and its forged payload would be cached
    // for late joiners. Every caller runs this BEFORE a peer record is created, before a payload is
    // cached, before a handshake verdict is written and before any presentation code.
    //
    //   server : accepted only while sender == origin. A participant may assert its own
    //            presentation and nobody else's; a mismatched frame is not "logged and not relayed",
    //            it is dropped whole.
    //   client : accepted only when it arrives from OUR server (the one peer a client can address or
    //            handshake at all) and we hold a matched direct handshake with that same server. The
    //            origin inside the frame is then the server's vouch, because the server only relays
    //            frames that passed its own rule above. A state frame's arrival is never taken as
    //            evidence that its origin has the mod, and never as authority for what it claims.
    //
    // Anything unproven is rejected, and it is rejected here - so a rejected frame leaves no trace.
    private static bool Vouched(IHostContext log, ulong sender, ulong origin, string what)
    {
        if (_isServer)
        {
            if (sender == origin) return true;
            log.LogWarning($"{Tag} REJECT {what} sender={sender} claims origin={origin}: a client may only " +
                           "assert its own presentation; dropped before peer state, cache and presentation");
            return false;
        }
        if (sender != _serverId)
        {
            log.LogWarning($"{Tag} REJECT {what} sender={sender} claims origin={origin}: a client accepts " +
                           $"peer frames only through its server (server={_serverId}); dropped");
            return false;
        }
        if (!_peers.TryGetValue(_serverId, out var sub) || sub.Handshake != Handshake.Mod)
        {
            log.LogWarning($"{Tag} REJECT {what} sender={sender} claims origin={origin}: no matched direct " +
                           $"handshake with the server client={_serverId} (handshake={HandshakeWord(_serverId)}); " +
                           "the origin cannot be vouched for; dropped");
            return false;
        }
        return true;
    }

    private static void HandshakeTick(IHostContext log, INetBridge net, float now)
    {
        foreach (var p in _peers.Values)
        {
            if (p.Handshake != Handshake.Pending) continue;
            if (p.HellosSent < HelloAttempts && now >= p.NextHelloAt) SendHello(log, net, p, now, "retry");
            else if (p.HellosSent >= 1 && now - p.FirstSeen >= HandshakeVerdictSeconds)
            {
                p.Handshake = Handshake.Vanilla;
                log?.LogInfo($"{Tag} handshake client={p.Id} mod=NO after {HandshakeVerdictSeconds:F1}s " +
                             $"({p.HellosSent} hello(s), no answer) -> native body, no mod traffic to this client");
            }
        }
    }

    private static void SendHello(IHostContext log, INetBridge net, Peer p, float now, string why)
    {
        var payload = new byte[3];
        payload[0] = MsgHello;
        payload[1] = ProtoVersion;
        payload[2] = CapsAll;
        bool ok = false;
        try { ok = net.Send(NetKind.App, p.Id, payload); } catch (Exception e) { log?.LogWarning($"{Tag} hello send failed: {e.Message}"); }
        p.HellosSent++;
        p.NextHelloAt = now + HelloRetrySeconds;
        if (ok || why == "session-start")
            log?.LogInfo($"{Tag} hello -> client={p.Id} attempt={p.HellosSent} proto={ProtoVersion} caps=0x{CapsAll:x2} ({why})");
    }

    // ================================================================== inbound

    private static void DrainInbound(IHostContext log, INetBridge net, float now)
    {
        var frame = default(NetInboundFrame);
        for (int i = 0; i < MaxFramesPerTick; i++)
        {
            bool got;
            try { got = net.TryTake(out frame); } catch { return; }
            if (!got) return;
            if (frame.Kind != NetKind.App || frame.Payload == null || frame.Payload.Length < 1) continue;
            try { HandleFrame(log, net, frame, now); }
            catch (Exception e) { log.LogWarning($"{Tag} frame from={frame.SenderClientId} threw: {e.Message}"); }
        }
    }

    private static void HandleFrame(IHostContext log, INetBridge net, in NetInboundFrame frame, float now)
    {
        var payload = frame.Payload;
        byte type = payload[0];
        // Lobby-music sync frames (20+). Dispatched first so the reel's range check cannot swallow
        // them (its handler returns false for types above its own range).
        if (type >= LobbyMusicNet.MsgState)
        {
            try { LobbyMusicNet.HandleFrame(log, net, frame, now); }
            catch (Exception e) { log.LogWarning($"{Tag} music frame type={type} failed: {e.Message}"); }
            return;
        }
        // Reel of Fortune authority frames (4..9). Dispatched before anything else: the host validates
        // requests and broadcasts its own settlements, a client only ever receives them.
        if (type >= ReelNet.MsgSpin)
        {
            try { ReelNet.HandleFrame(log, frame, now); }
            catch (Exception e) { log.LogWarning($"{Tag} reel frame type={type} failed: {e.Message}"); }
            return;
        }
        if (type == MsgHello)
        {
            byte proto = payload.Length > 1 ? payload[1] : (byte)0;
            byte caps = payload.Length > 2 ? payload[2] : (byte)0;
            var p = EnsurePeer(frame.SenderClientId, now);
            p.LastSeen = now;
            if (proto != ProtoVersion)
            {
                log.LogInfo($"{Tag} handshake client={p.Id} mod=MISMATCH proto={proto} (ours {ProtoVersion}) -> native body");
                p.Handshake = Handshake.Vanilla;
                return;
            }
            bool first = p.Handshake != Handshake.Mod;
            p.Handshake = Handshake.Mod;
            // A MATCHING hello is answered every time it arrives, not only the first one that turned
            // the peer into `mod`. The hello asks "is the mod over there, and what state do you
            // hold": a peer that reloaded (or reconnected) starts with an EMPTY peer table while
            // this side still has it recorded as `mod`, so anything gated on `first` was never sent,
            // the reloaded client never obtained the `Mod` record for the server that Vouched()
            // requires, and remote presentation stayed off for the whole session. So the hello itself
            // is always answered (one 3-byte frame per hello, and a client never answers a hello, so
            // the exchange cannot loop), and the heavier state replay - our model, every cached
            // state, the map's reel/music anchors - is replayed for the first hello and at most once
            // per HelloRetrySeconds after that, so a hello burst cannot become a state burst.
            bool replay = first || now >= p.NextAckAt;
            if (replay) p.NextAckAt = now + HelloRetrySeconds;
            if (first)
                log.LogInfo($"{Tag} handshake client={p.Id} mod=YES proto={proto} caps=0x{caps:x2} " +
                            $"({(p.HellosSent > 0 ? "answered" : "unsolicited")}) -> custom bodies enabled");
            else if (replay)
                log.LogInfo($"{Tag} handshake client={p.Id} mod=YES (re-hello) proto={proto} caps=0x{caps:x2} " +
                            "-> answering again: the peer has lost our handshake record; re-establishing");
            if (_isServer)
            {
                SendHello(log, net, p, now, first ? "reply" : "re-hello");
                if (!replay) return;             // our hello went back; the state replay waits a beat
                _localDirty = true;
                SendLocalState(log, net, now, "handshake");
                foreach (var other in _peers.Values)
                    if (other.Id != p.Id && other.Handshake == Handshake.Mod && other.HasState)
                        SendState(log, net, p, other.Id, other.CachedState, "late-join");
                // ...and the current map's machine anchors, epoch and revision (stage 3B).
                try { ReelNet.OnPeerMod(log, p.Id); }
                catch (Exception e) { log.LogWarning($"{Tag} reel late-join failed: {e.Message}"); }
                // ...and the music the host is playing, if any (late joiner starts mid-track).
                try { LobbyMusicNet.OnPeerMod(log, p.Id); }
                catch (Exception e) { log.LogWarning($"{Tag} music late-join failed: {e.Message}"); }
            }
            return;
        }

        if (type == MsgState)
        {
            if (payload.Length < 12) { log.LogWarning($"{Tag} state from={frame.SenderClientId} short bytes={payload.Length}"); return; }
            ulong origin = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(1, 8));
            byte flags = payload[9];
            int len = payload[10];
            if (11 + len > payload.Length) { log.LogWarning($"{Tag} state from={frame.SenderClientId} bad-length len={len} bytes={payload.Length}"); return; }
            var slug = len == 0 ? "" : Encoding.UTF8.GetString(payload, 11, len);
            int at = 11 + len;
            int count = at < payload.Length ? payload[at++] : 0;
            if (count > MaxCosmetics) { log.LogWarning($"{Tag} state from={frame.SenderClientId} cosmetic count={count} over the bound; ignored"); return; }
            var hats = new List<string>(count);
            for (int h = 0; h < count; h++)
            {
                if (at >= payload.Length) { log.LogWarning($"{Tag} state from={frame.SenderClientId} truncated cosmetic list"); return; }
                int il = payload[at++];
                if (il == 0) continue;
                if (at + il > payload.Length) { log.LogWarning($"{Tag} state from={frame.SenderClientId} truncated cosmetic id"); return; }
                hats.Add(Encoding.UTF8.GetString(payload, at, il));
                at += il;
            }

            if (origin == _localId)
            {
                // Our own frame relayed back by the server (or read from the ring after a role change).
                log.LogInfo($"{Tag} ignoring own state (client={origin} slug='{slug}')");
                return;
            }

            // Authority BEFORE mutation (see Vouched): an unproven claimed origin returns here, so no
            // peer record is created or refreshed, nothing is cached for a late joiner, no handshake
            // verdict is written and no presentation code runs. The rejection is logged by Vouched
            // with the sender, the claimed origin and the rule that refused it.
            if (!Vouched(log, frame.SenderClientId, origin, "state")) return;

            var p = EnsurePeer(origin, now);
            p.LastSeen = now;
            p.CachedSlug = slug;
            p.CachedState = (byte[])payload.Clone();
            p.HasState = true;
            if (p.Handshake != Handshake.Mod)
            {
                // A VOUCHED state means the origin has the mod even if we never saw its hello (server:
                // the sender IS the origin; client: our server relayed it after the same check). This
                // flag decides whom we address, never whom we believe - acceptance is Vouched above.
                p.Handshake = Handshake.Mod;
                log.LogInfo($"{Tag} handshake client={origin} mod=YES (state received)");
            }

            string sig = slug + "|" + flags + "|" + string.Join(",", hats.ToArray());
            if (origin != _localId && p.Print != sig)
            {
                p.Print = sig;
                log.LogInfo($"{Tag} recv client={origin} slug='{(slug.Length == 0 ? "<native>" : slug)}' " +
                            $"cosmetics=[{string.Join(",", hats.ToArray())}] chest={((flags & FlagChest) != 0 ? "on" : "off")} " +
                            $"hips={((flags & FlagHips) != 0 ? "on" : "off")} " +
                            $"from={frame.SenderClientId} flags=0x{flags:x2} bytes={payload.Length}");
            }

            // Only the server relays, and only a frame whose sender is its own origin got this far.
            if (_isServer) Relay(log, net, frame.SenderClientId, payload, slug);

            ApplyRemote(log, origin, slug, hats, flags, now);
            return;
        }

        if (type == MsgEmote)
        {
            if (payload.Length < 22) { log.LogWarning($"{Tag} emote from={frame.SenderClientId} short bytes={payload.Length}"); return; }
            ulong origin = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(1, 8));
            byte kind = payload[9];
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(10, 4));
            double sentAt = BinaryPrimitives.ReadDoubleLittleEndian(payload.AsSpan(14, 8));
            int il = payload[22];
            if (23 + il > payload.Length) { log.LogWarning($"{Tag} emote from={frame.SenderClientId} truncated id"); return; }
            string id = il == 0 ? "" : Encoding.UTF8.GetString(payload, 23, il);
            if (origin == _localId) return;

            // The same authority rule as state - for starts AND for stops. A mismatched claimed origin
            // never reaches the allowlist, the relay or RemoteEmotes.Start/Stop (it used to be only
            // kept out of the relay, while the emote was still applied locally).
            if (!Vouched(log, frame.SenderClientId, origin, kind == EmoteStart ? "emote-start" : "emote-stop")) return;

            var ep = EnsurePeer(origin, now);
            ep.LastSeen = now;
            if (ep.Handshake != Handshake.Mod)
            {
                // Vouched above: this only decides whom we address (see Vouched).
                ep.Handshake = Handshake.Mod;
                log.LogInfo($"{Tag} handshake client={origin} mod=YES (emote received)");
            }
            // Relay acceptance is allowlist-only, for starts AND for a stop that names an id: an id this
            // build does not offer is not forwarded and not acted on (an EMPTY stop id is the
            // established "stop whatever this actor runs" protocol and is relayed unchanged).
            string startId = kind == EmoteStart ? EmoteCatalog.Canonical(id) : null;
            if (kind == EmoteStart && startId == null)
            {
                log.LogWarning($"{Tag} emote-start client={origin} id='{id}' is not in the emote allowlist " +
                               $"({EmoteCatalog.List()}); not relayed and not played");
                return;
            }
            if (kind != EmoteStart && id.Length > 0 && EmoteCatalog.Canonical(id) == null)
            {
                log.LogWarning($"{Tag} emote-stop client={origin} id='{id}' is not in the emote allowlist " +
                               $"({EmoteCatalog.List()}); not relayed and not applied (an unknown id cannot stop " +
                               "another actor's clip)");
                return;
            }
            // Only the server relays, and only a frame whose sender is its own origin got this far.
            if (_isServer) RelayRaw(log, net, origin, payload, $"emote{kind}");

            double age = Clock() - sentAt;
            if (!ClockIsServer && (age < 0.0 || age > 5.0)) age = 0.0;   // clocks not comparable: play from the start
            float startAge = (float)Math.Max(0.0, age);
            if (kind == EmoteStart) RemoteEmotes.Start(log, origin, startId, seq, startAge);
            else
            {
                log.LogInfo($"{Tag} recv client={origin} emote-stop seq={seq} bytes={payload.Length}");
                RemoteEmotes.Stop(log, origin, id, seq);
            }
            return;
        }

        log.LogInfo($"{Tag} unknown message type={type} from={frame.SenderClientId} bytes={payload.Length}");
    }

    // Server-side fan-out for the reel authority: only clients that answered the handshake are
    // addressed, and never the emitter (except == 0 means everyone). Returns how many sends landed.
    internal static int SendToModPeers(IHostContext log, INetBridge net, byte[] payload, string what, ulong except)
    {
        if (net == null || payload == null) return 0;
        int sent = 0;
        var targets = new List<ulong>(4);
        foreach (var p in _peers.Values)
        {
            if (p.Handshake != Handshake.Mod) continue;
            if (except != 0 && p.Id == except) continue;
            bool ok = false;
            try { ok = net.Send(NetKind.App, p.Id, payload); } catch { }
            if (!ok) continue;
            sent++;
            targets.Add(p.Id);
        }
        return sent;
    }

    // Server-side fan-out of an event frame (emote start/stop): the same rule as state - only a
    // mod-confirmed peer is addressed, and never the emitter.
    private static void RelayRaw(IHostContext log, INetBridge net, ulong from, byte[] payload, string what)
    {
        var targets = new List<ulong>(4);
        foreach (var p in _peers.Values)
        {
            if (p.Id == from || p.Handshake != Handshake.Mod) continue;
            bool ok = false;
            try { ok = net.Send(NetKind.App, p.Id, payload); } catch { }
            if (ok) targets.Add(p.Id);
        }
        if (targets.Count > 0)
            log.LogInfo($"{Tag} relay {what} client={from} -> [{string.Join(",", targets.ConvertAll(x => x.ToString()).ToArray())}]");
    }

    // Server-side fan-out: only clients that answered the handshake get a mod frame. The emitter of
    // the frame never gets it back (it already knows its own choice).
    private static void Relay(IHostContext log, INetBridge net, ulong from, byte[] payload, string slug)
    {
        int sent = 0;
        var targets = new List<ulong>(4);
        foreach (var p in _peers.Values)
        {
            if (p.Id == from || p.Handshake != Handshake.Mod) continue;
            bool ok = false;
            try { ok = net.Send(NetKind.App, p.Id, payload); } catch { }
            if (!ok) continue;
            p.SentSlug = p.CachedSlug;      // this peer now has the origin's latest
            targets.Add(p.Id);
            sent++;
        }
        if (sent > 0)
            log.LogInfo($"{Tag} relay client={from} slug='{(slug.Length == 0 ? "<native>" : slug)}' -> " +
                        $"[{string.Join(",", targets.ConvertAll(x => x.ToString()).ToArray())}]");
    }

    // ================================================================== outbound

    private static void SendLocalTick(IHostContext log, INetBridge net, float now)
    {
        bool heartbeat = now >= _nextHeartbeat;
        bool changed = _localDirty;
        if (!heartbeat && !changed) return;
        if (now < _nextStateSend) return;
        SendLocalState(log, net, now, heartbeat ? "heartbeat" : "change");
    }

    private static void SendLocalState(IHostContext log, INetBridge net, float now, string why)
    {
        var payload = BuildLocalState(out int hatCount);
        string sig = Convert.ToBase64String(payload);
        // Only mod-confirmed peers are addressed. A vanilla peer never receives a state frame.
        var targets = new List<ulong>(4);
        foreach (var p in _peers.Values)
        {
            if (p.Handshake != Handshake.Mod) continue;
            if (why == "change" && p.SentSlug == sig) continue;
            targets.Add(p.Id);
        }
        if (targets.Count == 0)
        {
            // Nothing to send to yet; keep the change queued for the moment a peer answers.
            _nextStateSend = now + StateMinInterval;
            return;
        }

        var sent = new List<ulong>(4);
        foreach (var id in targets)
        {
            if (id == _localId) continue;
            bool ok = false;
            try { ok = net.Send(NetKind.App, id, payload); } catch { }
            if (!ok) continue;
            if (_peers.TryGetValue(id, out var p)) p.SentSlug = sig;
            sent.Add(id);
        }
        _nextStateSend = now + StateMinInterval;
        _nextHeartbeat = now + HeartbeatSeconds;
        if (sent.Count > 0)
        {
            _localDirty = false;
            bool first = !_localLogged;
            if (first || why != "heartbeat")
            {
                if (first) _localLogged = true;
                log.LogInfo($"{Tag} sent model='{(_localSlug.Length == 0 ? "<native>" : _localSlug)}' " +
                            $"cosmetics={hatCount} chest={(EnhancementSettings.ChestEnabled ? "on" : "off")} " +
                            $"hips={(EnhancementSettings.HipsEnabled ? "on" : "off")} " +
                            $"client={_localId} -> [{string.Join(",", sent.ConvertAll(x => x.ToString()).ToArray())}] ({why})");
            }
        }
    }

    // ---- the one encoder for our own state frame. Both the model slug and the equipped cosmetic set
    // are bounded by the format, never by what the local set happens to contain.
    private static byte[] BuildLocalState(out int hatCount)
    {
        int len = Encoding.UTF8.GetByteCount(_localSlug);
        if (len > 96) len = 0;                      // a slug is a short stable id; never a payload
        var hats = HostedCosmetics();
        var ids = new List<byte[]>(hats.Count);
        foreach (var id in hats)
        {
            int n = Encoding.UTF8.GetByteCount(id);
            if (n == 0 || n > MaxIdBytes) continue;
            var b = new byte[n];
            Encoding.UTF8.GetBytes(id, 0, id.Length, b, 0);
            ids.Add(b);
            if (ids.Count >= MaxCosmetics) break;
        }
        hatCount = ids.Count;
        int total = 11 + len + 1;
        foreach (var b in ids) total += 1 + b.Length;
        var payload = new byte[total];
        payload[0] = MsgState;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1, 8), _localId);
        payload[9] = (byte)((EnhancementSettings.ChestEnabled ? FlagChest : 0) |
                            (EnhancementSettings.HipsEnabled ? FlagHips : 0));
        payload[10] = (byte)len;
        int at = 11;
        if (len > 0) { Encoding.UTF8.GetBytes(_localSlug, 0, _localSlug.Length, payload, at); at += len; }
        payload[at++] = (byte)ids.Count;
        foreach (var b in ids) { payload[at++] = (byte)b.Length; b.CopyTo(payload, at); at += b.Length; }
        return payload;
    }

    private static List<string> HostedCosmetics()
    {
        var rt = CosmeticsRuntime.Current;
        if (rt == null) return new List<string>(0);
        try { return rt.EquippedIds(); } catch { return new List<string>(0); }
    }

    private static int HostedCosmeticCount()
    {
        var rt = CosmeticsRuntime.Current;
        try { return rt == null ? 0 : rt.EquippedCount; } catch { return 0; }
    }

    // Everything the wearer's state frame carries, as one comparable string: a change in any part
    // (slug, either toggle, the equipped set) marks the frame dirty exactly once.
    private static void RefreshLocalSignature(IHostContext log)
    {
        string hats = string.Join(",", HostedCosmetics().ToArray());
        string sig = _localSlug + "|" + (EnhancementSettings.ChestEnabled ? 1 : 0) +
                     (EnhancementSettings.HipsEnabled ? 1 : 0) + "|" + hats;
        if (sig == _localSig) return;
        string from = _localSig;
        _localSig = sig;
        if (from.Length == 0) return;               // first evaluation of the session
        _localDirty = true;
        _nextStateSend = 0f;
        try
        {
            log?.LogInfo($"{Tag} local state changed ('{from}' -> '{sig}'); queued for peers");
        }
        catch { }
    }

    private static void SendEmoteTick(IHostContext log, INetBridge net)
    {
        if (_pendingEmoteId == null) return;
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextEmoteSend) return;
        var targets = new List<ulong>(4);
        foreach (var p in _peers.Values)
            if (p.Handshake == Handshake.Mod) targets.Add(p.Id);
        if (targets.Count == 0) { _nextEmoteSend = now + 0.1f; return; }   // keep it queued until a peer answers

        string pendingId = _pendingEmoteId;
        byte pendingKind = _pendingEmoteKind;
        uint pendingSeq = _pendingEmoteSeq;
        double pendingAt = _pendingEmoteAt;
        bool pendingClock = _pendingEmoteClock;
        int len = Encoding.UTF8.GetByteCount(pendingId);
        if (len > MaxEmoteIdBytes) len = 0;
        var payload = new byte[23 + len];
        payload[0] = MsgEmote;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(1, 8), _localId);
        payload[9] = _pendingEmoteKind;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(10, 4), _pendingEmoteSeq);
        BinaryPrimitives.WriteDoubleLittleEndian(payload.AsSpan(14, 8), _pendingEmoteAt);
        payload[22] = (byte)len;
        if (len > 0) Encoding.UTF8.GetBytes(_pendingEmoteId, 0, _pendingEmoteId.Length, payload, 23);

        var sent = new List<ulong>(4);
        foreach (var id in targets)
        {
            if (id == _localId) continue;
            bool ok = false;
            try { ok = net.Send(NetKind.App, id, payload); } catch { }
            if (ok) sent.Add(id);
        }
        _pendingEmoteId = null;
        _nextEmoteSend = now + 0.05f;
        if (sent.Count > 0)
            log.LogInfo($"{Tag} sent emote {(pendingKind == EmoteStart ? "start" : "stop")} " +
                        $"id='{(pendingId.Length == 0 ? "<idle>" : pendingId)}' seq={pendingSeq} t={pendingAt:F3} " +
                        $"clock={(pendingClock ? "server" : "local")} bytes={payload.Length} client={_localId} -> " +
                        $"[{string.Join(",", sent.ConvertAll(x => x.ToString()).ToArray())}]");
    }

    // Late joiner: replay the origin's last state frame byte for byte, so the model, the toggles and
    // the equipped set arrive together and cannot be assembled from stale pieces.
    private static void SendState(IHostContext log, INetBridge net, Peer target, ulong origin, byte[] payload, string why)
    {
        if (payload == null || payload.Length < 12) return;
        bool ok = false;
        try { ok = net.Send(NetKind.App, target.Id, payload); } catch { }
        if (ok)
        {
            target.SentSlug = Convert.ToBase64String(payload);
            log.LogInfo($"{Tag} relay client={origin} state bytes={payload.Length} -> [{target.Id}] ({why})");
        }
    }

    // ================================================================== remote bodies

    private static void ApplyRemote(IHostContext log, ulong clientId, string slug, List<string> hats, byte flags, float now)
    {
        if (!_remotes.TryGetValue(clientId, out var r))
        {
            r = new Remote { ClientId = clientId };
            _remotes[clientId] = r;
        }
        r.HasState = true;
        r.LastStateAt = now;
        r.Chest = (flags & FlagChest) != 0;
        r.Hips = (flags & FlagHips) != 0;
        hats ??= new List<string>(0);
        bool sameHats = HatsEqual(r.Hats, hats);
        if (!sameHats || r.HatVersion < 0)
        {
            r.Hats.Clear();
            foreach (var h in hats) if (!string.IsNullOrEmpty(h)) r.Hats.Add(h);
            r.Hats.Sort(StringComparer.Ordinal);
            if (r.Hats.Count > MaxCosmetics) r.Hats.RemoveRange(MaxCosmetics, r.Hats.Count - MaxCosmetics);
            r.HatVersion = (r.HatVersion < 0 ? 0 : r.HatVersion) + 1;
            if (!sameHats) ReconcileHats(log, r, "peer-set");
        }

        if (slug.Length == 0)
        {
            if (r.Slug.Length != 0) TouchPresentation();   // identity changed: native now
            if (r.BuiltKind != 0) DisposeBody(log, r, "peer-native");
            r.Slug = "";
            // Native advert = the game's own body is the body. Nothing is built, but the body still
            // has to be resolved so its head bone can carry the wearer's cosmetics and so a rebuilt
            // native body (a respawn) re-seats them: the seat is measured on the game's own head
            // mesh (rule=measured-native), the same measurement the local owner's native seat uses.
            ResolveNative(log, r, now);
            if (r.Hats.Count > 0) ReconcileHats(log, r, "native-body");
            return;
        }
        if (!string.Equals(r.Slug, slug, StringComparison.Ordinal)) TouchPresentation();
        r.Slug = slug;
        // A different advertised model is a different clone: the block belongs to the old slug and
        // must not outlive it, so A -> B -> A can rebuild A instead of staying on B's native body.
        if (r.CloneBlockedSlug.Length > 0 && !string.Equals(r.CloneBlockedSlug, slug, StringComparison.Ordinal))
            ClearCloneBlock(r);

        if (r.BuiltSlug == slug && BodyAlive(r)) return;
        if (r.BuiltKind != 0)
        {
            if (r.Print != "liveness:" + slug)
            {
                r.Print = "liveness:" + slug;
                log.LogWarning($"{RemoteTag} liveness client={clientId} slug='{slug}' kind={r.BuiltKind} " +
                               $"rig={(r.Rig == null ? "null" : "set")} bodyGo={(r.Rig == null ? "n/a" : (r.Rig.S.BodyGo == null ? "null" : "set"))} " +
                               $"rigid={(r.Rigid == null ? "null" : "set")} built='{r.BuiltSlug}'");
            }
            DisposeBody(log, r, "model-change");
        }

        var spec = ModelRoster.BySlug(slug);
        if (spec == null)
        {
            if (!r.LoggedUnknown)
            {
                r.LoggedUnknown = true;
                log.LogWarning($"{RemoteTag} client={clientId} advertises unknown slug '{slug}' " +
                               "(not in this build's roster) -> native body; no further retry logged");
            }
            return;
        }
        Build(log, r, spec, now);
    }

    // Liveness of the body we built, not of the lease's snapshot: the snapshot is rewritten by the
    // pose pass, and an early-returning pose used to leave it looking dead while the body was fine,
    // which tore down and rebuilt a healthy remote body on every state heartbeat.
    private static bool BodyAlive(Remote r)
    {
        try
        {
            if (r.BuiltKind == 2) return r.Rig != null && r.BodyGo != null && r.BodyGo.scene.IsValid();
            if (r.BuiltKind == 1) return r.Rigid != null && r.BodyGo != null && r.BodyGo.scene.IsValid();
        }
        catch { }
        return false;
    }

    // ================================================================== stage 3A: hats + toggles
    private static bool HatsEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    // Per-tick rules for state that is not part of the body build: the wearer's equipped set can
    // change without the model changing, the enhancement toggles likewise, and a rebuilt body needs
    // both re-attached. One place, so no path can leave a hat behind or stack a second copy.
    private static void RuleTick(IHostContext log, float now)
    {
        if (_remotes.Count == 0) return;
        var cosmetics = CosmeticsRuntime.Current;
        foreach (var r in _remotes.Values)
        {
            if (r.BuiltKind == 0)
            {
                // A native advert is not "the body is gone": the game's own body is the body, and the
                // wearer's cosmetics sit on its head bone (measured-native). Only an unresolved or
                // unknown state may not carry anything.
                if (r.Slug.Length == 0)
                {
                    Transform nHead = HeadOf(r);
                    IntPtr nPtr = IntPtr.Zero;
                    try { if (nHead != null) nPtr = nHead.Pointer; } catch { }
                    bool nChanged = nHead != null && nPtr != r.HatHeadPtr;
                    if (r.Attached != null && (nChanged || !HatsEqual(r.Attached.Ids, r.Hats)))
                        ReconcileHats(log, r, nChanged ? "head-changed" : "set-changed");
                    else if (r.Attached == null && r.Hats.Count > 0 && nHead != null)
                        ReconcileHats(log, r, "attach");
                    continue;      // no enhancements: a native body has no mesh of ours to deform
                }
                // The body is gone (unresolved, unknown slug, tearing down): nothing may outlive it.
                if (r.Attached != null) ReconcileHats(log, r, "no-body");
                continue;
            }
            // Head identity: a rebuilt skeleton invalidates an attachment even at the same set.
            Transform head = HeadOf(r);
            IntPtr headPtr = IntPtr.Zero;
            try { if (head != null) headPtr = head.Pointer; } catch { }
            bool headChanged = head != null && headPtr != r.HatHeadPtr;
            if (r.Attached != null && (headChanged || !HatsEqual(r.Attached.Ids, r.Hats)))
                ReconcileHats(log, r, headChanged ? "head-changed" : "set-changed");
            if (r.Attached == null && r.Hats.Count > 0) ReconcileHats(log, r, "attach");
            if (cosmetics != null && r.Attached != null && r.Attached.SetVersion != cosmetics.EquippedVersion &&
                !HatsEqual(r.Attached.Ids, r.Hats))
                ReconcileHats(log, r, "local-set-version");

            // Enhancements: the wearer's two booleans, applied with the same per-model profile code
            // the local body uses. Re-applied when either boolean or the body changes.
            if (!r.EnhApplied || r.EnhChest != r.Chest || r.EnhHips != r.Hips)
                ApplyEnhancements(log, r, "sync");
        }
    }

    private static Transform HeadOf(Remote r)
    {
        try
        {
            if (r.Model == null) return null;
            var bones = r.Model.Bones;
            return bones == null ? null : bones.Head;
        }
        catch { return null; }
    }

    private static Renderer BodyRendererOf(Remote r)
    {
        try
        {
            if (r.BuiltKind == 2 && r.Rig != null) return r.Rig.S.Smr;
            if (r.BuiltKind == 1 && r.Rigid != null)
            {
                var smr = r.Rigid.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null) return smr;
                return r.Rigid.GetComponentInChildren<Renderer>(true);
            }
        }
        catch { }
        return null;
    }

    private static void ApplyEnhancements(IHostContext log, Remote r, string why)
    {
        var spec = ModelRoster.BySlug(r.BuiltSlug);
        var body = BodyRendererOf(r);
        if (spec == null || body == null)
        {
            if (why != "sync") log.LogWarning($"{RemoteTag} client={r.ClientId} enhancement apply skipped (spec/body missing)");
            return;
        }
        try
        {
            // Releasing first makes the two-boolean transition exact: the writer always re-derives
            // from the recorded originals, and a body rebuilt underneath us is a new record anyway.
            CustomEnhancements.ApplyExternal(log, spec, body, r.Model == null ? null : r.Model.transform,
                                             r.Chest, r.Hips, $"remote:{r.ClientId}:{why}");
            r.EnhApplied = true;
            r.EnhChest = r.Chest;
            r.EnhHips = r.Hips;
            r.EnhApplies++;
        }
        catch (Exception e) { log.LogWarning($"{RemoteTag} client={r.ClientId} enhancement failed: {e.Message}"); }
    }

    private static void ReleaseEnhancements(IHostContext log, Remote r, string why)
    {
        var body = BodyRendererOf(r);
        if (body != null)
        {
            try { CustomEnhancements.ReleaseExternal(log, body, $"remote:{r.ClientId}:{why}"); } catch { }
            r.EnhReleases++;
        }
        r.EnhApplied = false;
    }

    private static void ReconcileHats(IHostContext log, Remote r, string why)
    {
        var cosmetics = CosmeticsRuntime.Current;
        if (cosmetics == null) return;
        if (r.Attached != null)
        {
            try { cosmetics.DisposeRemote(r.Attached); } catch { }
            r.Attached = null;
            r.HatDetaches++;
            r.HatHeadPtr = IntPtr.Zero;
        }
        // A native advert (slug empty) has no build of ours but is still a body to dress; every
        // other BuiltKind==0 state (unresolved, unknown slug, tearing down) must not carry anything.
        if (why == "no-body" || r.Hats.Count == 0) return;
        if (r.BuiltKind == 0 && r.Slug.Length != 0) return;
        var spec = ModelRoster.BySlug(r.BuiltSlug);
        var head = HeadOf(r);
        bool nativeBody = spec == null && r.BuiltKind == 0 && r.Slug.Length == 0;
        if ((spec == null && !nativeBody) || head == null || r.Model == null)
        {
            if (r.Print != "hats-wait:" + r.BuiltSlug)
            {
                r.Print = "hats-wait:" + r.BuiltSlug;
                log.LogInfo($"{RemoteTag} client={r.ClientId} cosmetics=[{string.Join(",", r.Hats.ToArray())}] " +
                            "waiting for a resolved head bone");
            }
            return;
        }
        try
        {
            r.Attached = cosmetics.AttachRemote(r.ClientId, $"client={r.ClientId}", r.Model, head, spec, r.Hats);
            r.HatAttaches++;
            try { r.HatHeadPtr = head.Pointer; } catch { }
            log.LogInfo($"{RemoteTag} client={r.ClientId} cosmetics attach why={why} set=[{string.Join(",", r.Hats.ToArray())}] " +
                        $"instances={(r.Attached == null ? 0 : r.Attached.Instances.Count)} " +
                        $"renderers={(r.Attached == null ? 0 : r.Attached.Renderers)} attaches={r.HatAttaches} " +
                        $"detaches={r.HatDetaches} anchor={(ModelRoster.AnchorFor(spec) != null)}");
        }
        catch (Exception e) { log.LogWarning($"{RemoteTag} client={r.ClientId} cosmetics attach failed: {e.Message}"); }
    }

    // The local rig's hide pass asks this before it snapshots a renderer: a remote body's renderers
    // belong to that body's lease, and two leases on one renderer is how a body ends up invisible.
    internal static bool IsLeased(Renderer renderer)
    {
        if (renderer == null) return false;
        IntPtr p;
        try { p = renderer.Pointer; } catch { return false; }
        foreach (var r in _remotes.Values)
        {
            for (int i = 0; i < r.HiddenRenderers.Count; i++)
            {
                var e = r.HiddenRenderers[i];
                if (e.R == null) continue;
                try { if (e.R.Pointer == p) return true; } catch { }
            }
            List<SahurRig.BodyRenderer> body = null;
            try { body = r.Rig == null ? null : r.Rig.S.Body; } catch { }
            if (body != null)
                for (int i = 0; i < body.Count; i++)
                {
                    var e = body[i];
                    if (e.R == null) continue;
                    try { if (e.R.Pointer == p) return true; } catch { }
                }
        }
        return false;
    }

    // The native half of Build(): no body of ours exists, so all that is kept is the game's own
    // ThirdViewPlayerModel (r.Model/r.Root) and, through it, its head bone. Throttled like Build.
    private static void ResolveNative(IHostContext log, Remote r, float now)
    {
        if (now < r.NextResolve) return;
        bool alive = r.Model != null && ModelAlive(r.Model);
        if (alive) return;
        r.NextResolve = now + ResolveSeconds;
        var (model, player) = Resolve(r.ClientId);
        if (model == null)
        {
            if (!r.LoggedUnresolved)
            {
                r.LoggedUnresolved = true;
                log.LogWarning($"{RemoteTag} client={r.ClientId} is native but no ThirdViewPlayerModel " +
                               "resolves for that client yet; waiting");
            }
            return;
        }
        r.LoggedUnresolved = false;
        r.Model = model;
        r.Root = model.transform;
        log.LogInfo($"{RemoteTag} native client={r.ClientId} player='{(player == null ? "null" : PathOf(player.transform))}' " +
                    $"model='{PathOf(model.transform)}' owner={(player == null ? "?" : player.OwnerClientId.ToString())} " +
                    $"cosmetics=[{string.Join(",", r.Hats.ToArray())}]");
    }

    private static void Build(IHostContext log, Remote r, ModelSpec spec, float now)
    {
        if (now < r.NextResolve) return;
        r.NextResolve = now + ResolveSeconds;
        // A remote body aborted for a failed mesh clone is retried after a doubling backoff, not
        // blacklisted: one transient exception must not hide this peer's model for the session. The
        // block is also cleared outright when the wearer advertises another model or the scene
        // changes. `NextCloneRetryAt` is a delay, never a verdict - after it, the build is attempted
        // again with the same fail-closed path.
        if (r.CloneBlockedSlug == spec.Slug && now < r.NextCloneRetryAt)
        {
            if (r.Print != "clone-retry-wait:" + spec.Slug)
            {
                r.Print = "clone-retry-wait:" + spec.Slug;
                log.LogInfo($"{RemoteTag} client={r.ClientId} '{spec.Slug}' clone is blocked until " +
                             $"+{r.NextCloneRetryAt - now:F1}s after {r.CloneBlockFails} failure(s); then retried");
            }
            return;
        }

        var (model, player) = Resolve(r.ClientId);
        if (model == null)
        {
            if (!r.LoggedUnresolved)
            {
                r.LoggedUnresolved = true;
                log.LogWarning($"{RemoteTag} client={r.ClientId} advertises '{spec.Slug}' but no " +
                               "ThirdViewPlayerModel resolves for that client yet; waiting");
            }
            return;
        }
        r.LoggedUnresolved = false;

        int layer = 0;
        string modelPath = PathOf(model.transform);
        try { layer = model.gameObject.layer; } catch { }
        log.LogInfo($"{RemoteTag} resolve client={r.ClientId} player='{(player == null ? "null" : PathOf(player.transform))}' " +
                    $"model='{modelPath}' layer={layer} ('{LayerMask.LayerToName(layer)}') " +
                    $"owner={(player == null ? "?" : player.OwnerClientId.ToString())}");

        if (r.Model != null && r.Root != null && r.Model.Pointer != model.Pointer)
            DisposeBody(log, r, "model-instance-changed");

        r.Model = model;
        r.Root = model.transform;

        try
        {
            if (spec.Kind == ModelKind.Rigged)
            {
                // Own skeleton, delta-retargeted from this model's bones. The lease hides the native
                // renderers itself (Build -> HideBody) and restores them on Detach.
                // All rig roles now own the private mesh and shared-resource lease in SahurRig.
                // A failed clone/attach leaves the native body visible and uses bounded retries.
                r.Rig = SahurRig.AttachExternal(log, spec, model, r.Root, $"remote:{r.ClientId}:{spec.Slug}", layer);
                if (r.Rig == null)
                {
                    r.CloneBlockedSlug = spec.Slug;
                    r.CloneBlockFails++;
                    float delay = ModelWantedPolicy.RetryDelay(r.CloneBlockFails, CloneRetryBaseSeconds, CloneRetryMaxSeconds);
                    r.NextCloneRetryAt = now + delay;
                    log.LogWarning($"{RemoteTag} client={r.ClientId} rigged attach failed for '{spec.Slug}'; retry in {delay:F0}s");
                    DisposeBody(log, r, "rigged-attach-failed");
                    return;
                }
                r.BodyGo = r.Rig.S.BodyGo;
                r.BuiltKind = 2;
            }
            else
            {
                HideNative(log, r, model);
                r.Rigid = SahurModel.BuildPreviewBody(log, spec, model, layer, out var mesh, out var note);
                r.RigidMesh = mesh;
                r.BodyGo = r.Rigid;
                if (r.Rigid == null)
                {
                    log.LogWarning($"{RemoteTag} client={r.ClientId} rigid build failed for '{spec.Slug}': {note}");
                    RestoreNative(log, r);
                    return;
                }
                r.BuiltKind = 1;
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"{RemoteTag} client={r.ClientId} build threw for '{spec.Slug}': {e}");
            RestoreNative(log, r);
            return;
        }

        r.BuiltSlug = spec.Slug;
        TouchPresentation();                         // a body for this slug exists now
        ClearCloneBlock(r);                          // the body is here: whatever was blocked is not
        r.HatHeadPtr = IntPtr.Zero;                 // a new body is a new head bone
        r.Builds++;
        log.LogInfo($"{RemoteTag} BUILD client={r.ClientId} slug='{spec.Slug}' kind={spec.Kind} label='{spec.Label}' " +
                    $"host='{modelPath}' hiddenNative={r.HiddenRenderers.Count} " +
                    $"body='{(r.BuiltKind == 2 ? "rigged skeleton" : "rigid bind")}' builds={r.Builds} " +
                    $"cosmetics={r.Hats.Count} chest={(r.Chest ? "on" : "off")} hips={(r.Hips ? "on" : "off")}");
        // Attach the wearer's current state in the same frame the body appears, so there is never a
        // visible frame with a body and no cosmetics, and never two attach passes for one build.
        ReconcileHats(log, r, "body-built");
        ApplyEnhancements(log, r, "body-built");
    }

    private static void ClearCloneBlock(Remote r)
    {
        r.CloneBlockedSlug = "";
        r.CloneBlockFails = 0;
        r.NextCloneRetryAt = 0f;
    }

    private static void RemoteTick(IHostContext log, float now)
    {
        foreach (var r in _remotes.Values)
        {
            if (r.BuiltKind == 0) continue;
            bool alive = ModelAlive(r.Model);
            if (!alive)
            {
                if (r.GoneSince <= 0f) r.GoneSince = now;
                else if (now - r.GoneSince >= ModelGoneSeconds) DisposeBody(log, r, "model-gone");
                continue;
            }
            r.GoneSince = 0f;
        }
    }

    private static bool ModelAlive(ThirdViewPlayerModel model)
    {
        try
        {
            if (model == null) return false;
            var go = model.gameObject;
            if (go == null) return false;
            if (go.scene.name == null || go.scene.name.Length == 0) return false;
            return true;
        }
        catch { return false; }
    }

    // ================================================================== resolve / hide

    // WHO the remote body belongs to is the whole point: never Owner: 0, never Camera.main, never
    // the local player provider. The client id is matched against the Player's Netcode ownership,
    // with the live scene name marker ("... | Owner: N") only as the fallback the mirror work
    // already measured.
    private static (ThirdViewPlayerModel, Player) Resolve(ulong clientId)
    {
        Player owner = null;
        try
        {
            var players = SceneCache.Scan(Il2CppType.Of<Player>());
            for (int i = 0; players != null && i < players.Length; i++)
            {
                var p = players[i].TryCast<Player>();
                if (p == null || p.transform == null) continue;
                if (!InScene(p.transform)) continue;
                if (p.OwnerClientId != clientId) continue;
                owner = p;
                break;
            }
        }
        catch { }

        if (owner != null)
        {
            var models = SafeModels(owner);
            if (models != null) return (models, owner);
        }

        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<ThirdViewPlayerModel>());
            ThirdViewPlayerModel idle = null;
            string marker = "Owner: " + clientId;
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var m = all[i].TryCast<ThirdViewPlayerModel>();
                if (m == null || m.transform == null) continue;
                if (!InScene(m.transform)) continue;
                string path = PathOf(m.transform);
                int at = path.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) continue;
                int after = at + marker.Length;
                if (after < path.Length && char.IsDigit(path[after])) continue;      // Owner: 1 vs 10
                if (m.gameObject.activeInHierarchy) return (m, owner);
                if (idle == null) idle = m;
            }
            if (idle != null) return (idle, owner);
        }
        catch { }
        return (null, owner);
    }

    private static ThirdViewPlayerModel SafeModels(Player owner)
    {
        try
        {
            var own = owner.GetComponentsInChildren<ThirdViewPlayerModel>(true);
            ThirdViewPlayerModel idle = null;
            for (int i = 0; own != null && i < own.Length; i++)
            {
                var m = own[i];
                if (m == null || m.transform == null || !InScene(m.transform)) continue;
                if (m.gameObject.activeInHierarchy) return m;
                if (idle == null) idle = m;
            }
            return idle;
        }
        catch { return null; }
    }

    private static bool InScene(Transform t)
    {
        try { var s = t.gameObject.scene; return s.IsValid() && !string.IsNullOrEmpty(s.name); }
        catch { return false; }
    }

    private static void HideNative(IHostContext log, Remote r, ThirdViewPlayerModel model)
    {
        if (r.HiddenRenderers.Count > 0) return;
        try
        {
            var comps = model.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
            for (int i = 0; comps != null && i < comps.Length; i++)
            {
                var rr = comps[i]?.TryCast<Renderer>();
                if (rr == null) continue;
                if (CosmeticMarkers.IsCosmetic(rr)) continue;      // our own body/cosmetic must not be leased
                if (DefaultHands.IsLeased(rr)) continue;
                var go = rr.gameObject;
                if (go == null || ModelRoster.IsOurObjectName(go.name ?? "")) continue;
                bool had;
                try { had = rr.enabled; } catch { continue; }
                var e = new NativeRenderer { R = rr, HadEnabled = had };
                try { rr.enabled = false; e.Hidden = true; } catch { }
                r.HiddenRenderers.Add(e);
            }
            log.LogInfo($"{RemoteTag} lease client={r.ClientId} hid {r.HiddenRenderers.Count} native renderer(s) " +
                        $"under '{PathOf(model.transform)}' (disabled, not destroyed)");
        }
        catch (Exception e) { log.LogWarning($"{RemoteTag} hide client={r.ClientId} failed: {e.Message}"); }
    }

    private static void RestoreNative(IHostContext log, Remote r)
    {
        int restored = 0;
        foreach (var e in r.HiddenRenderers)
        {
            if (!e.Hidden || e.R == null) continue;
            try { e.R.enabled = e.HadEnabled; restored++; } catch { }
            e.Hidden = false;
        }
        if (r.HiddenRenderers.Count > 0)
            log.LogInfo($"{RemoteTag} lease client={r.ClientId} restored {restored} native renderer(s)");
        r.HiddenRenderers.Clear();
    }

    private static void DisposeBody(IHostContext log, Remote r, string why)
    {
        if (r.BuiltKind != 0) TouchPresentation();
        int kind = r.BuiltKind;
        string slug = r.BuiltSlug;
        try
        {
            RemoteEmotes.Drop(log, r.ClientId, $"body:{why}");
            ReleaseEnhancements(log, r, why);
            ReconcileHats(log, r, "no-body");
            if (r.Rig != null) { SahurRig.DetachExternal(log, r.Rig, $"remote:{r.ClientId}:{why}"); r.Rig = null; }
            if (!ReferenceEquals(r.RigidMesh, null)) { SahurModel.DestroyPreviewBody(log, r.Rigid, r.RigidMesh); r.Rigid = null; r.RigidMesh = null; }
            RestoreNative(log, r);
        }
        catch (Exception e) { log.LogWarning($"{RemoteTag} dispose client={r.ClientId} failed: {e.Message}"); }
        r.BuiltKind = 0;
        r.BuiltSlug = "";
        r.Model = null;
        r.Root = null;
        r.BodyGo = null;
        r.GoneSince = 0f;
        r.NextResolve = 0f;
        if (kind != 0)
        {
            r.Disposes++;
            log.LogInfo($"{RemoteTag} disposed client={r.ClientId} slug='{slug}' why={why} " +
                        $"builds={r.Builds} disposes={r.Disposes} hiddenLeft={r.HiddenRenderers.Count} " +
                        $"hatAttaches={r.HatAttaches} hatDetaches={r.HatDetaches} " +
                        $"enhApplies={r.EnhApplies} enhReleases={r.EnhReleases}");
        }
    }

    private static void TeardownAll(IHostContext log, string why)
    {
        if (_remotes.Count == 0) return;
        var clients = new List<ulong>(_remotes.Keys);
        foreach (var id in clients)
        {
            if (!_remotes.TryGetValue(id, out var r)) continue;
            DisposeBody(log, r, why);
            if (why == "module-dispose" || why == "session-end") _remotes.Remove(id);
        }
        log?.LogInfo($"{RemoteTag} teardown ({why}) remaining={_remotes.Count}");
    }

    // ================================================================== census / request file

    // One line per known client plus our object census: the leave/respawn/reload evidence.
    internal static void Dump(IHostContext log)
    {
        try
        {
            log.LogInfo($"{Tag} state epoch={_epoch} gen={_gen} local={_localId} role={(_isServer ? "server" : "client")} " +
                        $"session={_sessionLive} localModel='{(_localSlug.Length == 0 ? "<native>" : _localSlug)}' " +
                        $"peers={_peers.Count} remotes={_remotes.Count}");
            foreach (var p in _peers.Values)
                log.LogInfo($"{Tag} peer client={p.Id} handshake={Word(p.Handshake)} hellos={p.HellosSent} " +
                            $"state='{(p.HasState ? (p.CachedSlug.Length == 0 ? "<native>" : p.CachedSlug) : "?")}'");
            foreach (var r in _remotes.Values)
                {
                var sb2 = new StringBuilder();
                try
                {
                    if (r.Model != null)
                        sb2.Append($"root={F(r.Model.transform.position)} active={r.Model.gameObject.activeInHierarchy} ");
                    var go = r.BuiltKind == 2 ? (r.Rig == null ? null : r.Rig.S.BodyGo) : r.Rigid;
                    if (go != null)
                    {
                        var rends = go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);
                        int on = 0, materialSlots = 0, healthySlots = 0; string bnd = "?";
                        for (int ri = 0; rends != null && ri < rends.Length; ri++)
                        {
                            var rr = rends[ri].TryCast<Renderer>();
                            if (rr == null) continue;
                            bool en = false; try { en = rr.enabled; } catch { }
                            if (en) on++;
                            var mats = rr.sharedMaterials;
                            for (int mi = 0; mats != null && mi < mats.Length; mi++)
                            {
                                materialSlots++;
                                var mat = mats[mi];
                                if (mat != null && mat.shader != null && mat.shader.isSupported &&
                                    (!mat.HasProperty("_BaseMap") || mat.GetTexture("_BaseMap") != null)) healthySlots++;
                            }
                            try
                            {
                                var wb = rr.bounds;
                                bnd = $"[{F(wb.center)} e={F(wb.extents)}]";
                            }
                            catch { }
                        }
                        sb2.Append($"body='{go.name}' active={go.activeInHierarchy} layer={go.layer} renderers={on}/{rends?.Length ?? 0} " +
                                   $"materialsHealthy={healthySlots}/{materialSlots} bounds={bnd} ");
                    }
                }
                catch { }
                log.LogInfo($"{RemoteTag} client={r.ClientId} slug='{r.Slug}' built='{r.BuiltSlug}' kind={r.BuiltKind} " +
                            $"model='{(r.Model == null ? "null" : PathOf(r.Model.transform))}' " +
                            $"hidden={r.HiddenRenderers.Count} builds={r.Builds} disposes={r.Disposes} poses={r.Poses} " +
                            $"cosmetics=[{string.Join(",", r.Hats.ToArray())}] hatVersion={r.HatVersion} " +
                            $"attached={(r.Attached == null ? 0 : r.Attached.Instances.Count)} " +
                            $"hatRenderers={(r.Attached == null ? 0 : r.Attached.Renderers)} " +
                            $"chest={(r.Chest ? "on" : "off")} hips={(r.Hips ? "on" : "off")} " +
                            $"emote='{RemoteEmotes.PlayingId(r.ClientId)}' {sb2}");
            }
            Census(log);
        }
        catch (Exception e) { log.LogWarning($"{Tag} dump failed: {e.Message}"); }
    }

    // Every Sahur object in the scene, by name, and a count of the bodies we built per client.
    private static void Census(IHostContext log)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        try
        {
            var all = SceneCache.Scan(Il2CppType.Of<Transform>());
            for (int i = 0; all != null && i < all.Length; i++)
            {
                var t = all[i].TryCast<Transform>();
                if (t == null || t.gameObject == null) continue;
                var sc = t.gameObject.scene;
                if (string.IsNullOrEmpty(sc.name)) continue;
                string n = t.name ?? "";
                if (!ModelRoster.IsOurObjectName(n)) continue;
                if (t.parent != null && ModelRoster.IsOurObjectName(t.parent.name ?? "")) continue;
                counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
                total++;
            }
        }
        catch { }
        var sb = new StringBuilder();
        foreach (var kv in counts) sb.Append($"{kv.Key}={kv.Value} ");
        var perClient = new StringBuilder();
        foreach (var r in _remotes.Values)
            perClient.Append($"c{r.ClientId}:kind={r.BuiltKind},hidden={r.HiddenRenderers.Count}," +
                             $"hats={(r.Attached == null ? 0 : r.Attached.Instances.Count)} " +
                             $"({(r.Attached == null ? 0 : r.Attached.Renderers)}r),emote='{RemoteEmotes.PlayingId(r.ClientId)}' ");
        log.LogInfo($"{RemoteTag} census ours={total} [{sb}] remotes=[{perClient}] " +
                    $"remoteCosmetics={CosmeticsRuntime.Current?.RemoteSetCount ?? 0}/" +
                    $"{CosmeticsRuntime.Current?.RemoteRendererCount ?? 0}r " +
                    $"enhExternal={CustomEnhancements.ExternalRecordCount} emoteActors={RemoteEmotes.ActiveCount} " +
                    $"markers={CosmeticMarkers.RendererCount}");
    }

    private static string RequestPath(IHostContext log)
    {
        try { return log.ScreensDir == null ? null : Path.Combine(log.ScreensDir, RequestFileName); }
        catch { return null; }
    }

    private static void PollRequest(IHostContext log)
    {
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextRequestPoll) return;
        _nextRequestPoll = now + RequestPollSeconds;
        string path = RequestPath(log);
        if (path == null) return;
        bool exists;
        try { exists = File.Exists(path); } catch { return; }
        if (!exists) return;
        string text = "";
        try { text = File.ReadAllText(path); } catch (Exception e) { log.LogWarning($"{RemoteTag} request read failed: {e.Message}"); }
        try { File.Delete(path); } catch { }

        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            string key = eq > 0 ? line.Substring(0, eq).Trim().ToLowerInvariant() : line.ToLowerInvariant();
            string val = eq > 0 ? line.Substring(eq + 1).Trim() : "";
            switch (key)
            {
                case "": case "1": case "dump":
                    Dump(log);
                    break;
                case "shot":
                    {
                        // shot=<clientId> [dist=<metres>] [side=<0..1>]
                        var parts = val.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0 || !ulong.TryParse(parts[0], out var cid)) { log.LogWarning($"{RemoteTag} shot: '{val}' is not a client id"); break; }
                        float dist = 2.6f, side = 0f;
                        for (int pi = 1; pi < parts.Length; pi++)
                        {
                            int e2 = parts[pi].IndexOf('=');
                            if (e2 <= 0) continue;
                            var k2 = parts[pi].Substring(0, e2).ToLowerInvariant();
                            if (!float.TryParse(parts[pi].Substring(e2 + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v2)) continue;
                            if (k2 == "dist") dist = v2; else if (k2 == "side") side = v2;
                        }
                        ShotRemote(log, cid, null, dist, side);
                    }
                    break;
                // Unattended lobby join for the two-instance test (the click path needs the desktop's
                // UI; this calls the game's own SteamLobbyManager.TryJoinLobby, no UI involved).
                // The id is the Steam lobby id A logs as `[lobby] raw=<id>`.
                case "join":
                    if (ulong.TryParse(val, out var lobbyId)) JoinByLobbyId(log, lobbyId);
                    else log.LogWarning($"{RemoteTag} join: '{val}' is not a lobby id");
                    break;
                case "startgame":
                case "oneplayer":
                    {
                        // The mod's own one-player start switch (lobby onePlayer cfg), reachable without
                        // the widget so an unattended run can put two players into the map.
                        bool on = key != "startgame" || val != "0";
                        // Prefer the widget path: the pause menu's row sync pushes its own state back
                        // into the owner, so setting only the owner is reverted on the next sync.
                        if (!ModPauseSettings.SetWidget(log, "oneplayer", on))
                            LobbyStartSettings.Set(log, on);
                        log.LogInfo($"{RemoteTag} one-player start -> {(on ? "on" : "off")}");
                    }
                    break;
                case "reelreq":
                    {
                        // Write a reel.request body into this instance's screens dir and let the booth's
                        // own poller consume it (same file path an unattended test uses).
                        try
                        {
                            Directory.CreateDirectory(log.ScreensDir);
                            File.WriteAllText(Path.Combine(log.ScreensDir, "reel.request"), val.Replace("|", "\n"));
                            File.WriteAllText(Path.Combine(log.ScreensDir, "interact.request"), val.Replace("|", "\n"));
                            log.LogInfo($"{RemoteTag} reelreq wrote '{val}' into the screens dir");
                        }
                        catch (Exception e) { log.LogWarning($"{RemoteTag} reelreq failed: {e.Message}"); }
                    }
                    break;
                default:
                    log.LogWarning($"{RemoteTag} request key '{key}' is not dump/shot");
                    break;
            }
        }
    }

    private static void JoinByLobbyId(IHostContext log, ulong lobbyId)
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<Steam.SteamLobbyManager>());
            Steam.SteamLobbyManager mgr = null;
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var m = arr[i].TryCast<Steam.SteamLobbyManager>();
                if (m != null) { mgr = m; break; }
            }
            if (mgr == null) { log.LogWarning($"{Tag} join: no SteamLobbyManager in the scene"); return; }
            var task = mgr.TryJoinLobby(lobbyId);
            log.LogInfo($"{Tag} join: TryJoinLobby({lobbyId}) started status={task.Status} " +
                        $"isInLobby={Steam.SteamLobbyManager.IsInLobby}");
        }
        catch (Exception e) { log.LogError($"{Tag} join {lobbyId} failed: {e}"); }
    }

    // ================================================================== evidence capture

    // The remote shot's own framing, resolved but not written: the focus point above the body's root,
    // the away/side offset and the look rotation. Every caller of the remote-shot placement comes
    // through here - `shot=`/`try capture-remote` render it, the console's
    // `alterum.test.camera-remote` reports it - so a pose report cannot describe a different camera
    // placement from the one the evidence path would use. Pure reads: no transform is written, no
    // render happens and no resource is acquired, so there is nothing to restore.
    internal struct RemoteShotView
    {
        internal Vector3 Focus;
        internal Vector3 Position;
        internal Quaternion Rotation;
        internal Camera Camera;
        internal Transform Body;
        internal float Distance;
        internal float Side;
    }

    internal static bool TryResolveRemoteShotView(IHostContext log, ulong clientId, float dist, float side,
                                                  out RemoteShotView view, out string why)
    {
        view = default;
        why = null;
        if (!_remotes.TryGetValue(clientId, out var r) || r.Model == null)
        {
            why = $"no built remote for client={clientId}";
            return false;
        }

        Camera cam = null;
        try { cam = ScreenShot.FindGameCamera(log); }
        catch (Exception e) { why = $"no game camera: {e.Message}"; return false; }
        if (cam == null) { why = "no game camera"; return false; }

        try
        {
            var root = r.Model.transform;
            Vector3 focus = root.position + Vector3.up * 1.05f;
            var away = cam.transform.position - root.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = root.forward;
            away.Normalize();
            var sideways = Vector3.Cross(Vector3.up, away).normalized * (side * dist);
            Vector3 pos = root.position + away * dist + sideways + Vector3.up * 1.15f;
            var dir = focus - pos;
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.forward;
            view.Focus = focus;
            view.Position = pos;
            view.Rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            view.Camera = cam;
            view.Body = root;
            view.Distance = dist;
            view.Side = side;
            return true;
        }
        catch (Exception e)
        {
            why = $"target read failed: {e.Message}";
            return false;
        }
    }

    // The deterministic remote evidence path: pose the game's own camera at one remote body for
    // exactly one rendered frame and write that frame as a PNG. Same camera object, same
    // fov/near/far/culling mask/post-processing; only the transform is borrowed, and it is put back
    // as soon as the frame has rendered, while the RenderTexture and the temporary texture are
    // released in a finally so no failure leaves render state behind.
    //
    // Two rendering callers, one implementation: the `shot=` request line (ShotRemote below) and the
    // console's `alterum.test.capture-remote <clientId> <tag>` verb, which needs the artifact's path
    // and hash back. The placement itself comes from TryResolveRemoteShotView, so the console's
    // `alterum.test.camera-remote` reports the same numbers this method renders. Failure is returned,
    // never thrown: `why` names the precondition that failed, the warning line is the one the `shot=`
    // transport has always logged, and this run's artifact is published only by the final rename. A
    // previous file that cannot be cleared is named in `why` rather than assumed absent.
    internal static bool TryCaptureRemote(IHostContext log, ulong clientId, string file, float dist,
                                         float side, out string path, out string sha256, out string why)
    {
        path = null;
        sha256 = null;
        why = null;

        if (log == null) { why = "no host context"; return false; }
        if (!_remotes.TryGetValue(clientId, out var r) || r.Model == null)
        {
            why = $"no built remote for client={clientId}";
            log.LogWarning($"{RemoteTag} shot: {why}");
            return false;
        }
        // The name becomes a file name: one predicate, shared with the console's tag check, so
        // neither transport can be the looser one and write a path this module did not intend.
        if (!IsSafeFileName(file, MaxCaptureName))
        {
            why = $"'{file}' is not a safe artifact name (A-Z a-z 0-9 '-' '_', at most {MaxCaptureName} chars)";
            log.LogWarning($"{RemoteTag} shot: {why}");
            return false;
        }
        if (!TryResolveRemoteShotView(log, clientId, dist, side, out var view, out why))
        {
            log.LogWarning($"{RemoteTag} shot: {why}");
            return false;
        }
        Camera cam = view.Camera;
        Vector3 focus = view.Focus, pos = view.Position;
        Quaternion rot = view.Rotation;

        try
        {
            Directory.CreateDirectory(log.ScreensDir);
            path = Path.Combine(log.ScreensDir, file + ".png");
        }
        catch (Exception e)
        {
            why = $"path failed: {e.Message}";
            log.LogWarning($"{RemoteTag} shot: {why}");
            return false;
        }

        // The name is deterministic on purpose (same client id + tag -> same file), so a previous
        // run's artifact must not survive a capture that fails: clear it first. A failure here is
        // reported by name instead of pretending the path is empty when the old file could not go.
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e)
        {
            why = $"cannot clear the previous artifact '{path}': {e.Message}; it may still be there, " +
                  "so this run produced no new evidence";
            log.LogWarning($"{RemoteTag} shot: {why}");
            return false;
        }

        // Locked-desktop note: ScreenCapture only returns the last presented frame when the game's
        // window is not being composited, which is why the evidence path renders the game's own
        // camera object to a RenderTexture instead (same camera, mask, fov, near/far) and reads that.
        string hook = "game-camera-to-rt";
        try
        {
            var trsPos = cam.transform.position;
            var trsRot = cam.transform.rotation;
            var prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            var rt = RenderTexture.GetTemporary((int)Mathf.Max(320, cam.pixelWidth), (int)Mathf.Max(180, cam.pixelHeight), 24, RenderTextureFormat.ARGB32);
            if (rt == null) throw new InvalidOperationException("RenderTexture.GetTemporary returned null");
            Texture2D tex = null;
            string tmp = path + ".tmp";
            bool published = false;
            try
            {
                // The pose write and the target swap are one unit: whatever throws inside it
                // (including Camera.Render()) puts both back before this method leaves.
                try
                {
                    cam.transform.position = pos;
                    cam.transform.rotation = rot;
                    cam.targetTexture = rt;
                    cam.Render();
                }
                finally
                {
                    try { cam.targetTexture = prevTarget; } catch { }
                    try { cam.transform.position = trsPos; } catch { }
                    try { cam.transform.rotation = trsRot; } catch { }
                }

                RenderTexture.active = rt;
                tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply(false, false);
                byte[] png = tex.EncodeToPNG();
                // Hashed before the write: a returned hash always belongs to a file that was written,
                // and a hashing failure writes nothing at all.
                sha256 = Sha256Hex(png);
                // A temp sibling plus one rename: a failed write can leave a partial .tmp, never a
                // partial artifact at the deterministic path a test asserts on.
                File.WriteAllBytes(tmp, png);
                File.Move(tmp, path, true);
                published = true;
            }
            finally
            {
                try { if (!published && File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (!published && File.Exists(path)) File.Delete(path); } catch { }
                try { if (tex != null) UnityEngine.Object.Destroy(tex); } catch { }
                try { RenderTexture.active = prevActive; } catch { }
                try { RenderTexture.ReleaseTemporary(rt); } catch { }
            }
        }
        catch (Exception e)
        {
            why = $"render failed: {e.Message}";
            log.LogWarning($"{RemoteTag} shot: render failed: {e}");
            return false;
        }

        try
        {
            log.LogInfo($"{RemoteTag} shot client={clientId} slug='{r.Slug}' cam='{PathOf(cam.transform)}' " +
                        $"hook={hook} from={F(pos)} look={F(focus)} fov={cam.fieldOfView:F1} mask=0x{cam.cullingMask:X8} " +
                        $"file='{path}' sha256={sha256}");
        }
        catch { }
        return true;
    }

    // The `shot=` request line's caller: the same path and the same lines, the artifact facts are
    // just not needed here (that transport answers in the log).
    private static void ShotRemote(IHostContext log, ulong clientId, string name, float dist = 2.6f, float side = 0f)
    {
        string file = string.IsNullOrEmpty(name) ? $"remote_client{clientId}" : name;
        TryCaptureRemote(log, clientId, file, dist, side, out _, out _, out _);
    }

    // One predicate for an artifact name, used by the console (the `<tag>` it accepts) and by the
    // path builder above (defence in depth). Letters, digits, '-' and '_' only: no separator, no dot
    // segment, no space and no non-ASCII, so no name can leave ScreensDir, pick another extension or
    // depend on the filesystem's case/normalisation rules. The two caps differ because a tag and a
    // file stem are different things: the stem is the tag plus a fixed prefix and the client id, so a
    // tag at its own cap still fits comfortably.
    internal const int MaxCaptureTag = 48;
    private const int MaxCaptureName = 96;

    internal static bool IsSafeCaptureTag(string s) => IsSafeFileName(s, MaxCaptureTag);

    private static bool IsSafeFileName(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length > max) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                      c == '-' || c == '_';
            if (!ok) return false;
        }
        return true;
    }

    // Lower-case hex SHA-256 of the artifact's own bytes. A test compares this instead of reading the
    // file back, so a re-encoded, truncated or stale PNG cannot pass as this call's evidence.
    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    // ================================================================== formatting

    private static string F(Vector3 v) => string.Format("({0:F2},{1:F2},{2:F2})", v.x, v.y, v.z);

    private static string PathOf(Transform t)
    {
        try
        {
            if (t == null) return "null";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && sb.Length < 220; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
        catch { return "?"; }
    }
}
