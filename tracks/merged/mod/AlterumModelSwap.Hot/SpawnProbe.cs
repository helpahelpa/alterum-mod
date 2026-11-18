using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AlterumModelSwap.Contracts;
using GameLobby;
using Gameplay.HandheldSpawner;
using Gameplay.ItemCollections;
using Gameplay.SpellSystem;
using InventorySystem.Core;
using InventorySystem.Core.Item;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Where the "what can spawn here" truth lives, read from the live scene.
//
// The asset-side attempt (tracks/merged/notes/item-extraction-log.md) could not read chest/shop
// membership out of raw bytes, so this reads the runtime objects the game itself spawns with:
//
//   HandheldSpawnConfigGetter        per-map spawn table. GetConfigs() gives one
//                                    {InventoryItemData Item, ItemPlacersConfig} per item the
//                                    scene may place; the config's placer table says where.
//   HandheldSpawnConfigPresetsGetter HandheldSpawnPresetContainer (selected HandheldSpawnPreset's
//                                    Items + the ItemPlacersConfig of placer probabilities).
//   HandheldSpawnPlacer              a live spawn place: placerType names what it is.
//   InventoryRandomItemsFiller       the inventory filler used by chests/altered chests. Its
//                                    ItemsProvider.GetItemsForInventoryNonAlloc(list, inv, loc)
//                                    fills OUR list with the wrappers it would insert (read-only).
//   Collection / CollectionItemWrapper  "Collection [Tier Two Chests]" groups; each item exposes a
//                                    Key and its Properties.
//   AlteredChest                     a MARKER on an ordinary chest: no item list of its own.
//
// Unattended trigger: <ScreensDir>/spawn.request, deleted on pickup. Lines:
//   probe            dump every source above for the current scene (+ the eligible set/deck)
//   sets | deck      dump the three id lists, their union and the reel's deck census
//   maps             list the lobby map book (index, name, scene)
//   map=<substr>     select a map by name substring (lobby only)
//   start            start the transition to the selected map (lobby only)
internal static class SpawnProbe
{
    internal const string RequestFile = "spawn.request";
    private const float PollSeconds = 0.5f;
    private static float _nextPoll;
    private static bool _clientRefused;

    internal static void Tick(IHostContext log)
    {
        if (log == null) return;
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + PollSeconds;
        string path;
        try { path = Path.Combine(log.ScreensDir, RequestFile); }
        catch { return; }
        if (!File.Exists(path)) return;
        string body = "";
        try { body = File.ReadAllText(path); } catch { }
        try { File.Delete(path); } catch { }
        try { Dispatch(log, body); }
        catch (Exception e) { log.LogError($"[spawn] probe threw: {e}"); }
        try { TickAuto(log); }
        catch (Exception e) { log.LogError($"[spawn] auto threw: {e}"); }
    }

    private static void Dispatch(IHostContext log, string body)
    {
        string low = (body ?? "").ToLowerInvariant();
        // `map=` / `start` / `lobby` / `auto` write server-owned NetworkVariables
        // (MapBookNetworkMapSelector.SelectedMapIndex, PlayersReadinessController.CanStartExpedition)
        // and start the scene transition on the server. A joining client attempting them logs the
        // same "Write permissions (Server) ... not allowed" error the one-player switch did, so the
        // write directives are refused here once; the read-only probe still answers.
        bool writes = low.Contains("auto") || low.Contains("start") || low.Contains("lobby") ||
                      ValueOf(body, "map") != null;
        if (writes && !NetRole.IsServer)
        {
            if (!_clientRefused)
            {
                _clientRefused = true;
                log.LogWarning($"[spawn] this instance is a client ({NetRole.Word()}): map select/start/lobby are " +
                               "server-owned, so the request's writes are refused (probe and report still run)");
            }
            Probe(log);
            ReportEligible(log);
            return;
        }
        if (low.Contains("auto"))
        {
            // One request for the whole unattended map run: dump here, select + start the map, then
            // dump again as soon as the map scene is up. Instance A's hot dll can be replaced by the
            // other agent's harness at any moment, so fewer round-trips is fewer chances to lose.
            string want = ValueOf(body, "map");
            Probe(log);
            ReportEligible(log);
            if (want != null) SelectMap(log, want);
            if (low.Contains("start"))
            {
                StartSelectedMap(log);
                _autoArmed = true;
                _autoScene = "";
                _autoAt = Time.unscaledTime + 20f;
                log.LogInfo("[spawn] auto armed: map probe fires once the map scene is up");
            }
            return;
        }
        string mapName = ValueOf(body, "map");
        if (mapName != null) { SelectMap(log, mapName); if (!low.Contains("start")) return; }
        if (low.Contains("maps")) { ListMaps(log); return; }
        if (low.Contains("lobby")) { GoLobby(log); return; }
        if (low.Contains("start")) { StartSelectedMap(log); return; }
        if (low.Contains("deck") || low.Contains("sets")) { ReportEligible(log); return; }
        Probe(log);
        ReportEligible(log);
    }

