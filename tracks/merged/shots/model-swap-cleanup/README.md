# Model-swap cleanup — atlas cache eviction + `Sahur_Bat` teardown

Instance A (`C:\Program Files (x86)\Steam\steamapps\common\Alterum`, pid 29384), 640×360 client,
hot reload only (`gen 50 → 57`). Source: `tracks/merged/mod/`.

## Eviction policy

Two-entry LRU **per renderer path** — `SahurModel` (rigid) and `SahurRig` (rigged) each keep the
selected entry plus the previous one. Cache hit moves the slug to MRU; a third distinct load evicts
the LRU entry unless it is

* the player body's current asset (`_bodyRoot`/`_bodyGo` live, `_asset` identity), or
* the spec the wardrobe preview is still attached to (`SahurPreview.IsSpecAttached`).

Eviction destroys the mesh, **all** material slots (not only slot 0), and every decoded albedo /
normal mip chain. `[sahur:cache]` / `[sahurrig:cache]` log `load`/`evicted` with held count and
decoded MB; `wardrobe=dump` reports both caches on `[sahur:count]`.

Peak held after a full pass: **4 assets / 32.00 MB decoded** (rigid 2 / 10.67 MB, rigged 2 /
21.33 MB). The 16-entry set it replaces is 154.49 MB decoded by the same per-spec counts.

## Measurements

Stable run (gen 56, clean baseline via a test-only `Resources.UnloadUnusedAssets`):

| point | process WS | Unity totalAlloc | our live decoded textures | cache |
|---|---:|---:|---:|---:|
| before pass | 2,848,096,256 | 2242 MB | 0 (0.00 MB) | 0 |
| after pass 1 | 2,920,804,352 (+72,708,096) | 2247 MB (+5) | 6 (24.00 MB) | 32.00 MB |
| after pass 2 | 2,324,160,512 (−596,643,840) | 2247 MB (0) | 6 (24.00 MB) | 32.00 MB |

Each pass selects indices 0–25 (10 native + 16 mod) in order, 2.5 s per row. Cache and Unity
allocator are flat; process WS is **not** a reliable signal on this box (it moved by ~1 GB while
idle as Windows trimmed the Unity arena). Final build (gen 57) repeated the pass: WS 2.608 GB →
1.909 GB → 2.826 GB, cache 32.00 MB after both passes.

## Verification

* Native → all 16 mod entries → native: final `[sahur:count]` `our body/skeleton objects: 0 []`;
  `sweep.log` has the per-entry counts (2 rigid, 4 rigged = player + preview).
* Native → Classic → native: `[sahur:bat] built … parts=4` while selected, `[sahur:bat] destroyed`
  on revert, final census 0 (`classic-with-bat_640x360.png`, `after-revert_640x360.png`).
* All 26 rows selectable twice: 52 `apply index` lines, no errors. Same-entry re-select produces no
  `cache load` and no rebuild.
* Spot checks: Sahur, Capuchino, Pyro, Peter bodies and wardrobe previews in
  `body_*_640x360.png`; portrait sprites still 256×256 / 256×320.
* `[Error]` = 0, `grep -c SWAP` = 0, reel `cells=64`.

## Notes

* Verification-only request key added: `wardrobe=index=<n>` (`SahurWardrobe.ApplyIndex`) selects
  any `PlayerModelDatabase.Models` row through `PlayersAppearanceManager.SetBaseModelIndex`.
* No restart, no `win/run-game.ps1`. Reel files, baked assets, `Contracts`, T1/T2/T3 and the other
  tracks were not touched.
* Engine-wide `Resources.UnloadUnusedAssets` is not called by production code; a hot reload cannot
  reclaim textures orphaned by generations that ran before this fix. It was used once to establish
  the clean baseline above, then removed.
