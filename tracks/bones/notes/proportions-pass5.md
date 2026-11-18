# proportions pass 5 — the tank top's chest weights follow the skin, and the chest goes to 1.90

Scope: `tracks/bones/mod/AlterumModelSwap.Hot/ChestWeights.cs` (new), `Proportions.cs` (hook,
toggle, new cage default), `ProportionShots.cs` (`chestfix=`, `chestdump=` request keys),
`tools/chestgrab.sh`, `tools/chest-sweep.sh`, `tools/patcheck5.py`. Install `C:\AlterumT1`.
Evidence in `shots/proportions-pass5/`.

## Result

| knob | pass 4 (shipped) | **pass 5 (shipped)** |
|---|---|---|
| breast leaf (`chest_02_l/r`) | 1.55 | 1.55 (unchanged) |
| chest cage (parent of `chest_01_l/r`) | 1.097 | **1.22581** |
| total chest | 1.70 | **1.90** |
| hips | 1.50 | 1.50 (unchanged) |
| tank top chest weights | as authored | **follow the skin under them** |

Skin-through-garment pixels at the strap junction (Diana, one frozen pose and camera, 720x1080):

| total chest | cage | authored weights | **fixed weights** |
|---|---|---|---|
| 1.55 | 1.000 | 10 | **0** |
| 1.70 | 1.097 | 97 | **0** |
| 1.80 | 1.161 | 238 | **0** |
| **1.90 (shipped)** | **1.226** | **311** | **0** |
| 1.95 / 2.00 / 2.10 | 1.258 / 1.290 / 1.355 | — | **0 / 0 / 0** |

`shots/proportions-pass5/ladder-fix.png` (3.7×, authored vs fixed, 1.70/1.80/1.90),
`pokepix-190.png` (the counted pixels at 1.90), `ship-3.7x.png` (pass 4's 1.70 vs the shipped 1.90).

## 1. Why the hole existed, in one table

Garment vertices around the hole (bind pose, `Jane_Undershirt`) against the skin vertices 10 mm
under them (`Diana_Optimized_Body`), from `chestdump=`:

| | spine_04 (cage) | clavicle_l | chest_02_l | spine_03 | upperarm_twist |
|---|---|---|---|---|---|
| tank top at the hole | 0.55–0.71 | 0.13–0.39 | 0.11–0.13 | 0.03–0.06 | 0 |
| skin under it | 0.30–0.40 | 0.20–0.31 | 0.20–0.40 | — | 0.08–0.15 |

The fabric put 0.11 on the bone that carries the 1.55× leaf and 0.13–0.39 on a bone that holds 1×
while the skin under it put 0.2–0.4 on the leaf. Scaling the chest therefore separates the two
surfaces, and a 20 k-vertex skin facet pushed out through one 2 k-vertex fabric triangle. That is
why `pen=` (garment vertices inside the body) never saw it: no garment vertex has to move for a
whole skin facet to appear.

## 2. What the fix does (the region rule)

At runtime, for every female rig, per garment renderer:

1. **Which meshes.** Only the torso garments by name (`shirt|vest|tank|blouse|tee`, never `body`,
   `head`, `hair`), and only if the mesh's bone array contains a `chest_01_l/r` + `chest_02_l/r`
   pair. Logged per renderer: `[chest:apply] mesh='Jane_Undershirt' ...` or
   `[chest:skip] mesh='Diana_Pouch' reason=not-a-torso-garment`.
2. **Region.** In the garment's bind pose (`bindposes⁻¹·0` per bone, placed by the renderer
   transform), measured **along the skeleton's own axes**: `up` = pelvis → mid `chest_01`, `side` =
   `chest_01_l` → `chest_01_r`. The band is `[-0.075, +0.095] m` of up around the `chest_01`
   mid-point with a 0.030 m smoothstep at both ends, and `±0.160 m` of side with a 0.020 m fade.
   Skeleton-relative on purpose: the wardrobe clones (at x≈25.7, not at the origin) get the same 916
   candidates as a clone at the origin.
