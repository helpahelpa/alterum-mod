using System;
using System.Collections.Generic;
using System.IO;
using AlterumModelSwap.Contracts;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace AlterumModelSwap.Hot;

// "Start with one player" for the lobby, and the only place that value is applied.
//
// The game insists on the map's own minimum (4 on both shipped maps) before the readiness pad will
// start the countdown. This switch changes EXACTLY ONE thing: that minimum
// (`PlayersReadinessController.RequiredPlayerNumberToStartGame`, a server-owned NetworkVariable),
// from the map's number to 1. It does not touch readiness, does not write `canStart` and starts
// nothing: the pad's own `PlayersReadinessTrigger` stays the only thing that may mark a player
// ready, and `GameSceneStarter` stays the only thing that starts a map. So "one player" means "one
// player standing on the pad", which is the point of the option.
//
// What used to be here: Apply() also fired `MakePlayerUnreadyRpc(0)` then `MakePlayerReadyRpc(0)`
// to make the controller re-run its own update. That was the bypass - it marked client id 0 ready
// without consulting the trigger, so the countdown began with the player anywhere in the lobby, and
// it used a literal id instead of the owner id of whoever was on the pad. Measured on the pinned
// instance (2026-11-16):
//   [lobby:one] onePlayer=on wrote=True applies=1 before[required=4 ready=0 min=4 canStart=False]
//                                        after[required=1 ready=1 min=4 canStart=True]
// `before` says the trigger had marked nobody ready (nobody inside); `after` says "startable" - that
// is the synthetic pulse, not the pad. The two RPCs are gone; the same setting now reports
// ready=0 / canStart=False until somebody actually steps on the pad.
//
// The controller recomputes `required` from the map's own minimum on its own events, which is what
// reverted the value. A hot postfix (hot assembly only, no permanent change) therefore holds the
// one-player number after that recompute - installed only while the switch is on, only on the host,
// and acting only on the active lobby's controller. Eligibility is still always the controller's
// own `UpdateCanStartExpedition()`.
//
// With the switch OFF nothing is installed: the native requirement comes back and the native
// readiness path is the only one that runs. The single exception is corrective and never grants
// anything - a ready entry that no player inside the pad owns (the old build's synthetic pulse,
// measured left behind as `ready=1` with the switch already off) is dropped when the mod adopts a
// lobby, because that entry is not native readiness, it is the bypass's leftover.
//
// Default is OFF. The value is persisted to the mod's own client config (same file layout as
// EnhancementSettings: `com.user0.alterum.lobby[.<instance>].cfg`), which is per-instance, so a
// second instance's lobby is unaffected.
//
// SERVER ONLY. `RequiredPlayerNumberToStartGame` is a server-write NetworkVariable: on a joining
// client the assignment logs
//   |Client-N|Readiness Controller|_requiredPlayerNumberToStartGame| Write permissions (Server)
//   for this client instance is not allowed!
// through UnityEngine.Debug.LogError (measured on C:\AlterumB, 2026-11-15 07:53:07, from a client
// `ui.request req=1`). So Apply() early-outs when `!NetworkManager.IsServer` and says so once; the
// switch value itself is still persisted locally, because the same cfg is read by the widget.
internal static class LobbyStartSettings
{
    internal const string FileName = "com.user0.alterum.lobby.cfg";

    // The number the switch asks the lobby to accept: one player, still on the pad.
    internal const byte OnePlayerRequirement = 1;

    // One Harmony id for every generation. A hot reload throws the old statics away but not its
    // patch, and UnpatchSelf is by id - so a new generation can always remove a previous one's
    // postfix, and can never end up with two live at once.
    internal const string PatchId = "com.user0.alterum.lobbyone";

    private static ConfigFile _file;
    private static ConfigEntry<bool> _onePlayerEntry;
    private static bool _onePlayer;
    private static bool _appliedOnce;      // we have written the value at least once this lobby
    private static int _applies;           // bounded: the game recomputes `required` on its own events
    private static IntPtr _appliedToPtr;   // stable identity: Scan() returns fresh wrappers
    private static string _path = "(not loaded)";
    private static string _last = "not applied";
    private static bool _clientLogged;     // the "this instance is a client" line, once

