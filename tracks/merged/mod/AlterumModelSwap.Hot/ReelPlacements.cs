using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlterumModelSwap.Hot;

// Named placements for the Reel of Fortune.
//
// The store is one versioned JSON file in the instance's config folder, keyed by the game's own map
// identity: the GameMapData asset that owns the scene reference (`Necropolis_GameMapData`) together
// with the scene name it points at. `scene.name` alone is not the key, because the same display name
// can be reached through different scene assets and a map asset is what the game itself uses to pick
// a run.
//
// Writes are atomic (temp + rename) and a file that cannot be parsed is moved aside, never
// overwritten. Every read validates the entry (finite numbers, name shape, map compatibility) before
// it can move a booth.
//
// Restore is once per map generation: a generation is the Unity scene handle (unique per scene load,
// unchanged by a hot reload) plus the map key. The marker is persisted, so a hot reload during a map
// does not restore a second time.
internal static class ReelPlacements
{
    internal const int FormatVersion = 2;
    private const string FileName = "alterum-reel-placements.json";
    private const string RequestFile = "reel.request";

    internal sealed class Entry
    {
        public string Name;
        public string MapKey;
        public Vector3 Position;
        public float Yaw;
        public bool IsDefault;
        public string SavedAt;
    }

    internal sealed class MapIdentity
    {
        public string Key;          // "<mapData>|<scene>" (or "scene:<scene>" when no map asset)
        public string Scene;        // Unity scene name
        public string ScenePath;    // scene asset path (map identity that is not the display name)
        public int BuildIndex;
        public string MapData;      // GameMapData asset name, "" when the scene is not a map run
        public int Handle;          // Scene.handle: one value per scene load
        public override string ToString() => Key;
    }

    private static readonly List<Entry> _entries = new List<Entry>();
    private static bool _loaded;
    private static string _storePath;
    private static string _status = "not loaded";
    private static string _lastGeneration;
    private static string _spawnGeneration;
    private static readonly List<LobbyPropRuntime> _auto = new List<LobbyPropRuntime>();
    private static float _nextOrphanLog;
    private static float _nextRequestPoll;
    private static string _lastRequest = "";

    internal static string Status => _status;
    internal static int Count => _entries.Count;
    internal static string StorePath => _storePath;

    // ---- storage path --------------------------------------------------------------------

    private static string ConfigDir(IHostContext log)
    {
        try
        {
            string cfg = log?.ConfigPath;
            if (!string.IsNullOrEmpty(cfg)) return Path.GetDirectoryName(cfg);
        }
        catch { }
        try { return Path.Combine(Environment.CurrentDirectory, "BepInEx", "config"); } catch { }
        return Environment.CurrentDirectory;
    }

    private static string StoreFile(IHostContext log) => Path.Combine(ConfigDir(log), FileName);

    // ---- map identity --------------------------------------------------------------------

    // The game's map identity: the GameMapData asset whose scene reference is the active scene, plus
    // that scene's asset path and build index. The scene handle makes each load a new generation.
    internal static MapIdentity Current(IHostContext log)
    {
        var id = new MapIdentity { Key = "scene:?", Scene = "", ScenePath = "", MapData = "", Handle = 0 };
        try
        {
            var s = SceneManager.GetActiveScene();
            id.Scene = s.name ?? "";
            id.ScenePath = s.path ?? "";
            id.BuildIndex = s.buildIndex;
            id.Handle = s.handle;
        }
        catch { }
        try
        {
            id.MapData = FindMapData(log, id.Scene);
        }
        catch { }
        id.Key = string.IsNullOrEmpty(id.MapData) ? "scene:" + id.Scene : id.MapData + "|" + id.Scene;
        return id;
    }

    // Scene-transition warm-up: the map identity is an asset-table scan, so resolve it while the
    // transition is still covering the screen instead of on the first Tick after it.
    internal static void Prewarm(IHostContext log)
    {
        try { Current(log); } catch { }
    }

    // The loaded GameMapData whose GameMapScene points at `scene`. Read through reflection because
    // SceneReference lives in the Eflatun package and the hot assembly does not reference it.
    private static string FindMapData(IHostContext log, string scene)
    {
        if (string.IsNullOrEmpty(scene)) return "";
        try
        {
            var maps = ReelInteraction.All<GameLobby.GameMapData>();
            for (int i = 0; i < maps.Length; i++)
            {
                var m = maps[i];
                if (m == null) continue;
                string name = "";
                try { name = m.name ?? ""; } catch { }
                if (string.IsNullOrEmpty(name)) continue;
                var sceneRef = ReelInteraction.F(m, "GameMapScene");
                string refName = ReadString(sceneRef, "Name");
                string refPath = ReadString(sceneRef, "Path");
                bool match = (!string.IsNullOrEmpty(refName) && refName.Equals(scene, StringComparison.OrdinalIgnoreCase))
                             || (!string.IsNullOrEmpty(refPath) && Path.GetFileNameWithoutExtension(refPath)
                                     .Equals(scene, StringComparison.OrdinalIgnoreCase));
                if (match) return name;
            }
        }
        catch (Exception e) { log?.LogWarning($"[reel:place] map data scan failed: {e.Message}"); }
        return "";
    }

