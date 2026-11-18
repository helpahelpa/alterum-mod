# LC emote port vs real Lethal Company — comparison report

Track `emotes`, install `C:\AlterumT2`, working copy `alterum/tracks/emotes`.
Nothing outside this track was modified. `AlterumModelSwap.Contracts`, `Mirror.cs` and
`MirrorPlayerVisibility.cs` are md5-identical to the baseline. Lethal Company itself was not run;
the comparison uses its extracted assets.

## 1. What the real LC install gave us

Extracted `Player/ScavengerModel/metarig` from `Lethal Company_Data/sharedassets3.assets`
(`compare/lc_rig.json`, `compare/scripts/extract_lc_rig.py`), plus the player body bind pose from
the `SkinnedMeshRenderer` bindposes (`Mesh "Circle"`, 43 bones; `compare/lc_bind.json`,
`compare/scripts/lc_bindpose.py`).

* **Hierarchy and names are correct in the old copy.** 163 transforms under `metarig`, 43 skinned
  bones; every clip path hash still resolves, and the six CRC32 fixtures still match.
* **The bundle copy is not a rest pose, and neither is the retail prefab.** The retail prefab's
  serialized pose is a mid-animation frame: against the A-pose copy, its world bone positions differ
  by 5.6 cm at the pelvis and 20–52 cm at the hands/feet, with 85–165° world rotation differences on
  the limbs. The mesh bind pose is a T-pose (arms straight out, legs down); the copy is an A-pose
  (arms down). The target model's bind is an A-pose, so the **copy remains the right retarget rest**;
  switching the whole tooling to the retail prefab pose would bake arbitrary offsets. The retail
  **bind pose** is worse for retargeting for the same reason. What the install did give us:
  * the **missing `Rig 1` target subtree** (95 nodes: leg targets/hints, arm target/hint layout) that
    the MoreCompany copy does not contain at all — without it the leg IK could never be baked;
  * the actual constraint components (see below);
  * the real hierarchy for the canonical reference.
* **The arms and legs are IK-driven by Unity Animation Rigging.** Decoded the components on
  `metarig/Rig 1/*`:
  * `ChainIKConstraint` L/R: `root=upperarm`, `tip=hand`, `target={Left,Right}Arm_target`,
    `m_Weight=1`, **`chainRotationWeight=1`, `tipRotationWeight=1`**, maxIterations=15,
    tolerance=1e-4, maintain-offsets=false.
  * `TwoBoneIKConstraint` L/R: `root=thigh`, `mid=shin`, `tip=foot`,
    `target={Left,Right}Leg_target`, hint weight 0, **targetPositionWeight=1,
    targetRotationWeight=1**, weight 1.
  * The clips animate both constraint weights (constant 1 on every ported clip) and both targets'
    positions **and rotations** (euler curves). The port's first four passes only used arm target
    positions and never applied target rotations; the legs were not IK-driven at all.

The currently installed target is `3P_ConcreteModel_Female_02(Clone)` / `Cowboy_Girl_1Avatar`
(UE names, same 51 mapped bones), not `SK_Cowboy_2Avatar`. Its proportions differ from LC's:
arm reach 0.527 m vs source 0.694×0.760 = 0.527 m (matched by the retarget scale), leg reach
0.853 vs 0.976×0.874 = 0.853 (matched), but clavicle→upperarm 0.151 m vs source 0.073×0.760 =
0.055 m, and pelvis→neck 0.539 m vs source 0.435×1.559 = 0.678 m. The hand/arm IK triangle
therefore cannot land in the same place even with perfect rotations.

## 2. Canonical-joint pipeline

