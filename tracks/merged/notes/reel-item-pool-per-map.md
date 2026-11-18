# Reel item pool: per-map eligibility

The Reel of Fortune's item cards used to be drawn from all 51 usable rows of
`ReelPool.g.cs` (the 55-item extraction minus `rune`/`lantern`). In a map they are now restricted
to the items that map can actually spawn — in the world, in chests, or in altered chests. Type
weights (BLNK 22 / DMG 10 / DBF 6 / item 21 / X2 5 = 64 cells) and the per-category item weights
are unchanged; only *which* ids an item card may name changed.

## Where the set lives (Phase 1)

Not in the assets. `RegisteredGameItemDataContainer`, `InventoryItemConfigCollection` and
`Collection` carry no item PPtr in raw bytes (see `item-extraction-log.md`), so the truth is the
live scene's own spawn table:

| source | runtime object | what it gives |
|---|---|---|
| map placement + altered | `Gameplay.HandheldSpawner.HandheldSpawnConfigGetter` (43 in Lair, 40 in Necropolis) | `GetConfigs()` → one `HandheldSpawnConfig` per item the scene may place: `{InventoryItemData Item, ItemPlacersConfig, BaseAmount, Modifiers}` |
| chest membership | `ItemPlacersConfig.PlacersConfigs` (`PlacerConfig {Type, MaxItems, Probability}`) | the per-item-group placer table: `BlessingTotem / Books / ChestT1 / ChestT2 / Corpse / PileOfStones / Table`. Chest-eligible = `ChestT1` or `ChestT2` probability > 0 |
| altered chest | branch prefix of the getter's scene path | getters under `.../Altered Handhelds Spawner/...` use the `*_Altered_SpawnPreset` item groups of the same map |
| (diagnostic) | `Gameplay.SpellSystem.InventoryRandomItemsFiller` | one per map (`Game Global/Inventory Filler`), no parent inventory → its `ItemsProvider.GetItemsForInventoryNonAlloc` answers 0 items. Kept as a logged source, not used |

An **altered chest** is an ordinary chest placer under `Altered Handhelds Spawner` carrying the
`Gameplay.AlteredChests.AlteredChest` marker component (Lair: 7 `Inventory Spawn Placer [Vintage
Chest]` objects). The marker holds no item list; it only adds the altered highlight/quality path,
so the altered item table is the altered-spawner table above, and its chest membership is read the
same way. The `Collection` components in the scene are key collections (`Objects Selectors [Tier
Two Chests]`, keys `Tier2Key_KeyCollectionItem_KeyHolder`), not loot tables.

Placer census for the record (Lair): 381 `HandheldSpawnPlacer` in scene —
BlessingTotem 33, Books 68, ChestT1 29, ChestT2 19, Corpse 73, PileOfStones 120, Table 39.

## The lists

Lair of Occultists (`Lair Of Occultists Map Scene`), 35 normal tables + 8 altered tables:

- world (36): `axe_chopping axe_serrated axe_tactical axe_woodsman axe_wrapper-tomahawk
  ceremonial_candle crucifix dagger_black-dagger dagger_curved-knife dagger_rusted-blade
  dagger_rusty-dagger dagger_rusty-stiletto dagger_utility-machete healthflask healthsyringe
  key-tier-1 rope rune ss_chipmunkvoice ss_exit_A..F ss_farewell ss_favor ss_mirrorhaze
  ss_silencevoice ss_stasis ss_veilsofdeception ss_windfall throwingdagger_azure-edge
  throwingdagger_rusty-reaper throwingdagger_shadow-striker walkietalkie`
- chest (31): the same minus `key-tier-1` and `ss_exit_A..F` (their `ChestT1/T2` probability is 0),
  plus `ss_cursingtotem ss_thirstfordeath` (altered tables, chest probability > 0)
- altered (4): `ss_cursingtotem ss_farewell ss_favor ss_thirstfordeath`
- **union (38)**; 37 of them are usable ReelPool rows → the deck's item cards draw from 37 rows
  instead of 51 (`rune` is in the union but stays weight 0).

Necropolis (`Necropolis Map Scene`), 32 normal + 8 altered tables:

- world (31), chest (27), altered (4, same four altered spells), **union (33)**, pool 32/51.
- Lair-only ids: `axe_tactical crucifix dagger_curved-knife key-tier-1 ss_stasis`.
  Necropolis-only: none. Shared: 33.

## Implementation (Phase 2)

- `ReelEligible.cs` (new): reads the sources above, computes world/chest/altered/union, maps them
  onto `ReelPool` rows and stores `ReelDeck.AllowedItems` (array of row indices). Cached per scene
  name; rebuilt only on a scene change (`ReelEligible.Tick` from `ModelSwapRuntime.Update`).
- `ReelDeck.Compose` draws item cards over `AllowedItems` with the same per-item weights
  (`AllowedWeight`/`WeightedItem`); the deck is still exactly 64 cells with the same type split, so
  item cards repeat when the eligible set is small (Necropolis: 21 item cells over 15 distinct ids,
  rope ×3 / syringe ×3 as an example).
- Deck/strip construction is unchanged: compose at prop creation, recompose only when the debuff
  mask changes, reshuffle per spin.
- Lobby: no map, no spawn table → `AllowedItems` stays null and the pool falls back to all 51
  usable rows (pre-existing behaviour), logged as `source=lobby-fallback`. If a map scene yields no
  readable source at all, the fallback is the same and it logs `source=empty-fallback`.
- `rune` and `lantern` stay out: both are weight 0 / `Exclusion` rows in `ReelPool.g.cs`, so the
  filtered draw cannot name them either.

## Verify (instance A, pid 29384)

- Lair: `[reel:pool] scene='Lair Of Occultists Map Scene' source=map world=36 chest=31 altered=4
  union=38 pool=37/51`; prop placed with `reel.request place=here`; strip order and a
  `reel.request spin outcome=item` settled on card 30 `CCA` → `granted='ceremonial_candle' ok=True`.
- Necropolis: `source=map world=31 chest=27 altered=4 union=33 pool=32/51`; spin settled on card 10
  `DRD` → `granted='dagger_rusty-dagger' ok=True`. Every card code in both strips maps to an id
  inside that map's union (`[spawn:deck] outsideUnion=0`).
- Deck census both maps: `cells=64 blank=22 item=21 damage=10 debuff=6 double=5`.
- Lobby (after returning through `NetworkSceneLoader.LoadSceneOnServer("Lobby")`):
  `source=lobby-fallback ... pool=51/51`.
- `[Error]` after the first deploy of this change: 0. `grep -c SWAP` on the log: 0.
  `tools/reel-dist` still builds (ReelDeck keeps no UnityEngine dependency).

## Approximate / open

- The altered item table is read from the altered-spawner *branch*; nothing else in the scene
  publishes "which items an altered chest may hold" (`AlteredChest` has only highlighters). If the
  game narrows it further at runtime, that narrowing is not visible here.
- `ItemPlacersConfig` probabilities are read, not simulated: an item with probability 0.01 for
  `ChestT2` counts as chest-eligible.
- `InventoryRandomItemsFiller` is logged as a source but returns nothing in these maps, so a map
  that fills chests through it would currently contribute only through its config getters.
- `Instance A` is shared with another agent's `hatanchor` harness that hot-deploys to the same
  `BepInEx/hot`; `tracks/merged/tools/probe.sh` exists so a deploy + `spawn.request` + read survives
  that race (it parks the request first, then re-deploys until it is consumed).