    private static string ReadString(object o, string name)
    {
        if (o == null) return "";
        try
        {
            var v = ReelInteraction.F(o, name) ?? ReelInteraction.F(o, "_" + name);
            return v as string ?? "";
        }
        catch { return ""; }
    }

    // ---- load / save / validate ----------------------------------------------------------

    private static void EnsureLoaded(IHostContext log)
    {
        if (_loaded && _storePath == StoreFile(log)) return;
        _loaded = true;
        _storePath = StoreFile(log);
        _entries.Clear();
        if (!File.Exists(_storePath))
        {
            _status = "empty (no store yet)";
            log?.LogInfo($"[reel:place] store '{_storePath}' absent; starting empty");
            return;
        }
        string text;
        try { text = File.ReadAllText(_storePath); }
        catch (Exception e)
        {
            _status = "read failed: " + e.Message;
            log?.LogWarning($"[reel:place] store read failed: {e.Message}");
            return;
        }
        try
        {
            Import(text, log);
            _status = $"loaded {_entries.Count} placement(s)";
            log?.LogInfo($"[reel:place] store '{_storePath}' {_status} maps=[{MapsSummary()}]");
        }
        catch (Exception e)
        {
            Quarantine(log, _storePath, e.Message);
            _status = "quarantined: " + e.Message;
        }
    }