    // Armed by an `auto` request: one dump per map scene, no further requests needed.
    private static bool _autoArmed;
    private static string _autoScene = "";
    private static float _autoAt;

    private static void TickAuto(IHostContext log)
    {
        if (!_autoArmed || Time.unscaledTime < _autoAt) return;
        string scene = "";
        try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
        if (!ReelLogic.IsMapRun(out _)) return;
        if (string.Equals(scene, _autoScene, StringComparison.OrdinalIgnoreCase)) return;
        _autoScene = scene;
        _autoArmed = false;
        log.LogInfo($"[spawn] auto map probe scene='{scene}'");
        Probe(log);
        ReportEligible(log);
    }

    internal static string ValueOf(string body, string key)
    {
        if (body == null) return null;
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim();
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (!string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            return t.Substring(eq + 1).Trim();
        }
        return null;
    }

    // ---------------------------------------------------------------- sources

    private static void Probe(IHostContext log)
    {
        string scene = "";
        try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
        log.LogInfo($"[spawn] ==== probe scene='{scene}' map={ReelLogic.IsMapRun(out _)} ====");
        DumpConfigGetters(log);
        DumpPlacers(log);
        DumpFillers(log);
        DumpCollections(log);
        DumpPlacerConfigAssets(log);
        DumpAltered(log);
    }

    private static void DumpConfigGetters(IHostContext log)
    {
        HandheldSpawnConfigGetter[] getters = All<HandheldSpawnConfigGetter>();
        log.LogInfo($"[spawn:cfg] getters={getters.Length}");
        for (int i = 0; i < getters.Length; i++)
        {
            var g = getters[i];
            if (g == null) continue;
            string path = PathOf(g);
            string extra = "";
            var presets = g.TryCast<HandheldSpawnConfigPresetsGetter>();
            if (presets != null)
            {
                try
                {
                    var sel = presets.TryCast<HandheldSpawnConfigPresetsSerializeFieldGetter>();
                    var preset = sel != null ? sel._selectedPreset : null;
                    var container = presets._presets;
                    extra += $" preset='{(preset != null ? N(preset) : "null")}'";
                    if (preset != null) extra += " presetItems=[" + Ids(preset.Items) + "]";
                    if (container != null) extra += $" probAsset='{N(container)}' placers=[" + PlacerSummary(container.Probabilities) + "]";
                }
                catch (Exception e) { extra += $" presetErr='{e.Message}'"; }
            }
            log.LogInfo($"[spawn:cfg] '{path}' type={g.GetType().Name}{extra}");

            HandheldSpawnConfig[] cfgs = null;
            try { cfgs = g.GetConfigs(); } catch (Exception e) { log.LogWarning($"[spawn:cfg]   GetConfigs threw: {e.Message}"); }
            if (cfgs == null) continue;
            log.LogInfo($"[spawn:cfg]   configs={cfgs.Length}");
            var lines = new StringBuilder();
            int shown = 0;
            for (int c = 0; c < cfgs.Length; c++)
            {
                var cfg = cfgs[c];
                if (cfg == null) continue;
                string id = IdOf(cfg.Item);
                if (id == null) continue;
                string ipc = cfg.ItemPlacersConfig != null ? N(cfg.ItemPlacersConfig) : "null";
                if (shown++ > 0) lines.Append(' ');
                lines.Append(id).Append('/').Append(cfg.BaseAmount).Append('/').Append(ipc);
                if (lines.Length > 1500) { log.LogInfo($"[spawn:cfg]   items(id/base/ipc)=[{lines}]"); lines.Clear(); shown = 0; }
            }
            if (lines.Length > 0) log.LogInfo($"[spawn:cfg]   items(id/base/ipc)=[{lines}]");
        }
    }

