# Item extraction log (Alterum)

Time-boxed ~70 min. Read-only on the install. Scripts kept in `~/assets/monorepo/alterum-data/work/items/`.

## What worked (and is reusable)

The blocker on the previous attempt was IL2CPP typeless `MonoBehaviour`s. Two tricks get around it without a dumper run:

1. **Manual base-header parse.** A typeless MonoBehaviour still serializes the standard base:
   `PPtr m_GameObject (4+8)`, `m_Enabled (4, aligned)`, `PPtr m_Script (4+8)`, `string m_Name (4+len, 4-aligned)`, then the class fields as raw bytes.
   `obj.get_raw_data()` gives those bytes; parse the prologue by hand. `obj.read()` raises `ValueError: Expected to read 56 bytes, but only read 32` on these objects — do not use it.
2. **`MonoScript` map.** `MonoScript` is an engine type and its name strings are plain at the front of the object: `m_Name`, `m_ClassName`, `m_Namespace`, `m_AssemblyName`. Build `(file, path_id) -> class` for all files, then resolve each `m_Script` PPtr using that file's `externals` list (`fid` is 1-based). Result: 9,253 scripts, 3,246 distinct classes across 276,821 MonoBehaviours.

Everything after that is byte-string and PPtr scanning of the field area, no field names required.

## Where the items are

- `Game.Items.ItemData` — 55 assets, `sharedassets0.assets`, `path_id` 20690–20744. This is the item definition pool. Script `ItemData` lives in `globalgamemanagers.assets:6380`.
- `InventoryItemData` — 55 components (`sharedassets0.assets`, 20460–20514), each pointing at exactly one `ItemData`. These sit on the handheld prefabs.
- `ItemDataGroup` — 10 assets. Groups of item datas; membership not decoded.
- Item-adjacent classes present, all inspected, **no** direct item PPtr found in raw bytes:
  - `RegisteredGameItemDataContainer` / `RegisteredInventoryItemDataContainer` (7 each; resources.assets, level3/4/8/9) — the registries; GO names `Game Item Datas Registrator` / `Inventory Item Datas Registrator`.
  - `GameItemDatasRegistrator` (strings: `Data/Items`), `InventoryItemDatasRegistrator` (strings: `Data/Gameplay/Inventory/Items/Data`) — addressables folders, not item lists.
  - `InventoryItemConfigCollection` (121, GO name `Configs`/`Inventory`) — per-prefab configs, no item PPtrs.
  - `Collection` (7, e.g. `Objects Selectors [Tier Two Chests]`) and `CollectionItemWrapper` (34, e.g. `Inventory Spawn Placer [King Chest]`) — chest-side grouping; refs are to `Collection`/key-holder objects, not items.
  - `InventoryRandomItemsFiller` (GO name `Inventory Filler`, present in level3/4/8) — the chest filler. **Weights deliberately not read.**
  - `ItemPlacersConfig` (13 assets, resources.assets ~874812–874909): `CandleOfRevelation`, `Crucifix`, `Flashlight` (x2), `HighTierHealthing`, `Rope`, `ExitSpellParts_PlacerSpawnProbabilities`, `FreeLootSpell`, `Spell`, `Key-Tier-1`, `Tier2Chest`, `WalkieTalkie`, `HQWeapon`, `LQWeapon`. These are the world-spawn placers. Names only were used; probabilities untouched.
  - `ManualItemData` (6: Altar of Exile, Altered, CurseAltar, Explorer, OblivionPit, VoicePlatform) — not items.
  - `Handheld` (69), `HandheldSpawnPlacer_Inventory` (143), `HarvestableSpawnPlace` (404), `MultiplayerInteractiveRandomSpawner` (107), `HandheldSpawnConfigPresetsSerializeFieldGetter` (438) — spawn machinery, not needed for the list.

## Icons

`item-icons` is a `Texture2D` in `sharedassets0.assets`; the `Sprite` objects in the same file carry the names used for matching (126 sprites there: `item-icons_*`, `item-*`, `spells-icons_*`, `weapons-*`, `effects-*`, `portraits-*`, `complex-shapes_*`). 51/55 items map to a sprite by name. Unmapped (4): ceremonial_candle, healthflask, ss_highjump, test_lagcomprensationgun — no sprite in this file; their icons, if any, live in another atlas.

Note: `item-vodka` exists as a sprite but no `ItemData` matches it — either an unused/removed item or an item defined elsewhere. Not investigated.

## Where it stalled / what to do next

- Item→chest / shop / reward membership: the registries do not store item PPtrs in their raw bytes. The likely route is the addressables catalog (`StreamingAssets/aa/catalog.bin`) plus `InventoryItemConfigCollection` entries, or an il2cpp field map. Not attempted (out of the time box).
- Stack counts: need il2cpp field offsets for `Game.Items.ItemData` (`QualityItemAttribute`, `HealAmountItemAttribute` etc. show the class has attribute-decorated fields; the field order is unknown). A partial metadata dump exists at `decomp/types.txt` (class names only, no fields) — no dumper run was started.
- Localized names: `ItemData` holds a `Unity.Localization` `LocalizedString`; string tables are in addressables, not read.

## Files

- `work/items/scan_all.py` — header scan of all assets → `mb_all.tsv`, `class_counts.json`, `scripts.json`
- `work/items/dump_items.py`, `extract_v1.py` — ItemData field-area dump → `itemdata_v1.json`, `itemdata_raw.json`
- `work/items/resolve_refs.py` → `refs.json` — registry/collection PPtr resolution
- `work/items/finalize.py` → the deliverable
- `data/item-pool.json`, `data/item-pool.md`
