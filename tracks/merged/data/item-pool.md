# Alterum item pool — what this is and how it was got

**Data:** `data/item-pool.json` — 55 items, one record each. Machine-readable, no prose inside except notes fields.

## Method (3 lines)

1. Every `MonoBehaviour` object header in `Alterum_Data/*.assets` + `level0..9` was read with UnityPy but **not** through the type tree (IL2CPP strips it). The typeless base layout was parsed by hand: `m_GameObject` (PPtr), `m_Enabled`, `m_Script` (PPtr), `m_Name` (string). That yields object name + owning GameObject name + a script pointer, all without field mapping — 276,821 objects indexed.
2. `m_Script` was resolved through a 9,253-entry `MonoScript` map (class name per `(file, path_id)`), so every object got its real C# class name — 3,246 distinct classes. The item definition class is **`Game.Items.ItemData`**.
3. All 55 `ItemData` assets sit in `sharedassets0.assets`, `path_id` 20690–20744. Their raw field area was read as byte strings + PPtrs: serialized item id (`adrenalinemushroom`, `key-tier-1`, `ss_exit_A`, …), the prefab PPtr (resolved to the GameObject name, e.g. `Health Flask Handheld`), and the addressables GUID token. Icon sprites were cross-referenced against the `Sprite` names present in the same file (the `item-icons` atlas source: `item-icons_key`, `item-icons_lantern`, `item-icons_crucifix`, `item-icons_health-injector`, `spells-icons_*`, `weapons-*`, …) — 51/55 match.

## Fields per item

| field | meaning |
|---|---|
| `itemId` | serialized id string from the asset |
| `assetName` | asset name minus `_ItemData` |
| `className` | `Game.Items.ItemData` (resolved from `m_Script`) |
| `displayName` | **derived** from the prefab name with ` Handheld` stripped — not a string-table read |
| `prefab` / `prefabPathId` | the world/handheld prefab the item instantiates, in `sharedassets0.assets` |
| `sourceFile` / `sourcePathId` | where the `ItemData` asset lives |
| `iconSprite` / `iconVerifiedInSpriteList` | sprite name in `sharedassets0.assets`, verified against the Sprite list |
| `spawnContext.worldSpawnPlacerConfig` | set only where a placer asset name matches the item name exactly (5 items) |
| `spawnContext.placerGroupInference` | group-level only, from placer asset names (`HQWeapon`, `LQWeapon`, `Spell`, `ExitSpellParts`) — **inference, not membership** |
| `stackable` | `null` — not extracted |
| `byteSize` | raw object size, for anyone who wants to re-walk the fields |

## Deliberately not extracted

- **Spawn weights / probabilities.** Not touched. The `*_PlacerSpawnProbabilities` / `InventoryRandomItemsFiller` assets were left alone.
- **Loot-table / chest / shop / reward membership.** Not resolved. `RegisteredGameItemDataContainer`, `RegisteredInventoryItemDataContainer`, `InventoryItemConfigCollection`, `Collection` and `ItemPlacersConfig` were located (see `notes/item-extraction-log.md`) but none of their raw bytes contain a PPtr to an `ItemData`/`InventoryItemData` in the ranges scanned, so membership is not recoverable by the cheap string/PPtr route.
- **Stack / count.** `ItemData` has no type tree; naming its integer fields requires il2cpp field offsets. Left `null` rather than guessed.
- **Localized display names.** `ItemData` serializes a `Unity.Localization` `LocalizedString` (smart-format variable `fragmentName` seen on Exit Spell Part A). The string tables were not read.

## Confidence

High on: class, asset name, item id, prefab, icon sprite name, source path id — all read as raw bytes, not inferred.
Medium on: `displayName` (prefab-derived), `placerGroupInference` (asset-name grouping).
Not claimed: weights, chest/shop/reward assignment, stackability.