3. **Rims.** The garment's open rims are its welded boundary loops (UV seams welded away first:
   4052 triangles → 96 boundary edges → 106 rim vertices on the tank top). The blend is 0 at the
   rim and ramps to 1 at 12 mm inside it, so the neckline hem, collar and armhole rings keep their
   authored weights exactly — every rim vertex is unchanged.
4. **Target.** The weight mix of the 6 nearest body-skin vertices within 60 mm (inverse-distance
   weighted, bones matched by name, bones the garment does not have dropped).
5. **Blend.** `w' = (1-a)·w + a·target`, `a` from the region falloff; the 4 largest influences are
   kept and the result normalised. Verified on the written data every time:
   `normalised=ok(sum=1,<=4)`.
6. **Mesh.** The renderer's `sharedMesh` is **cloned** (`Object.Instantiate`), `boneWeights` is
   written on the clone (IL2CPP allows it — `Mesh.boneWeights` has a working setter, and the value
   is read back and length-checked before the renderer is re-pointed), and the clone is assigned.
   Teardown restores the authored mesh and `Destroy`s the clone:
   `[chest:restore] mesh='Jane_Undershirt' original sharedMesh restored, clone destroyed`.

Verts changed: **901 of 2232** on Diana's tank top (916 candidates: 13 have no skin within 60 mm —
they are more than 60 mm off the body under the bust, where the garment hangs free — and 2 shift by
< 1e-4). Kiona 896/2232, Sarah
901/2232, max weight shift 0.372 (mean 0.083).

Freya/Emma are **not** touched and need nothing: their `body_Optimized` has no vertices in the
chest band at all (it stops at y≈1.21 under a closed shirt — the shirt *is* the bust surface), so
the sampler finds nothing and the apply is skipped. Their 1.90 renders with the fix on and off are
byte-identical (md5), and the pixel counts are equal at every value.

## 3. Sweep, both characters

`tools/patcheck5.py`, all values in one frozen session per character, every capture taken twice
and the second kept (the pass-4 capture-lag proof), jiggle solver disabled for the rest shape.
The counted area is the union of the garment silhouettes over every state and value in the sweep
(each captured with every other renderer hidden), a pixel counts when it is skin-coloured, inside
that state's own garment silhouette, not under the over-shirt pouch strap, and its blob does not
reach the body's background outline.

Diana (`Female_3_Diana`, tank top `Jane_Undershirt`, body `Diana_Optimized_Body`):

| total | cage | authored: chest patch / whole area | fixed: chest patch / whole area |
|---|---|---|---|
| 1.55 | 1.000 | 10 / 290 | **0 / 246** |
| 1.70 | 1.097 | 97 / 344 | **0 / 246** |
| 1.80 | 1.161 | 238 / 485 | **0 / 246** |
| 1.90 | 1.226 | 311 / 558 | **0 / 246** |
| 1.95 | 1.258 | — | **0 / 246** |
| 2.00 | 1.290 | — | **0 / 246** |
| 2.10 | 1.355 | — | **0 / 246** |

The constant 246 px are four blobs at image y≈418–432 (the tank top's hem over the belt, all
present at 1.55 and unchanged by the value or the fix) — not the chest.

Freya (`Female_2`, `Shirt`): 173 / 155 / 142 / 130 px at 1.55 / 1.70 / 1.80 / 1.90, one blob at
(385, 176) — the neck skin visible inside the collar, not through the shirt — and identical with
the fix on and off. She was never the limiter and still is not.

**Ceiling.** The weight fix removes the chest poke at every value tested up to 2.10; what stops
the sweep now is the look, not a hole: at 2.10 the tank top reads as two spheres with a crease
under the bust and a pulled-thin strap (`diana/d-2.10-fx1-three.png`). 1.90 is shipped — the
largest value in the requested sweep, visibly clean at 3.7× (`ship-3.7x.png`).

## 4. Two things the first attempt got wrong