* **RETIRED:** `compare/scripts/ref_eval_RETIRED.py` — offline reference, 60 Hz, whole clip.
  It is at **half scale** (LC's world is `ScavengerModel` 2.0 x `metarig` 0.5608 = 1.1216; it
  applied only 0.5608, so every position it emits is 0.500x LC's world) and its arm rest is the
  bundle's, not the retail rest, so its arms are wrong too. Its outputs (`compare/ref/*.csv`,
  `compare/ref/ref_meta.json`, `compare/errors*.json`, this directory's `ERRORS.md`) are
  **not usable**. Kept only as a historical record; see `../RETIRED.md` and
  `../compare_vs_lc/EVIDENCE.txt`. Source FK from the real
  hierarchy, A-pose local rotations where the clip has no curve, then LC's arm ChainIK and leg
  TwoBoneIK with the decoded weights and tip rotations. Emits 22 joints (pelvis, spine_01..03,
  neck_01, head, clavicles, upper/lower arms, hands, thighs, calves, feet, balls) as world positions
  and world rotations, plus the pelvis root offset, to `compare/ref/<clip>.csv`.
* Mod dumper — `emote.request` with `dump=1` writes `emote_joints_<clip>.csv` (same 22 joints,
  world positions/rotations, root, fade) from `LateUpdate` after the pose write. Gated per request;
  no change to the pose path when off.
* `compare/scripts/compare_joints.py` — maps both into the body frame (pelvis origin, model-root
  orientation, target units, one scale from the leg segment sum). Reports body-frame position error,
  parent→child segment direction error, rest-corrected absolute orientation error `dq` (cancels the
  two rigs' bone-axis conventions), and animated pelvis range per axis. Full per-clip rankings:
  `compare/ERRORS.md`; raw: `compare/errors_baseline.json`, `compare/errors_fixed.json`.

## 3. Ranked errors and the fixes

Baseline = previous build, all fixes off. Fixed = `pelvis3=1 tiprot=1 legik=1 claviconform=0`
(root-relative IK anchoring is always on in the fixed build). Body height 1.56 m.

| clip | frames B/F | pelvis range err cm B→F | hand dq deg B→F | foot/ball dq B→F | leg pos cm B→F | arm pos cm B→F |
|---|---|---|---|---|---|---|
| Clap | 17/19 | 0.1→0.0 | 40.8→12.7 | 26.3→23.8 | 41→34 | 43→43 |
| D_Clap | 17/19 | 0.1→0.0 | 40.9→12.7 | 26.3→23.8 | 41→34 | 43→42 |
| D_Middle_Finger | 5/6 | 0.6→0.7 | 61.6→4.6 | 21.0→18.6 | 48→37 | 65→65 |
| D_Sign | 40/40 | 5.3→0.4 | 12.0→12.1 | 26.7→26.7 | 45→45 | 58→58 |
| Idle1Fix | 51/52 | 1.9→0.4 | 20.5→13.9 | 53.0→52.9 | 33→35 | 46→46 |
| Middle_Finger | 5/6 | 0.6→0.8 | 94.0→11.1 | 21.0→18.6 | 48→37 | 53→53 |
| Prisyadka | 92/93 | 8.9→1.6 | 10.9→10.9 | 36.5→33.8 | 84→60 | 32→32 |
| Salute | 19/19 | 3.4→0.7 | 28.4→13.2 | 19.4→12.9 | 29→34 | 45→45 |
| Shy | 30/31 | 2.5→0.5 | 94.2→15.3 | 17.0→18.7 | 39→35 | 50→50 |
| Sign | 24/24 | 0.6→0.1 | 17.0→17.0 | 26.7→26.7 | 44→44 | 55→55 |
| The_Griddy | 89/89 | 12.4→2.3 | 88.8→14.7 | 31.3→15.3 | 61→49 | 56→56 |
| Twerk | 26/25 | 18.2→0.7 | 132.4→20.3 | 33.8→32.7 | 69→54 | 54→55 |

| clip | worst position (cm) | worst dq (deg) | worst segment (deg) |
|---|---|---|---|
| Clap | ball_l 52, hand_r 49, ball_r 48 | ball_l 47, ball_r 45, hand_l 19 | clavicle_l 113, foot_l 90, upperarm_l 82 |
| D_Clap | ball_l 52, hand_r 49, ball_r 48 | ball_l 47, ball_r 45, hand_l 19 | clavicle_l 113, foot_l 90, upperarm_l 82 |
| D_Middle_Finger | hand_r 80, hand_l 75, ball_l 73 | ball_l 41, calf_l 38, ball_r 34 | clavicle_l 113, upperarm_r 92, foot_l 90 |
| D_Sign | hand_r 71, hand_l 60, ball_l 59 | calf_r 38, calf_l 36, foot_r 33 | clavicle_l 111, foot_r 108, lowerarm_r 89 |
| Idle1Fix | ball_l 54, ball_r 50, lowerarm_l 48 | ball_r 111, ball_l 101, calf_l 42 | clavicle_l 113, foot_l 91, foot_r 82 |
| Middle_Finger | hand_l 75, ball_l 73, foot_l 57 | ball_l 41, calf_l 38, ball_r 34 | clavicle_l 113, foot_l 90, upperarm_l 90 |
| Prisyadka | ball_r 77, ball_l 69, calf_l 68 | ball_l 69, ball_r 63, thigh_r 36 | clavicle_l 115, thigh_l 94, upperarm_r 85 |
| Salute | lowerarm_r 57, ball_l 54, ball_r 48 | ball_l 28, calf_l 23, ball_r 22 | clavicle_l 113, foot_l 92, foot_r 84 |
| Shy | hand_r 59, hand_l 49, ball_l 47 | ball_l 37, ball_r 31, calf_r 26 | clavicle_l 111, foot_l 89, foot_r 82 |
| Sign | hand_l 61, ball_l 60, lowerarm_l 58 | foot_r 33, calf_l 33, foot_l 33 | clavicle_l 111, foot_r 108, lowerarm_l 92 |
| The_Griddy | ball_r 78, ball_l 72, hand_r 66 | ball_r 39, thigh_l 19, thigh_r 19 | clavicle_l 106, neck_01 89, foot_l 85 |
| Twerk | ball_l 79, ball_r 76, calf_l 63 | ball_r 65, ball_l 58, calf_l 28 | clavicle_l 112, thigh_l 92, foot_l 90 |

Full per-joint tables (all 22 joints, baseline and fixed): `compare/ERRORS.md`. The worst remaining
items are the toe (`ball`) orientation after the foot tip fix, the knee plane (`thigh/calf`
segment), and the clavicle/upperarm segment bias from the target's wider shoulders.

`D_Sign`/`Sign` are unchanged by the tip fix because their arm constraint weight is 0 (LC does not
drive their arms through IK). Isolated Clap changes on the fixed build:

| variant | hand dq L/R | foot/ball dq | leg pos | pelvis range err | clavicle seg |
|---|---|---|---|---|---|
| all off (prev build) | 26.7 / 54.9 | 31.5 / 32.5 | 41 cm | 0.08 cm | 113° |
| `pelvis3` only | 26.8 / 55.8 | 31.5 / 32.5 | 41 | 0.01 | 113° |
| `tiprot` only | **<20** / **<20** | 31.6 / 32.5 | 41 | 0.08 | 113° |
| `legik` only | 26.3 / 56.4 | 23.7 / 28.7 | **37** | 0.08 | 113° |
| `claviconform` only | 26.2 / 56.4 | 31.6 / 32.5 | 41 | 0.08 | **90°** (hand r pos 45.6→53.5, clavicle dq 14→32–35) |
| all fixed (legtip on) | **19.0** / 6.7 | **46.9 / 44.7** (ball) | **33.7** | 0.0 | 113° |

What that says:

1. **Pelvis 3-axis translation (real, fixed).** The old code added the source's vertical offset to
   the target pelvis' **local Y**, which on this rig is the body-forward axis, and dropped X/Z. The
   clips animate `spine` position on all three axes; the horizontal component is only >1 cm on
   `Middle_Finger`/`D_Middle_Finger` (8.9 cm forward), `D_Sign` (0.56 cm) and `Twerk` is a constant
   offset whose local-axis error alone was 18 cm. `Pelvis3` maps the full source offset through the
   same basis `C`, into the pelvis parent's frame, scaled by the leg ratio. Pelvis range error drops
   to 0–2.3 cm; the residual is the constant rest offset, not motion.
2. **IK target rotation (real, the biggest visual error).** `tipRotationWeight=1` in LC means the
   hand's world rotation is the target's rotation. The port carried the target rotation but never
   applied it. It is now applied as a rest delta,
   `C * (targetRot * srcTipRest^-1) * C^-1 * anchor`, so the target's bone-axis convention is not
   imposed on a differently-oriented bone. Mean hand dq falls 40–132°→5–20° on 10/12 clips
   (`D_Sign`/`Sign` have weight 0 and are correctly unchanged). This is the fix that removes the
   twisted-wrist look.
3. **Leg IK (not in the suspect list, but the evidence is unambiguous).** LC drives thigh/shin/foot
   through `TwoBoneIKConstraint` (both weights 1); the port ignored the leg targets because the
   bundle copy does not contain them. Added the same solve, a root-relative target (the feet stay
   planted while the hips translate), and the foot tip rotation. Leg position error drops on 10/12
   clips (`Prisyadka` 84→64 cm, `The_Griddy` 61→53, `Clap` 41→37). It is **slightly worse** on
   `Idle1Fix` (33→40) and `Salute` (29→37) because their leg targets are close-in and the target's
   leg is short; flagged, not hidden.
4. **Clavicle conform (does not matter; ruled out).** Enabling it improves the clavicle segment
   direction (113→90°) but the hand's rest-corrected orientation goes 14→32–35° and the right hand
   position 45.6→53.5 cm; the shoulder is set by this model's 0.151 m clavicle, not by the clavicle's
   rest direction. Kept off (`claviconform=0`), measurable with `claviconform=1`.
5. **Clamping (unchanged, geometric).** The reference itself clamps 91 arm frames / 29 leg frames on
   `Salute` and 32 arm frames on `Twerk`; the goals are beyond the source's reach too (Twerk
   1.03–1.17×, Salute's left target 3.6 cm beyond). The port's shorter arm stops short by the same
   order. This is not a solver defect; making it reach would require stretching bone lengths.
6. **Non-arm rest bias (still the dominant positional residual).** Spine/clavicle/legs were never
   rest-conformed. Body-frame position errors remain 30–80 cm at hands/feet and segment errors
   70–113° at the clavicle/upperarm because the target rig's shoulder is 9.6 cm wider than the
   scaled source, its torso is ~14 cm shorter, and the knee plane is chosen from the port's pre-IK
   pose. The root-relative IK anchor (extra change) removes the clavicle/hip length from the
   target *direction* but cannot change the bone lengths; position errors barely moved. Conforming
   all bones would re-baseline the whole skeleton and was out of scope.
7. **Layers/phase (partly measured).** The canonical set includes neck/head; `head` is the port's
   head base vs LC's head top, so its position is excluded from the ranking. `spine.004` (neck) has
   no clip curve in most clips and LC's `LookHead` MultiRotationConstraints (weights 0.45/1.0) are
   procedural, not in the clip; the reference cannot reproduce them, and neck segment errors reach
   77–88° on `Sign`/`The_Griddy`. Fingers were not in the canonical set; the pack still carries them.

## 4. Visuals

Per-clip contact sheets (1500 px wide, all ≤600 KB), reference/port stick figures front+side plus
the actual ported character for the same phases:

```
compare/sheets/Clap_compare.png
compare/sheets/D_Clap_compare.png
compare/sheets/D_Middle_Finger_compare.png
compare/sheets/D_Sign_compare.png
compare/sheets/Idle1Fix_compare.png
compare/sheets/Middle_Finger_compare.png
compare/sheets/Prisyadka_compare.png
compare/sheets/Salute_compare.png
compare/sheets/Shy_compare.png
compare/sheets/Sign_compare.png
compare/sheets/The_Griddy_compare.png
compare/sheets/Twerk_compare.png
```

Animation: `compare/emote_compare.mp4` (12 clips × 2 s, 12 fps, reference blue / port orange,
front+side). The actual frames are captured by the existing verify camera (768×768) and are also in
`BepInEx/screens/t2/emote_<clip>_r<run>_f*.png`.

The real-character frames show the current pose: Clap's hands are together at the chest; Shy and
Griddy hands are no longer twisted. The actual-run labels in the sheets are the run numbers from
`LogOutput.log` (the `fixes[...]` line says which gates were on); the numeric table is the
authoritative before/after evidence, the RT strip is illustrative. The skeleton overlays still show the reference's arms/legs
in different absolute places because of the rig proportions listed above.

## 5. Verification

* Twelve clips run end-to-end through `emote.request`; the dump files exist for all twelve; no
  `PORT_PACK_INVALID`, `EMOTE_LATE_WRITER` or `EMOTE_OWNERSHIP_FAILED` in the final log.
* Pack compatibility: `emote.bin` first 1,575,876 bytes are byte-identical to the pre-IK-coverage
  pack, and `manifest.txt` first 143 lines are identical; the new `pelvis3`/`legik`/`ikroot`/
  `legroot` blobs and lines are appended. Contract/Mirror md5s OK.
* Clip durations, frame counts, weight curves, ownership lease and release, the jiggle gate and the
  mirror code are untouched. Only `EmotePack.cs`, `PosePlayer.cs`, `EmoteRuntime.cs`, the extractor
  and new comparison tooling changed.
* Regression gates: `pelvis3=0|1 tiprot=0|1 legik=0|1 claviconform=0|1` per request, logged as
  `[emote:request] fixes ...` and `[emote:rig] fixes ...`.

## Reproduce

```bash
python3 tools/extract_emotes.py                       # pack: pelvis3 + leg IK + root-relative targets
python3 compare/scripts/ref_eval.py                   # reference canonical joints (compare/ref/)
./track.sh emotes build && ./track.sh emotes deploy
cp emotes/{manifest.txt,emote.bin} /mnt/c/AlterumT2/BepInEx/plugins/AlterumEmotes/
compare/scripts/run_port.sh fixed pelvis3=1 tiprot=1 legik=1 legtiprot=1 claviconform=0
python3 compare/scripts/compare_joints.py --tag fixed
python3 compare/scripts/render_contact.py
python3 compare/scripts/render_video.py
```

Gates: `emote.request` keys `pelvis3=`, `tiprot=`, `legik=`, `legtiprot=`, `claviconform=`, `dump=`; the values are
echoed as `fixes[...]` in the log. `dump=1` writes `emote_joints_<clip>.csv` to
`C:\AlterumT2\BepInEx\screens\t2\`; `shot`/`ClipRecorder` are not needed for the dumps.

## 6. Honest state

The three requested fixes are implemented and measured: hand orientation, pelvis translation and the
excluded clavicle. The hand fix is large and visually clear; the pelvis fix is real on
`Middle_Finger`/`D_Sign` and removes an 18 cm local-axis error on `Twerk`; the clavicle does not
help and is off. The extra leg IK is the second-largest improvement. The emotes **read** correctly
in the actual render for Clap/Shy/Griddy/Twerk; they do **not** yet match LC's absolute arm and leg
positions, and the remaining gap is a rig-shape/knee-plane problem, not the original motion
transfer. The foot/toe orientation is the one measured regression of the leg IK (`ball` dq 20→47° on
Clap): LC's foot target rotation is faithful, but the port's toe rest is not conformed. The next
concrete fixes would be (a) bake a per-frame knee/elbow pole from the source solve and use it for the
port's plane, (b) conform the toe rest, and (c) either accept or compensate the target's 9.6 cm wider
clavicle.
