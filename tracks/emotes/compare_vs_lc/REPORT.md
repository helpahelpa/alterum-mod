# Port vs real Lethal Company frames

Track `emotes`, install `C:\AlterumT2`, working copy `tracks/emotes`. Nothing outside this track was
touched; `AlterumModelSwap.Contracts`, `Mirror.cs`, `MirrorPlayerVisibility.cs` are unchanged.
Lethal Company was not started: all ground truth comes from the existing capture in
`../../lc-reference/`.

## 1. Verdict on the retired reference tools

`compare/scripts/ref_eval.py` and `tools/source_metrics.py` are now `*_RETIRED.py` (2026-11-12),
with a header, a banner in `compare/REPORT.md`, `compare/ERRORS.md` and `emotes-port.md`, and the
index `RETIRED.md`. Do not use them or their outputs for anything; the half-scale reason is the
first one below.

**Retire it as ground truth; keep it as a rough shape check for looping clips only.** It is
fixable in principle, but three of its assumptions are wrong and one of them makes every number it
emitted wrong by 2x. Evidence (numbers in `EVIDENCE.txt`, pictures in `evidence/`):

1. **It is at half scale.** The retail prefab is `ScavengerModel` (scale 2.0) -> `metarig`
   (scale 0.5608); world scale is therefore 1.1216. `ref_eval.fk()` multiplies local positions by
   0.5608 only. Every position it writes is exactly 0.500x the game's world: ref
   pelvis->neck 0.435 vs the pack's 0.7756 (ratio 1.783 = 1/0.5608), ref arm reach 0.389 vs 0.694.
   The whole comparison chain (`compare/ERRORS.md`, the 0.760/1.559/0.816 correction factors, the
   "body height 1.56 m") is built on those half-size positions, so its centimetre columns are not
   centimetres.
2. **Its arms are in the wrong place.** With the camera fitted on the torso and legs only (2-3 px
   residual) and the ref's skeleton drawn on the real frame, the ref's hands cross at the *belly* in
   `Clap` while the real hands are at the sternum, its forearms lie at hip height pointing sideways
   in `The_Griddy` while the real forearms are bent up in front of the chest, and its hands hang in
   front of the hips in `Twerk` while the real hands are at the sides of the thighs. The old claim
   "the port's arms are in the wrong place" was measured against this.
3. **It plays clips that the game never plays.** `Salute` and `Middle_Finger` are non-looping
   (`loop=False` in the pack): the game runs the emote state past the clip end and *holds the last
   frame*. The capture confirms it (35/26 distinct images out of 75; the raised arm's pixel count is
   constant to +-1 over 1.7-3.4 state loops), while the reference predicts 53.2/51.3 cm of joint
   motion. Sampling our port at those phases is not a comparison.
4. **What it is missing, mechanically:** the procedural rig (Animation Rigging runs after the
   animator): `Rig 1/LookHead` and `LookHead2` MultiRotationConstraints on neck/head, the
   `Left/RightArmNotTorsoRelative` ChainIK pair, the leg hints/poles, and LC's own chain solve order.
   It picks the elbow plane from the pre-IK pose. It also has no notion of the one-shot hold.
5. **What it is *not* missing (hypothesis ruled out):** the emote animator layer. The local
   controller (`NEWmetarig`) has 6 layers; every layer's `(int&)m_LayerBlendingMode` is 0
   (**Override**, not additive) and the body mask is full (0xFFFFFFFF/0xFFFFFFFF/0x7FFFF). Layer 1's
   serialized `m_DefaultWeight` is 0 because the game raises it; the pose is the clip, not a blend.
   `Clap`'s and `Twerk`'s real frames agree with the pack's *shape*, so the authored curves are not a
   different revision either.

Where the reference and the real frames agree, the port agrees too: `Middle_Finger` (left hand on
the hip, right arm out with the finger up), `Clap` (hands together at the chest), `Salute` (end
pose), `Twerk` and `The_Griddy` in gross pose. So the port's retarget/IK is *better* than the old
table said.

## 2. Capture protocol (new, verification only)

`mod/AlterumModelSwap.Hot/EmoteRuntime.cs` and `PosePlayer.cs` gained a request-driven matched-phase
capture:

