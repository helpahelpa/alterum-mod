# bones track — our own jiggle solver (replaces the dead MagicaCloth path)

Scope: the breast jiggle feature. `AlterumModelSwap.Hot/JiggleSolver.cs`, driven from
`ModelSwapRuntime.Update` (request + discovery) and `.LateUpdate` (solve + write). Install
`C:\AlterumT1`, hot reload only. Ground truth this builds on is the retraction in
`cloth-lifecycle.md` / `cloth-cleanup-evidence.log`: no MagicaCloth2 team ever steps in this build,
so the shipped cloth cannot be revived and is not used.

## Status

**Jiggle works, and it is now this solver, not the cloth.** The previous "9–15°" claim stayed
retracted; the numbers below come from a skeleton that provably cannot move without us (frozen
animator, no simulation), and from rendered frames, not just from a bone read.

## Decision: `ClothSim` is default OFF

* `ClothSim.DefaultEnabled = false`. It logs
  `[bounds:cloth-sim] DEFAULT OFF (module=…): … 0 simulation at 0.2-0.5 ms/frame of overhead …`
  once per module generation. `clothsim.request enabled=1` still re-enables it for a re-test, and
  removing the file now returns to the **default** (off), not to `on`.
* Reason: no simulation, pure per-frame bookkeeping (see the cost table in `cloth-lifecycle.md`).
* The class stays: it is reversible at the enum level, it documents the negative result, and the
  next build can re-test it with one config line.

## Design (what the code does)

Per female rig with `chest_02_l` / `chest_02_r`: one Verlet particle per leaf. `chest_01_*` is the
pendulum pivot and is **never written**.

* **Simulation space / anchor.** The particle lives in world space. Its spring target is
  `parent.TransformPoint(animatedLocalPosition)`, i.e. the animation pose as it stands *now*. Body
  motion, steps and turns move that target and the particle lags behind it, so response is
  parent-relative while nothing drifts, because the target is welded to the skeleton. (A pure
  world-space spring with a fixed rest point would drift; a pure local-space one would ignore body
  motion.)
* **Integrator.** Position Verlet in `Time.fixedDeltaTime` units (clamped to 1/240…1/30, at most 4
  substeps per frame, accumulator capped so a hitch cannot run away). Per substep: gravity
  (world `-Y`), an angular spring toward the animated pose, velocity damping, then two Lerp
  constraints — a soft pendulum **length** constraint (pull onto the sphere of radius
  `|anchor - pivot|`) and a small **soften** pull toward the anchor.
* **Write phase.** `LateUpdate`, after the game's animation has written the skeleton, and it is
  additive: it reads the animated `localPosition`/`localRotation` and writes
  `animated + offset`. The rotation write-back swings the bone from the animated direction toward
  the particle direction (`rotGain` of it, angle-capped), so the breast volume both shifts and
  tilts. Written *before* the mirror's LateUpdate render, so the mirror shows the current frame.
* **Own-write detection.** Each write is remembered. Next frame the live value is compared with
  it: identical (within 0.02 mm / 0.02°) means nothing re-wrote the bone, so the offset is
  subtracted before it is used as the animated pose; different means the animation overwrote it and
  the fresh value is used. Without this a frozen animator would fold our own offset into the anchor.
* **Ordering vs the scaling.** Attach requires `Proportions.KnowsRig(modelKey)`, i.e. the 1.45x leaf
  scale is already applied. The offset is therefore always on top of the established scale.
* **Gating.** No writes when: opt-out (`solver.request enabled=0`), the model is destroyed,
  `!activeInHierarchy` (despawn / dead), a bone or its parent is destroyed, or the rig is no longer
  in the `ThirdViewPlayerModel` scan. Every path strips the offset first and logs
  `[jsolve:detach]` / `[jsolve:gate]` / `[jsolve:restore]`.
* **Bounds.** Offset clamped to 45 mm, applied rotation to 32°, with a rate-limited
  `[jsolve:clamp]` warning naming the raw magnitude. Raw values seen: 113 mm (6 m/s impulse),
  928 mm (3 m teleport) — both clamped, both recovered.

### The dead cloth writes over us — and has to be switched off

First working version was silently erased: the trace showed a 200 mm offset while the rendered
frames were pixel-identical (`jsolve_who` / `jsolve_who2`, 200 mm gravity test: 2.4k px "difference"
= noise, and the frame-to-frame delta never moved). Cause: the MagicaCloth2 `Breast` BoneCloth never
simulates, but it still writes the pose it captured at build time **onto the leaves every frame,
after LateUpdate**. It is the only thing in the process that can fight the jiggle.

