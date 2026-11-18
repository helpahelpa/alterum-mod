# bones track — implementation notes

Scope: remove the Diana→Freya swap, add enlarged breasts/hips by bone scaling with the shipped
MagicaCloth jiggle. Source: `tracks/bones/mod/`, install `C:\AlterumT1`, log
`C:\AlterumT1\BepInEx\LogOutput.log` (per-instance copy `BepInEx/logs/t1.log`), captures in
`BepInEx/screens/t1/`.

## Files

| file | change |
|---|---|
| `AlterumModelSwap/Plugin.cs` | deleted 5 `Swap*` `ConfigEntry` fields, their `[Swap]` `Bind`s, their `ConfigSnapshot` initialisers |
| `AlterumModelSwap.Contracts/Contracts.cs` | deleted the `// Swap` block (5 fields) |
| `AlterumModelSwap.Hot/ModelSwapRuntime.cs` | `DumpAndSwap()` → `DumpModelTable()` (dump kept, swap body deleted), `_dumpedData` → `_dumpedModels`, `Dispose()` now also disposes the bone pass, `Update()` ticks it |
| `AlterumModelSwap.Hot/HotModule.cs` | stores `_host`, calls `ScreenShot.Tick(host, dt)` |
| `AlterumModelSwap.Hot/ScreenShot.cs` | copied verbatim from the shared tree (baseline, unmodified) |
| `AlterumModelSwap.Hot/Proportions.cs` | new: the bone pass, cloth report, SetupModel hook, verify/restore |
| `AlterumModelSwap.Hot/ChestWeights.cs` | new (pass 5): rewrites the tank top's chest weights to the skin's and clones the mesh per renderer; restored + destroyed on teardown |
| `AlterumModelSwap.Hot/BonesProbe.cs` | new: unattended verification helpers (`mirror.request`, `probe.request`, `pose.request`) |
| `Alterum.References.props` | added a `<Reference>` to `$(RefsDir)MagicaClothV2.dll` (track copy only) |

`Mirror.cs`, `MirrorPlayerVisibility.cs`, `ScreenShot.cs` are untouched.

## Tunables (`Proportions.cs`)

* `ChestCageScaleDefault = 1.22581f` — uniform scale on the parent of `chest_01_l/r` (the bone the
  garment rides); total chest = 1.55 × 1.22581 = **1.90**. Only safe because the garment's chest
  weights now follow the skin (`ChestWeights.cs`); see `proportions-pass5.md`.
* `BreastLeafScaleDefault = 1.55f` — uniform scale on `chest_02_l` / `chest_02_r` (leaves).
  Pass 2 raised the request to 1.80, but the bare skin poked through the tank top from 1.62 on;
  the measured clean ceiling is 1.60 and 1.55 ships with margin. See `proportions-pass2.md`.
* `HipScaleDefault = 1.50f` — uniform scale on `pelvis`, every direct child counter-scaled to
  `1/s` with its world offset from the pelvis kept in Y and multiplied by `s` in X/Z. The
  thigh-root separation goes 0.2432 -> 0.2990 m (= exactly 1.50/1.22), height and shoulders
  unchanged. 1.55/1.60 were also clip-free but read as a modelling error on the male rigs.

Both are static fields (defaults above): rebuild + `track.sh bones deploy` hot-reloads them. The
`proportions.request` keys `breastscale=`/`hipscale=` re-derive every node from the recorded unit
originals without re-basing them (used by the pass-2 captures; `SetScales` + `ReapplyAll`).
Capture rig and montages: `ProportionShots.cs`, `shots/proportions-pass2/`.

## Measured effect (orthographic capture, magenta background, same pose)

Diana (`3P_ConcreteModel_Female_03_Diana(Clone)`), base = 1.0/1.0, big = 1.45/1.22:

| band (fraction of body height) | front width base→big | side depth base→big |
|---|---|---|
| 0.70 (chest) | 0.437 → 0.444 m (+1.5 %) | 0.265 → 0.278 m (+4.9 %) |
| 0.56 (hips) | 0.490 → 0.508 m (+3.7 %) | 0.250 → 0.290 m (+15.6 %) |
| 0.50 (hips) | 0.493 → 0.523 m (+6.0 %) | 0.247 → 0.282 m (+14.4 %) |
| 0.40 (mid-thigh) | 0.359 → 0.406 m (+13.2 %) | 0.172 → 0.188 m (+9.4 %) |
| body height | 498 px → 498 px (unchanged) | — |

