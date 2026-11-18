using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.PlayerSystem;
using Gameplay.Wardrobe;
using HarmonyLib;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Random custom model on lobby join.
//
// Policy (see the task this implements):
//   * one roll per player per lobby membership, after the 16 custom rows are registered;
//   * pool = ALL custom entries (ModelRoster.All, demo-only included: "assume they have the mod");
//   * applied through the game's own path (PlayersAppearanceManager.SetBaseModelIndex), never by
//     calling SetupModel directly;
//   * no reroll on scene load, respawn or hot reload;
//   * a manual wardrobe pick before the roll wins (the roll is skipped), and a manual pick after it
//     is never overwritten because the roll runs exactly once;
//   * the roll is the DEFAULT for a player who has not chosen: a persisted explicit choice (the
//     `explicit=1` flag in SahurWardrobe's wanted cfg) suppresses it, and the explicit entry is
//     re-applied instead - a deliberate pick has to survive a restart / the next session, which a
//     roll on every fresh join would otherwise replace.
//
// "Lobby membership" is the Steam lobby id plus the local Netcode client id, opened when the game
// enters a lobby and closed when it leaves it. `IsInLobby` false -> true starts a new membership
// even for the same lobby id, so leave+rejoin rolls again.
//
// Hot reload loses managed state, so the active membership and the chosen entry are written next
// to the config file and read back by the next generation. The process id is part of the record:
// a state file left behind by a dead process can never silence the roll in a new one.
//
// The record is keyed by the entry's SLUG, never by its roster index. A roster index means
// "whatever sits at that position in this build's ModelRoster.All": appending, removing or
// reordering an entry silently reinterprets an old number as a different model. A file written by
// an older build has `entry=` and no `slug=`; that record is IGNORED (with a log line) instead of
// being resumed onto a possibly different entry, and the membership rolls again.
internal static class RandomModelOnJoin
{
    private const string Tag = "[random:join]";
    private const string FileName = "alterum-random-model-onjoin.txt";
    private const float GraceSeconds = 3.0f;       // let the game's own join/restore land first
    private const float PollSeconds = 0.5f;

    private static IHostContext _log;
    private static Harmony _harmony;
    private static string _file;
    private static int _pid;

    private static float _nextPoll, _rollAt;
    private static bool _active, _rolled, _skipped, _warnedNotReady, _explicitWarned;
    private static string _scene = "";
    private static bool _waitLogged;
    private static ulong _lobbyId, _localClient = ulong.MaxValue;
    private static int _manualAtStart, _rollEntry = -1, _last;

    // The entry this membership rolled, or -1; and the slug it was persisted as. Read by the
    // wardrobe's wanted-state recovery: after the map re-applied a stale index there is no other
    // record of what the player chose. The SLUG is the durable identity - `RolledEntry` is only a
    // same-generation convenience and is never what a file records.
    internal static int RolledEntry => _rolled ? _rollEntry : -1;
    internal static string RolledSlug => _rolled && _rollEntry >= 0 ? (SahurWardrobe.SpecAt(_rollEntry)?.Slug ?? "") : "";
    private static string _rollLabel = "?";
    private static string _rollSlug = "";
    private static int _rollDb = -1;

    // Record loaded from the previous generation (or a dead process) at install time. `_resumeSlug`
    // is the identity; the roster entry is resolved from it on this build's roster.
    private static bool _resumeActive;
    private static string _resumeSlug;
    private static int _resumePid;
    private static ulong _resumeLobby, _resumeClient;