    // The requirement patch and the adoption reconcile.
    private static IHostContext _log;
    private static Harmony _harmony;
    private static bool _patchInstalled;
    private static bool _unpatchSettled;   // the "off" unpatch already ran for this generation
    private static bool _inHold;           // re-entrancy: a postfix calls back into the controller
    private static int _holdHits;          // recomputes intercepted
    private static int _holdWrites;        // of those, the ones that had restored 4
    private static IntPtr _reconciledPtr;  // controller whose readiness this generation reconciled
    private static string _lastReconcile = "not yet";
    private static int _reconciles;

    internal static bool OnePlayer => _onePlayer;
    internal static string ConfigPathUsed => _path;
    internal static string LastApply => _last;

    internal static string Summary() =>
        $"onePlayer={(_onePlayer ? "on" : "off")} applied={_appliedOnce} cfg='{_path}' " +
        $"patch={(_patchInstalled ? "installed" : "off")} holds={_holdWrites}/{_holdHits} " +
        $"reconcile='{_lastReconcile}' last='{_last}'";

    private static void EnsureFile(IHostContext log)
    {
        _log = log;
        if (_file != null) return;
        try
        {
            string name = string.IsNullOrEmpty(log.InstanceName) ? FileName : FileName.Replace(".cfg", "." + log.InstanceName + ".cfg");
            _path = Path.Combine(ConfigDir(log), name);
            _file = new ConfigFile(_path, true);
            _onePlayerEntry = _file.Bind("Lobby", "StartWithOnePlayer", false,
                "Let a lobby start with one player. Lowers PlayersReadinessController.RequiredPlayerNumberToStartGame " +
                "to 1 and nothing else: the readiness pad still decides, so the one player has to be standing on it. " +
                "Off = the game's own requirement.");
            _onePlayer = _onePlayerEntry.Value;
            log?.LogInfo($"[lobby:cfg] onePlayer={(_onePlayer ? "on" : "off")} from '{_path}'" +
                         (File.Exists(_path) ? "" : " (created with the default: off)"));
        }
        catch (Exception e)
        {
            log?.LogWarning($"[lobby:cfg] config unavailable: {e.Message}");
            _file = null;
            _onePlayer = false;
        }
    }