    internal static string PlacerSummary(ItemPlacersConfig cfg)
    {
        if (cfg == null) return "";
        try
        {
            var arr = cfg.PlacersConfigs;
            if (arr == null) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                var pc = arr[i];
                if (pc == null) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(N(pc.Type)).Append(':').Append(pc.Probability.ToString("0.###"))
                  .Append("x").Append(pc.MaxItems);
            }
            return sb.ToString();
        }
        catch (Exception e) { return "err:" + e.Message; }
    }

    private static void DumpPlacers(IHostContext log)
    {
        HandheldSpawnPlacer[] placers = All<HandheldSpawnPlacer>();
        int kept = 0;
        var byType = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < placers.Length; i++)
        {
            var p = placers[i];
            if (p == null) continue;
            string path = PathOf(p);
            if (path.Length == 0) continue;   // prefab assets, not scene objects
            kept++;
            string type = N(p.placerType);
            byType.TryGetValue(type, out int have);
            byType[type] = have + 1;
            if (kept > 40) continue;
            log.LogInfo($"[spawn:placer] '{path}' type={p.GetType().Name} placerType='{type}' " +
                        $"guaranteed={p.GuaranteedSpawnCount} weight={p.SpawnWeight} spawned={p.SpawnedObjectsCount}");
        }
        var sb = new StringBuilder();
        foreach (var kv in byType) { if (sb.Length > 0) sb.Append(' '); sb.Append(kv.Key).Append(':').Append(kv.Value); }
        log.LogInfo($"[spawn:placer] total={placers.Length} inScene={kept} byPlacerType=[{sb}]");
    }

    private static void DumpFillers(IHostContext log)
    {
        InventoryRandomItemsFiller[] fillers = All<InventoryRandomItemsFiller>();
        log.LogInfo($"[spawn:filler] fillers={fillers.Length}");
        for (int i = 0; i < fillers.Length; i++)
        {
            var f = fillers[i];
            if (f == null) continue;
            string path = PathOf(f);
            var prov = f._itemsProvider;
            string provPath = prov != null ? PathOf(prov) : "";
            string provType = prov != null ? prov.GetType().Name : "null";
            log.LogInfo($"[spawn:filler] '{path}' provider={provType} '{provPath}' bias={f._randomSlotsCountBias}");

            var fromWorld = prov != null ? prov.TryCast<FromWorldNotInInventoriesItemsProvider>() : null;
            if (fromWorld != null)
            {
                try { log.LogInfo($"[spawn:filler]   exclude=[" + Ids(fromWorld._excludeItems) + "]"); } catch { }
            }

            Inventory inventory = null;
            byte loc = 0;
            try
            {
                var dbg = f.GetComponentsInParent<InventoryRandomItemsFillerDebugger>(true);
                if (dbg != null && dbg.Length > 0 && dbg[0] != null)
                {
                    loc = dbg[0]._locationIndex;
                    inventory = dbg[0]._inventoryComponent != null ? dbg[0]._inventoryComponent.Inventory : null;
                    log.LogInfo($"[spawn:filler]   debugger='{PathOf(dbg[0])}' loc={loc} invComp={(dbg[0]._inventoryComponent != null)}");
                }
                if (inventory == null)
                {
                    var comps = f.GetComponentsInParent<InventoryComponent>(true);
                    if (comps != null && comps.Length > 0) inventory = comps[0].Inventory;
                }
            }
            catch { }
            log.LogInfo($"[spawn:filler]   inventory={(inventory != null)} locationIndex={loc}");
            if (prov == null || inventory == null) continue;
            try
            {
                var list = new Il2CppSystem.Collections.Generic.List<NetworkInventoryItemWrapper>();
                prov.GetItemsForInventoryNonAlloc(list, inventory, loc);
                var ids = new StringBuilder();
                for (int k = 0; k < list.Count; k++)
                {
                    if (k > 0) ids.Append(' ');
                    ids.Append(WrapId(list[k]));
                }
                log.LogInfo($"[spawn:filler]   items={list.Count} [{ids}]");
            }
            catch (Exception e) { log.LogWarning($"[spawn:filler]   GetItemsForInventoryNonAlloc threw: {e.Message}"); }
        }
    }

    internal static string WrapId(NetworkInventoryItemWrapper w)
    {
        try
        {
            if (w == null) return "?";
            var data = w.InventoryItemData;
            return IdOf(data) ?? "?";
        }
        catch { return "?"; }
    }

    private static void DumpCollections(IHostContext log)
    {
        Collection[] colls = All<Collection>();
        log.LogInfo($"[spawn:coll] collections={colls.Length}");
        for (int i = 0; i < colls.Length; i++)
        {
            var c = colls[i];
            if (c == null) continue;
            string path = PathOf(c);
            var ids = new StringBuilder();
            var keys = new StringBuilder();
            int n = 0;
            try
            {
                var items = c.Items;
                n = items != null ? items.Length : 0;
                for (int k = 0; k < n; k++)
                {
                    var it = items[k];
                    if (it == null) continue;
                    string id = null, keyType = "?", keyName = "?";
                    try
                    {
                        var key = it.Key;
                        if (key != null)
                        {
                            keyType = key.GetType().Name;
                            var d = key.TryCast<InventoryItemData>();
                            if (d != null) { id = IdOf(d); keyName = id; }
                            else { var so = key.TryCast<UnityEngine.Object>(); keyName = so != null ? N(so) : key.ToString(); }
                        }
                    }
                    catch (Exception e) { keyType = "err:" + e.Message; }
                    if (id != null) { if (ids.Length > 0) ids.Append(' '); ids.Append(id); }
                    if (keys.Length > 0) keys.Append(' ');
                    keys.Append(keyType).Append('/').Append(keyName);
                }
            }
            catch (Exception e) { ids.Clear().Append("err:").Append(e.Message); }
            log.LogInfo($"[spawn:coll] '{path}' items={n} ids=[{ids}]");
            if (keys.Length > 0) log.LogInfo($"[spawn:coll]   keys=[{keys}]");
        }
    }

    private static void DumpPlacerConfigAssets(IHostContext log)
    {
        ItemPlacersConfig[] cfgs = All<ItemPlacersConfig>();
        log.LogInfo($"[spawn:ipc] assets={cfgs.Length}");
        for (int i = 0; i < cfgs.Length; i++)
        {
            var c = cfgs[i];
            if (c == null) continue;
            log.LogInfo($"[spawn:ipc] '{N(c)}' placers=[" + PlacerSummary(c) + "]");
        }
    }

    private static void DumpAltered(IHostContext log)
    {
        var arr = All<Gameplay.AlteredChests.AlteredChest>();
        var sb = new StringBuilder();
        int n = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            var a = arr[i];
            if (a == null) continue;
            string p = PathOf(a);
            if (p.Length == 0) continue;
            n++;
            if (sb.Length < 900) sb.Append(" '").Append(p).Append("'");
        }
        log.LogInfo($"[spawn:altered] markers={n}{sb}");
    }

    // ---------------------------------------------------------------- reel bridge

    private static void ReportEligible(IHostContext log)
    {
        string scene = "";
        try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
        if (!string.Equals(scene, ReelEligible.Scene, StringComparison.OrdinalIgnoreCase))
            ReelEligible.Refresh(log, scene);
        ReelEligible.LogDeck(log, "spawn:deck");
    }

    // ---------------------------------------------------------------- map book

    private static GameSceneStarter FindStarter()
    {
        var arr = SceneCache.Scan(Il2CppType.Of<GameSceneStarter>());
        for (int i = 0; i < arr.Length; i++)
        {
            var s = arr[i].TryCast<GameSceneStarter>();
            if (s == null) continue;
            var go = s.gameObject;
            if (go == null || !go.activeInHierarchy) continue;
            return s;
        }
        return null;
    }

    private static Gameplay.Books.MapBookNetworkMapSelector Selector(GameSceneStarter s)
    {
        try { return s._mapSelector != null ? s._mapSelector.TryCast<Gameplay.Books.MapBookNetworkMapSelector>() : null; }
        catch { return null; }
    }

    private static void ListMaps(IHostContext log)
    {
        var s = FindStarter();
        if (s == null) { log.LogWarning("[spawn:map] no active GameSceneStarter (not in the lobby)"); return; }
        var sel = Selector(s);
        if (sel == null) { log.LogWarning("[spawn:map] no MapBookNetworkMapSelector"); return; }
        log.LogInfo($"[spawn:map] selected={sel.SelectedMapIndex.Value} effective={sel.EffectiveMapIndex}");
        try
        {
            var list = sel._mapBook._mapsDatas.MapList;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null) continue;
                string scene = "", path = "";
                try { scene = SceneRefName(m); } catch { }
                try { path = SceneRefPath(m); } catch { }
                log.LogInfo($"[spawn:map] {i}: '{N(m)}' scene='{scene}' path='{path}' " +
                            $"min={m.MinRequiredPlayers} max={m.MaxRequiredPlayers}");
            }
        }
        catch (Exception e) { log.LogWarning($"[spawn:map] map list threw: {e.Message}"); }
    }

    // GameMapData.GameMapScene is an Eflatun.SceneReference, an assembly this mod does not
    // reference; read its two string properties reflectively rather than adding a reference just
    // for a log line.
    private static string SceneRefName(GameMapData m)
    {
        var sr = typeof(GameMapData).GetProperty("GameMapScene")?.GetValue(m);
        return sr?.GetType().GetProperty("Name")?.GetValue(sr) as string ?? "";
    }

    private static string SceneRefPath(GameMapData m)
    {
        var sr = typeof(GameMapData).GetProperty("GameMapScene")?.GetValue(m);
        return sr?.GetType().GetProperty("Path")?.GetValue(sr) as string ?? "";
    }

    private static void SelectMap(IHostContext log, string needle)
    {
        var s = FindStarter();
        if (s == null) { log.LogWarning("[spawn:map] no active GameSceneStarter (not in the lobby)"); return; }
        var sel = Selector(s);
        if (sel == null) { log.LogWarning("[spawn:map] no MapBookNetworkMapSelector"); return; }
        try
        {
            var list = sel._mapBook._mapsDatas.MapList;
            for (int i = 0; i < list.Count; i++)
            {
                var m = list[i];
                if (m == null) continue;
                string name = N(m);
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sel.SelectedMapIndex.Value = (sbyte)i;
                log.LogInfo($"[spawn:map] selected index={i} '{name}' (now {sel.SelectedMapIndex.Value})");
                return;
            }
            log.LogWarning($"[spawn:map] no map matching '{needle}'");
        }
        catch (Exception e) { log.LogWarning($"[spawn:map] select threw: {e.Message}"); }
    }

    // Return to the lobby through the game's own scene loader, so a second map can be reached
    // without an expedition result and without killing the process. Server-authoritative path
    // (we are the host): NetworkSceneLoader.LoadSceneOnServer, one client (us).
    private static void GoLobby(IHostContext log)
    {
        try
        {
            var clients = new Il2CppSystem.Collections.Generic.List<ulong>();
            // LoadSceneOnServer wants IReadOnlyList<ulong>; Il2CppSystem's List implements it, but
            // the interop List<> does not upcast, so build the interface reference explicitly.
            Il2CppSystem.Collections.Generic.IReadOnlyList<ulong> readOnly = null;
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm != null) clients.Add(nm.LocalClientId);
            }
            catch (Exception e) { log.LogWarning($"[spawn:map] LocalClientId: {e.Message}"); }
            var loader = SceneHandle.NetworkSceneLoader.Instance;
            if (loader == null) { log.LogWarning("[spawn:map] no NetworkSceneLoader instance"); return; }
            readOnly = clients.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyList<ulong>>();
            log.LogInfo($"[spawn:map] LoadSceneOnServer('Lobby') clients={clients.Count} iface={(readOnly != null)}");
            loader.LoadSceneOnServer("Lobby", readOnly, false, null);
        }
        catch (Exception e) { log.LogError($"[spawn:map] lobby load threw: {e}"); }
    }

    private static void StartSelectedMap(IHostContext log)
    {
        var s = FindStarter();
        if (s == null) { log.LogWarning("[spawn:map] no active GameSceneStarter (not in the lobby)"); return; }
        var sel = Selector(s);
        string chosen = "-";
        try { chosen = sel != null && sel.SelectedGameMap != null ? N(sel.SelectedGameMap) : "-"; } catch { }
        log.LogInfo($"[spawn:map] starting map='{chosen}' index={(sel != null ? sel.SelectedMapIndex.Value.ToString() : "-")}");
        // Readiness is the PLAYERS' gate, and this diagnostic must not manufacture it: no
        // MakePlayerReadyRpc and no assignment to CanStartExpedition (both were a bypass - the
        // countdown began with nobody on the pad). The controller's own recompute is the only
        // thing asked here, and a closed gate refuses the start with the reason.
        var ready = s._readinessController;
        bool canStart = ready != null && ready.CanStartExpedition != null && ready.CanStartExpedition.Value;
        if (!canStart && ready != null)
        {
            try { ready.UpdateCanStartExpedition(); } catch (Exception e) { log.LogWarning($"[spawn:map] readiness recompute: {e.Message}"); }
            try { canStart = ready.CanStartExpedition != null && ready.CanStartExpedition.Value; } catch { canStart = false; }
        }
        if (!canStart)
        {
            log.LogWarning($"[spawn:map] refused: CanStartExpedition is false " +
                           $"(ready={(ready != null ? ready.NumberOfReadyPlayers : -1)}/" +
                           $"{(ready != null ? ready.MinRequiredPlayers : -1)}); the pad and the countdown are the " +
                           "players' own path - this probe will not force readiness or the gate");
            return;
        }
        try
        {
            s.StartTransitionToGameScene();
            log.LogInfo("[spawn:map] StartTransitionToGameScene invoked");
        }
        catch (Exception e) { log.LogError($"[spawn:map] start threw: {e}"); }
    }

    // ---------------------------------------------------------------- helpers

    internal static T[] All<T>() where T : UnityEngine.Object
    {
        try
        {
            var arr = SceneCache.Scan(Il2CppType.Of<T>());
            var outArr = new List<T>(arr.Length);
            for (int i = 0; i < arr.Length; i++)
            {
                var v = arr[i] != null ? arr[i].TryCast<T>() : null;
                if (v != null) outArr.Add(v);
            }
            return outArr.ToArray();
        }
        catch { return new T[0]; }
    }

    internal static string N(UnityEngine.Object o)
    {
        try { return o != null ? o.name : "null"; } catch { return "?"; }
    }

    internal static string PathOf(Component c)
    {
        if (c == null) return "";
        try
        {
            var t = c.transform;
            if (t == null) return "";
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 40) { sb.Insert(0, p.name + "/"); p = p.parent; }
            string scene = t.gameObject.scene.name;
            if (scene.Length == 0) return "";
            return scene + ":" + sb;
        }
        catch { return ""; }
    }

    internal static string IdOf(InventoryItemData d)
    {
        try
        {
            var g = d != null ? d.GameItemData : null;
            return g != null ? g.ItemDataID : null;
        }
        catch { return null; }
    }

    internal static string Ids(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<InventoryItemData> arr)
    {
        if (arr == null) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < arr.Length; i++)
        {
            string id = IdOf(arr[i]);
            if (i > 0) sb.Append(' ');
            sb.Append(id ?? "?");
        }
        return sb.ToString();
    }

    internal static string Ids(Il2CppSystem.Collections.Generic.List<InventoryItemData> list)
    {
        if (list == null) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            string id = IdOf(list[i]);
            if (i > 0) sb.Append(' ');
            sb.Append(id ?? "?");
        }
        return sb.ToString();
    }
}
