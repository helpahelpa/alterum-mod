# proportions pass 2 — bigger chest/hips, and where the clothing stops us

Scope: `tracks/bones/mod/AlterumModelSwap.Hot/Proportions.cs` (tunables) and
`ProportionShots.cs` (new capture rig). Install `C:\AlterumT1`. Evidence in
`shots/proportions-pass2/`.

## Final values

| knob | previous | pass-2 first try | **shipped** |
|---|---|---|---|
| `chest_02_l/r` leaf scale | 1.45 | 1.80 | **1.55** |
| `pelvis` scale | 1.22 | 1.50 | **1.50** |

Both are recomputed from the same rule as before: every pelvis child is counter-scaled
`1/HipScale` and its local Y is divided by `HipScale` so the torso keeps its height and the feet
stay on the floor. The compensation is *derived* from `HipScale` (not stored), so changing the
constant cannot leave a ratio computed for the old value. Measured: thigh-root separation
0.2432 m -> 0.2990 m = exactly 1.50/1.22; head-to-foot bone height and shoulder-joint width are
unchanged (1.5284 m / 0.2935 m).

## Why not 1.80 (chest): visible poke-through

Same pose, same orthographic camera, Diana's tank top, chest scale sweep:

| leaves | front chest band | side depth | shirt |
|---|---|---|---|
| 1.45 | 0.480 m | 0.282 m | clean |
| 1.55 | 0.460 m* | 0.287 m | clean |
| 1.60 | — | — | clean (no skin pixel) |
| 1.62 | — | — | first 1-pixel skin poke at the strap |
| 1.70 | — | — | 2-3 px skin hole |
| 1.80 | 0.43 m* | 0.300 m | clear jagged skin hole |

(*) front width is dominated by the arms/strap and the two captures are different poses; the
side depth is the usable clothed measure. The skin is what grows; the tank top's chest verts are
weighted mostly to `chest_01`/spine, so the bare skin outgrows the fabric well before the clothed
silhouette reaches 1.8x. 1.55 is the value shipped with margin: the ceiling measured on this
outfit is 1.60, 1.62 is the first visible poke. Emma/Freya/Sarah were also clean at 1.60.

**Skirt**: Freya (`Female_2`, the plaid skirt cone) at 1.55/1.50 — the skirt widens with the hips
and shows no poke-through (front/side, `raw` has the run). The pelvis-child list includes the six
`Skirt_*` bones; their counter-scale keeps the skirt cone's own proportions.

**Hips 1.50 vs 1.55/1.60**: 1.55/1.60 were clean on Diana and Freya, but the pelvis scale is
applied to *every* rig, and at 1.55 the male (James) shirt waist pinches into an hourglass that
reads as a modelling error. 1.50 is the largest value that still reads as a deliberate
exaggeration on the male body.

## Jiggle

The solver is unchanged. A leaf scale does not move the leaf's origin: `chest_01 -> chest_02`
distance measured **0.0492 m at 1.45, 1.55 and 1.80** (identical), because only `localScale` of
the leaf changes. The Verlet particle, spring target, pendulum length and clamp are therefore
scale-invariant in bone space; the *rendered* excursion of the swollen volume grows with the leaf
scale (surface radius x1.069 at 1.55, x1.24 at 1.80 relative to 1.45).

- Old build, documented frozen-animator impulse 0.8 m/s: peak 16.3 deg / 38.1 mm, one visible
  overshoot, ~1.4 s settle (both leaves; `jiggle-solver.md`).
- New build, 0.8 m/s impulse with gravity disabled: `[jsolve:rate]` peak 21.651 deg / 35.87 mm on
  a 1.55 leaf rig, no clamps. The larger angle is the same spring released from a different
  frozen rest phase, not a scaling effect; the position channel (35.9 mm vs 38.1 mm) is within the
  phase/measurement spread. The leaf rotation trace in the *display clone* reports devOrigDeg=0
  while the applied rotation is ~30 deg (the clone has no animator controller), so its settle time
  is not claimed; the bone-space parameters were not changed because there is no rest-length
  change to compensate.
- No new clamp or instability: the 45 mm / 32 deg clamps were not reached in the clean impulse;
  the only clamps seen were from a reload-time re-read of a stale request (logged, recovered).

Conclusion: no solver retune is justified by the leaf-only change; the parameters stay at
2.0 Hz / zeta 0.33 / 45 mm / 32 deg / rotGain 0.5.

## Measurements (metres)

Bone measures (same frozen pose, exact):

| measure | previous | shipped | delta |
|---|---|---|---|
| hip width (thigh roots) | 0.2432 | 0.2990 | +0.0558 (+23%, =1.50/1.22) |
| shoulder width (upper-arm joints) | 0.2935 | 0.2935 | 0 |
| total height (head->lowest foot bone) | 1.5284 | 1.5284 | 0 |
| chest pivot->leaf (rest length) | 0.0492 | 0.0492 | 0 |

Rendered (orthographic side, same camera): chest depth 0.282 -> 0.287 m at 1.55
(+0.005); the 1.80 first pass measured 0.300 (+0.018) but visibly clipped.
Rendered body height 1.820 m both.

## Montages

- `before-after.png` — previous 1.45/1.22 vs first try 1.80/1.50, front/3-4/side, **same frozen
  pose and camera** (same spawn + snapshot, no reload in between).
- `previous-vs-shipped.png` — previous vs shipped 1.55/1.50; same camera/scene/lighting, pose
  re-captured after a hot reload so the arms/legs sit a few centimetres differently.
- `iterations.png` — first try vs shipped, same camera/pose family.
- `chest-closeup.png` — 1.80 vs 1.55 at 4x: the skin hole at 1.80, clean at 1.55.

## Capture rig

`ProportionShots.cs` (request file `propcap.request`, one-shot, polled at 2 Hz) provides:
`spawn=<PlayerModelData>` (instantiate a private display body), `freeze=1`, `posecopy=1`
(`worldrot=1` retargets by world orientation; `posefrom=<name>` selects another rig),
`views=front,three,side`, `breastscale=/hipscale=` (live `Proportions.SetScales`), `measure=1`,
`names=1`, `clips=1`, `play=`, `hide=/show=`, `release=1`. It renders an orthographic camera at
720x1080 on a flat background, frames from foot/head bones (not renderer bounds, which come back
in the wrong space on a fresh prefab), restores the pose snapshot before every view, and writes
`propcap_<tag>.txt` with the ortho parameters, metres-per-pixel and every landmark's pixel row.

## Regression checks on T1 (final build, 1.55/1.50)

- mirror: `[mirror:body-check] ... renderers=9 ... layer=14 ... isolated=9 driven=9` — the layer
  14 binding still works (local player body: Sheriff at this run).
- `grep -c SWAP LogOutput.log` = **0**.
- teardown: `[jsolve] shutdown: rigs=0 ... all offsets removed` then
  `[bounds:disposed] ... patchesRemoved=True`, next `[hot] generation N active`; no errors.
- The 12-clip emote sweep could **not** be run here: `PosePlayer`/`EmoteRuntime` are not part of
  the bones track (they live in `tracks/emotes` / `tracks/merged`, which isolation forbids loading
  here). Evidence that the arm conform input is unchanged: the upper-arm shoulder joints measure
  exactly the same before/after (0.2935 m) — the chest leaves are leaves and the counter-scaled
  children keep their subtrees. Leg IK roots do move outward (the hip widening itself) with Y
  preserved; the emote sweep must be run on the merged build to confirm the leg-IK conform.
