using System;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Steam;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// " (Modded)" on the lobby this mod creates.
//
// The name a lobby publishes is `SteamLobbyManager._lobbyName`, and the only place it is fed from is
// the `lobbyName` argument of `SteamLobbyManager.CreateLobby(int maxMembers, string lobbyName,
// LobbyType lobbyType)` (verified against the interop: the async state machine `_CreateLobby_d__22`
// captures exactly those three fields, and the private `OnLobbyCreated(Result, Lobby)` publishes
// `SetData("name", ...)` from that captured value). So the patch point is that argument, in a
// Harmony prefix: it is rewritten there BEFORE the state machine exists, so there is nothing that
// can be captured first and overwritten later.
//
// The prefix is not trusted with the published value on its own:
//   * the game's creation path may republish the name from another field (the task's warning), and
//   * a patch that silently stops applying would go unnoticed.
// `Tick` therefore reads the name back out of the lobby Steam actually published
// (`CurrentLobby.GetData("name")`, the same value the server browser rows are built from) and
// compares it with what the prefix handed over. If they differ it republishes the intended name
// once, and logs both values either way ("published='…' intended='…'"). It only ever touches a
// lobby this instance created in this generation (`_pending`, cleared as soon as the name matches,
// or after ReconcileSeconds), never one that was joined.
//
// Details that matter:
//   * one suffix, ever: a base name that already ends in " (Modded)" (any capitalisation) is
//     published unchanged, so stacked patches or a re-created lobby cannot produce two.
//   * empty/blank base name -> "Modded" alone.
//   * length: the base is trimmed so base+suffix fits the cap — the live creator field's own
//     `characterLimit` when it has one (that is the length the game itself accepts for a lobby
//     name), else DefaultMaxLength. The trim is logged.
//   * capacity and privacy are not touched: the prefix only rewrites the string; maxMembers and
//     lobbyType are forwarded unchanged (and logged with the name so the log proves it).
//   * hot reload: one Harmony id per generation, `Dispose` unpatches it, and the suffix add is
//     idempotent, so reloading cannot stack two suffixes even while two generations overlap.
//
// WIRING IS MANDATORY, and it is the host's (HotModule), not this file's: Install in Initialize,
// Tick in Update, Dispose in Dispose. Without the Dispose call the patch outlives the generation
// that installed it and a later lobby creation would jump into an unloaded assembly - so this class
// is only safe to merge together with those three calls. (A second generation cannot double the
// suffix anyway: the add is idempotent, and each generation patches under its own Harmony id.)

internal static class LobbyNaming
{
    internal const string Suffix = " (Modded)";
    private const string Tag = "[lobby:name]";
    private const string EmptyName = "Modded";           // no base name at all -> the marker alone
    private const int DefaultMaxLength = 64;             // published-name cap when the game has none
    private const float ReconcileSeconds = 30f;

    private static Harmony _harmony;
    private static bool _installed;
    private static IHostContext _log;
    private static int _maxLength = -1;                  // cached cap; -1 = not measured yet
    private static float _nextPoll;
    private const string RequestFile = "lobby.request";

    // The name this generation handed to CreateLobby, until the published value is confirmed.
    private static string _pending;
    private static float _pendingAt;
    private static int _prefixHits, _forcedRepublish, _alreadySuffixed;

    internal static bool Installed => _installed;

    // ================================================================ install / dispose

