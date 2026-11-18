# proportions pass 3 — the chest grows, and the tank top grows with it

> **Superseded in part by pass 4** (`proportions-pass4.md`): the shipped 1.80 panel shows a skin
> patch on the tank top at the strap junction in the three-quarter view (the body pushes through
> between fabric vertices, which the vertex count below cannot see), and the per-value renders this
> note's "render verdict" column was read from lag one request behind the scale change. The cage is
> **1.097 (total 1.70)**; leaf 1.55 and hips 1.50 stand. Everything else here — the cage premise,
> the counter-scale maths, the invariance measurements, the jiggle analysis — still holds.

Scope: `tracks/bones/mod/AlterumModelSwap.Hot/Proportions.cs` (the new chest-cage scale and its
counter-compensation), `ProportionShots.cs` (the measurement/capture additions). Install
`C:\AlterumT1`. Evidence in `shots/proportions-pass3/`.

## Final constants

| knob | field | value |
|---|---|---|
| breast leaf (`chest_02_l/r` uniform scale) | `BreastLeafScaleDefault` | **1.55** |
| chest cage (parent of `chest_01_l/r`) | `ChestCageScaleDefault` | **1.097** (pass 4; was 1.161) |
| hips (`pelvis` uniform scale) | `HipScaleDefault` | **1.50** (unchanged) |

Total chest enlargement = 1.55 × 1.097 = **1.70×** the natural rig. The sweep below is the pass-3
work that produced the cage; its 1.80 rows are history, not the shipped value.

## 1. The premise had the wrong bone — measured, not assumed

`propcap.request` gained `weights=<mesh>`: it reads `Mesh.boneWeights` (readable in this build) and
prints the weight mass per bone, plus the mass restricted to the chest band (pivot → neck). Diana
(`Jane_Undershirt`, the tank top, 2232 verts, 2232 total weight):

| bone | mass | share |
|---|---|---|
| `spine_04` | 847 | **38 %** |
| `clavicle_l` + `clavicle_r` | 597 | 27 % |
| `spine_03` | 300 | 13 % |
| `pelvis` | 217 | 10 % |
| `spine_02` | 148 | 7 % |
| `chest_02_l` + `chest_02_r` | 109 | **5 %** |
| `chest_01_l` + `chest_01_r` | **0** | **0 %** |

Freya (`Shirt`, 6734 verts, chest band): `Chest_02` 1141, `clavicle_l/r` 1070, `neck_01` 72,
`spine_03` 53. Her shirt **does** ride the leaves, so leaf-only scaling moves it (she was never the
limiting outfit).

So scaling `chest_01` would move only the skin (0 garment weight on Diana) and make the poke worse.
The garment-carrying bone is the **parent of `chest_01_l/r`** — `spine_04` on
Diana/Kiona/Sarah, `spine_02` on Emma/Freya — the "cage". It is scaled, and `chest_01`/`chest_02`
come with it as children (the leaf keeps its own 1.55 relative bulge). That is exactly the bone the
pass-2 note blamed for "the shirt does not follow", one level further up than the guess.

## 2. Counter-scale maths

Cage `s = ChestCageScale`. For every rig with both leaves:

* `cage.localScale = orig × s` — the chest, the ribcage skin and the garment grow together.
* the **trunk** (the parent of the clavicles: `spine_05` on Diana, `spine_03` on Freya) keeps the
  inherited `s` (its own skin and the garment panel it carries are chest and must grow) but
  `trunk.localPosition = orig / s` so it does not rise.
* `clavicle_l`, `clavicle_r`, `neck_01`: `localScale = orig / s`, `localPosition = orig / s`.
  Their subtrees (`upperarm_*` → arms, `neck_02` → head, hair) then inherit exactly 1.0× and stay
  where they were. Their attachment points are pinned, so **shoulder width, arm size and total
  height are unchanged**.
* every other cage/trunk child (pouch root, walkie-talkie, frisk, hitboxes): `scale / s`,
  `position / s` — accessories must not inflate or move.
* `Proportions.LateTick` (new, called from `ModelSwapRuntime.LateUpdate`) re-asserts the pinned
  positions every frame *after* the animator, because the humanoid animation rewrites
  `spine_01`/`thigh_*`/… local positions (measured `posDrift=3` on animated rigs). Without it the
  invariance would hold only on frozen capture clones.