Front-view pixel diff: legs move apart (base-only pixels on the inner contours, big-only pixels on
the outer contours), hip band grows on both sides. Side-view diff: big-only band along the breast
front and the buttocks, no height change. Front-view chest width barely moves because the arms and
the chest pouch own the outer silhouette there; the breast change is mostly depth.

## What the log proves

* `[bounds:audit]`: 5 female model assets have `chest_01/02_l/r` (case differs: `Chest_*` for
  Female_01/02, `chest_*` for Female_03/04/05), `pelvis`, `thigh_l/r`; the 5 male assets have no
  chest bone at all.
* `[bounds:setup]`: the `PlayerModelSetuper.SetupModel` Harmony postfix fires on a cold start
  before the Lobby scene finishes loading; the sweep applies on the next update.
* `[bounds:apply]` per rig: name, bone count, which bones scaled, hip width, cloth state.
* `[bounds:verify]`: `scaleDrift=0` on every rig, repeatedly. `posDrift` is 0 on every non-animated
  (wardrobe) rig and 3 on the animated player rig: the humanoid idle animation rewrites
  `spine_01`/`thigh_l`/`thigh_r` localPosition, i.e. the leg-root *offset* compensation is transient
  on an animated model while the pelvis scale always holds.
* Cloth: `breastJiggleCloth=1 valid=True running=True usedTransforms=5
  chestTransforms=[chest_01_l,chest_01_r,chest_02_r,chest_02_l] scaledLeavesInCloth=2/2` on all 5
  female rigs — the exact Transform objects that were scaled are in the cloth's used set.
* The shipped MagicaCloth2 steps **no** team in this build (0 calls to
  `TeamManager.SimulationStepTeamUpdate`, `SimulationStepCount=0`) whatever the
  culling/visibility/owner state, and with the animator frozen an impulse is 0.0000°. The earlier
  "re-registering the manager's custom game loop starts it" claim is **retracted**; the cloth is
  kept reported but is not what produces jiggle. See `cloth-lifecycle.md`, `cloth-simulation.md`
  and the retraction evidence in `cloth-cleanup-evidence.log`.

## Captures (`tracks/bones/shots/`)

`cap-front-base.png`, `cap-side-base.png` (scale 1.0), `cap-front-big.png`, `cap-side-big.png`
(1.45/1.22), `diff-front.png`, `diff-side.png` (red = big-only, blue = base-only),
`cap-front-final.png`, `cap-side-final.png` (male player, hips only), `cap-static180.png` (mirror RT,
shows the room: the baked fixed mirror pose does not face this session's player — pre-existing).

## Jiggle (replaces the cloth path)

`AlterumModelSwap.Hot/JiggleSolver.cs`: substepped position-Verlet on the two `chest_02` leaves,
world-space particles anchored to the animated pose (so body motion is felt and nothing drifts),
additive `localPosition` + `localRotation` write in LateUpdate, hard clamps (45 mm / 32°), and an
opt-out that restores the exact animated transforms. `chest_01_*` is only the pivot and is never
written; the offset is applied only after `Proportions` has registered the rig, so the 1.45x scale
is always established first. The dead Breast BoneCloth's write-back is switched off for those
leaves with `SetSkipWriting(true)` (restored on release) because it rewrote the build pose over the
jiggle after LateUpdate — that is why the first version was invisible in the rendered frames.

`ClothSim` (the loop re-registration) is now **default off** and logs that decision: it bought no
simulation and cost 0.2–0.5 ms/frame.

* Frozen-animator proof, resting wobble, bounds, restore and the clip: `jiggle-solver.md`.
* Clip: `tracks/bones/clips/bones-jiggle-solver.mp4` (300 frames, 19.95 s wall, encoded at
  15.04 fps); stills `tracks/bones/shots/jsolve-peak*.png`, `jsolve-rest.png`.
* The earlier `bones-jiggle-realtime.mp4` and its numbers belong to the retracted claim.
