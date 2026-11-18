# Marker off the card face, garment weights by rig structure, stale launchers gone

Scope: `tracks/merged/mod/AlterumModelSwap.Hot/{LobbyProp.cs,ReelArt.cs,ChestWeights.cs}` only.
No Contracts change, no host change, hot reload only (`[hot] generation 2 -> 3 -> 4`, one deploy
each, atomic `cp` to `.AlterumModelSwap.Hot.dll.tmp` + `mv -f`).

Evidence: `~/assets/monorepo/alterum-data/filmstrips/reel_marker_v2.png` (before/after/mid-scroll),
`~/assets/monorepo/alterum-data/filmstrips/reel_scroll_v2.png` (seven-point filmstrip),
`~/assets/monorepo/alterum-data/filmstrips/chest_band_37x_diana_sarah.png` (chest band at 1.90, 3.7x),
`~/assets/monorepo/alterum-data/.temp/sarah/` (raw 720x1080 captures, `bandcount.py`, `patcheck5.py` output),
`~/assets/monorepo/alterum-data/.temp/reel2/` (film/spin log excerpts, filmstrip generator).

## 1. Centre marker: outside the card face

The old marker was a 6 mm cream rule plus a 52 mm additive glow (alpha 0.40, cream
1.42/1.30/0.94) drawn over the full window height at z = 0.1848, i.e. across the middle card -
on the settled frame it sat on the winning icon and split the three-letter code.

New geometry, all of it in the 9 mm band between the card face edge (|y| = 0.1500 m) and the
recessed well's inner shadow (|y| = 0.1590 m):

| part | extent | note |
|---|---|---|
| `marker_pointer_top` / `_bottom` | apex |y| = 0.1505, base |y| = 0.1580, 27 mm wide | inward-pointing triangle each side |
| `marker_tick_top_±1` / `_bottom_±1` | x = 17..48 mm, y = 0.1532..0.1580 | the pair that turns the lone pointer into a bracket (`---v---`) |
| `marker_glow_top` / `_bottom` | 60 x 14 mm behind each tick bar | additive, alpha 0.18 |

Glyph cream 1.12/1.03/0.78 (was 1.42/1.30/0.94) - one step below the title (1.20/1.05/0.64 after
the 1.20 sign gain), so the marker is no longer the brightest thing on the cabinet. Glow width
52 -> 14 mm, alpha 0.40 -> 0.18; the same lamp pulse still drives both ticks
(`ApplyScrollEmphasis`, one property block for the pair). The atlas' full-height 18 px marker seat
is replaced by two 26 x 12 px seats inside the same gaps, so nothing darkens the cards.

Measured on the settled frame, centre +-20 columns of the card area: 2997 bright px before
(bar + glow) -> 2537 after (icon + code only), while the two neighbouring card columns are
byte-identical (72 / 53 px). Class counts unchanged: `parts=76 renderers=76 colliders=0`,
pose `(0.35, 0.09, -8.57)` yaw 66, material `Cosmetic_PropLit`, self-lit `AlterumPropSign`
face, `MegaBook Page Back` clone, `grants=0`.

## 2. The garment reweight finds its skin source by structure

`ChestWeights` picked the skin by name (`*body*`). Sarah's third-person rig exposes no renderer
whose name says body (`Sarah_Belt / Chain / Pants / Shirt / Hands / Head / Hairstyle`), so the
apply logged `[chest:skip] no body/skin SkinnedMeshRenderer found` and her garment kept authored
weights.

New rule (`SelectSkinSource`), per rig, on the live renderers:

1. the renderer carries the chest chain in its own bone array (`chest_01_l/r` + `chest_02_l/r`) -
   the band frame is built from those bones in that renderer's bind pose;
2. `>= 40` of its vertices fall inside the chest band (`BandAlpha > 0.25`);
3. **skin vs worn kit**: `bandVerts <= 10 %` of its vertices. A body merely passes through the
   chest band; a pouch, chain, scarf, suspender or vest IS the chest band;
4. it is not one of the chest garments being fixed (a garment sampled against itself can only
   return its own weights);
5. among the candidates the one with the most vertices inside the band wins.

Measured census, printed per apply (`[chest:pick] ... census=[...] band/nv share= leaf=`):

| rig | candidate picked | band/nv | band share | leaf w | result |
|---|---|---|---|---|---|
| Diana | `Diana_Optimized_Body` | 601/20836 | 2.9 % | 0.19 | `changed=901 noSkin=13` (identical to the shipped numbers) |
| Kiona | `Kiona_Optimized_Body` | 466/21338 | 2.2 % | 0.09 | `changed=896 noSkin=19` (identical) |
| Emma (`Female_01`) | none | `Body_Optimized` 30/4570 (0.7 %) | - | - | no source: her body has 30 verts in the band; `Medallion` 41 %, `Scarf` 61 %, `Suspenders` 29 %, `Shirt` garment are all rejected |
| Freya (`Female_02`) | none | `body_Optimized` 0/5656 | - | - | body surface stops under the closed shirt (pass 5) |
| Sarah | none | `Sarah_Shirt` 1345/7171 (19 %, garment) | - | - | no other renderer has a vertex in the band |

