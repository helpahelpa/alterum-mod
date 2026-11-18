using System;
using System.Collections.Generic;
using System.Text;
using AlterumModelSwap.Contracts;
using Gameplay.HandheldSpawner;
using Gameplay.SpellSystem;
using InventorySystem.Core;
using InventorySystem.Core.Item;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AlterumModelSwap.Hot;

// Which items this map can actually spawn — the reel's item cards are restricted to these.
//
// The set is not guessed from asset names: it is the live map's own spawn table, read from the
// objects the game spawns with (see SpawnProbe for the component-level dump):
//
//   world placement  HandheldSpawnConfigGetter.GetConfigs() -> HandheldSpawnConfig.Item, i.e. every
//                    InventoryItemData the selected HandheldSpawnPreset lists for this scene.
//   chest            the same configs, kept only where the item's ItemPlacersConfig allows a placer
//                    whose PlacerType is a chest (probability > 0).
//   altered chest    the filler's own answer: InventoryRandomItemsFiller._itemsProvider
//                    .GetItemsForInventoryNonAlloc(...) on the altered chest's inventory. Read-only;
//                    it fills our list and inserts nothing.
//
// Altered chests are ordinary chests carrying the AlteredChest marker component (it holds no item
// list of its own), so their item table is the chest table; the marker only adds the altered-quality
// attribute / highlight. If the filler answered nothing, the altered list stays empty and the union
// is unaffected — that is logged rather than papered over.
//
// Lobby: there is no map and no spawn table, so the pool falls all the way back to every usable row
// of ReelPool (the pre-filter behaviour). The reel is scenery there and cannot pay out.
//
// The result is cached per scene name; a spin never rescans the scene.
internal static class ReelEligible
{
    internal static readonly List<string> World = new List<string>();
    internal static readonly List<string> Chest = new List<string>();
    internal static readonly List<string> Altered = new List<string>();
    internal static readonly List<string> Union = new List<string>();
    internal static readonly List<string> AlteredFiller = new List<string>();
    internal static readonly List<string> OutsidePool = new List<string>();

    internal static string Scene = "";
    internal static string Source = "not built";   // map | lobby-fallback | empty-fallback
    internal static string Summary = "not built";
    private static readonly List<int> _indices = new List<int>();

    // Indices into ReelPool.All that an item card may name. Read by ReelDeck at compose time.
    internal static int[] Indices => _indices.ToArray();
    internal static int Count => _indices.Count;