    private static string ConfigDir(IHostContext log)
    {
        try
        {
            var p = log.ConfigPath;
            if (!string.IsNullOrEmpty(p))
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch { }
        try
        {
            string baseDir = log.ScreensDir;
            var d = string.IsNullOrEmpty(baseDir) ? null : new DirectoryInfo(baseDir);
            while (d != null && !string.Equals(d.Name, "BepInEx", StringComparison.OrdinalIgnoreCase)) d = d.Parent;
            if (d != null) return Path.Combine(d.FullName, "config");
        }
        catch { }
        return Environment.CurrentDirectory;
    }

    // Public entry point for the widget and for automation. Persists, then applies live.
    internal static bool Set(IHostContext log, bool on)
    {
        _log = log;
        EnsureFile(log);
        _onePlayer = on;
        _applies = 0;              // a deliberate change gets a fresh re-assert budget
        try { _appliedToPtr = Controller()?.Pointer ?? IntPtr.Zero; } catch { _appliedToPtr = IntPtr.Zero; }
        _reconciledPtr = IntPtr.Zero;   // a deliberate change adopts the lobby's readiness again
        try
        {
            if (_onePlayerEntry != null && _onePlayerEntry.Value != on) { _onePlayerEntry.Value = on; _file.Save(); }
            else if (_onePlayerEntry == null) { _file?.Save(); }
        }
        catch (Exception e) { log?.LogWarning($"[lobby:cfg] save failed: {e.Message}"); }

        Apply(log);
        log?.LogInfo($"[lobby:one] set {(on ? "on" : "off")} ({Summary()})");
        return true;
    }

    // Polled by the section's sync: nothing is written unless the value on the controller differs
    // from the switch, so this is not a per-frame network write.
    //
    // OFF means "the game's own requirement", and that includes not undoing a value something else
    // wrote: the old version re-applied MinRequiredPlayers whenever the switch was off, which is
    // exactly what ate the `ui.request req=1` poke 100 ms after it set canStart true. Now off only
    // restores once, after we had applied one-player ourselves.
    internal static void Sync(IHostContext log)
    {
        // "Off" is a state of this mod, not of the lobby: the one-player patch is removed even when
        // no lobby (and therefore no controller) is resolvable, so an inert callback never outlives
        // the option that installed it.
        if (!_onePlayer) RemovePatch(log);
        // The readiness controller only exists in the lobby. Without this guard a map's periodic
        // settings sync scanned the object table for it forever (empty-retry backoff still paid a
        // ~42 ms scan every few seconds) and never found it.
        if (!InLobby()) return;
        EnsureFile(log);
        // Off: nothing of ours stays installed, so the controller's own recompute is untouched.
        if (!_onePlayer) RemovePatch(log);
        // Polling a value we may not write would produce a write attempt every scan on a client.
        if (!NetRole.IsServer) { NoteClient(log); return; }
        var ctl = Controller();
        if (ctl == null) { _last = "no active PlayersReadinessController"; _appliedToPtr = IntPtr.Zero; _applies = 0; return; }
        IntPtr ptr = IntPtr.Zero; try { ptr = ctl.Pointer; } catch { }
        if (ptr != _appliedToPtr) { _appliedToPtr = ptr; _applies = 0; _appliedOnce = false; }

        byte want = (byte)(_onePlayer ? OnePlayerRequirement : Math.Max(1, SafeInt(() => ctl.MinRequiredPlayers)));
        byte have = 0;
        try { have = ctl.RequiredPlayerNumberToStartGame?.Value ?? 0; } catch { }

        // Adoption, once per controller instance, before the hands-off guards below: readiness no
        // player inside owns is dropped (the old build's synthetic pulse, or a previous generation's
        // - measured once as `ready=1` with the switch already off). Removal only when the switch is
        // off; `required` is not touched here.
        if (ptr != _reconciledPtr && Reconcile(log, ctl, Reason()))
            _reconciledPtr = ptr;

        if (!_onePlayer && !_appliedOnce) return;      // hands off unless we changed something
        if (have == want && _appliedOnce) return;
        if (_applies >= MaxApplies)
        {
            if (_applies == MaxApplies)
            {
                _applies++;
                log?.LogWarning($"[lobby:one] gave up re-asserting after {MaxApplies} writes " +
                                $"(the lobby keeps restoring required={have}); last='{_last}'");
            }
            return;
        }
        Apply(log);
    }

    // The game recomputes `required` on its own readiness events; a bounded number of re-asserts is
    // enough to win a race, and a runaway would be worse than the requirement. This is now only the
    // fallback: with the switch on the patch below holds the number on every recompute.
    private const int MaxApplies = 12;

    private static void Apply(IHostContext log)
    {
        // The single write path, guarded. Set() still persists the switch on a client (a local
        // config write) and then lands here, which is where the refusal is decided.
        _log = log;
        // Turning the option off unpatches immediately, whatever the lobby state: a controller that
        // is not resolvable right now (off the lobby) is exactly the case that used to leave the
        // hold patch installed, because the early return below came before RemovePatch.
        if (!_onePlayer) RemovePatch(log);
        if (!NetRole.IsServer) { NoteClient(log); return; }
        var ctl = Controller();
        if (ctl == null) { _last = "no active PlayersReadinessController"; return; }

        string before = Describe(ctl);
        byte want = (byte)(_onePlayer ? OnePlayerRequirement : Math.Max(1, SafeInt(() => ctl.MinRequiredPlayers)));

        // 1. The threshold. This is the whole switch: one player instead of four, pad unchanged.
        bool wrote = WriteRequired(ctl, want, log);

        // 2. Host only: keep that threshold from being recomputed away, and - once per lobby the mod
        //    is newly in charge of - make the ready list agree with who is actually on the pad.
        if (_onePlayer) EnsurePatch(log); else RemovePatch(log);
        bool reconciled = false;
        if (Ptr(ctl) != _reconciledPtr)
        {
            reconciled = Reconcile(log, ctl, Reason());
            if (reconciled) _reconciledPtr = Ptr(ctl);
        }

        // 3. Eligibility, through the controller's own recompute. Nothing here starts a scene and
        //    nothing here writes canStart: with nobody on the pad the controller itself says no.
        try { ctl.UpdateCanStartExpedition(); }
        catch (Exception e) { log?.LogWarning($"[lobby:one] UpdateCanStartExpedition: {e.Message}"); }

        // 4. That recompute can itself re-derive `required` (the patch covers it, read it back
        //    anyway): the value the countdown will see has to be the switch's.
        if (WriteRequired(ctl, want, log)) wrote = true;

        _appliedOnce = true;
        _applies++;
        _last = $"required {want}" + (wrote ? ", written" : "") + (reconciled ? ", reconciled" : "");
        log?.LogInfo($"[lobby:one] onePlayer={(_onePlayer ? "on" : "off")} wrote={wrote} applies={_applies} " +
                     $"patch={(_patchInstalled ? "installed" : "off")} before[{before}] after[{Describe(ctl)}]");
        Evidence(log, ctl, _onePlayer ? "enabled" : "off");
    }

    // ================================================================ the requirement patch

    private static void EnsurePatch(IHostContext log)
    {
        _log = log;
        if (_patchInstalled) return;
        if (!NetRole.IsServer) return;          // a client cannot own the value the patch would write
        try
        {
            var required = AccessTools.Method(typeof(GameLobby.PlayersReadinessController),
                                               "UpdateRequiredPlayerNumberToStartGame", Type.EmptyTypes);
            var canStart = AccessTools.Method(typeof(GameLobby.PlayersReadinessController),
                                               "UpdateCanStartExpedition", Type.EmptyTypes);
            if (required == null && canStart == null)
            {
                log?.LogWarning("[lobby:one] no PlayersReadinessController recompute method found - " +
                                "the one-player minimum is only re-asserted on the switch's own events");
                return;
            }
            var h = new Harmony(PatchId);
            // A previous generation may still own a patch under this id (hot reload) - remove it
            // first, so exactly one postfix per method is ever live.
            try { h.UnpatchSelf(); } catch { }
            if (required != null)
                h.Patch(required, postfix: new HarmonyMethod(typeof(LobbyStartSettings), nameof(RequirementPostfix)));
            if (canStart != null)
                h.Patch(canStart, postfix: new HarmonyMethod(typeof(LobbyStartSettings), nameof(EligibilityPostfix)));
            _harmony = h;
            _patchInstalled = true;
            _unpatchSettled = false;
            log?.LogInfo($"[lobby:one] patch installed (host only, active lobby only): postfix on " +
                         $"PlayersReadinessController.UpdateRequiredPlayerNumberToStartGame={required != null} " +
                         $"UpdateCanStartExpedition={canStart != null}; removed again when the switch is off");
        }
        catch (Exception e)
        {
            try { new Harmony(PatchId).UnpatchSelf(); } catch { }
            _harmony = null;
            _patchInstalled = false;
            log?.LogWarning($"[lobby:one] patch failed ({e.GetType().Name}: {e.Message}) - " +
                            "the one-player minimum is only re-asserted on the switch's own events");
        }
    }

    private static void RemovePatch(IHostContext log)
    {
        if (!_patchInstalled && _unpatchSettled) return;
        bool was = _patchInstalled;
        _patchInstalled = false;
        _unpatchSettled = true;
        try { (_harmony != null ? _harmony : new Harmony(PatchId)).UnpatchSelf(); }
        catch (Exception e) { log?.LogWarning($"[lobby:one] unpatch: {e.Message}"); }
        _harmony = null;
        if (was)
            log?.LogInfo($"[lobby:one] patch removed: the controller's own requirement recompute and its readiness " +
                         $"path are untouched again (holds={_holdWrites}/{_holdHits})");
    }

    // Postfix on the controller's own requirement recompute: the native method has just written the
    // map's minimum back, and this puts the switch's number in its place before anything reads it.
    private static void RequirementPostfix(GameLobby.PlayersReadinessController __instance)
        => HoldRequirement(__instance, "required-recompute");

    // Postfix on the eligibility recompute - the choke point every readiness and map change goes
    // through. If `required` was restored by a path the postfix above did not see, it is corrected
    // here and the native recompute is re-run, so `canStart` is never derived from the four-player
    // minimum while the switch is on.
    private static void EligibilityPostfix(GameLobby.PlayersReadinessController __instance)
        => HoldRequirement(__instance, "eligibility-recompute");

    private static void HoldRequirement(GameLobby.PlayersReadinessController ctl, string source)
    {
        if (!_onePlayer || _inHold || ctl == null) return;
        if (!NetRole.IsServer) return;
        if (!InLobby()) return;
        if (!IsActiveLobbyController(ctl)) return;

        _inHold = true;   // our own recompute call re-enters the postfix; it must not write
        try
        {
            _holdHits++;
            byte have = RequiredOf(ctl);
            if (have == OnePlayerRequirement)
            {
                // The patch is alive and the native recompute agreed with the switch: nothing to do.
                if (_holdHits <= 2)
                    _log?.LogInfo($"[lobby:one] {source} seen: required={have} ready={SafeReady(ctl)} " +
                                  $"canStart={SafeCanStart(ctl)} (nothing to hold, hits={_holdHits})");
                return;
            }
            if (!WriteRequired(ctl, OnePlayerRequirement, _log)) return;
            _holdWrites++;
            // ...and let the controller derive eligibility from the number it now holds.
            try { ctl.UpdateCanStartExpedition(); }
            catch (Exception e) { _log?.LogWarning($"[lobby:one] {source} canStart: {e.Message}"); }
            _log?.LogInfo($"[lobby:one] {source} held: required {have}->{OnePlayerRequirement} " +
                          $"ready={SafeReady(ctl)} min={SafeMin(ctl)} canStart={SafeCanStart(ctl)} " +
                          $"holds={_holdWrites}/{_holdHits}");
        }
        catch (Exception e) { _log?.LogWarning($"[lobby:one] {source} failed: {e.Message}"); }
        finally { _inHold = false; }
    }

    // "the active lobby's controller": a live GameObject in a real scene, and the same instance the
    // lobby lookup returns (SceneCache-served, so at most one object-table walk per generation).
    private static bool IsActiveLobbyController(GameLobby.PlayersReadinessController c)
    {
        try
        {
            var go = c.gameObject;
            if (go == null || !go.activeInHierarchy) return false;
            if (string.IsNullOrEmpty(go.scene.name)) return false;
            var active = Controller();
            if (active == null) return true;   // scan came back empty: the instance we were called on is live
            return Ptr(active) == Ptr(c);
        }
        catch { return false; }
    }

    // ================================================================ adoption reconcile

    private static string Reason() => _reconciles == 0 ? (_onePlayer ? "enabled" : "adoption(off)") : "adopted";

    // Hot adoption: a lobby the mod has already been in can carry readiness no player earned - the
    // old build's synthetic `MakePlayerReadyRpc(0)` (which also left `ready=1` behind when the
    // switch was turned off, see the off line in the header), or a previous generation's. Physical
    // truth is the pad's own `PlayersIn`; the ready list is made to match it, so an entry nobody
    // inside owns is dropped and a player standing on the pad is added.
    //
    // Every id used is a player's real `OwnerClientId`. The literal 0 the old code passed was only
    // ever "right" because the host happens to own client id 0 - on any other peer it marked a
    // player who was not there.
    //
    // Never adds a player with the switch off (native readiness is then the only path that grants
    // it) and never when no active pad can be found: without it there is no physical truth to match,
    // so the list is left exactly as the game has it. Removal is allowed from any single active pad
    // (it can only lower readiness); adding a player needs the pad that is provably wired to this
    // controller and the switch on.
    private static bool Reconcile(IHostContext log, GameLobby.PlayersReadinessController ctl, string why)
    {
        if (!NetRole.IsServer) return false;
        var trigger = TriggerFor(ctl, false);
        if (trigger == null)
        {
            _lastReconcile = $"no active PlayersReadinessTrigger ({why})";
            log?.LogWarning($"[lobby:one] reconcile({why}): {_lastReconcile} - readiness left alone " +
                            "(without the pad there is nothing to reconcile against)");
            return false;
        }

        // Drop stale entries from any active pad (removal can only lower readiness, i.e. restore the
        // native truth); only ADD players when the switch is on AND the pad is provably this
        // controller's own.
        bool linked = false;
        try { linked = Ptr(ctl) != IntPtr.Zero && Ptr(trigger._readinessController) == Ptr(ctl); } catch { }
        bool allowAdds = _onePlayer && linked;
        var inside = OwnersInside(trigger);
        var ready = ReadySnapshot(ctl);
        var removed = new List<ulong>();
        var added = new List<ulong>();
        for (int i = 0; i < ready.Count; i++)
        {
            ulong id = ready[i];
            if (inside.Contains(id)) continue;
            try { ctl.MakePlayerUnreadyRpc(id); removed.Add(id); }
            catch (Exception e) { log?.LogWarning($"[lobby:one] unready({id}) failed: {e.Message}"); }
        }
        if (allowAdds)
        {
            for (int i = 0; i < inside.Count; i++)
            {
                ulong id = inside[i];
                if (ready.Contains(id)) continue;
                try { ctl.MakePlayerReadyRpc(id); added.Add(id); }
                catch (Exception e) { log?.LogWarning($"[lobby:one] ready({id}) failed: {e.Message}"); }
            }
        }

        _reconciles++;
        _lastReconcile = $"{why}: linked={linked} adds={allowAdds} inside=[{Ids(inside)}] removed=[{Ids(removed)}] added=[{Ids(added)}]";
        log?.LogInfo($"[lobby:one] reconcile({why}): linked={linked} adds={allowAdds} inside=[{Ids(inside)}] readyWas=[{Ids(ready)}] " +
                     $"removed=[{Ids(removed)}] added=[{Ids(added)}] required={RequiredOf(ctl)} " +
                     $"readyCount={SafeReady(ctl)} canStart={SafeCanStart(ctl)} " +
                     "(ids are OwnerClientId: stale synthetic readiness is dropped, players on the pad are kept/added)");
        return true;
    }

    // The trigger wired to this controller. `linkedOnly` returns null unless the pad's own
    // `_readinessController` is this controller. The lenient form falls back to the lobby's single
    // active pad: the reconcile uses it for removals (dropping an entry nobody inside owns can only
    // restore the native list) and checks the link itself before adding anything, and the evidence
    // line uses it in read-only form.
    private static GameLobby.PlayersReadinessTrigger TriggerFor(GameLobby.PlayersReadinessController ctl, bool linkedOnly)
    {
        try
        {
            IntPtr want = Ptr(ctl);
            var arr = SceneCache.Scan(Il2CppType.Of<GameLobby.PlayersReadinessTrigger>());
            GameLobby.PlayersReadinessTrigger sole = null;
            int candidates = 0;
            for (int i = 0; i < arr.Length; i++)
            {
                var t = arr[i].TryCast<GameLobby.PlayersReadinessTrigger>();
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (string.IsNullOrEmpty(go.scene.name)) continue;
                candidates++;
                if (sole == null) sole = t;
                if (want != IntPtr.Zero && Ptr(t._readinessController) == want) return t;
            }
            if (!linkedOnly && candidates == 1) return sole;
            return null;
        }
        catch { }
        return null;
    }

    // Who the pad says is inside, by owner id. `PlayersIn` is the trigger's read-only view of the
    // same list; the interop wrapper for IReadOnlyList<T> carries no Count, so the backing list is
    // what gets walked.
    private static List<ulong> OwnersInside(GameLobby.PlayersReadinessTrigger t)
    {
        var ids = new List<ulong>();
        try
        {
            var list = PlayersInOf(t);
            if (list == null) return ids;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                ulong id;
                try { id = p.OwnerClientId; } catch { continue; }
                if (!ids.Contains(id)) ids.Add(id);
            }
        }
        catch (Exception e) { _log?.LogWarning($"[lobby:one] PlayersIn: {e.Message}"); }
        return ids;
    }