    internal static void Install(IHostContext log)
    {
        _log = log;
        if (_installed) { log?.LogInfo($"{Tag} already installed (reload guard) — nothing patched twice"); return; }

        var harmony = new Harmony("com.user0.alterum.lobbyname." + Guid.NewGuid().ToString("N"));
        var target = AccessTools.Method(typeof(SteamLobbyManager), "CreateLobby",
            new[] { typeof(int), typeof(string), typeof(Steam.LobbyType) });
        if (target == null)
        {
            log?.LogWarning($"{Tag} SteamLobbyManager.CreateLobby(int, string, LobbyType) not found — no lobby label");
            return;
        }
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(LobbyNaming), nameof(CreateLobbyPrefix)));
            _harmony = harmony;
            _installed = true;
            log?.LogInfo($"{Tag} installed: prefix on {target.DeclaringType?.FullName}.{target.Name}(int maxMembers, " +
                         $"string lobbyName, LobbyType lobbyType); published name = base + '{Suffix}'");
        }
        catch (Exception e)
        {
            try { harmony.UnpatchSelf(); } catch { }
            log?.LogWarning($"{Tag} patch failed ({e.GetType().Name}: {e.Message}) — lobby names stay as the game makes them");
        }
    }

    internal static void Dispose()
    {
        if (!_installed && _harmony == null) return;
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
        _installed = false;
        _pending = null;
        _maxLength = -1;
        try { _log?.LogInfo($"{Tag} uninstalled (generation retired)"); } catch { }
    }

    // ================================================================ the patch

    // Runs before the async state machine is built, so this is the value it captures. Kept total:
    // an exception here would propagate into the game's lobby creation, so nothing may escape.
    private static void CreateLobbyPrefix(ref string lobbyName)
    {
        _prefixHits++;
        try
        {
            string published = Compose(lobbyName, out bool trimmed, out bool already);
            if (already) _alreadySuffixed++;
            _pending = published;
            _pendingAt = Now();
            var sb = new StringBuilder();
            sb.Append($"{Tag} create lobby: field='{lobbyName ?? "null"}' -> published='{published}'");
            if (already) sb.Append(" (base already ended in the marker — not doubled)");
            if (trimmed) sb.Append($" (base trimmed to the {MaxLength()}-char cap)");
            sb.Append($" hit=#{_prefixHits}");
            _log?.LogInfo(sb.ToString());
            lobbyName = published;
        }
        catch (Exception e)
        {
            try { _log?.LogWarning($"{Tag} prefix failed ({e.GetType().Name}: {e.Message}) — name left as the game passed it"); } catch { }
        }
    }

    // base + exactly one suffix, base trimmed to the cap. `already` = the base carried the marker.
    internal static string Compose(string name, out bool trimmed, out bool already)
    {
        trimmed = false;
        already = false;
        string n = (name ?? "").Trim();
        if (n.Length == 0) return EmptyName;
        if (n.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) { already = true; return n; }
        int cap = MaxLength();
        int room = cap - Suffix.Length;
        if (room > 0 && n.Length > room)
        {
            n = n.Substring(0, room).TrimEnd();
            trimmed = true;
            if (n.Length == 0) return EmptyName;
        }
        return n + Suffix;
    }

    // The game's own limit for a lobby name when the creator field has one, else DefaultMaxLength.
    // Read once per generation; a missing creator view is not an error (the cap is only a guard).
    private static int MaxLength()
    {
        if (_maxLength > 0) return _maxLength;
        _maxLength = DefaultMaxLength;
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<Pause.UI.Controllers.LobbyCreatorView>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var v = arr[i].TryCast<Pause.UI.Controllers.LobbyCreatorView>();
                if (v == null) continue;
                var f = v._nameInputField;
                if (f == null) continue;
                int limit = f.characterLimit;                        // 0 = unlimited in TMP
                _log?.LogInfo($"{Tag} creator field '{f.name}' characterLimit={limit} -> cap=" +
                              $"{(limit > 0 ? limit : DefaultMaxLength)}");
                if (limit > 0) _maxLength = limit;
                break;
            }
        }
        catch (Exception e) { try { _log?.LogInfo($"{Tag} characterLimit read failed ({e.Message}); cap={DefaultMaxLength}"); } catch { } }
        return _maxLength;
    }

    // ================================================================ published-name check

    // Called every frame by the wired HotModule. Polls `lobby.request` and then does nothing unless
    // this generation created a lobby and has not yet confirmed the published name.
    internal static void Tick(IHostContext log)
    {
        _log = _log ?? log;
        try { PollRequest(log); } catch (Exception e) { log?.LogWarning($"{Tag} request poll failed: {e.Message}"); }
        if (_pending == null) return;
        _log = _log ?? log;
        if (Now() - _pendingAt > ReconcileSeconds)
        {
            log?.LogWarning($"{Tag} gave up confirming '{_pending}' after {ReconcileSeconds:F0}s (no lobby we created)");
            _pending = null;
            return;
        }
        try
        {
            if (!SteamLobbyManager.IsInLobby) return;
            var lobby = SteamLobbyManager.CurrentLobby;
            if (lobby.Id.Value == 0) return;
            string actual = lobby.GetData("name") ?? "";
            if (string.Equals(actual, _pending, StringComparison.Ordinal))
            {
                log?.LogInfo($"{Tag} published name confirmed: '{actual}' id={lobby.Id.Value} " +
                             $"owner={(lobby.Owner.Id.Value == SteamClient.SteamId.Value ? "us" : lobby.Owner.Id.Value.ToString())} " +
                             $"members={lobby.MemberCount}/{lobby.MaxMembers} forcedRepublish={_forcedRepublish}");
                try { log?.LogInfo($"{Tag} lobby metadata: name='{actual}' tag='{lobby.GetData("tag")}' " +
                                   $"vers='{lobby.GetData("vers")}' started='{lobby.GetData("started")}' " +
                                   $"joinable={lobby.GetData("joinable")}"); } catch { }
                _pending = null;
                return;
            }
            // Someone else's lobby is never renamed; ours is republished with the intended name.
            if (lobby.Owner.Id.Value != SteamClient.SteamId.Value)
            {
                log?.LogInfo($"{Tag} published name is '{actual}', not ours (owner {lobby.Owner.Id.Value}); left alone");
                _pending = null;
                return;
            }
            log?.LogWarning($"{Tag} published name was '{actual}', not '{_pending}' — republishing (creation path kept its own copy)");
            bool ok = lobby.SetData("name", _pending);
            _forcedRepublish++;
            log?.LogInfo($"{Tag} SetData(\"name\", \"{_pending}\") -> {ok}");
        }
        catch (Exception e) { log?.LogWarning($"{Tag} reconcile failed: {e.Message}"); }
    }

    private static void PollRequest(IHostContext log)
    {
        if (Now() < _nextPoll) return;
        _nextPoll = Now() + 0.5f;
        string dir = log?.ScreensDir;
        if (string.IsNullOrEmpty(dir)) return;
        string path = Path.Combine(dir, RequestFile);
        if (!File.Exists(path)) return;
        string text = null;
        try { text = File.ReadAllText(path); }
        catch (Exception e) { log?.LogWarning($"{Tag} read {RequestFile} failed: {e.Message}"); return; }
        try { File.Delete(path); } catch { }
        if (text == null) return;
        log?.LogInfo($"{Tag} request '{RequestFile}'");
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            Command(log, line);
        }
    }

    // `/dev`-free clock: unscaled game time, the same clock the rest of the module ticks on.
    private static float Now()
    {
        try { return Time.unscaledTime; } catch { return (float)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds; }
    }

    // The live Lobby Creator view (only exists while the creator menu is up).
    private static Pause.UI.Controllers.LobbyCreatorView Creator()
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<Pause.UI.Controllers.LobbyCreatorView>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var v = arr[i].TryCast<Pause.UI.Controllers.LobbyCreatorView>();
                if (v == null || v.gameObject == null) continue;
                if (v.gameObject.scene.name == null || v.gameObject.scene.name.Length == 0) continue;
                return v;
            }
        }
        catch { }
        return null;
    }

    // The live SteamLobbyManager (the singleton the game itself calls CreateLobby on).
    private static SteamLobbyManager Manager()
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<SteamLobbyManager>());
            for (int i = 0; arr != null && i < arr.Length; i++)
            {
                var m = arr[i].TryCast<SteamLobbyManager>();
                if (m == null || m.gameObject == null) continue;
                if (m.gameObject.scene.name == null || m.gameObject.scene.name.Length == 0) continue;
                return m;
            }
        }
        catch { }
        return null;
    }

    // Request-file entry point, polled by Tick: `state` prints what the patch has seen,
    // `compose=<name>` shows the transformation, `setname=` fills the creator field, `create=` makes
    // a lobby through the game's own entry point.
    internal static void Command(IHostContext log, string line)
    {
        try
        {
            string text = (line ?? "").Trim();
            int eq = text.IndexOf('=');
            string key = eq > 0 ? text.Substring(0, eq).Trim().ToLowerInvariant() : text.ToLowerInvariant();
            string val = eq > 0 ? text.Substring(eq + 1).Trim() : "";
            switch (key)
            {
                case "state": case "": case "dump":
                    log?.LogInfo($"{Tag} installed={_installed} prefixHits={_prefixHits} alreadySuffixed={_alreadySuffixed} " +
                                 $"forcedRepublish={_forcedRepublish} pending='{_pending ?? "none"}' cap={MaxLength()} " +
                                 $"suffix='{Suffix}'");
                    break;
                case "compose":
                    {
                        string p = Compose(val, out bool trimmed, out bool already);
                        log?.LogInfo($"{Tag} compose '{val}' -> '{p}' trimmed={trimmed} alreadySuffixed={already}");
                        break;
                    }
                case "read":
                    // What Steam currently publishes for the lobby we are in, read back through the
                    // Lobby handle (the same value a browser row is built from).
                    {
                        if (!SteamLobbyManager.IsInLobby) { log?.LogInfo($"{Tag} read: not in a lobby"); break; }
                        var l = SteamLobbyManager.CurrentLobby;
                        log?.LogInfo($"{Tag} read: id={l.Id.Value} name='{l.GetData("name")}' tag='{l.GetData("tag")}' " +
                                     $"started='{l.GetData("started")}' dmods='{l.GetData("dmods")}' " +
                                     $"members={l.MemberCount}/{l.MaxMembers} " +
                                     $"owner={(l.Owner.Id.Value == SteamClient.SteamId.Value ? "us" : l.Owner.Id.Value.ToString())}");
                        break;
                    }
                case "setname":
                    // Fills the game's own Lobby Creator name field, so the UI path (button -> the
                    // game's CreateLobby call) is what produces the lobby under test.
                    {
                        var v = Creator();
                        if (v == null) { log?.LogWarning($"{Tag} setname: no LobbyCreatorView"); break; }
                        var f = v._nameInputField;
                        if (f == null) { log?.LogWarning($"{Tag} setname: no name field"); break; }
                        try { f.text = val; } catch (Exception e) { log?.LogWarning($"{Tag} setname failed: {e.Message}"); break; }
                        try { f.onValueChanged.Invoke(val); } catch { }
                        try { f.onEndEdit.Invoke(val); } catch { }
                        log?.LogInfo($"{Tag} creator field '{f.name}' <- '{val}' (limit={f.characterLimit})");
                        break;
                    }
                case "create":
                    // The same entry point the UI uses, called from managed code: proves the prefix is
                    // on the path and gives a second creation with a chosen name.
                    {
                        if (SteamLobbyManager.IsInLobby) { log?.LogWarning($"{Tag} create: already in a lobby — leave first"); break; }
                        var m = Manager();
                        if (m == null) { log?.LogWarning($"{Tag} create: no SteamLobbyManager in the scene"); break; }
                        string n = val.Length == 0 ? "Requested lobby" : val;
                        log?.LogInfo($"{Tag} create: calling CreateLobby(4, '{n}', Private)");
                        try { var _ = m.CreateLobby(4, n, Steam.LobbyType.Private); }
                        catch (Exception e) { log?.LogWarning($"{Tag} create failed: {e.Message}"); }
                        break;
                    }
                default:
                    log?.LogWarning($"{Tag} unknown command '{line}' (state | compose=<name> | setname=<text> | create=<name>)");
                    break;
            }
        }
        catch (Exception e) { log?.LogWarning($"{Tag} command failed: {e.Message}"); }
    }
}