Fix: on attach, every `MagicaCloth` on the rig whose `serializeData.rootBones` contain a `chest_0*`
bone gets `SetSkipWriting(true)` (the prior value is captured and restored on release; the 1 Hz
rescan re-asserts it because a rebuild resets it). The component keeps running, so
`breastJiggleCloth=1 running=1` and `scaledLeavesInCloth=2/2` are unchanged.

Proof it was the gate: before, `held=0` on every frozen-animator frame and a 200 mm offset was
invisible; after, `held=1` in the frozen pose and the same offset visibly moves the shirt
(`shots/jsolve-peak*.png`).

## Parameters (effective values, logged on attach and on every request change)

Seeded from the reference mod's baked breast settings; the reference's numbers are unitless and its
integrator is not ours, so each is mapped to a rate by a constant in the file, and the mapping is
what `[jsolve:params]` prints.

| key | value | effective |
|---|---|---|
| `gravity` | 0.1 | 0.98 m/s² (≈6 mm resting sag at ω²=156) |
| `friction` | 0.4 | 8.0 1/s velocity damping |
| `angleElasticity` | 0.6 | ω² = 156 → 12.5 rad/s, **2.0 Hz**, ζ ≈ 0.33 |
| `blend` | 1.0 | write-back gain |
| `airDrag` | 0.01 | +0.3 1/s damping |
| `lengthElasticity` | 0.1 | 30 1/s radial (pendulum length) correction |
| `soften` | 0.1 | 0.1 1/s positional pull to rest |
| `rotGain` | 0.5 | fraction of the swing applied as leaf rotation |
| clamps | 45 mm / 32° | per-frame hard bounds, logged when hit |
| substep | `Time.fixedDeltaTime` = 20 ms | ≤4 substeps/frame, accumulator capped |

Tuned, not copied: 2.0 Hz with ζ 0.33 gives one visible overshoot and a ~1.5 s settle, and stays
under the sampling limit at the game's 15 fps cap (7.5 frames/cycle). The reference's raw numbers are
kept as the seed because they are the ones the task names; the scale constants are the tuning.

## Evidence

`jsolve_final_frozen2` (final build): `jiggle.request model=Female_01 freeze=1 follow=0`,
`solver.request gravity=0 maxmm=45 reset=1` (baseline), then `impulse=0.8`, then `enabled=0`.

### 1. Frozen animator — the decisive test

`freeze=1` disables the Animator; the pose is static (`geom` in the probe log identical for 14 s),
nothing else in the process writes the leaves (the cloth is on `skipWriting`), `gravity=0`, so the
leaf sits at exactly 0.000° and every non-zero number below is ours.

| frame | t (s) | dev (deg) | dev (mm) | off (mm) |
|---|---|---|---|---|
| 83 | 5.983 | 0.000 | 0.02 | 0.02 |
| 84 | 6.061 | 13.323 | 32.06 | 32.06 |
| 85 | 6.117 | **16.302** | **38.14** | 38.13 |
| 86 | 6.189 | 7.654 | 18.26 | 18.26 |
| 87 | 6.251 | 1.116 | 2.88 | 2.88 |
| 88 | 6.315 | 6.700 | 15.97 | 15.97 |
| 89 | 6.382 | 6.897 | 16.39 | 16.39 |
| 91 | 6.518 | 0.882 | 2.13 | 2.13 |
| 95 | 6.785 | 1.260 | 3.00 | 3.00 |
| 99 | 7.057 | 0.898 | 2.15 | 2.15 |
| 103 | 7.322 | 0.532 | 1.27 | 1.27 |
| 107 | 7.587 | 0.319 | 0.77 | 0.77 |
| 111 | 7.851 | 0.186 | 0.44 | 0.44 |
| 115 | 8.122 | 0.000 | 0.19 | 0.19 |

Peak 16.302° / 38.14 mm from a 0.8 m/s impulse, visibly ringing down for ~1.4 s to 0.000°.
Independent cross-check: JiggleProbe's own sample of the same leaves against its own frozen baseline
(`jsolve-probe-frozen.csv`) peaks at **16.92° / 39.4 mm** on frame 80 — two separate measurements of
the same event, ~4 % apart (different sample instants).
chest_02_r is the mirror image (same frames, same magnitude). Rendered frames agree: the chest
region's frame-to-frame difference jumps to 32.4k / 31.0k / 25.2k pixels at frames 81/84/87 and is
back to 7–60 px by frame 205.