    internal static void Install(IHostContext log)
    {
        _log = log;
        try { _pid = System.Diagnostics.Process.GetCurrentProcess().Id; }
        catch { _pid = 0; }
        try
        {
            var cfg = log?.ConfigPath;
            var dir = string.IsNullOrEmpty(cfg) ? null : Path.GetDirectoryName(cfg);
            if (!string.IsNullOrEmpty(dir)) _file = Path.Combine(dir, FileName);
        }
        catch { }
        Load();

        // The manual signal: the game's own RPC is only reachable from the wardrobe's Select
        // button (WardrobeController.OnModelSelectedServerRpc), which is unambiguous. It is also
        // where a local pick becomes the wanted entry - provenance the applied-row heuristic could
        // never establish (SahurWardrobe.NoteLocalWardrobePick).
        try
        {
            var m = AccessTools.DeclaredMethod(typeof(WardrobeController), "OnModelSelectedServerRpc",
                                               new[] { typeof(ulong), typeof(ushort) });
            if (m != null)
            {
                _harmony = new Harmony("com.user0.alterum.randomjoin." + Guid.NewGuid().ToString("N"));
                _harmony.Patch(m, prefix: new HarmonyMethod(typeof(RandomModelOnJoin), nameof(ManualPickPrefix)));
                log?.LogInfo($"{Tag} manual-pick hook on WardrobeController.OnModelSelectedServerRpc");
            }
            else log?.LogWarning($"{Tag} WardrobeController.OnModelSelectedServerRpc not found; " +
                                 "a manual pick before the roll cannot be detected");
        }
        catch (Exception e) { log?.LogWarning($"{Tag} manual-pick hook failed: {e.Message}"); }

        log?.LogInfo($"{Tag} installed (pid={_pid} state='{(_file ?? "none")}')");
    }

    internal static void Dispose()
    {
        try { _harmony?.UnpatchSelf(); } catch { }
        _harmony = null;
    }

    private static bool _unattributedLogged;

    // The game's own select RPC. This is the ONLY path a wardrobe tab click takes, so a call to it
    // is provenance for a pick - the one thing "the applied row changed" is not, because the lobby
    // re-applies the row it captured at join and that looks identical.
    //
    // A prefix returning false skips the original RPC; that is used for exactly one case, a pick
    // whose record could not be persisted (applying it would then be undone by the next
    // generation). Everything else allows the original: the game applies the row exactly once on
    // its own path, and this hook never calls ApplyIndex/ApplyEntry/SetupModel or a body module.
    private static bool ManualPickPrefix(ulong localClientId, ushort chosenModelIndex)
    {
        try
        {
            Unity.Netcode.NetworkManager nm = null;
            try { nm = Unity.Netcode.NetworkManager.Singleton; } catch { }
            if (nm == null || !nm.IsListening || !nm.IsClient)
            {
                // No session to attribute the call to. There is no authoritative local client id
                // here, so neither this host's own pick nor a remote client's RPC can be proven -
                // and a counter is only meaningful for a call this instance owns. Nothing is
                // counted, adopted or persisted; the original RPC is allowed to run.
                if (!_unattributedLogged)
                {
                    _unattributedLogged = true;
                    _log?.LogInfo($"{Tag} wardrobe RPC client={localClientId} index={chosenModelIndex} " +
                                  "not attributed: no local network session (not listening as a client) -> " +
                                  "not counted, not adopted (an unowned call cannot be a choice)");
                }
                return true;
            }

            // This module's own selection pass (the lobby re-assert of a wanted row) calls the game's
            // own RPC on purpose. It is NOT a player pick: counting it would retire the membership's
            // roll as if the player had clicked, and persisting it would rewrite an adopted record as
            // a deliberate choice. Allow the original RPC - the host still applies the row.
            if (SahurWardrobe.OurApply)
            {
                _log?.LogInfo($"[sahur:choice] client={localClientId} index={chosenModelIndex} is this module's own " +
                              "selection pass (not a player pick) - RPC allowed, not counted, wanted record untouched");
                return true;
            }

            ulong me = nm.LocalClientId;
            if (localClientId != me)
            {
                // Another player's pick. The server runs this RPC for every client's request, so it
                // says nothing about what THIS player wants: neither the wanted state nor the
                // counter moves (counting it would cancel this player's roll for someone else's
                // click).
                _log?.LogInfo($"[sahur:choice] ignored wardrobe RPC client={localClientId} local={me} reason=remote-owner");
                return true;
            }

            return SahurWardrobe.NoteLocalWardrobePick(_log, localClientId, chosenModelIndex);
        }
        catch (Exception e)
        {
            _log?.LogWarning($"{Tag} manual-pick hook failed ({e.Message}); allowing the game's own RPC");
            return true;
        }
    }