Measured invariance, **one frozen pose, same session**, cage 1.0 → 1.161 (Diana):

| measure | cage 1.000 | cage 1.161 | delta |
|---|---|---|---|
| shoulder joint width (`upperarm_l`..`upperarm_r`) | 0.2903 m | 0.2903 m | **0** |
| height (head → lowest foot bone) | 1.5812 m | 1.5812 m | **0** |
| hip width (thigh roots) | 0.2834 m | 0.2834 m | **0** |
| chest pivot → leaf (rest length) | 0.0492 m | **0.0572 m** | ×1.161 |
| `spine_05` local | scale 1.00, pos −0.16 | scale 0.86, pos −0.14 | ×1/1.161 |
| world of `spine_05`, `clavicle_l/r`, `upperarm_l/r`, `neck_01/02`, `head` | — | **identical** | 0 |

(pass-2 quoted 0.2935 m / 1.5284 m from a *different* idle pose; the correct comparison is within
one pose, where the invariance is exact. The retargeted A-pose of that session gives 0.2903 /
1.5812; the model's own idle gives 0.2925 / 1.5972 / 0.2990 hips at cage 1.000 **and** 1.161,
with only `chestPivotToLeaf` 0.0492 → 0.0572.)

## 3. Sweep — what each value looks like, and what limits it

Objective test added as `propcap.request pen=<garment>`: it skins the garment and the body mesh on
the CPU (`boneWeights × bindposes × bone.localToWorld`), then counts garment vertices that are
*inside* the body surface (nearest body vertex within 25 mm whose skinned normal points away).
Camera/colour/shadows cannot fool it. "front" counts only vertices whose normal faces the capture
camera.

Diana, tank top (2232 verts, 1257 matched). Rows below 1.55 are leaf-only (cage 1.0), the rest are
the cage change; all at one frozen pose.

| chest | cage | inside | max depth | >3 mm | >5 mm | front-facing | render verdict |
|---|---|---|---|---|---|---|---|
| 1.55 (previous) | 1.000 | 29 | 6.17 mm | 12 | 5 | 20 | clean tank top; pouch strap z-fights the shirt |
| 1.60 leaf-only | 1.000 | 39 | 7.00 mm | 15 | 7 | 25 | pass-2 "clean" reference |
| 1.80 leaf-only | 1.000 | **65** | **10.34 mm** | 40 | 22 | 37 | visible skin holes at the strap (pass-2 fail) |
| 1.90 leaf-only | 1.000 | 79 | 12.01 mm | 49 | 32 | 45 | clear jagged hole |
| 1.60 | 1.032 | 31 | 6.37 mm | 13 | 5 | 21 | clean |
| 1.70 | 1.097 | 35 | 6.79 mm | 15 | 5 | 22 | clean |
| 1.75 | 1.129 | 35 | 7.00 mm | 17 | 4 | 21 | clean |
| **1.80 (shipped)** | **1.161** | **38** | **7.21 mm** | 20 | 6 | 21 | clean tank top, strap seam same as previous |
| 1.85 | 1.194 | 42 | 7.41 mm | 21 | 7 | 23 | not rendered; leaves the shipped envelope (>5 mm count 6→7) |
| 1.90 (first failing) | 1.226 | 45 | 7.73 mm | 25 | 7 | 25 | visible skin patch at the strap/neckline |
| 1.95 | 1.258 | 51 | 8.07 mm | 24 | 9 | 30 | patch grows |
| 2.00 | 1.290 | 72 | 8.43 mm | 36 | 12 | 34 | knee — folds, clear hole |