    private static Il2CppSystem.Collections.Generic.List<Gameplay.PlayerSystem.Player> PlayersInOf(GameLobby.PlayersReadinessTrigger t)
    {
        try
        {
            var view = t.PlayersIn;
            var list = view == null ? null : view.TryCast<Il2CppSystem.Collections.Generic.List<Gameplay.PlayerSystem.Player>>();
            if (list != null) return list;
        }
        catch { }
        try { return t._playersIn; } catch { }
        return null;
    }

    private static List<ulong> ReadySnapshot(GameLobby.PlayersReadinessController ctl)
    {
        var ids = new List<ulong>();
        try
        {
            var list = ctl.ReadyPlayerIndexes;
            if (list == null) return ids;
            for (int i = 0; i < list.Count; i++)
            {
                try { ids.Add(list[i]); } catch { }
            }
        }
        catch (Exception e) { _log?.LogWarning($"[lobby:one] ReadyPlayerIndexes: {e.Message}"); }
        return ids;
    }

    // ================================================================ evidence

    // One line with everything the acceptance cases are read from: whether the local player is
    // inside the pad, the owner ids inside, the ready list and its count, the requirement and the
    // controller's own verdict. Written on every Apply (host), i.e. per enable / disable edge.
    private static void Evidence(IHostContext log, GameLobby.PlayersReadinessController ctl, string when)
    {
        ulong me = 0;
        bool haveMe = false;
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null) { me = nm.LocalClientId; haveMe = true; }
        }
        catch { }
        var trigger = TriggerFor(ctl, true);
        var inside = trigger != null ? OwnersInside(trigger) : new List<ulong>();
        var ready = ReadySnapshot(ctl);
        string pad = "<no linked readiness trigger>";
        try
        {
            if (trigger != null)
                pad = $"'{ClothInternals.PathOf(trigger.gameObject.transform)}' scene='{trigger.gameObject.scene.name}'";
        }
        catch { }
        log?.LogInfo($"[lobby:one] {when}: local={(haveMe ? me.ToString() : "?")} " +
                     $"localInside={(haveMe && inside.Contains(me))} insideOwners=[{Ids(inside)}] " +
                     $"ready=[{Ids(ready)}] readyCount={SafeReady(ctl)} required={RequiredOf(ctl)} " +
                     $"min={SafeMin(ctl)} canStart={SafeCanStart(ctl)} option={(_onePlayer ? "on" : "off")} " +
                     $"pad={pad}");
    }

    private static bool WriteRequired(GameLobby.PlayersReadinessController ctl, byte want, IHostContext log)
    {
        try
        {
            var rv = ctl.RequiredPlayerNumberToStartGame;
            if (rv == null) { _last = "RequiredPlayerNumberToStartGame is null"; return false; }
            if (rv.Value == want) return false;
            rv.Value = want;
            return true;
        }
        catch (Exception e)
        {
            _last = $"set failed: {e.Message}";
            log?.LogWarning($"[lobby:one] {_last}");
            return false;
        }
    }

    // A client reads the value the server owns; it never writes it. Reported once per process so a
    // client-side `ui.request req=1` is visibly refused instead of silently doing nothing, and so
    // the log does not grow a line per scan.
    private static void NoteClient(IHostContext log)
    {
        _last = "client: required is server-owned, not written";
        if (_clientLogged) return;
        _clientLogged = true;
        log?.LogInfo($"[lobby:one] this instance is a client ({NetRole.Word()}): " +
                     "RequiredPlayerNumberToStartGame is server-owned, so the one-player switch is " +
                     "persisted here and NOT written (the host's value is the one that counts)");
    }

    private static string Describe(GameLobby.PlayersReadinessController ctl)
    {
        try
        {
            return $"required={ctl.RequiredPlayerNumberToStartGame?.Value} ready={ctl.NumberOfReadyPlayers} " +
                   $"min={ctl.MinRequiredPlayers} canStart={ctl.CanStartExpedition?.Value}";
        }
        catch (Exception e) { return $"<{e.Message}>"; }
    }

    private static string Ids(List<ulong> v) => v == null || v.Count == 0 ? "" : string.Join(",", v);

    private static int SafeInt(Func<int> f) { try { return f(); } catch { return 1; } }

    private static byte RequiredOf(GameLobby.PlayersReadinessController ctl)
    {
        try { var rv = ctl.RequiredPlayerNumberToStartGame; return rv == null ? (byte)0 : rv.Value; }
        catch { return 0; }
    }

    private static int SafeReady(GameLobby.PlayersReadinessController ctl) { try { return ctl.NumberOfReadyPlayers; } catch { return -1; } }

    private static int SafeMin(GameLobby.PlayersReadinessController ctl) { try { return ctl.MinRequiredPlayers; } catch { return -1; } }

    private static bool SafeCanStart(GameLobby.PlayersReadinessController ctl)
    {
        try { var cv = ctl.CanStartExpedition; return cv != null && cv.Value; } catch { return false; }
    }

    private static bool InLobby()
    {
        try
        {
            string s = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "";
            return s.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    private static IntPtr Ptr(GameLobby.PlayersReadinessController c)
    {
        try { return c == null ? IntPtr.Zero : c.Pointer; } catch { return IntPtr.Zero; }
    }

    private static GameLobby.PlayersReadinessController Controller()
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<GameLobby.PlayersReadinessController>());
            for (int i = 0; i < arr.Length; i++)
            {
                var c = arr[i].TryCast<GameLobby.PlayersReadinessController>();
                if (c == null) continue;
                var go = c.gameObject;
                if (go == null || !go.activeInHierarchy) continue;
                if (string.IsNullOrEmpty(go.scene.name)) continue;
                return c;
            }
        }
        catch { }
        return null;
    }
}