    // ------------------------------------------------------------------ tick

    internal static void Tick(IHostContext log, float dt)
    {
        _log = log;
        if (log == null) return;
        float now = 0f;
        try { now = Time.unscaledTime; } catch { }
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;

        bool inLobby = false;
        ulong lobby = 0;
        try
        {
            inLobby = Steam.SteamLobbyManager.IsInLobby;
            if (inLobby) lobby = Steam.SteamLobbyManager.CurrentLobby.Id;
        }
        catch { return; }

        ulong client = 0;
        try
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null) client = nm.LocalClientId;
        }
        catch { }

        if (inLobby && (!_active || lobby != _lobbyId || client != _localClient))
            BeginMembership(log, lobby, client, now);
        else if (!inLobby && _active)
            EndMembership(log);

        // The roll belongs to the lobby, not to the menu where the Steam lobby object already
        // exists: the player model, the appearance manager and the game's own join apply only
        // show up with the Lobby scene. Entering it (re)starts the grace timer.
        string scene = "";
        try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
        if (scene != _scene)
        {
            _scene = scene;
            if (_active && !_rolled && IsLobbyScene(scene))
            {
                _rollAt = now + GraceSeconds;
                _waitLogged = false;
                log.LogInfo($"{Tag} lobby scene ready ('{scene}'); roll in {GraceSeconds:F0}s " +
                            $"unless a manual pick lands first (lobby={_lobbyId} client={_localClient})");
            }
        }

        // A manual pick this instance owns retires this membership's roll record, whether it arrived
        // before the roll or after it. The record must not survive to a generation that would then
        // treat the pick as stale - and, the other way round, a pick is never demoted as if it were
        // the roll (that is what the `why` word on the wanted record decides).
        if (_active && SahurWardrobe.ManualSelections != _manualAtStart)
        {
            _skipped = true;
            if (_rolled || _resumeActive) Save(active: false);
            _resumeActive = false;
            log.LogInfo($"{Tag} no roll: reason=manual wardrobe pick " +
                        $"(picks {_manualAtStart} -> {SahurWardrobe.ManualSelections}), manual wins " +
                        $"(lobby={_lobbyId} client={_localClient}, rolled={_rolled}); this membership's " +
                        "roll record is retired");
            _manualAtStart = SahurWardrobe.ManualSelections;
        }

        if (!_active || _rolled || _skipped) return;

        if (!IsLobbyScene(_scene))
        {
            if (!_waitLogged)
            {
                _waitLogged = true;
                log.LogInfo($"{Tag} roll waiting: reason=not in the lobby scene yet (scene='{_scene}'); " +
                            $"the game's own join apply must land first (lobby={_lobbyId} client={_localClient})");
            }
            return;
        }

        if (!SahurWardrobe.RosterReady)
        {
            if (!_warnedNotReady)
            {
                _warnedNotReady = true;
                log.LogInfo($"{Tag} roll waiting: roster not ready yet ({SahurWardrobe.EntryCount} entries, " +
                            $"models={PlayerModelDatabase.Models?.Length ?? -1}); reason=the 16 custom rows are not all registered");
            }
            return;
        }
        if (now < _rollAt) return;

        // An explicit persisted choice outranks the roll. `why=apply` on a fresh join is exactly the
        // case this covers: the game re-applies its own captured row there, not the player's.
        if (ExplicitChoiceWins(log, now)) return;

        // A persisted ADOPTED custom record outranks it too. The 2026-11-17 08:58Z real leave/rejoin:
        // B's wanted record was Golubiro (adopted), the new client id spawned the actor with the
        // game's own NATIVE row (Sarah), and the guard below only looked at the applied row - so the
        // roll replaced the identity with capuchino. The native row is the game's default, not a new
        // choice, so this membership keeps the recorded identity and lets the lobby re-assert own the
        // row (SahurWardrobe.LobbyReassert) instead of rolling over it.
        if (PersistedCustomWins(log, now)) return;

        // The game's OWN applied row can already be one of our custom rows: its join apply restores
        // the row this machine last wore, and for a player who picked one of ours that row is ours.
        // The roll exists so a NATIVE-bodied player does not stay native; rolling over a custom
        // assignment the game's own HUD portrait and wardrobe tab are already showing is the
        // "portrait says one model, the body is another" divergence this class must not create. The
        // body follows the applied row (ModelWantedPolicy), so the roll follows it too and stays out
        // of the way for this membership.
        string appliedOurs;
        try { appliedOurs = SahurWardrobe.SlugOfData(SahurWardrobe.AppliedData(log)); }
        catch { appliedOurs = ""; }
        if (!string.IsNullOrEmpty(appliedOurs))
        {
            _skipped = true;
            // Retire any record this class wrote for an earlier membership in this process: a roll
            // that never applies must not be resumed by the next generation as the session's model.
            Save(active: false);
            log.LogInfo($"{Tag} no roll: reason=the game's own applied row is already our custom model " +
                        $"'{appliedOurs}' (its HUD portrait and wardrobe tab show it), so a roll would only " +
                        $"give that assignment a body it does not name (lobby={_lobbyId} client={_localClient}, " +
                        $"pid={_pid})");
            return;
        }

        int pool = SahurWardrobe.EntryCount;
        if (pool <= 0) return;
        int pick = Pick(pool, _last);
        if (!SahurWardrobe.ApplyEntry(log, pick, SahurWardrobe.RollWhy))
        {
            log.LogWarning($"{Tag} roll deferred: entry {pick} not registered yet");
            _rollAt = now + 1f;
            return;
        }

        _rolled = true;
        _rollEntry = pick;
        _rollLabel = SahurWardrobe.EntryLabel(pick);
        _rollSlug = SahurWardrobe.SpecAt(pick)?.Slug ?? "";
        _rollDb = SahurWardrobe.EntryDatabaseIndex(pick);
        _last = pick;
        Save(active: true);
        log.LogInfo($"{Tag} ROLL lobby={_lobbyId} client={_localClient} pool={pool} " +
                    $"(all custom wardrobe entries, db 10..{9 + pool}) chosenEntry={pick} chosenSlug='{_rollSlug}' " +
                    $"chosenDbIndex={_rollDb} chosenLabel='{_rollLabel}' " +
                    $"reason=lobby membership active, lobby scene loaded, roster ready, " +
                    $"game's own join apply finished (grace={GraceSeconds:F0}s), no manual pick, " +
                    $"first roll for this membership (pid={_pid})");
    }

    // True when a persisted EXPLICIT choice exists: the roll is the default for a player who has not
    // chosen, so the choice suppresses it and is re-applied through the game's own path when the
    // join apply did not land it (a restored `why=apply` model on a fresh join).
    private static bool ExplicitChoiceWins(IHostContext log, float now)
    {
        int kind, entry;
        string label;
        bool applied;
        try { kind = SahurWardrobe.ExplicitWanted(log, out entry, out label, out applied); }
        catch (Exception e)
        {
            log.LogWarning($"{Tag} explicit-choice probe failed ({e.Message}); rolling");
            return false;
        }
        if (kind == 0) return false;

        bool reapplied = applied;
        if (kind == 1 && !applied)
        {
            if (SahurWardrobe.ApplyEntry(log, entry)) reapplied = true;
            else
            {
                // The choice still outranks the roll, but there is nothing to re-apply onto yet.
                if (!_explicitWarned)
                {
                    _explicitWarned = true;
                    log.LogWarning($"{Tag} no roll: reason=an explicit model choice is persisted " +
                                   $"('{label}' entry={entry}) but re-applying it did not land; retrying every " +
                                   $"{PollSeconds:F1}s (lobby={_lobbyId} client={_localClient})");
                }
                _rollAt = now + 1f;
                return true;
            }
        }
        else if (kind == 2) reapplied = true;    // native row: the game's own model IS the choice

        _skipped = true;
        log.LogInfo($"{Tag} no roll: reason=an explicit model choice is persisted " +
                    $"({(kind == 1 ? $"'{label}' entry={entry}" : "the game's own (native) row")}, explicit=1), " +
                    $"and it outranks the roll on a fresh join - re-applied={(reapplied ? "yes" : "no")} " +
                    $"(pool={SahurWardrobe.EntryCount}, lobby={_lobbyId} client={_localClient}, pid={_pid})");
        return true;
    }

    // True when a persisted custom wanted record exists that this membership must keep (explicit
    // records are handled by ExplicitChoiceWins above; this is the adopted half). It never applies
    // anything itself: the row is the wardrobe's to own, and it re-asserts it exactly once per edge.
    private static bool PersistedCustomWins(IHostContext log, float now)
    {
        int entry;
        string label;
        bool applied;
        try
        {
            if (!SahurWardrobe.PersistedCustomWanted(log, out entry, out label, out applied)) return false;
        }
        catch (Exception e)
        {
            log.LogWarning($"{Tag} adopted-choice probe failed ({e.Message}); rolling");
            return false;
        }
        _skipped = true;
        if (_resumeActive) { Save(active: false); _resumeActive = false; }
        log.LogInfo($"{Tag} no roll: reason=a persisted ADOPTED custom wanted ('{label}' entry={entry}) outranks " +
                    $"the roll on this membership - the game's {(applied ? "row" : "native join row")} is not a new " +
                    $"choice (lobby={_lobbyId} client={_localClient}, pid={_pid})");
        return true;
    }

    private static void BeginMembership(IHostContext log, ulong lobby, ulong client, float now)    {
        _active = true;
        _lobbyId = lobby;
        _localClient = client;
        _manualAtStart = SahurWardrobe.ManualSelections;
        _skipped = false;
        _warnedNotReady = false;
        _explicitWarned = false;
        _waitLogged = false;
        _rollAt = now + GraceSeconds;

        if (_resumeActive && _resumePid == _pid && _resumeLobby == lobby && _resumeClient == client &&
            !string.IsNullOrEmpty(_resumeSlug))
        {
            // The record belongs to a roll THIS process made for THIS membership. It is demoted only
            // when the record still carries the roll's own provenance and the game applied a
            // DIFFERENT one of our rows (see SahurWardrobe.DemoteRolledWanted); otherwise it stands.
            try
            {
                if (SahurWardrobe.DemoteRolledWanted(log, _resumeSlug)) { Save(active: false); _resumeActive = false; }
            }
            catch (Exception e) { log?.LogWarning($"{Tag} demote of the rolled wanted entry failed ({e.Message})"); }
        }

        if (_resumeActive && _resumePid == _pid && _resumeLobby == lobby && _resumeClient == client)
        {
            // Resolve the persisted slug against THIS build's roster. A slug that no longer exists
            // is not resumed (the entry was withdrawn): the membership rolls normally instead of
            // silencing the roll for a model this build cannot show.
            int resumed = SahurWardrobe.EntryIndexForSlug(_resumeSlug);
            if (resumed < 0)
            {
                log.LogWarning($"{Tag} no resume: the record's slug='{_resumeSlug}' is not in this build's " +
                               $"roster; rolling for this membership instead (lobby={lobby} client={client}, pid={_pid})");
                _resumeActive = false;
            }
            else
            {
                _rolled = true;
                _rollEntry = resumed;
                _rollSlug = _resumeSlug;
                _rollLabel = SahurWardrobe.EntryLabel(resumed);
                _rollDb = SahurWardrobe.EntryDatabaseIndex(resumed);
                log.LogInfo($"{Tag} no roll: reason=this lobby membership was already rolled before the " +
                            $"hot reload (lobby={lobby} client={client} chosenSlug='{_rollSlug}' " +
                            $"chosenEntry={resumed} chosenLabel='{_rollLabel}', pid={_pid})");
                return;
            }
        }

        _rolled = false;
        _rollEntry = -1;
        log.LogInfo($"{Tag} membership start: inLobby=true lobby={lobby} client={client} " +
                    $"roster={(SahurWardrobe.RosterReady ? "ready" : "not ready")} " +
                    $"-> roll in {GraceSeconds:F0}s unless a manual pick lands first");
    }

    private static void EndMembership(IHostContext log)
    {
        _active = false;
        bool rolled = _rolled;
        if (rolled) Save(active: false);
        _rolled = false;
        _skipped = false;
        _rollEntry = -1;
        _waitLogged = false;
        log.LogInfo($"{Tag} membership end: inLobby=false; {(rolled ? "next join rolls again" : "no roll had happened")}");
    }

    private static bool IsLobbyScene(string scene) =>
        !string.IsNullOrEmpty(scene) &&
        scene.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0;

    // ------------------------------------------------------------------ pick

    private static int Pick(int pool, int avoid)
    {
        int seed;
        try
        {
            var b = Guid.NewGuid().ToByteArray();
            seed = BitConverter.ToInt32(b, 0) ^ BitConverter.ToInt32(b, 4) ^
                   BitConverter.ToInt32(b, 8) ^ (int)(DateTime.UtcNow.Ticks & 0x7fffffff);
        }
        catch { seed = Environment.TickCount; }
        var rng = new System.Random(seed);
        int pick = rng.Next(pool);
        // Do not hand out the same model twice in a row when there is a choice; still one roll.
        if (pool > 1 && pick == avoid) pick = (pick + 1 + rng.Next(pool - 1)) % pool;
        return pick;
    }

    // ------------------------------------------------------------------ state file

    private static void Load()
    {
        try
        {
            if (string.IsNullOrEmpty(_file) || !File.Exists(_file)) return;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(_file))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            string v;
            // `lastslug` is the only form that survives a roster change; the old `last=<index>` is
            // not parsed (an unchanged pool position could mean a different model now).
            if (map.TryGetValue("lastslug", out v)) _last = SahurWardrobe.EntryIndexForSlug(v);
            else if (map.TryGetValue("last", out v) && v.Length > 0 && !int.TryParse(v, out _))
                _last = SahurWardrobe.EntryIndexForSlug(v);
            if (map.TryGetValue("active", out v) && v == "1")
            {
                // `entry=` alone is a record from a build before slug persistence. A roster index is
                // not stable across a roster change, so it is NOT reinterpreted: the record is
                // dropped with a line and this membership rolls.
                map.TryGetValue("slug", out _resumeSlug);
                if (string.IsNullOrEmpty(_resumeSlug))
                {
                    int legacy = Int(map, "entry");
                    _log?.LogWarning($"{Tag} legacy state record has no slug (entry={legacy}) - ignored: a roster " +
                                     "index is not stable across a roster change, so it is never resumed");
                    return;
                }
                _resumeActive = true;
                _resumePid = Int(map, "pid");
                _resumeLobby = ULong(map, "lobby");
                _resumeClient = ULong(map, "client");
            }
        }
        catch { }
    }

    private static void Save(bool active)
    {
        try
        {
            if (string.IsNullOrEmpty(_file)) return;
            var sb = new StringBuilder();
            sb.AppendLine("version=1");
            sb.AppendLine("pid=" + _pid.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("active=" + (active ? "1" : "0"));
            sb.AppendLine("lobby=" + _lobbyId.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("client=" + _localClient.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("slug=" + (_rollSlug ?? ""));
            // No `entry=`/`dbindex=`: a roster index is never persisted (the criterion is that
            // indices never leave memory). `label` and `rolledAt` stay - they are display strings -
            // and `lastslug` is the slug-only form Load consumes.
            sb.AppendLine("label=" + (_rollLabel ?? "?"));
            sb.AppendLine("rolledAt=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            sb.AppendLine("lastslug=" + (SahurWardrobe.SpecAt(_last)?.Slug ?? ""));
            // The legacy `last=<index>` key is not written any more: an index here would be
            // reinterpreted after a roster change.
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(_file)) File.Delete(_file);
            File.Move(tmp, _file);
        }
        catch (Exception e) { _log?.LogWarning($"{Tag} state save failed: {e.Message}"); }
    }

    private static int Int(Dictionary<string, string> map, string key)
    {
        string v;
        return map.TryGetValue(key, out v) && int.TryParse(v, out int n) ? n : 0;
    }

    private static ulong ULong(Dictionary<string, string> map, string key)
    {
        string v;
        return map.TryGetValue(key, out v) && ulong.TryParse(v, out ulong n) ? n : 0UL;
    }
}