The cage is a strict improvement **per unit of chest size**: at the same 1.80 total it has 38
penetrating vertices / 7.21 mm against leaf-only's 65 / 10.34 mm (−42 % / −30 %); the shipped 1.80
sits at the same level as the leaf-only 1.60 the pass-2 note accepted as clean (38 vs 39 vertices,
7.21 vs 7.00 mm max). In the model's own idle pose (arms down; the retargeted A-pose above is the
worse case) it is 24 vertices / 3.97 mm at the shipped 1.80 against 17 / 4.12 mm at the previous
1.55 — seven more shallow contacts, no deep ones (>5 mm count 0 at both). The residual
is not the volume: it is the **strap/neckline seam** (all hits cluster at x≈0.10–0.13, y≈1.38–1.40,
z≈0.09–0.12 — the clavicle/neck region), and it **already exists at 1.55** (29 vertices). The
tank top's hem there is weighted to the clavicles/neck while the skin under it is weighted to
`spine_05`/`chest_01`, so *any* chest scale opens it a little; no bone scale can close it.

**Shipped in pass 3: 1.80 (cage 1.161); shipped now: 1.70 (cage 1.097)** — see
`proportions-pass4.md` for why the 1.80 panel was not clean. 1.90 is the first value that widens the
strap notch visibly (`strap-closeup.png`: 45 vertices / 25 over 3 mm against 38 / 20) and 1.85
already measures past the shipped envelope; on the vertex count 1.80 was the ceiling, but the count
does not see skin pushing out between garment vertices, and the picture does.

Freya (`Shirt`, 6734 verts): **2 penetrating vertices / 1.39 mm at every value tested**
(1.55, 1.60 leaf, 1.80 leaf, cage 1.097, cage 1.161) — her garment and skin share the same bones
(`Chest_02`, `spine_02/03`, clavicles), so they scale together and there is no clipping to find.
Not the limiter.

## 4. Jiggle at the new scale

A cage scale *does* move the solver pivot (`chest_01`) and the pendulum rest length, unlike the
leaf-only change of pass 2:

* rest length `|chest_01 → chest_02|`: **0.0492 m → 0.0572 m** (measured, = ×1.161)
* impulse 0.8 m/s, gravity 0, frozen animator, cage 1.161:
  **peak 21.646° / 35.87 mm, settle ≈1.53 s** (0 clamps, `[jsolve:rate]` + `[jsolve:tr]` trace)
* old documented (pass 2 / `jiggle-solver.md`, 0.0492 m): 16.3° / 38.1 mm, ~1.4 s
* pass 2's own cage-1.0 measurement on this build: 21.651° / 35.87 mm

The response is **identical at cage 1.0 and 1.161 to 0.005° / 0.00 mm**, so the solver needs no
retune and none was made (still 2.0 Hz, ζ 0.33, rotGain 0.5, 45 mm / 32° clamps). The reason is
that the spring is positional (ω² fixed), the clamp is in world mm, and the rotation is computed
from the leaf's *local* vector (unchanged by a parent scale). What does change is the lever arm:
35.87 mm at a 57.2 mm rest length is 35.9° of pendulum swing instead of 41.8° at 49.2 mm, and the
rendered surface excursion is rotation × radius, i.e. +16 % for the same rotation on the 1.16×
bigger chest. That is the physically right direction (bigger chest, same angular wobble), so
nothing in the solver was touched.

## 5. Capture rig additions (`ProportionShots.cs`)

New request keys, all one-shot and logged:

| key | what it does |
|---|---|
| `weights=<mesh>` | per-bone weight mass (whole mesh + chest band) — the evidence in §1 |
| `pen=<garment>` | CPU-skinned garment-vs-body penetration: inside count, max/mean depth, >1/3/5/10 mm, front-facing subset |
| `hier=1` | the chest/spine/shoulder subtree with parent, local transform and children |
| `comps=1` | component types under the model (this is how the ragdoll was found) |
| `chestscale=` / `clavgrow=` | live cage value / the clavicle-grow experiment (default off) |
| `partsall=1`, `hideparts=a,b`, `showparts=` | per-renderer show/hide for the render checks |
| `solo=1` | deactivate every other model (the local player's body is on the same layer and keeps animating) |
| `phys` (implicit in `spawn`/`freeze`) | make the spawned clone's ragdoll kinematic and switch off its Animancer/IK/ragdoll drivers, restored on `release=1` |

`solo` and the ragdoll freeze were required to make the captures honest: the instantiated 3P
prefab is a live ragdoll (17 `Rigidbody`, 16 `CharacterJoint`, `TransformDeltaGetter`, `Animancer`,
`PersonIK`). With only the Animator disabled it collapses to the floor and re-poses the legs between
the pose restore and the render, so two captures of the "same frozen rig" differed by a whole pose
(measured 171 k differing pixels); after the freeze the same two captures differ by **1 pixel**.

## 6. Montages (`shots/proportions-pass3/`)

* `diana-previous-candidate-shipped.png` — previous 1.55 / leaf-only 1.80 (the pass-2 failure) /
  shipped 1.80, front + ¾ + side, one frozen pose, one camera, one light (same session).
* `freya-previous-candidate-shipped.png` — the same three on Freya (one frozen session).
* `strap-closeup.png` — 4× front: shipped 1.80, first failing 1.90, leaf-only 1.80.
* `chest-closeup.png` — 2.6×: previous vs shipped vs leaf-only 1.80.
* `raw/` — every render plus the `propcap_*.txt` landmark files and the per-value renders used by
  the sweep (`-nos` garment hidden, `-nob` body hidden, `-nop` pouch hidden).

The visible result: at 1.80 the shipped build has a fuller chest and no hole of the kind the
leaf-only 1.80 shows; the strap-junction notch that exists at 1.55 is still there and widens a little
at 1.90. The previous 1.55 build renders a broken / z-fighting pouch strap over the shirt (visible in
the front and ¾ rows), which the cage removes.

## 7. Regression (T1, final build)

* mirror binding: `[mirror:body-check] ... bound=True renderers=9 isolated=9 driven=9` — unchanged.
* `grep -c SWAP LogOutput.log` = **0**.
* teardown on hot reload: `[propcap:phys] thawed 17 rigidbody(s), 88 driver component(s)`,
  `[jsolve] shutdown: rigs=0 ... all offsets removed`, `[bounds:disposed] ... patchesRemoved=True`,
  then `[hot] generation 22 active`; no `[Error]` lines in the reload window.
* the capture session leaves the local player's body hidden via `solo` if the module reloads while
  it is active; it was re-shown (`propcap:setactive ... -> True`) and the mirror re-created
  (`mirror:static-created`, `render-ok`).
* the 12-clip **emote sweep still cannot run in this track** (`PosePlayer`/`EmoteRuntime` live in
  `tracks/merged` / `tracks/emotes`). The leg IK roots moved with the hip widening in pass 2 and
  that has not changed; the merged build must run the sweep.

## 8. What would raise the ceiling

The limiter is a **weight mismatch**, not a size limit (Freya, whose garment shares the skin's
weights, is clean at every value). The lever that would go past 1.80 without a patch is to transfer
the body's chest weights onto the tank top's chest/strap vertices (or drive a corrective blend shape
from `ChestCageScale`) — an asset/weights edit, i.e. the asset pipeline the original recon ruled
out of scope, not a bone scale. On the bones alone, 1.80 is the honest ceiling; 1.85 already measures past it and 1.90
is the first value that shows.

## Files to merge

| file | change |
|---|---|
| `mod/AlterumModelSwap.Hot/Proportions.cs` | `ChestCageScaleDefault = 1.161f` (line 68), `_cageScale`/`ChestTotalScale` (78/90), `LateTick` (159), `SetClavicleGrow` experiment (188), `IsChestChain` (464), `ScaleCage` (483), cage roles in `SetNode` (656–665), verify `want`/posDrift, request keys `chestscale`/`clavgrow`, node `LatePins`, header comment |
| `mod/AlterumModelSwap.Hot/ProportionShots.cs` | `hier`/`weights`/`pen`/`comps`/`solo`/`partsall`/`hideparts`/`chestscale`/`clavgrow` request keys, `FreezePhysics`/`ThawPhysics` (876), `Solo` (933), `DumpPenetration`+`Skin` (962), `SetPartsEnabled` (1127), `DumpHierarchy` (1309), `DumpWeights` (1347) |
| `mod/AlterumModelSwap.Hot/ModelSwapRuntime.cs` | `_proportions?.LateTick()` in `LateUpdate` (line 625) |

No other file changed; `Mirror.cs`, `MirrorPlayerVisibility.cs`, `ScreenShot.cs`,
`JiggleSolver.cs` are untouched (the solver's parameters were deliberately not changed, §4).