Stills from that run (`shots/jsolve-peak.png`, `jsolve-rest.png`, taken from the clip run):
rest → peak shows the shirt front pushing out and up, then settling back.

### 2. Restore

`enabled=0` → `[jsolve:restore] rig='3P_ConcreteModel_Female_01(Clone)' reason='enabled=0'
restored=2 exact=2/2 verify=0.000000mm/0.000000deg chest_02_l:dOrig=0.004221mm/0.000000deg
chest_02_r:dOrig=0.006239mm/0.000000deg`.

i.e. the bones are written back to the exact animated values, and against the pose recorded at
`reset` the difference is 4–6 µm (Transform local→matrix round-trip, not a residual offset).
`[jsolve:reset]` shows the same. Teardown (hot reload) strips the same way before the bone pass
restores its own recorded transforms.

### 3. Animated lobby (`jsolve_video`, 300 frames / 19.95 s, achieved 15.04 fps)

* Idle + scripted body sway (`sway=0.05 m @ 1.3 Hz`, camera follows): `late_deg` mean 6.90°, max
  11.04° over the first 70 frames — a continuous resting wobble driven by the body motion, which is
  what a resting jiggle should be.
* Impulses 0.6 / 1.0 m/s peak at 19.64° / 45 mm (clamped) and settle back to the same baseline:
  frames 250–300 mean 7.39°, max 10.74°.
* No runaway: 3 clamp events in 20 s, all at the impulse peaks, `peakDev=19.644°`.

### 4. Instability checks (`jsolve_stress2`, final build)

| case | raw offset | clamped to | frames clamped | recovered |
|---|---|---|---|---|
| impulse 6 m/s | 113.0 mm | 45.0 mm | 2 | decays to 0 in ~1.5 s |
| teleport: rig root jumped 3 m for one solve | 928.2 mm / 913.3 mm | 45.0 mm | 5 | 22.6 → 10.7 → 4.8 mm in 5 frames, 0.000° by +0.9 s |
| long idle, 30.7 s (`trace=5`, animated, no sway/impulse) | dev mean 2.51°, **max 2.69°**, off max 6.41 mm | — | **0** | steady, no growth (`[jsolve:rate]` peak unchanged for 5 windows) |

Ampitude over all runs never exceeded the clamp (max `off` exactly 45.00 mm, max `dev` 20.57°).

### 5. Video

* `tracks/bones/clips/bones-jiggle-solver.mp4` — 640x360, h264, **300 frames over 19.95 s wall =
  15.04 fps achieved**, encoded at **15.04 fps** (true speed). `ffprobe`: `r_frame_rate=376/25`,
  `nb_frames=300`, `duration=19.946809`.
* `tracks/bones/shots/jsolve-peak.png` (clip frame 80: 19.64° / 45 mm) and
  `tracks/bones/shots/jsolve-rest.png` (frame 3: 0.00°), chest crop, same camera;
  `jsolve-peak-full.png` is the whole frame.

## Requests (`solver.request`, persistent, polled at 1 Hz)

`enabled=0|1`, `tag=`, `trace=0|1|N` (CSV `jsolve_trace_<tag>.csv`), `impulse=<m/s>`,
`impulseat=<s>`, `teleport=<m>` (one-shot root jump, instability check), `reset=1`, `dump=1`,
parameter overrides `gravity friction angleelasticity blend airdrag lengthelasticity soften rotgain
maxmm maxdeg`. Absent file = enabled with the defaults above (or with the last values it read).

## Evidence files left in the track

`notes/jsolve-trace-frozen.csv` (the frozen-animator impulse trace above),
`notes/jsolve-probe-frozen.csv` (JiggleProbe's independent per-frame sample of the same run),
`notes/jsolve-trace-stress.csv` (impulse + teleport + recovery), `notes/jsolve-trace-video.csv`
(the clip run). Raw CSVs also stay in `C:\AlterumT1\BepInEx\screens\t1\`.

## Guardrails

* `Proportions` constants untouched (`BreastLeafScale=1.45`, `HipScale=1.22`), transactional apply and
  `Restore` unchanged; `[bounds:verify] scaleDrift=0 posDrift=0 scaledLeavesInCloth=2/2` on every rig.
* `Mirror.cs` / `MirrorPlayerVisibility.cs` md5 unchanged (`08b77a17…`, `3f33c824…`).
* `AlterumModelSwap.Contracts` untouched (only the pre-existing `// Swap` block difference).
* The JiggleProbe cleanup fixes are untouched; the solver has its own freeze-free request surface, so
  the probe is only used for its camera and frame capture.