Rejected by the share test on Diana: `Diana_Pouch` 1448/4915 = 29 % (an earlier version that
ranked by band vertices alone picked it and changed 367 verts instead of 901 - that is why the
share test exists). Emma's `Shirt` is not reweighted against the Medallion any more.

### Sarah at the shipped 1.90

Same method as pass 5 (`tracks/bones/tools/patcheck5.py`) plus two safeguards that the salmon
shirt made necessary: the count is restricted to the chest band the fix uses, and a pixel only
counts if the full render differs there from the garment-only render (something drawn in front of
the garment). Whole-garment patcheck5 numbers are also unchanged (259 px in both states; the
constant blobs are the hem over the belt and a boot highlight, none in the band).

| rig | chest weights | chest-band skin px at 1.90 |
|---|---|---|
| Diana | authored | **275** (one blob at (388,270): the strap junction) |
| Diana | fixed | **0** (patcheck5 whole-garment 522 -> 246, and 246 is pass 5's constant hem baseline) |
| Sarah | authored | **0** |
| Sarah | fixed | **0** |

Conclusion: Sarah's third-person rig has no skin mesh at all - the shirt is the torso surface
(`[bounds:apply] ... chestWeights=none`, `[chest:pick] no skin surface in the chest band`). There
is nothing to reweight and nothing to poke through, so her authored weights are left exactly as
authored: the rig reports 8 renderers, 3 with the chest chain, 0 candidates. Nothing was invented.

## 3. Stale scheduled tasks

All `AlterumHelper_*` / `AlterumSecond*` tasks were one-shot launchers with **no trigger**
(`Schedule Type = On demand only`, `Next Run Time = N/A`, `Delete Task If Not Rescheduled =
Disabled`), all `Ready`, last results 0 / -1 / 267014 - i.e. every one of them had already run.

Deleted (59): 45 `AlterumHelper_*` (creation-time names minted by `win/run-helper.ps1`, never
reused: 29 `desktop-size.ps1`, 7 `desktop-shot.ps1`, 4 `window-rect.ps1`, 1 each `window-640.ps1`,
`windows.ps1`, `winrun.ps1`, `save-fg.ps1`, `run-game.ps1`) and 14 `AlterumSecond*` (13
`AlterumSecond_HHMMSS` from `win/run-second.ps1` plus the bare `AlterumSecond`, which no script
mints any more). The task brief said 46 `AlterumHelper_*`; the store held 45 of them plus the 14
`AlterumSecond*`, 59 in total.

Kept, and why:

| task | why |
|---|---|
| `AlterumInteractive` | the main-instance launcher; default `-TaskName` of `win/run-game.ps1` and `lobby-model/tools/run-a-nokill.ps1`; currently Running for A |
| `AlterumTrackT1`, `AlterumTrackT2` | stable names computed by `win/track-instance.ps1` (`AlterumTrack<INSTANCE>`), documented in `LAUNCH-POLICY.md`, reused by `track.sh` |
| `AlterumTest-Game`, `AlterumTest-SaveFg`, `AlterumTest-RestoreFg`, `FocusAlterum{,2,3}` | outside the stated scope (not `AlterumHelper_*`/`AlterumSecond_*`); the Test-Game one launches the main install, so it is deliberately untouched |

After the purge: `AlterumHelper_*/AlterumSecond* remaining: 0`.

## 4. Nothing else regressed

* `grep -rc SWAP` over `tracks/merged/mod/**/*.cs` = **0** (only three `.cs` files changed).
* 50-clip emote path: `[emote] pose player ready (12 LC clips + 38 game clips); F6 toggles,
  F9 cycles the 50-entry list`.
* Jiggle lease: `[jsolve:rate] enabled=1 rigs=1 leaves=2 ... held=0 clamps=24` (local body Diana).
* Third person + first person masks: `tools/tp-verify.sh` full pass - TP `bit=present` for the body
  (layer 14) and the hat (layer 6), mask `0x4DDBCFC7`; FP `bit=absent`, mask back to the pristine
  `0x4DDB8F87`; mirror camera keeps both bits in both POVs (`0x4CDB4FC7`); `engages=3`,
  `insideBodyFrames=0`, left at the spawn in third person.
* Window: client area 640x360 (`win/window-rect.ps1`), headed, private lobby, 15 fps cap.