* `clip=<Clip> repeats=0 cap=<tag> phases=<p,p,...> camyaw=<deg> camdist=<m> camaim=<m> camfov=<deg>`
* `PosePlayer.FreezeAt(phase)` pins the clip at `phase*duration` with full weight and no advance, so
  a frame is shot at exactly the requested normalised time. Normal playback is untouched.
* `CaptureMatch` renders 960x540 at FOV 42, camera 4.2 m away, +0.2 m up, 25 deg off the model's
  forward (LC's reference geometry), culling everything but the body layer (LC's `onlybody`), to
  `BepInEx/screens/t2/emote_cap_<Clip>_<tag>_NNN.png`.
* Phases are resampled by **normalised clip time** from the LC CSVs
  (`scripts/lc_phases.py`), never by frame index.

## 3. Sheets and video

One PNG per emote, 8 matched pairs, normalised time labelled on every pair, LC left / port right,
equal standing height and a fixed camera per clip (so proportions are deliberately not comparable):

```
compare_vs_lc/sheets/Clap_vs_LC.png          487 KB
compare_vs_lc/sheets/Twerk_vs_LC.png         518 KB
compare_vs_lc/sheets/The_Griddy_vs_LC.png    563 KB
compare_vs_lc/sheets/Prisyadka_vs_LC.png     496 KB
compare_vs_lc/sheets/Salute_vs_LC.png        107 KB   (held-pose sheet, see below)
compare_vs_lc/sheets/Middle_Finger_vs_LC.png 122 KB   (held-pose sheet)
compare_vs_lc/port/<Clip>/emote_cap_<Clip>_lc_NNN.png   16 phases each, raw 960x540
```

`compare_vs_lc/lc_vs_port.mp4` alternates LC and port at the same phases (title card per emote,
"held last frame" where the game holds): H.264, 800x362, 4 fps, 120 frames, 30.0 s, 465 696 B
(re-cut from the post-fix captures in §4b; ffprobe in `EVIDENCE.txt`).

## 4. Iterations