* **Rim clearance 8→28 mm** (still leaving the rim untouched) left a thin notch *on* the neckline
  edge at 1.80/1.90: 8 px at 1.80, 21 px at 1.90, visible at 6× (`shots/…/notch-6x.png` of that
  build). The skin under the pin-line outgrew every vertex the ramp had starved. Ramping 0→12 mm
  instead — rim vertices still exactly untouched, the first ring inside fully blended — closes it
  and changes nothing else in the count.
* **World-axis band.** The first version measured the band along world Y. The wardrobe clones —
  which stand at x≈25.7 in the lobby, not at the origin — then got 813 candidates against the
  spawned clone's 906 from the same mesh, i.e. the band cut differently because it was not
  following the body. Measured along the skeleton's own axes every rig gets 916. Both are in the log of this pass if the difference
  is ever needed again.

## 5. Invariance and jiggle at the new value

One frozen pose, same session, `propcap measure=1`, shipped build (no request overrides):

| measure | value |
|---|---|
| shoulder joint width (`upperarm_l..r`) | **0.2925 m** (unchanged) |
| height (head → lowest bone) | **1.5972 m** (unchanged) |
| hip width (thigh roots) | **0.2990 m** (unchanged) |
| hip half width | 0.1495 m (unchanged) |
| chest pivot → leaf (rest length) | **0.0603 m** (= 0.0492 × 1.226) |

Hips 1.50 untouched, shoulders and height bit-identical (the cage compensation is unchanged; only
its factor moved). The jiggle solver is untouched: pass 3 measured the impulse response identical
at cage 1.0 and 1.161 (21.646° / 35.87 mm, 0 clamps) because the spring is positional and the
clamp is in world millimetres; 1.226 is the same construction one step further, and the lever arm
scales with the rest length (0.0603 m here), so the rendered excursion grows with the chest
instead of the rotation. No retune, none needed.

## 6. Regression

* `grep -c SWAP LogOutput.log` = **0**; no `[Error]`/exception line after `[hot] generation 38
  active`.
* reload: `[chest:restore] … original sharedMesh restored, clone destroyed` for every rig of the
  previous generation, then the new generation re-applies: `[chest:apply] … normalised=ok(sum=1,<=4)`.
* teardown of a spawned capture rig: `[propcap:release]` → `[chest:restore] … clone destroyed` →
  `[bounds:restore] rig='PropCaptureBody' … reason='model gone'`.
* capture session released, solver re-enabled (`solver.request enabled=1`), no stale
  `proportions.request`.
* mirror: `mirror:static-created` + `mirror:render-ok` fire on the new build (frames 174641 /
  187311 / after generation 38). The `mirror:body-check … bound=True renderers=9 isolated=9
  driven=9` witness still predates this build — it only runs when the wardrobe mirror binds the
  local body, and `Mirror.cs` / `MirrorPlayerVisibility.cs` are untouched by this pass.
* skin meshes are never cloned or reweighted: the apply log lists garment meshes only.

## Files

| file | change |
|---|---|
| `mod/AlterumModelSwap.Hot/ChestWeights.cs` | new: region rule, skin sampling, blend, mesh clone/write/restore, `chestdump` probe |
| `mod/AlterumModelSwap.Hot/Proportions.cs` | `ChestCageScaleDefault` 1.097 → **1.22581**; rig carries its `ChestFixes`; `SetChestFix` toggle + `chestfix=` request key; restore on model gone/teardown |
| `mod/AlterumModelSwap.Hot/ProportionShots.cs` | `chestdump=<match>` and `chestfix=0\|1` request keys |
| `tools/chestgrab.sh` | one capture, sent twice, second kept (pass-4 lag rule) |
| `tools/chest-sweep.sh` | per value × fix-state: full, garment-only, pouch-hidden; garment-only reference at 1.55 |
| `tools/patcheck5.py` | the visible-skin pixel test with a union footprint across states |
| `shots/proportions-pass5/` | renders, `ladder-fix.png`, `pokepix-190.png`, `ship-3.7x.png`, `make_montage.py` |
| `tools/patcheck.py` (pass 4) | unchanged; its `MASK_BASE=mask` numbers (248 at 1.70, 471 at 1.80) still reproduce on the pass-4 renders |