    // Accepts both this format (version 2, `maps` keyed by map identity) and the hand-recorded seed
    // (version 1, a flat `placements` array with `map` / `mapData` / `pos`).
    private static void Import(string text, IHostContext log)
    {
        var root = MiniJson.Parse(text) as Dictionary<string, object>;
        if (root == null) throw new FormatException("root is not an object");
        int version = (int)Num(root, "version", 0);
        if (root.TryGetValue("maps", out var mapsObj) && mapsObj is Dictionary<string, object> maps)
        {
            foreach (var kv in maps)
            {
                var map = kv.Value as Dictionary<string, object>;
                if (map == null) continue;
                if (!map.TryGetValue("placements", out var arr) || !(arr is List<object> list)) continue;
                for (int i = 0; i < list.Count; i++) AddImported(kv.Key, list[i] as Dictionary<string, object>, log);
            }
            return;
        }
        if (root.TryGetValue("placements", out var legacy) && legacy is List<object> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i] as Dictionary<string, object>;
                if (e == null) continue;
                string mapName = Str(e, "map");
                string mapData = Str(e, "mapData");
                string key = string.IsNullOrEmpty(mapData) ? "scene:" + mapName : mapData + "|" + mapName;
                AddImported(key, e, log, legacy: true);
            }
            log?.LogInfo($"[reel:place] imported legacy seed (version {version}) as format {FormatVersion}");
            return;
        }
        throw new FormatException("no 'maps' object and no legacy 'placements' array");
    }

    private static void AddImported(string mapKey, Dictionary<string, object> e, IHostContext log, bool legacy = false)
    {
        if (e == null) return;
        string name = Str(e, "name");
        if (!ValidName(name, out string why)) { log?.LogWarning($"[reel:place] import skipped '{name}': {why}"); return; }
        Vector3 pos;
        if (!ReadPos(e, out pos)) { log?.LogWarning($"[reel:place] import skipped '{name}': bad position"); return; }
        float yaw = (float)Num(e, "yaw", 0);
        bool isDefault = Bool(e, "isDefault", false) || Bool(e, "default", false);
        _entries.Add(new Entry
        {
            Name = name,
            MapKey = mapKey,
            Position = pos,
            Yaw = yaw,
            IsDefault = isDefault,
            SavedAt = Str(e, "savedAt") ?? "",
        });
        if (legacy) log?.LogInfo($"[reel:place] imported '{name}' map='{mapKey}' pos=({pos.x:F2},{pos.y:F3},{pos.z:F2}) yaw={yaw:F1}");
    }

    private static bool ReadPos(Dictionary<string, object> e, out Vector3 pos)
    {
        pos = Vector3.zero;
        object raw = null;
        if (e.TryGetValue("position", out var p)) raw = p;
        else if (e.TryGetValue("pos", out var q)) raw = q;
        if (!(raw is List<object> a) || a.Count < 3) return false;
        float x = ToFloat(a[0]), y = ToFloat(a[1]), z = ToFloat(a[2]);
        if (!Finite(x) || !Finite(y) || !Finite(z)) return false;
        if (Math.Abs(x) > 100000f || Math.Abs(z) > 100000f || Math.Abs(y) > 10000f) return false;
        pos = new Vector3(x, y, z);
        return true;
    }

    internal static bool ValidName(string name, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(name)) { why = "empty name"; return false; }
        if (name.Length > 48) { why = "name longer than 48 chars"; return false; }
        foreach (char c in name)
        {
            if (c == '=' || c == '\n' || c == '\r' || c == '\t' || c < ' ') { why = $"name contains '{c}'"; return false; }
        }
        return true;
    }

    private static void Quarantine(IHostContext log, string path, string reason)
    {
        try
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string dest = path + ".corrupt-" + stamp;
            File.Move(path, dest);
            log?.LogWarning($"[reel:place] store quarantined -> '{dest}' ({reason}); starting empty");
        }
        catch (Exception e) { log?.LogError($"[reel:place] quarantine failed: {e.Message}"); }
    }

    // Atomic write: temp in the same directory, flush, then rename over the store.
    private static bool Write(IHostContext log)
    {
        try
        {
            string path = _storePath ?? StoreFile(log);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.Append("{\n  \"version\": ").Append(FormatVersion).Append(",\n");
            sb.Append("  \"note\": \"Reel of Fortune placements, keyed by game map identity (GameMapData asset + scene). Written by the mod; safe to hand-edit.\",\n");
            sb.Append("  \"maps\": {");
            var keys = new List<string>();
            for (int i = 0; i < _entries.Count; i++) if (!keys.Contains(_entries[i].MapKey)) keys.Add(_entries[i].MapKey);
            for (int k = 0; k < keys.Count; k++)
            {
                string key = keys[k];
                var id = Current(log);
                sb.Append(k == 0 ? "\n" : ",\n");
                sb.Append("    ").Append(J(key)).Append(": {\n");
                sb.Append("      \"scene\": ").Append(J(SceneOf(key))).Append(",\n");
                sb.Append("      \"mapData\": ").Append(J(MapDataOf(key))).Append(",\n");
                if (key == id.Key)
                {
                    sb.Append("      \"scenePath\": ").Append(J(id.ScenePath)).Append(",\n");
                    sb.Append("      \"buildIndex\": ").Append(id.BuildIndex).Append(",\n");
                }
                sb.Append("      \"placements\": [");
                bool first = true;
                for (int i = 0; i < _entries.Count; i++)
                {
                    var e = _entries[i];
                    if (e.MapKey != key) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("\n        { \"name\": ").Append(J(e.Name))
                      .Append(", \"position\": [").Append(F(e.Position.x)).Append(", ").Append(F(e.Position.y)).Append(", ").Append(F(e.Position.z)).Append(']')
                      .Append(", \"yaw\": ").Append(F(e.Yaw))
                      .Append(", \"isDefault\": ").Append(e.IsDefault ? "true" : "false")
                      .Append(", \"savedAt\": ").Append(J(e.SavedAt ?? "")).Append(" }");
                }
                sb.Append(first ? "]\n" : "\n      ]\n");
                sb.Append("    }");
            }
            // Close the maps object and the root object, whether or not any map has entries.
            sb.Append(keys.Count > 0 ? "\n  }\n}\n" : "}\n}\n");
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return true;
        }
        catch (Exception e)
        {
            log?.LogError($"[reel:place] store write failed: {e.Message}");
            return false;
        }
    }

    // ---- operations ----------------------------------------------------------------------

    // Save the CURRENT reel anchor. Never re-resolves a player pose: what the booth is at is what is
    // recorded, so a saved spot cannot drift when the player has moved on.
    internal static bool Save(IHostContext log, LobbyPropRuntime rt, string name, bool overwrite, bool isDefault)
    {
        EnsureLoaded(log);
        if (rt == null || rt.Root == null) { log?.LogWarning("[reel:place] save: no booth"); return false; }
        if (!ValidName(name, out string why)) { log?.LogWarning($"[reel:place] save rejected: {why}"); return false; }
        var id = Current(log);
        var existing = Find(id.Key, name);
        if (existing != null && !overwrite)
        {
            log?.LogWarning($"[reel:place] save rejected: duplicate name '{name}' on map '{id.Key}' (use overwrite=1)");
            return false;
        }
        Vector3 pos = rt.Anchor;
        float yaw = rt.Yaw;
        if (!Finite(pos.x) || !Finite(pos.y) || !Finite(pos.z) || !Finite(yaw))
        {
            log?.LogWarning($"[reel:place] save rejected: non-finite pose {pos} yaw={yaw}");
            return false;
        }
        if (isDefault)
            for (int i = 0; i < _entries.Count; i++) if (_entries[i].MapKey == id.Key) _entries[i].IsDefault = false;
        var e = existing ?? new Entry { Name = name, MapKey = id.Key };
        e.Position = pos; e.Yaw = yaw; e.IsDefault = isDefault;
        e.SavedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        if (existing == null) _entries.Add(e);
        if (!Write(log)) return false;
        _status = $"saved '{name}'";
        log?.LogInfo($"[reel:place] saved '{name}' map='{id.Key}' pos=({pos.x:F2},{pos.y:F3},{pos.z:F2}) yaw={yaw:F1} " +
                     $"isDefault={isDefault} overwrite={existing != null} entries={_entries.Count} file='{_storePath}'");
        return true;
    }

    internal static bool Load(IHostContext log, LobbyPropRuntime rt, string name)
    {
        EnsureLoaded(log);
        var id = Current(log);
        var e = Find(id.Key, name);
        if (e == null)
        {
            // A name that exists on another map is a map mismatch, not a typo: say which map it is on.
            var other = FindAny(name);
            if (other != null)
                log?.LogWarning($"[reel:place] load rejected: '{name}' belongs to map '{other.MapKey}', not '{id.Key}'");
            else log?.LogWarning($"[reel:place] load rejected: no placement '{name}' on map '{id.Key}'");
            return false;
        }
        if (!Compatible(log, id, e)) return false;
        if (rt == null || rt.Root == null) { log?.LogWarning("[reel:place] load: no booth to move (use load= before the booth exists?)"); return false; }
        rt.MoveTo(log, e.Position, e.Yaw);
        _status = $"loaded '{name}'";
        log?.LogInfo($"[reel:place] loaded '{name}' map='{id.Key}' pos=({e.Position.x:F2},{e.Position.y:F3},{e.Position.z:F2}) " +
                     $"yaw={e.Yaw:F1}");
        return true;
    }

    internal static bool Delete(IHostContext log, string name)
    {
        EnsureLoaded(log);
        var id = Current(log);
        var e = Find(id.Key, name);
        if (e == null) { log?.LogWarning($"[reel:place] delete rejected: no placement '{name}' on map '{id.Key}'"); return false; }
        _entries.Remove(e);
        if (!Write(log)) return false;
        _status = $"deleted '{name}'";
        log?.LogInfo($"[reel:place] deleted '{name}' map='{id.Key}' entries={_entries.Count}");
        return true;
    }

    internal static void List(IHostContext log)
    {
        EnsureLoaded(log);
        var id = Current(log);
        log.LogInfo($"[reel:place] list map='{id.Key}' scene='{id.Scene}' path='{id.ScenePath}' buildIndex={id.BuildIndex} " +
                    $"mapData='{id.MapData}' handle={id.Handle} entries={_entries.Count} file='{_storePath}'");
        int shown = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            bool here = e.MapKey == id.Key;
            log.LogInfo($"[reel:place]   {(here ? "*" : " ")} '{e.Name}' map='{e.MapKey}' pos=({e.Position.x:F2},{e.Position.y:F3},{e.Position.z:F2}) " +
                        $"yaw={e.Yaw:F1} isDefault={e.IsDefault} savedAt='{e.SavedAt}'");
            shown++;
        }
        log.LogInfo($"[reel:place] list end shown={shown} maps=[{MapsSummary()}]");
    }

    // ---- spawn every saved anchor once per map generation ------------------------------

    // Called every frame by ModelSwapRuntime. On the first frame of each map generation (and again
    // after a hot reload, whose new assembly starts with empty lists) it creates one booth per
    // saved anchor for the active map and appends them to `reels`. The map is the only place the
    // reel exists: the lobby and menu are skipped. It refuses to spawn while older booths are
    // still in the scene (a retiring generation has not removed them yet), which is what stops a
    // reload from doubling the count.
    //
    // WHO owns the anchors is decided here, once per generation:
    //   solo / netcode server -> this process's placements file (solo play, and the host's layout)
    //   netcode client        -> the host's published layout; the local file is NOT used, so a
    //                            client's own alterum-reel-placements.json cannot move a machine
    // The source that won is logged on both paths.
    internal static void Tick(IHostContext log, List<LobbyPropRuntime> reels)
    {
        try
        {
            if (log == null || reels == null) return;
            try { ReelNet.Tick(log); } catch { }
            // Unity destroys the scene objects but not the managed booth runtime. Dispose it
            // before pruning so subscriptions and the indexed ReelInteraction.Live list retire too.
            for (int i = _auto.Count - 1; i >= 0; i--)
                if (_auto[i] == null || _auto[i].Root == null) _auto.RemoveAt(i);
            for (int i = reels.Count - 1; i >= 0; i--)
            {
                var reel = reels[i];
                if (reel != null && reel.Root != null) continue;
                reels.RemoveAt(i);
                try { reel?.Dispose(); } catch { }
            }

            if (!ReelLogic.IsMapRun(out string scene))
            {
                // Leaving a map: the booths are gone with the scene, so the next map is a fresh
                // spawn. No booth is ever created here. Log the transition once so "the lobby has
                // no reel" is in the log, not just the absence of a spawn line.
                if (_spawnGeneration != null || _auto.Count > 0)
                {
                    _spawnGeneration = null;
                    _auto.Clear();
                    log.LogInfo($"[reel:spawn] scene='{scene}' mapRun=false: no reel outside a map " +
                                $"sceneRoots={SceneBoothCount()}");
                }
                return;
            }

            var id = Current(log);
            ReelNet.NoteLocalMap(id.Key);
            var authority = ReelNet.Authority;
            if (authority == ReelAuthority.Client)
            {
                TickClient(log, reels, id);
                return;
            }
            string generation = id.Handle + "|" + id.Key;
            bool newGeneration = generation != _lastGeneration;
            if (newGeneration)
            {
                _lastGeneration = generation;
                log.LogInfo($"[reel:spawn] generation={generation} scene='{id.Scene}' map='{id.Key}'");
            }
            // One read per process: a store written by hand (or left corrupt) is picked up here,
            // and a corrupt file is quarantined before anything tries to use it.
            EnsureLoaded(log);
            if (_spawnGeneration == generation) return;

            // After a reload the new generation runs before the retired one's Dispose is complete
            // in some hosts. Wait until every scene root not in `reels` is gone, otherwise two
            // generations' booths would briefly coexist and the count could settle at four.
            int orphans = OrphanBoothCount(reels);
            if (orphans > 0)
            {
                if (Time.unscaledTime >= _nextOrphanLog)
                {
                    _nextOrphanLog = Time.unscaledTime + 2f;
                    log.LogInfo($"[reel:spawn] map='{id.Key}' orphans={orphans} live={_auto.Count}: " +
                                "waiting for the retiring generation to remove its booths");
                }
                return;
            }

            var entries = AllOn(id.Key);
            var names = new StringBuilder();
            int spawned = 0, failed = 0;
            var created_list = new List<LobbyPropRuntime>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!Compatible(log, id, e)) { failed++; continue; }
                var created = LobbyPropRuntime.CreateAt(log, e.Position, e.Yaw);
                if (created == null)
                {
                    failed++;
                    log.LogWarning($"[reel:spawn] failed to create '{e.Name}' map='{id.Key}' " +
                                   $"pos=({e.Position.x:F2},{e.Position.y:F3},{e.Position.z:F2})");
                    continue;
                }
                created.PlacementName = e.Name;
                created.MachineId = MachineIdFor(e.Name, i + 1);
                created.IsMapSpawn = true;
                created.RequestOwner = false;
                _auto.Add(created);
                reels.Add(created);
                created_list.Add(created);
                spawned++;
                if (names.Length > 0) names.Append(',');
                names.Append(e.Name);
                log.LogInfo($"[reel:spawn] '{e.Name}' map='{id.Key}' " +
                            $"pos=({created.Anchor.x:F2},{created.Anchor.y:F3},{created.Anchor.z:F2}) yaw={created.Yaw:F1} " +
                            $"machine='{created.MachineId}' oplog=\"collider={ColliderText(created)} lookCentre={LookText(created)} bounds={BoundsText(created)}\"");
            }

            _spawnGeneration = generation;
            log.LogInfo($"[reel:spawn] map='{id.Key}' authority=local({(authority == ReelAuthority.Host ? "host" : "solo")}) " +
                        $"saved={entries.Count} spawned={spawned} failed={failed} " +
                        $"expected=2 live={_auto.Count} sceneRoots={SceneBoothCount()} names=[{names}]");
            if (entries.Count < 2)
                log.LogWarning($"[reel:spawn] map='{id.Key}' has only {entries.Count} saved anchor(s); " +
                               "spawned what exists (no positions invented)");
            // The host publishes exactly what it just put in the scene: the same ids, the same
            // anchors, one new wire epoch per map entry. Clients then stand their machines there.
            if (authority == ReelAuthority.Host)
                ReelNet.PublishPlacements(log, id.Key, generation, created_list);
        }
        catch (Exception e)
        {
            log?.LogWarning($"[reel:spawn] tick failed: {e.Message}");
        }
    }

    // A machine's network id: the placement's own name (stable across both sides because the host
    // sends it), or an index only when the name could not be a wire id.
    private static string MachineIdFor(string name, int index)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Length <= 32)
        {
            bool clean = true;
            foreach (char c in name) if (c < ' ' || c > '~') { clean = false; break; }
            if (clean) return name;
        }
        return "machine-" + index;
    }

    // Client path: the host's published anchors, and NOTHING from the local file. A client that has
    // not received them yet waits (it must not fall back - that is the override this stage forbids).
    private static void TickClient(IHostContext log, List<LobbyPropRuntime> reels, MapIdentity id)
    {
        // Read the local store anyway: not to use it, but so the log can name what was ignored.
        EnsureLoaded(log);
        if (!ReelNet.TryHostPlacements(id.Key, out uint epoch, out uint rev, out var machines))
        {
            string key = id.Handle.ToString();
            if (_lastGeneration != key)
            {
                _lastGeneration = key;
                int local = CountOn(id.Key);
                log.LogInfo($"[reel:spawn] map='{id.Key}' authority=host: waiting for the host's placements " +
                            $"(local file has {local} anchor(s) for this map; they are NOT used in a session)");
                ReelNet.ResetSnapshotBudget();
            }
            ReelNet.RequestSnapshot(log);
            return;
        }

        string generation = "net|" + id.Scene + "|" + epoch + "|" + rev;
        if (_spawnGeneration == generation) return;

        int orphans = OrphanBoothCount(reels);
        if (orphans > 0)
        {
            if (Time.unscaledTime >= _nextOrphanLog)
            {
                _nextOrphanLog = Time.unscaledTime + 2f;
                log.LogInfo($"[reel:spawn] map='{id.Key}' orphans={orphans}: waiting for the previous booths to go");
            }
            return;
        }

        // Rebuild the client's machines from the host's layout.
        for (int i = _auto.Count - 1; i >= 0; i--)
        {
            var old = _auto[i];
            _auto.RemoveAt(i);
            if (old != null) { try { old.Dispose(); } catch { } }
            reels.Remove(old);
        }

        var createdList = new List<LobbyPropRuntime>(machines.Count);
        int spawned = 0, failed = 0;
        var names = new StringBuilder();
        for (int i = 0; i < machines.Count; i++)
        {
            var m = machines[i];
            var created = LobbyPropRuntime.CreateAt(log, m.Position, m.Yaw);
            if (created == null)
            {
                failed++;
                log.LogWarning($"[reel:spawn] failed to create host machine '{m.Id}' map='{id.Key}' {V3(m.Position)}");
                continue;
            }
            created.PlacementName = m.Id;
            created.MachineId = m.Id;
            created.IsMapSpawn = true;
            created.RequestOwner = false;
            _auto.Add(created);
            reels.Add(created);
            createdList.Add(created);
            spawned++;
            if (names.Length > 0) names.Append(',');
            names.Append(m.Id);
            log.LogInfo($"[reel:spawn] '{m.Id}' map='{id.Key}' source=host epoch={epoch} rev={rev} " +
                        $"pos=({created.Anchor.x:F2},{created.Anchor.y:F3},{created.Anchor.z:F2}) yaw={created.Yaw:F1} " +
                        $"machine='{created.MachineId}'");
        }

        _spawnGeneration = generation;
        int localFile = CountOn(id.Key);
        log.LogInfo($"[reel:spawn] map='{id.Key}' authority=host epoch={epoch} rev={rev} spawned={spawned} " +
                    $"failed={failed} live={_auto.Count} names=[{names}] " +
                    $"local-file-entries-not-used={localFile} (a client renders the host's anchors)");
    }

    private static string V3(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    // ---- booth lookup for the network path ------------------------------------------------

    internal static LobbyPropRuntime FindBooth(string machineId)
    {
        if (string.IsNullOrEmpty(machineId)) return null;
        for (int i = 0; i < _auto.Count; i++)
        {
            var b = _auto[i];
            if (b == null || b.Root == null) continue;
            if (string.Equals(b.MachineId, machineId, StringComparison.Ordinal)) return b;
        }
        return null;
    }

    internal static LobbyPropRuntime BoothIndexed(int oneBased)
    {
        int n = 0;
        for (int i = 0; i < _auto.Count; i++)
        {
            var b = _auto[i];
            if (b == null || b.Root == null) continue;
            if (++n == oneBased) return b;
        }
        return null;
    }

    internal static LobbyPropRuntime BoothForSpin(uint spinId)
    {
        for (int i = 0; i < _auto.Count; i++)
        {
            var b = _auto[i];
            if (b != null && b.SpinId == spinId) return b;
        }
        return null;
    }

    internal static List<LobbyPropRuntime> AutoBooths()
    {
        var list = new List<LobbyPropRuntime>(_auto.Count);
        for (int i = 0; i < _auto.Count; i++)
            if (_auto[i] != null && _auto[i].Root != null) list.Add(_auto[i]);
        return list;
    }

    internal static string BoothIds()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _auto.Count; i++)
        {
            var b = _auto[i];
            if (b == null || b.Root == null) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(b.MachineId);
        }
        return sb.ToString();
    }

    // Counts our booth roots in the active scene (a manual F4 booth included). The spawn log prints
    // it so a reload can be checked against the object count, not only against our own list.
    private static int SceneBoothCount()
    {
        int n = 0;
        try
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                if (roots[i] != null && roots[i].name == LobbyPropFactory.RootName) n++;
        }
        catch { }
        return n;
    }

    // Scene booth roots that are not referenced by the live list: leftovers of a generation whose
    // Dispose has not run yet (or objects someone else created).
    private static int OrphanBoothCount(List<LobbyPropRuntime> reels)
    {
        int n = 0;
        try
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var go = roots[i];
                if (go == null || go.name != LobbyPropFactory.RootName) continue;
                bool ours = false;
                for (int k = 0; k < reels.Count; k++)
                    if (reels[k] != null && reels[k].Root == go) { ours = true; break; }
                if (!ours) n++;
            }
        }
        catch { }
        return n;
    }

    private static string ColliderText(LobbyPropRuntime rt)
    {
        try
        {
            var c = rt.Trigger;
            return c != null ? ReelInteraction.V(c.bounds.center) + "/" + ReelInteraction.V(c.bounds.size) : "-";
        }
        catch { return "-"; }
    }

    private static string LookText(LobbyPropRuntime rt)
    {
        try { return ReelInteraction.V(rt.LookCentre); } catch { return "-"; }
    }

    private static string BoundsText(LobbyPropRuntime rt)
    {
        try { return rt.Bounds != null ? ReelInteraction.V(rt.Bounds.center) + "/" + ReelInteraction.V(rt.Bounds.size) : "-"; }
        catch { return "-"; }
    }

    // Placement lines in `reel.request` when there is no booth to poll the file (a map run). With a
    // live booth, LobbyPropRuntime.PollReelRequest owns the file, so this is a no-op.
    internal static void PollRequest(IHostContext log, LobbyPropRuntime prop)
    {
        if (prop != null && prop.Root != null) return;
        if (Time.unscaledTime < _nextRequestPoll) return;
        _nextRequestPoll = Time.unscaledTime + 0.35f;
        string dir = log.ScreensDir;
        if (string.IsNullOrEmpty(dir)) return;
        string path = Path.Combine(dir, RequestFile);
        if (!File.Exists(path)) return;
        string text;
        try { text = File.ReadAllText(path); } catch { return; }
        // Test hook: `startmap` has to be answered here, because the lobby holds no booth (the reel
        // is map-only) and LobbyPropRuntime can only poll the file through a booth. Without this the
        // map-only reel could not be driven on a solo, unlocked-desktop-free test run at all.
        if (text.IndexOf("startmap", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try { File.Delete(path); } catch { }
            log.LogInfo("[reel:place] request startmap (this scene has no booth to poll the file)");
            ReelLogic.StartMap(log);
            return;
        }
        string op = PlacementLine(text);
        if (op == null) return;                       // not ours: leave it for the booth path
        try { File.Delete(path); } catch { }
        _lastRequest = op;
        log.LogInfo($"[reel:place] request {op.Replace("\r", " ").Replace("\n", " | ")}");
        Handle(log, prop, text, op);
    }

    // Handles the placement lines of a request body. Returns true when the body contained one.
    internal static bool HandleRequest(IHostContext log, LobbyPropRuntime prop, string text)
    {
        string op = PlacementLine(text);
        if (op == null) return false;
        _lastRequest = op;
        Handle(log, prop, text, op);
        return true;
    }

    private static void Handle(IHostContext log, LobbyPropRuntime prop, string text, string op)
    {
        string name = FirstToken(LineValue(text, "save") ?? LineValue(text, "load") ?? LineValue(text, "delete") ?? "");
        bool overwrite = (LineValue(text, "overwrite") ?? "") == "1" || HasToken(text, "overwrite");
        bool isDefault = (LineValue(text, "default") ?? "") == "1" || HasToken(text, "default");
        if (HasLine(text, "list")) List(log);
        else if (LineValue(text, "save") != null) Save(log, prop, name, overwrite, isDefault);
        else if (LineValue(text, "load") != null)
        {
            // A map run spawns its saved anchors on entry, so there is normally a booth to move.
            // With none, say so instead of creating an untracked booth outside the spawner: the
            // lobby is map-only and never gets one.
            if (!Load(log, prop, name) && (prop == null || prop.Root == null))
                log?.LogWarning($"[reel:place] load '{name}': no booth to move and this scene may not hold one " +
                                "(the reel is spawned per map)");
        }
        else if (LineValue(text, "delete") != null) Delete(log, name);
        else if (HasLine(text, "maps")) Maps(log);
    }

    // ---- map data listing (diagnostic) ---------------------------------------------------

    private static void Maps(IHostContext log)
    {
        var id = Current(log);
        log.LogInfo($"[reel:place] current map key='{id.Key}' scene='{id.Scene}' path='{id.ScenePath}' buildIndex={id.BuildIndex} handle={id.Handle}");
        var maps = ReelInteraction.All<GameLobby.GameMapData>();
        for (int i = 0; i < maps.Length; i++)
        {
            var m = maps[i];
            if (m == null) continue;
            var sceneRef = ReelInteraction.F(m, "GameMapScene");
            log.LogInfo($"[reel:place] mapData[{i}] '{m.name}' sceneRef='{ReadString(sceneRef, "Name")}' path='{ReadString(sceneRef, "Path")}' " +
                        $"buildIndex='{ReadString(sceneRef, "BuildIndex")}' state='{ReadString(sceneRef, "State")}'");
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    private static Entry Find(string mapKey, string name)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].MapKey == mapKey && string.Equals(_entries[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return _entries[i];
        return null;
    }

    private static Entry FindAny(string name)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (string.Equals(_entries[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _entries[i];
        return null;
    }

    private static List<Entry> AllOn(string mapKey)
    {
        var list = new List<Entry>();
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].MapKey == mapKey) list.Add(_entries[i]);
        return list;
    }

    // Map compatibility: the entry's map must be the map that is loaded now. The scene path/build
    // index recorded with an entry are checked when present, so a renamed scene asset is caught.
    private static bool Compatible(IHostContext log, MapIdentity id, Entry e)
    {
        if (e.MapKey != id.Key)
        {
            log.LogWarning($"[reel:place] map mismatch: placement '{e.Name}' is on '{e.MapKey}', active map is '{id.Key}'");
            return false;
        }
        if (!Finite(e.Position.x) || !Finite(e.Position.y) || !Finite(e.Position.z) || !Finite(e.Yaw))
        {
            log.LogWarning($"[reel:place] placement '{e.Name}' has a non-finite pose; refused");
            return false;
        }
        return true;
    }

    private static string MapsSummary()
    {
        var sb = new StringBuilder();
        var keys = new List<string>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (keys.Contains(_entries[i].MapKey)) continue;
            keys.Add(_entries[i].MapKey);
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(_entries[i].MapKey).Append('(').Append(CountOn(_entries[i].MapKey)).Append(')');
        }
        return sb.ToString();
    }

    private static int CountOn(string mapKey)
    {
        int n = 0;
        for (int i = 0; i < _entries.Count; i++) if (_entries[i].MapKey == mapKey) n++;
        return n;
    }

    private static string SceneOf(string mapKey)
    {
        int bar = mapKey.IndexOf('|');
        return bar >= 0 ? mapKey.Substring(bar + 1) : mapKey.StartsWith("scene:") ? mapKey.Substring(6) : "";
    }

    private static string MapDataOf(string mapKey)
    {
        int bar = mapKey.IndexOf('|');
        return bar > 0 ? mapKey.Substring(0, bar) : "";
    }

    // `save=necropolis-1 default=1` and `save=necropolis-1 default` both name the placement by the
    // first token and take the flags from the rest of the request body.
    private static string FirstToken(string v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        int sp = v.IndexOfAny(new[] { ' ', '\t' });
        return (sp < 0 ? v : v.Substring(0, sp)).Trim();
    }

    private static bool HasToken(string text, string token)
    {
        foreach (var raw in Split(text))
        {
            foreach (var part in raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (part.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                    part.StartsWith(token + "=", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string PlacementLine(string text)
    {
        foreach (var raw in Split(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            string key = eq > 0 ? line.Substring(0, eq).Trim() : line;
            if (key.Equals("save", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("load", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("list", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("maps", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        return null;
    }

    private static bool HasLine(string text, string key)
    {
        foreach (var raw in Split(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            string k = eq > 0 ? line.Substring(0, eq).Trim() : line;
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string LineValue(string text, string key)
    {
        foreach (var raw in Split(text))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line.Substring(eq + 1).Trim();
        }
        return null;
    }

    private static string[] Split(string text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Str(Dictionary<string, object> d, string key)
        => d != null && d.TryGetValue(key, out var v) ? v as string : null;

    private static bool Bool(Dictionary<string, object> d, string key, bool fallback)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return fallback;
        if (v is bool b) return b;
        if (v is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
        if (v is double dd) return Math.Abs(dd) > 0.5;
        return fallback;
    }

    private static double Num(Dictionary<string, object> d, string key, double fallback)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return fallback;
        if (v is double dd) return dd;
        if (v is long l) return l;
        if (v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p)) return p;
        return fallback;
    }

    private static float ToFloat(object o)
    {
        try
        {
            if (o is double d) return (float)d;
            if (o is long l) return l;
            if (o is string s) return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch { }
        return float.NaN;
    }

    private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    private static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

    private static string J(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