**Iteration 1** (`sheets/*_vs_LC.png` as first composed) paired every capture phase with an LC frame
at the same layer phase for all six clips. Reading: `Clap` and `Middle_Finger` match; `Salute` shows
our arm down at phases 0.11-0.25 that the game never displays; `Twerk`/`The_Griddy` match in gross
pose with our arms hanging a little wider/lower; `Prisyadka` is the one clear mismatch (our hands
~0.2 m higher and ~0.1 m further forward than the game's crossed-arms squat).

**Iteration 2** fixed the comparison, which is what the first pass got wrong:

* non-looping clips now get a held-pose sheet (LC's single held frame vs our clip-end pose) plus a
  strip of our clip at the phases the game never shows, instead of pretending those phases pair;
* the crop is anchored to the feet with a fixed per-clip body height, so a raised arm no longer
  changes the scale of the panel;
* 16 phases are captured per clip (8 are shown) and the video is cut from the same panels.

**Pose changes: none.** Every candidate change (arm IK anchor/scale, leg IK target height, the arm
rest conform, the pelvis translation) measured, in the fresh sheets, either in the port's favour or
inside the reference's own error bar, and the two largest disagreements (Prisyadka's arm height,
Griddy's arm width) are reproduced by the reference itself, i.e. they are data/rig, not retarget.
Changing them would have been chasing the retired table. That is a deliberate "changed nothing" per
the task, not an omission.

## 4b. Iterations 3-4 (this pass): Prisyadka, and the retired reference

**Retired:** `compare/scripts/ref_eval.py` -> `ref_eval_RETIRED.py`, `tools/source_metrics.py` ->
`source_metrics_RETIRED.py`, both with a header that names the **half-scale** bug (the retail
`ScavengerModel` 2.0 factor is missing, so every ref_eval position is 0.5x LC's world), plus a
banner in `compare/REPORT.md`, `compare/ERRORS.md`, `emotes-port.md` and
`compare/ref/HALF-SCALE-DO-NOT-USE.txt`. `RETIRED.md` at the track root is the index. Nothing in
this pass used their numbers.

### 4b.1 Per-clip arm conform (`armconform`, default on)

New manifest line `armconform <clip> 0|1`; the pack loader defaults to 1 and `PosePlayer` now keeps
the raw and the conformed arm anchors separately, so the decision is per clip (a request line
`armconform=0|1` forces it for an A/B). Prisyadka measured with it **off** (`port_before`/`e0`
captures): the hands rise beside the head at every phase, worse than with it on. **All six clips
keep `armconform=1`**, including Prisyadka; Clap/Twerk/The_Griddy were re-captured with it on and
their hand separation is unchanged.

### 4b.2 Why the arms were still overhead with the conform on

Prisyadka is the only sheeted clip whose arm IK weight is 0 on both sides, so its arm pose is the
authored bone chain. The prefab carries **two** arm skeletons: the third-person
`metarig/spine.003/shoulder.*` (TP) and `ScavengerModelArmsOnly/metarig/spine.003/shoulder.*` (AO,
the first-person arms model). The emote animates both with different values. The TP shoulder swings
**139 deg** across the clip while the AO chain keeps the forearms crossed in front of the chest —
which is exactly what all 75 real frames show, at every phase (the AO hands are ~constant:
(-0.02, 0.14, 0.32) / (-0.04, 0.08, 0.31) relative to its own spine.003, spread <1 cm). The port was
playing the TP arm curves.

Fix: a per-clip **arms-only source**. `extract_emotes.py` bakes the eight AO arm bones' global
rotation deltas (relative to the AO rest, emitted as `aobone`/`aorest`/`armao`), and `PosePlayer`
drives those bones from that blob with its own rest-frame conform (`_conformAoRot`, 38-71 deg)
whenever the clip has the bake. The TP curves are then ignored for those bones. Only clips with
both arm IK weights 0 are baked (`AO_ARM_CLIPS = ("Prisyadka",)`, checked with `ik_audit`).

### 4b.3 Legs: which error it was

Candidates measured one at a time, on the real frames and the joint dump:

* **leg IK target mapping — correct.** `legik Pos * c.Scale` lands exactly on LC's proportional
  foot position: planted ankle 0.22 m world, raised ankle 0.70 m, matching the alternating leg lift
  in the frames. `err=0.000` at every sampled phase.
* **leg target rotation — not the cause.** It only orients the foot (`legtiprot`); disabling it
  changes nothing about the fold.
* **clamping — not the cause in steady state.** `dist 0.23..0.52 m` against `reach 0.853 m`; the
  cumulative clamp counter (61) accrues during the fade-in only.
* **the knee plane was the pose error.** LC's `TwoBoneIKConstraint` has a `Leg_hint` pole node;
  the port ignored it and projected the current thigh direction onto the plane perpendicular to
  the target. In a deep squat the thigh and the target are nearly parallel, so that projection
  collapses and the knee folded under/inside the hip (the "legs folded up" look). The hint is now
  baked (`leghint <clip> <side> x y z`, model-root relative, constant) and used exactly like
  Unity's constraint: the bend plane contains root, target and hint. The cumulative clamp counter
  (61) is all from the fade-in, when the hips are still standing while the targets are already at
  the crouch position; every steady-state sample is `err=0.000`, `dist 0.23..0.52 < reach 0.853`.
* **the pelvis offset channel had a real scale bug.** `ComputeLegScale()` measured
  pelvis->foot from `_anchorPos`, which are local positions in *different parent frames*, giving
  0.811, while the leg IK targets use the leg-chain ratio 0.874. The hips were therefore held
  ~3.8 cm too high relative to the feet the IK planted. The pelvis offset now uses the leg-chain
  ratio (same 0.874): pelvis local y 0.462 -> 0.424 at phase 0.272, and 0.42-0.51 over the clip
  against a standing 1.038.

### 4b.4 Twerk / The_Griddy

Left alone. Both are arm-IK-driven, so their hand placement is the baked target mapping, and the
residual ~0.15 m (Twerk, forward) / ~0.1 m (Griddy, separation) is the target anchored at our
`spine_03` whose pelvis->neck length differs from the source's scaled one (0.539 m vs 0.678 m,
`compare/REPORT.md`). Changing the anchor or the reach scale for them would move Clap's clap and
Middle_Finger's finger as well, i.e. it risks the four good clips, so it was not done. Before/after
captures at the same phase are identical within the crop.

## 5. Honest per-emote state

Measured against the real frames in `sheets/` after the Prisyadka fix (see §4b).

| emote | state |
|---|---|
| Clap | **matches**: hands together in front of the upper chest through the cycle; our elbows sit ~5 cm lower. |
| Middle_Finger | **matches**: left hand on the hip (IK-pinned), right arm extended with the finger up, same torso twist; the game holds this pose, ours reaches it at the clip end. |
| Salute | **end pose matches** (right hand at the helmet, left hand at the hip); the motion our clip plays at phases 0.1-0.3 is not observable in the capture. |
| Twerk | **close**: same 38-41 deg forward lean and hand height; our hands sit ~0.15 m further forward of the thighs (unchanged this pass, see §4b.4) and the skirt hides the knee bend. |
| The_Griddy | **close**: same bob, same arm height band; our hands separate ~0.1 m more and our elbows point out where the game tucks them (unchanged this pass). |
| Prisyadka | **now matches the shape**: a deep squat with both forearms crossed on the chest, body leaning back, pelvis at 0.42-0.51 m above the root (standing 1.04 m), hands 0.12-0.14 m apart at the sternum, stable across all 16 phases. What still differs is the *skirt*: it hides the knees and the alternating leg lift, and it intersects the thighs (game geometry, not retarget). |

Protected basics unchanged: clip durations (`manifest.txt` `clip` lines byte-identical), ownership
lease (`released=True` on all 12 sweep steps), mirror (RT dumps continue), the five gates
(`pelvis3`/`tiprot`/`legik`/`legtiprot`/`claviconform`) still logged with the same defaults. The
jiggle/cloth gate lives in the other track's module and was not touched. Regression run after the
change: `RESTORE CHECK ... max bone change over 0.4 s = 2.64deg` (2.32 before), 12x `released=True`,
no `PORT_PACK_INVALID` / `EMOTE_LATE_WRITER` / `EMOTE_OWNERSHIP_FAILED`.

## 5b. Known visual differences that are NOT retarget bugs

* **The skirt.** Our character wears a long plaid skirt; LC's player wears a spacesuit with
  separate legs. In a deep squat the skirt is a near-rigid cone around the pelvis, so it (a) hides
  the knees and the alternating leg lift and (b) intersects the thighs, and the lifted boot appears
  to float free of it (`sheets/Prisyadka_vs_LC.png`, and the side/front diagnostic captures
  `emote_cap_Prisyadka_{side,front}_*.png`). The bone pose under it is correct: the joint dump has
  the planted ankle at 0.22 m and the raised ankle at 0.70 m with the knee out, exactly the
  alternating leg lift the frames show. This is game geometry, not the port.
* **Torso proportions.** The target rig is 0.539 m pelvis->neck against the source's 0.678 m
  (scaled), so the crossed forearms sit ~5-8 cm lower on our chest than on LC's, with the same
  angles.
* **No look-head / elbow hints on the arms.** LC also runs `LookHead`/`LookHead2` and elbow hints;
  those are not ported, so our head keeps the clip's authored neck/head curve instead of looking at
  the camera and our elbows use the chain's current plane (LC's arm constraints do not have a hint
  either, so this is only the head).

## 6. Reproduce

```bash
python3 compare_vs_lc/scripts/lc_phases.py 16               # phases.json (normalised time)
./compare_vs_lc/scripts/capture.sh Clap lc 25 "0.99,0.075,..."   # writes emote_cap_*.png
CLIPS=Clap,Twerk,The_Griddy,Prisyadka python3 compare_vs_lc/scripts/compose2.py  # subset
python3 compare_vs_lc/scripts/compose2.py                   # all six sheets (<=600 KB each)
COMPOSE_MAIN=0 python3 compare_vs_lc/scripts/make_video.py  # lc_vs_port.mp4
EXTRA="armconform=0" ./compare_vs_lc/scripts/capture.sh Prisyadka a0 25 "$PHASES"  # A/B switch
EXTRA="leghint=0"    ./compare_vs_lc/scripts/capture.sh Prisyadka nh 25 "$PHASES"
python3 tools/extract_emotes.py emotes                      # pack (aobone/aorest/armao/leghint/armconform)
```

`capture.sh` now forwards an `EXTRA` block of request lines (`armconform`, `leghint`, ...) so the
same install can A/B a switch without a rebuild. The retired overlay script is kept only as the
record of how `ref_eval_RETIRED`'s arms were shown to be wrong: `scripts/ref_overlay.py`.