    internal static void Tick(IHostContext log)
    {
        string scene = "";
        try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? ""; } catch { }
        if (scene.Length == 0 || string.Equals(scene, Scene, StringComparison.OrdinalIgnoreCase)) return;
        try { Refresh(log, scene); }
        catch (Exception e) { log.LogWarning($"[reel:pool] refresh threw: {e.Message}"); }
    }

    internal static void Refresh(IHostContext log, string scene)
    {
        Scene = scene;
        World.Clear(); Chest.Clear(); Altered.Clear(); Union.Clear(); OutsidePool.Clear();
        AlteredFiller.Clear();

        bool map = ReelLogic.IsMapRun(out _);
        var world = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var chest = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var altered = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var chestPlacers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ipcSeen = new List<string>();

        int alteredGetters = 0, normalGetters = 0;
        if (map)
        {
            var getters = SpawnProbe.All<HandheldSpawnConfigGetter>();
            for (int i = 0; i < getters.Length; i++)
            {
                var g = getters[i];
                if (g == null) continue;
                HandheldSpawnConfig[] cfgs = null;
                try { cfgs = g.GetConfigs(); } catch (Exception e) { log.LogWarning($"[reel:pool] GetConfigs threw: {e.Message}"); }
                if (cfgs == null) continue;
                // The scene splits its tables by branch: ".../Location Handhelds Spawner" is the
                // normal run, ".../Altered Handhelds Spawner" is the altered (cursed) variant of the
                // same map, with its own *_Altered_SpawnPreset item groups.
                string gName = SpawnProbe.PathOf(g);
                bool alteredTable = gName.IndexOf("Altered Handhelds Spawner", StringComparison.OrdinalIgnoreCase) >= 0;
                if (alteredTable) alteredGetters++; else normalGetters++;
                if (!ipcSeen.Contains(gName)) ipcSeen.Add(gName);
                for (int c = 0; c < cfgs.Length; c++)
                {
                    var cfg = cfgs[c];
                    if (cfg == null) continue;
                    string id = SpawnProbe.IdOf(cfg.Item);
                    if (string.IsNullOrEmpty(id)) continue;
                    if (alteredTable) altered.Add(id); else world.Add(id);
                    bool inChest = false;
                    try
                    {
                        var arr = cfg.ItemPlacersConfig != null ? cfg.ItemPlacersConfig.PlacersConfigs : null;
                        if (arr != null)
                        {
                            for (int k = 0; k < arr.Length; k++)
                            {
                                var pc = arr[k];
                                if (pc == null || pc.Probability <= 0f) continue;
                                string pt = SpawnProbe.N(pc.Type);
                                if (pt.IndexOf("chest", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                inChest = true;
                                chestPlacers.Add(pt);
                            }
                        }
                    }
                    catch { }
                    if (inChest) chest.Add(id);
                }
            }

            // The filler's own answer, for the record: in the Lair scene the only filler is
            // "Game Global/Inventory Filler" and it has no parent inventory, so it answers nothing.
            // Kept as a diagnostic because it would be a source list if a map ever ships one.
            var fillers = SpawnProbe.All<InventoryRandomItemsFiller>();
            for (int i = 0; i < fillers.Length; i++)
            {
                var f = fillers[i];
                if (f == null) continue;
                var prov = f._itemsProvider;
                if (prov == null) continue;
                Inventory inv = null;
                byte loc = 0;
                try
                {
                    var dbg = f.GetComponentsInParent<InventoryRandomItemsFillerDebugger>(true);
                    if (dbg != null && dbg.Length > 0 && dbg[0] != null)
                    {
                        loc = dbg[0]._locationIndex;
                        inv = dbg[0]._inventoryComponent != null ? dbg[0]._inventoryComponent.Inventory : null;
                    }
                    if (inv == null)
                    {
                        var comps = f.GetComponentsInParent<InventoryComponent>(true);
                        if (comps != null && comps.Length > 0) inv = comps[0].Inventory;
                    }
                }
                catch { }
                if (inv == null) continue;
                try
                {
                    var list = new Il2CppSystem.Collections.Generic.List<NetworkInventoryItemWrapper>();
                    prov.GetItemsForInventoryNonAlloc(list, inv, loc);
                    for (int k = 0; k < list.Count; k++)
                    {
                        string id = SpawnProbe.WrapId(list[k]);
                        if (string.IsNullOrEmpty(id) || id == "?") continue;
                        AlteredFiller.Add(id);
                        altered.Add(id);
                    }
                }
                catch { }
            }
        }

        var union = new SortedSet<string>(world, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(chest);
        union.UnionWith(altered);

        World.AddRange(world);
        Chest.AddRange(chest);
        Altered.AddRange(altered);
        Union.AddRange(union);

        // Map ids onto rows of the generated pool. Rune/lantern are weight 0 there, so the
        // exclusion survives this mapping without a special case.
        _indices.Clear();
        for (int i = 0; i < ReelPool.All.Length; i++)
        {
            var it = ReelPool.All[i];
            if (!it.Usable) continue;
            if (map && !union.Contains(it.Id)) continue;
            _indices.Add(i);
        }
        foreach (var id in union) if (ReelPool.IndexOf(id) < 0) OutsidePool.Add(id);

        Source = !map ? "lobby-fallback" : (Union.Count == 0 ? "empty-fallback" : "map");
        if (Source == "empty-fallback")
        {
            // Nothing readable: do not silently keep a whole-map pool, and do not empty the deck.
            // Fall back to every usable row and say so in the log.
            _indices.Clear();
            for (int i = 0; i < ReelPool.All.Length; i++) if (ReelPool.All[i].Usable) _indices.Add(i);
        }

        ReelDeck.AllowedItems = _indices.ToArray();

        Summary = $"source={Source} world={World.Count} chest={Chest.Count} altered={Altered.Count} " +
                  $"union={Union.Count} pool={Count}/{ReelPool.UsableCount()} " +
                  $"tables(normal={normalGetters} altered={alteredGetters}) fillerIds={AlteredFiller.Count} " +
                  $"chestPlacers=[{string.Join(" ", chestPlacers)}]";
        log.LogInfo($"[reel:pool] scene='{scene}' {Summary}");
        if (!map) return;
        log.LogInfo($"[reel:pool] world(" + World.Count + ")=[" + string.Join(" ", World) + "]");
        log.LogInfo($"[reel:pool] chest(" + Chest.Count + ")=[" + string.Join(" ", Chest) + "]");
        log.LogInfo($"[reel:pool] altered(" + Altered.Count + ")=[" + string.Join(" ", Altered) + "]");
        log.LogInfo($"[reel:pool] union(" + Union.Count + ")=[" + string.Join(" ", Union) + "]");
        log.LogInfo($"[reel:pool] outsidePool=" + OutsidePool.Count + " [" + string.Join(" ", OutsidePool) + "]");
        if (AlteredFiller.Count > 0)
            log.LogInfo($"[reel:pool] filler(" + AlteredFiller.Count + ")=[" + string.Join(" ", AlteredFiller) + "]");
    }

    // The compose-time census: the deck a spin would build right now, and a check that every item
    // card names an id inside the eligible union.
    internal static void LogDeck(IHostContext log, string tag)
    {
        var deck = ReelDeck.Compose(ReelDeck.Cells, 12345);
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        for (int i = 0; i < deck.Length; i++)
        {
            if (deck[i].Type != ReelCardType.Item) continue;
            string id = ReelDeck.IdOf(deck[i]);
            if (id.Length == 0) { bad.Add("empty"); continue; }
            ids.Add(id);
            if (Source == "map" && !Union.Contains(id)) bad.Add(id);
            var row = ReelPool.IndexOf(id) >= 0 ? ReelPool.All[ReelPool.IndexOf(id)] : null;
            if (row != null && row.Excluded) bad.Add(id + ":excluded");
        }
        log.LogInfo($"[{tag}] {Summary}");
        log.LogInfo($"[{tag}] deck " + ReelDeck.Census(deck) + " distinctItems=" + ids.Count +
                    " ids=[" + string.Join(" ", ids) + "] outsideUnion=" + bad.Count +
                    (bad.Count > 0 ? " [" + string.Join(" ", bad) + "]" : ""));
    }
}
