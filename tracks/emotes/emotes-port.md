# LC emotes in Alterum — port notes (track `emotes`)

Working tree: `tracks/emotes/mod/`. Install: `C:\AlterumT2` (via `./track.sh emotes ...` only).
Nothing outside this track was written. `AlterumModelSwap.Contracts` is unchanged; no restart was
needed for anything in this feature (hot reload + a data pack).

## What was built

| piece | file | role |
|---|---|---|
| offline extractor | `tools/extract_emotes.py` | LC clips -> baked 60 Hz global rotation deltas |
| data pack | `emotes/{manifest.txt,manifest.json,emote.bin,coverage.json}` | 12 clips, 51 mapped bones |
| pack loader | `mod/AlterumModelSwap.Hot/EmotePack.cs` | text manifest + flat float32 blob (no JSON/zip in IL2CPP) |
| pose player | `mod/AlterumModelSwap.Hot/PosePlayer.cs` | ownership lease, retarget, bone writes, fades |
| orchestration | `mod/AlterumModelSwap.Hot/EmoteRuntime.cs` | keys, self-test, cancellation, evidence, verify camera |
| wiring | `mod/AlterumModelSwap.Hot/ModelSwapRuntime.cs` | `PostAnimationUpdate` before the mirror tick |
| reference | `mod/Alterum.References.props` | adds `Kybernetik.Animancer[.FSM]` |

Deploy: `./track.sh emotes build && ./track.sh emotes deploy` plus
`cp emotes/{manifest.txt,emote.bin} /mnt/c/AlterumT2/BepInEx/plugins/AlterumEmotes/`.
The pack is resolved from the **host** assembly directory (`plugins/AlterumEmotes/`), not the CWD.

Keys: `F6` toggle the selected emote, `F7`/`F9` cycle the list (12 LC + 38 game clips), `F8` toggle the
selected game clip. The stage-1 extra-Animancer-layer diagnostic was replaced by the lease-backed
native path (see "Native emotes" at the end).

## Data pipeline (offline, UnityPy 1.25.4)

1. Authoritative source = the shipped `animationsbundle` (Unity 2022.3.9f1): read
   `m_ClipBindingConstant.genericBindings` for all 12 clips (74-81 unique path hashes each).
2. Resolve every hash by CRC32 (`zlib.crc32`, CRC-32/ISO-HDLC) against the **real LC metarig
   hierarchy** taken from MoreCompany's display model (`tracks/cosmetics/unpack_bundle_70132`,
   read-only): `metarig/spine`, `spine.001..004`, `shoulder.L/R`, `arm.*_upper/lower`, `hand.*`,
   `finger1..5.*`, `thigh/shin/foot/toe.*`. The six documented fixtures match exactly
   (`spine=2463100623`, `spine/spine.001=254801669`, `spine/.../spine.002=4095213152`,
   `spine/.../spine.003=674009458`, `spine/thigh.L=49267413`, `spine/thigh.R=4175495094`);
   the extractor aborts if they stop matching.
3. Curve values come from the author's `.anim` sources (`src/lc-better-emotes-main/Unity/moreemotes`),
   whose path names encode the same CRC32 (`path_0xDB36EA2A_...` == 3677809194). Verified equal to
   the compiled stream: every Euler value of Clap's first key appears verbatim in
   `m_StreamedClip.data`, and the authored curve set covers the compiled transform binding set
   **exactly** (`coverage.json`: `bundleOnly=0`, `authoredOnly=0` for all 12 clips). That is why no
   port of Unity's packed polynomial layout was needed; the streamed/dense/constant indices were
   only used for that cross-check.
4. Bake at 60 Hz: local rotations -> source globals (metarig root rotation included) -> per bone
   `dR(t) = G(t) * G_rest^-1`, plus the pelvis vertical delta. Euler -> quaternion uses Unity's
   `Quaternion.Euler` order (Ry*Rx*Rz), which fits the rest pose best (median local delta 11.5 deg
   vs 14+ deg for the other five orders).
5. Classify every binding: 83-158 transform bindings per clip resolve to a metarig bone;
   32-44 per clip are **ignored** (`EMOTE_HASH_IGNORED`) because they are props/IK targets
   (`LeftArm_target`, `RightArm_target`, `BetaBadge`, `LevelSticker`, blood decals, first-person
   legs) that exist only on LC's prefab. Mapped bones with no curve in a clip simply stay at rest.

## Retarget at runtime

* Target rest = **`SkinnedMeshRenderer` bind poses** (`bindposes[i]^-1 * renderer.localToWorld`),
  so it is the model's own bind pose, not an arbitrary animated frame. 141 of 218 transforms get a
  bind anchor; the rest fall back to the entry pose.
* Basis `C` from reference skeletons only (pelvis/neck/left-right thigh frames). Measured
  `C = 6.2 deg` about X for `SK_Cowboy_2Avatar`: both rigs are Y-up facing +Z.
* `target_global = C * dR * C^-1 * anchor`, walked parents-first, then `local = inv(parent_global) * global`.
  Only rotations are transferred; bone lengths/scales are never touched.
* Only the pelvis gets translation: the source's vertical delta scaled by the leg-length ratio
  (measured 0.802), horizontal components dropped (in-place).
* Ownership: one native `Il2CppSystem.Object` lease key + `DoingSomethingHandler.StartDoingSomething`
  + `ModelPersonAnimancerController.DisableAnimancerAndAnimator`. Verified in the log:
  `suspended=True`, `animEnabled=False` for the whole clip. Release uses the shipped typo
  `EnabledAnimancerAndAnimator(key)` and only ever releases our own key.
* Writes happen in `PostAnimationUpdate`, which `ModelSwapRuntime.LateUpdate` calls **before**
  `_mirrorRt.Tick()`, so the mirror renders the emote pose. A per-frame
  `EMOTE_LATE_WRITER` check compares what we wrote against the bone at the start of the next write;
  it never fired, i.e. nothing writes after us.
* Fade in 0.2 s from the captured entry pose; on stop the lease is released first and the pose is
  blended back over 0.2 s against the freshly evaluated live pose.

## Findings that matter for the next agent

* **The player rig is not what `notes.md` says.** The live third-person model is
  `SK_Cowboy_2Avatar`, Humanoid, with UE-style names: `pelvis`, `spine_01..03`, `neck_01`, `head`,
  `clavicle_l/r`, `upperarm_l/r`, `lowerarm_l/r`, `hand_l/r`, `index|middle|ring|pinky|thumb_01..03_l/r`,
  `thigh_l/r`, `calf_l/r`, `foot_l/r`, `ball_l/r` (+ twist and `[Hitbox]` children). There is no
  `spine.001`/`thigh.L`. The 20-bone `RiggedSkeletonAvatar` from `resources.assets` is a different
  asset; the player avatar reports `isHuman=True`.
* The 548 `AnimationClip`s in memory are the game's own Humanoid set. **No `A_POLY_IDL_*` gesture
  clip is loaded at runtime** (they exist in `resources.assets` but nothing references them, so
  `FindObjectsOfTypeAll` cannot see them). Stage 1 therefore used a native Humanoid locomotion
  clip on an added Animancer layer; that path is now `F8`.
* **Clap / Twerk / Prisyadka / Idle1Fix are IK-target driven on LC's rig.** Their bone rotations
  are nearly static (Clap: hands move 3 cm over the clip, max bone rotation change 48 deg) while
  `LeftArm_target`/`RightArm_target` move 1.0-1.2 m. Those clips are therefore *posed* correctly but
  lack their clapping/twerking choreography until a two-bone IK solve from the ported targets is
  added. `Salute` (180 deg, hand travels 0.82 m), `Middle_Finger`, `Sign`, `D_Sign` and
  `The_Griddy` are rotation-driven and port fully.
* `RiggedSkeletonAvatar`/Humanoid retargeting is *not* needed: LC's clips are Generic, and the
  delta transfer above needs no Humanoid semantics. `HumanPoseHandler` was not used.
* `Mirror.cs` / `MirrorPlayerVisibility.cs` are byte-identical to the baseline tree.

## Verification (desktop is locked)

`shot` is useless here, so the mod creates its own 768x768 camera in front of the model
(`AlterumEmoteVerifyCam`, layer-14 mask, `targetTexture` set so `ScreenCamera()` never treats it as
a conflict) and dumps PNGs to `BepInEx/screens/t2/emote_*.png`. The game's own mirror is untouched
and still renders (`[mirror:rt-dump] renders=90/180`).

Self-test sequence (armed per run, same code path as F6): Salute -> Middle_Finger -> The_Griddy
(4 s hold) -> Salute + a 0.6 m position nudge -> restore check. It is **off by default** because it
takes gameplay ownership and moves the player; arm it with `selftest=1` in `emote.request` or
`ALTERUM_EMOTE_SELFTEST=1` at launch. Evidence in `LogOutput.log`:

```
[emote] START clip='Salute' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=1.783s loop=False frames=108 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
[emote:active] ... clip='Salute' t=1.074s animEnabled=False metrics[pelvisY=0.940 pelvisHead=0.686 pelvisHandL=0.762 pelvisHandR=0.747 handLR=1.169 headHandR=0.433 legL=0.861]
[emote] STOP reason=end(Salute) clip='Salute' t=1.83s released=True fadeOut=0.20s
[emote] STOP reason=movement(0.600m) clip='Salute' t=1.02s released=True fadeOut=0.20s
[emote:selftest] RESTORE CHECK game animation running again: max bone change over 0.4s = 2.28deg ...
[emote:native] START 'Common_ThirdView_Movement_Idle' layer=6 weight=1.00 layers=7 len=6.133 loop=True
[emote:native:active] ... stateW=0.00 t=0.000/6.133 playing=1 ...   ->  stateW=1.00 t=0.192/6.133 ...
[emote:native] STOP reason=selftest-window clip='Common_ThirdView_Movement_Idle' layerW=0.000 layers=7
```

Source-vs-target joint metrics for Salute (source computed offline from the same baked data):

| | source t=0 | source t=0.9 | target t=0.07 | target t=1.07 |
|---|---|---|---|---|
| head->hand.R | 0.794 | 0.409 | 0.759 | 0.433 |
| hand.L<->hand.R | 0.887 | 1.087 | 0.549 | 1.169 |
| pelvis->head | 0.776 | 0.772 | 0.692 | 0.686 |
| pelvis->foot.L | 1.074 | 1.074 | 0.845 | 0.861 |

The hand-to-head collapse (salute) reproduces to within 2-4 cm of the scale-corrected source, so the
motion transfer is quantitatively right, not just plausible-looking.

---

# Second pass: two-bone arm IK (`Clap`, `Twerk`, `Prisyadka`, `Idle1Fix`)

Everything above still holds. This pass added the arm IK targets the first pass left unmapped;
`AlterumModelSwap.Contracts`, `Mirror.cs` and `MirrorPlayerVisibility.cs` are untouched, and the
pack's pre-existing bytes are unchanged (the new IK arrays are appended after them - the old
`emote.bin` is a byte prefix of the new one and every `clip`/`map`/`rest` line is identical).

## What LC actually does (new, and it corrects two earlier claims)

* `metarig/spine.003/{Left,Right}Arm_target` **do exist** in the metarig copy the extractor already
  reads (`unpack_bundle_70132`); they simply were not in `SEMANTIC`, so they were carried as
  "bones at rest" rather than ported. They are not in `ignoredHashes`.
* `metarig/Rig 1/{Left,Right}Arm` are Final IK `ChainIKConstraint`s (script GUID `c8047ffc`,
  classID 114) whose target is that node. The clips animate the constraint's weight through
  MonoBehaviour float curves: `Clap`, `Twerk`, `Middle_Finger`, `D_Middle_Finger`, `D_Clap` = 1;
  `Prisyadka`, `Sign`, `D_Sign` = 0; `Salute` = L 1 / R 0; `The_Griddy`, `Shy` = L 1 / R 1;
  `Idle1Fix` has no curve (prefab default 1). The reference mod restores 1 after every emote,
  which is consistent with 1 being the default.
* The `LeftArm_target` positions in the clips are **absolute local positions** near the chest or
  hips (Clap: `(-0.058, 0.077, 0.465)` relative to spine.003; Idle1Fix: `(-0.27, -0.55, 0.14)`).
  The earlier "targets travel 1.0-1.2 m" came from comparing the clip value with the display
  model's *rest* target (`x = +-1.05`, a layout used by that copy) - the within-clip target travel
  is 1-10 cm. What the IK changes is the **pose**, not intra-clip travel.
* Therefore **`Prisyadka` is not IK-driven**: its own clip sets both arm weights to 0 and its arms
  move through authored bone rotations (hands travel 0.59/0.64 m in the source). IK is baked for it
  but the runtime multiplies by the baked weight, so it stays rotation-driven like LC.
* **`Middle_Finger` is IK-driven** (both weights 1) even though it was in the "rotation-driven"
  list: 100% of its motion is the *left* target rising to the face, and its authored arm rotations
  are static. The current port plays a static pose for it (hand travel 0.001 m). Left alone in this
  pass because the five rotation-driven clips had to stay byte-for-byte unaffected.

## Extractor (`tools/extract_emotes.py`)

Per clip, per side, only for `IK_CLIPS = (Clap, Twerk, Prisyadka, Idle1Fix)`:

* `target position relative to spine.003` per 60 Hz frame, in source model space (`target - parent`,
  3 floats/frame) - the target's own animated local position plus the animated spine chain;
* `target global rotation` (4 floats/frame, carried for completeness; the solve uses position);
* `ChainIKConstraint.weight` (1 float/frame) read from the authored `m_FloatCurves` (path CRC32 of
  `Rig 1/{Left,Right}Arm`, script GUID `c8047ffc`), cross-checked against the bundle's type-114
  bindings (present for the three curves that exist; `Idle1Fix` has none -> default 1).

Manifest line (new, after the existing `clip` lines):
`ik <clip> <L|R> <posFloat> <rotFloat> <weightFloat> <frames> <srcParentPath>`.
`report["armIk"]` / `coverage.json` records the same. Regenerate with `python3 tools/extract_emotes.py`.

## Runtime (`PosePlayer.cs`, `EmotePack.cs`)

* Bind: resolve `upperarm_* / lowerarm_* / hand_* / spine_03` by name; chain lengths `L1`,`L2` from
  the live (rigid) bind pose in world units; `scale = (L1+L2) / sourceReach` per side (measured
  0.808 L / 0.824 R; source reach 0.694/0.681 m, target 0.561 m).
* Per frame after the rotation pass (so the chain already holds the rotation-driven pose):
  `target = spine_03.position + root.TransformVector(C * (Lerp(pos) * scale))` - reuses the existing
  basis `C`, no second convention. `C` maps source model directions to target model directions, and
  the offset is anchored to the target's own animated `spine_03`.
* Analytic two-bone solve to the clamped distance (`0.999*(L1+L2)` .. `|L1-L2|+1mm`), keeping the
  current elbow plane (project the animated upper-arm direction onto the plane perpendicular to the
  target direction - where a CCD solve from this pose lands). Rotates `upperarm` and `lowerarm`;
  the hand keeps its authored local rotation. World-space deltas are conjugated into the
  root-local `_globals` frame, then `local = inv(parentGlobal) * global` is written for the three
  bones. Unreachable targets clamp (no snap); `Clamps` counts them.
* Blended by `fade` (the existing 0.2 s fade-in) * the baked clip weight: weight 0 or fade 0 is a
  no-op, so entry is smooth and `Prisyadka` stays rotation-driven. Exit still hands the bones back
  and blends the locals, unchanged.
* Evidence: `IkReport()` adds `ik[L:dist= reach= err= w= clamps= ul= tl= hl= R:...]` to every
  heartbeat/sample line; `err` is measured from the *actual* hand transform to the target.
  `PosePlayer.IkEnabled` (const) = false reproduces the pre-IK build exactly, for A/B runs.

## Measurements (source = LC rig offline from the baked pack, `tools/source_metrics_RETIRED.py`)

> **RETIRED tools.** `tools/source_metrics.py` and `compare/scripts/ref_eval.py` are now
> `*_RETIRED.py`: ref_eval is **half scale** (it omitted `ScavengerModel`'s 2.0 factor, so every
> position it emitted is 0.5x LC's world) and both have wrong arm/leg pose models. The numbers in
> this section are history, not ground truth. The live comparison is `compare_vs_lc/`.

`tools/source_metrics.py` reconstructs the target rig's metrics on LC's own skeleton from
`manifest.txt`/`emote.bin` and solves the same IK, so raw/IK-off vs IK-on source numbers are
directly comparable. Runs used for the numbers below are in `verify/`.

Reach utilisation = `|hand-upperarm| / (L1+L2)` (scale free) and hand separation scaled by the arm
ratio 0.816 (`handLR`):

| clip | handLR before | handLR after | source IK x0.816 | travel L/R before | travel L/R after | source IK x(0.808/0.824) |
|---|---|---|---|---|---|---|
| Clap | 0.679 | 0.145..0.162 | 0.144..0.163 | 0.027/0.020 | 0.066/0.025 | 0.070/0.029 |
| Twerk | 0.829 | 0.407..0.434 | 0.407..0.436 | 0.021/0.027 | 0.040/0.050 | 0.048/0.058 |
| Prisyadka | 0.597..0.613 | 0.597..0.613 (w=0) | 0.092..0.564 (raw, w=0) | 0.201/0.152 | 0.202/0.153 | 0.480/0.524 (raw) |
| Idle1Fix | 0.899..0.902 | 0.623..0.625 | 0.623..0.626 | 0.012/0.006 | 0.009/0.010 | 0.023/0.017 |

`reachUtil` after IK: Clap 0.759..0.771/0.715..0.719 vs source IK 0.783/0.724; Twerk 0.996..0.999
(both clamped, source goal 1.03-1.17x reach); Idle1Fix 0.946..0.963/0.930..0.964 vs source IK
0.897..0.922/0.870..0.892. IK error 0.000 m and no clamps for Clap/Idle1Fix; Twerk is clamped on
both rigs (goal beyond reach) with a 1.6-4.5 cm shortfall (source scaled 1.6-9.7 cm).

Reproduce: build/deploy, then the automatic sweep (`Steps` in `EmoteRuntime.cs`) plays all nine
clips; grep `[emote:sample]` / `[emote:travel]` / `[emote:rig] two-bone`. `IkEnabled=false` gives
the before column. `python3 tools/source_metrics.py` gives the source columns.

## Still not reproduced

* **Prisyadka arms**: rotation-driven in LC with 0.59/0.64 m hand travel, the port gets 0.20/0.15 m.
  This is the rotation-retarget bind-pose bias below, not IK (LC's own weight is 0).
* **Middle_Finger**: still a static pose (see above).
* **Rotation-retarget arm bias**: LC's rest arms hang at the sides; the target model's bind pose is
  wider, so absolute arm poses from *deltas* drift. Visible in Salute (LC IK's the left arm to the
  hip, weight 1; the port leaves it at the side, weight 0 in the clip only for the right arm) and in
  the Prisyadka numbers. Fixing it would change the five "rotation-driven" clips, so it is not done.
* `Idle1Fix` reach utilisation is 0.05 (about 3 cm at the wrist) higher than the source because the
  model's clavicle/chest proportions differ from LC's; the hand separation and travel match.

> **→ superseded by the third pass below.** It fixes `Middle_Finger`, fixes the arm rest bias with a
> bind-time correction, and shows the `Prisyadka` travel gap quoted above was mostly a window
> mismatch (source measured over the whole 6.75 s clip, the port over a 2.0 s hold). It also
> corrects the classification: `The_Griddy` is IK-driven too (weights 1/1, target travel 1.0 m).

---

# Third pass: `Middle_Finger` IK + the rotation-retarget arm bias

Everything above still holds except where this section corrects it. `AlterumModelSwap.Contracts`,
`Mirror.cs` and `MirrorPlayerVisibility.cs` are md5-identical to the baseline
(`Contracts.cs ee94408c…`, `Mirror.cs 08b77a17…`, `MirrorPlayerVisibility.cs 3f33c824…`). The pack
stays backward compatible: every pre-existing byte is untouched, the IK arrays for the new clip are
**appended after** the old ones (`ik Middle_Finger L 393121 …`, `R 393545 …`), so the old `emote.bin`
(1572484 B) is a byte prefix of the new one (1575876 B) and the old 141 `manifest.txt` lines are a
byte prefix too.

## Gap 1 — `Middle_Finger` was IK-driven and played a static pose

Evidence, straight from the authored `.anim` (`tools/…` probe, same reader the extractor uses):

| | `Middle_Finger` | `Clap` (IK, already baked) |
|---|---|---|
| `ChainIKConstraint` weight L / R | **1.000 / 1.000** (bundle type-114 bindings present) | 1.000 / 1.000 |
| `LeftArm_target` position curve | 4 keys, local travel **0.812 m** (hip → forward, `y −0.48 → +0.10`) | 10 keys, 0.061 m |
| `RightArm_target` position curve | 2 keys, 0.170 m | 10 keys, 0.014 m |
| authored `arm.*_upper` euler spread | **0.00 deg** (static) | 0.00 deg |
| authored `arm.*_lower` / `hand.*` | no curves (= rest) | no curves |

So 100 % of LC's `Middle_Finger` motion is the left target rising; the port played the static
authored pose (hand travel 0.001 m).

**Change** — extractor only (`tools/extract_emotes.py`):

* `IK_CLIPS_PRIMARY` (the four shipped clips) + `IK_CLIPS_EXTRA = ("Middle_Finger",)`; the IK float
  arrays and the `ik` manifest lines are emitted primary-first, extra last (`IK_CLIP_ORDER`), which
  is what keeps every old offset identical.
* No runtime change was needed: `PosePlayer.ApplyIk` is generic (clip has an `ik` entry → solve,
  blend by `fade × bakedWeight`), so the new clip went through the same solver, the same basis `C`,
  the same `L1/L2`-from-bind lengths and the same `scale = targetReach/sourceReach` (0.808 L/0.824 R).
* Loader line in the log confirms the new arrays: `[emote] ik clip 'Middle_Finger': L=w[1.00..1.00]
  53f R=w[1.00..1.00] 53f`.

Measured, port vs source (source = `tools/source_metrics.py` on the same pack, IK variant, distances
scaled by the arm ratio 0.816 / 0.808 / 0.824 exactly like the previous pass's tables):

| `Middle_Finger` | before (no IK) | after (IK) | source IK ×0.816 |
|---|---|---|---|
| `handLR` | 0.821 (static) | **0.767..0.771** | 0.763..0.770 |
| travel L/R (post-fade window) | 0.001/0.001 | 0.008/0.001 | 0.024/0.007 † |
| `reachUtil` L / R | 0.979 / 0.995 | 0.954 / 0.784 | 0.937..0.975 / 0.731 |
| `pelvis→hand.L` | 0.302 | **0.779..0.788** | 0.837 (1.036 × 0.808) |
| `ikErr` / `clamps` | – | **0.000 m / 0** | 0.000 m / 0 |

† The travel column is not discriminating for this clip: its motion is front-loaded (the target
rises in the first ~0.2 s), and the runtime's travel window starts at 0.35 s — over that window the
source's own hand bbox is only 0.030 m. The meaningful "it is no longer static" numbers are the
wrist's radial lift **+0.48 m port vs +0.52 m source-scaled** and `handLR`, which now matches to
4 mm. The right target is static in this clip (weight 1 in LC too, so LC's right arm also snaps to
it), which is why R `reachUtil` is 0.784 vs 0.731 (same chest/clavicle proportion offset already
documented for `Idle1Fix`).

**The other four are untouched.** `Salute`, `Sign`, `D_Sign`, `The_Griddy` have no `ik` line in
`manifest.txt` (the file's original 141 lines are a byte prefix, so their 8 `ik` lines and their
`clip` lines are unchanged), and their `emote.bin` bytes are the unmodified prefix. `Salute` is
L=1/R=0: only its left arm would IK, it is left as authored — with the note in Gap 2 that its left
arm is now the clearest remaining mismatch (LC pins it at the hip, the port cannot).
A new finding while verifying that: **`The_Griddy` is also IK-driven** (both weights 1, target
travel 1.009/0.949 m, authored arm rotations static) — the second pass's classification was wrong.
It is left alone here as instructed; see "what this costs" below.

Between the pre-pass build and the Gap 1 build (only the pack changed, those clips' data did not)
the four measured, and only the sweep's frame phase moved: `handLR` `Salute` 1.086..1.239 →
1.093..1.228, `Sign` 0.795..0.904 → 0.795..0.905, `D_Sign` 0.876..1.234 → 0.876..1.236,
`The_Griddy` 0.933 → 0.933; travel L/R 0.018/0.712 → 0.018/0.693, 0.009/0.245 → 0.009/0.247,
0.437/0.658 → 0.469/0.653, 0.178/0.159 → 0.179/0.160. (Their runtime *poses* then change by design
in Gap 2 below — the pack bytes do not.)

## Gap 2 — rotation-retarget arm bias: diagnosis, then a contained fix

### The mechanism, measured

`global_i(t) = C · dR_i(t) · C⁻¹ · anchor_i` with `anchor_i` = this model's **bind** rotation, so a
limb segment's direction is `C · dR_i(t) · C⁻¹ · (bindChild − bind_i)_dir` while LC's is
`dR_i(t) · (srcRestChild − srcRest_i)_dir`. They agree at every frame only if the two *rest* segment
directions agree after `C`. They do not — new bind-time diagnostic
(`[emote:rig] arm rest L/R …`, model space, source mapped by `C`):

| segment | L angle | R angle | target bind dir (L) | source rest dir (L) |
|---|---|---|---|---|
| clavicle→upperarm | 20.5° | 23.8° | (−0.96, −0.01, −0.28) | (−0.99, −0.16, +0.04) |
| upperarm→lowerarm | 24.9° | 38.9° | (−0.71, −0.70, +0.07) | (−0.46, −0.85, −0.25) |
| lowerarm→hand | **71.5°** | **49.3°** | (−0.54, −0.53, +0.66) | (+0.56, −0.76, +0.33) |

plus the hand bone's own rest orientation, which differs by 104.3° (L) / 146.1° (R) and is what puts
LC's fingers in a different plane. Arm reach (upperarm+lowerarm) is 0.561 m on both sides against
LC's 0.694/0.681 (the 0.808/0.824 scale already used elsewhere).

### Correction to the second pass's number: the Prisyadka gap was mostly a window artifact

`[emote:travel]` measures the hand bbox over **[0.35 s, hold]** (the sweep's hold), while
`source_metrics.py` measured the source over **[0.35 s, clip end]**. `Prisyadka` is 6.75 s but is
only held for 2.0 s, so "source 0.59/0.64 vs port 0.20/0.15" compared 6.4 s of dance with a 1.65 s window.
Over the same window the source is **0.186/0.181 m** (×0.808/0.824 = 0.150/0.149), i.e. the port was
never 3× short — it was only slightly *larger* (0.201/0.152). The same applies to `D_Sign`
(3.15 s) and `The_Griddy` (6.53 s). `tools/arm_rest_diagnosis.py` now does the window-matched
comparison and the model below.

### The model, and why it is trustworthy

`tools/arm_rest_diagnosis.py <log>` reads the bind chain the runtime now logs
(`[emote:rig] rest chain pos:… rot:…`, `basis quat=…`, `legScale=…`), re-runs the port's arm FK from
the pack's deltas on that bind chain, and re-runs it with each arm segment's rest direction
conformed to LC's (`Q_i = FromTo(bind segment dir, C · source rest segment dir)` on the left of the
anchor). With `Q = identity` it reproduces the *measured* pre-fix port: `Clap` `handLR` 0.679
(measured 0.679), `Prisyadka` 0.597..0.613 (0.597..0.613), `The_Griddy` 0.933 (0.933), `Sign`
0.794..0.908 (0.795..0.905), `D_Sign` 0.875..1.242 (0.876..1.236). (One row it does not reproduce:
`Salute`'s right-arm travel, modelled 0.89 m vs measured 0.62 m — so the `Salute` travel column below
is not used as evidence.)

### The fix (contained: one constant quaternion per arm bone, at bind time)

`PosePlayer.ConformArmAnchors()`, called once per bind after `ComputeBasis()`:

* `upperarm_*`, `lowerarm_*`: `anchor = FromTo(bind segment dir, C · source rest segment dir) * anchor`.
  This makes every arm segment direction `C · dR_i(t) · (source rest dir)` = exactly LC's, at every
  frame, for free — no re-bake, no pack change, nothing per-frame.
* `hand_*`: LC's rest orientation outright (`C · srcRestRot`), because the hand has no child segment
  to swing and no clip animates it — that is LC's hand pose in every clip, and it is what puts the
  fingers in LC's plane.
* `clavicle_*`: deliberately **left alone**. Conforming it was tried and measured: it pulled the
  shoulder toward the IK targets and made the IK-driven clips' reach utilisation ~0.10 worse
  (`Clap` 0.758→0.680 L, 0.714→0.605 R) while changing the rotation-driven `handLR` by <1 cm.
  The reason: this model's clavicle→upperarm link is only ~0.17 m (the same 0.81 length ratio), so
  conforming its *direction* also shortens the shoulder-to-hand reach.
* Everything else (spine, legs, fingers) keeps the old bind anchors — that is the difference between
  this contained fix and "conform all 51 bones", which is the fix that would also shift the torso
  and is **not** done.

### Before / after, measured (port) vs window-matched source

`source` = LC's own pose from the pack with its baked IK applied at the clip's own weight: source IK
for `Clap`, `Twerk`, `Idle1Fix`, `Middle_Finger`; for `Prisyadka`/`Sign`/`D_Sign` the baked weight is
0, so it is their rotation-only pose (which *is* what LC shows). `Salute` (left arm w=1) and
`The_Griddy` (both w=1) are shown rotation-only because the port has no IK bake for them; LC itself
adds IK there (see "still not reproduced"). Distances scaled by 0.816 (handLR) / 0.808-0.824 (travel)
as before.

| clip | `handLR` before | `handLR` after | source×scale | travel L/R before | after | source×scale |
|---|---|---|---|---|---|---|
| Prisyadka (LC w=0) | 0.597..0.613 | **0.080..0.092** | 0.075..0.089 | 0.201/0.152 | 0.140/0.132 | 0.150/0.149 |
| Sign (LC w=0) | 0.795..0.905 | **0.334..0.501** | 0.363..0.549 | 0.009/0.247 | 0.006/0.188 | 0.009/0.208 |
| D_Sign (LC w=0) | 0.876..1.236 | **0.311..0.783** | 0.299..0.833 | 0.469/0.653 | 0.371/0.571 | 0.425/0.668 |
| The_Griddy (LC w=1, not baked) | 0.933 | **0.562** | 0.604 (raw) | 0.179/0.160 | 0.169/0.164 | 0.245/0.163 |
| Salute (LC L w=1, not baked) | 1.093..1.228 | **0.850..0.949** | 0.768..1.007 (raw) | 0.018/0.693 | 0.011/0.617 | 0.047/0.901 |
| Clap (IK) | 0.144..0.162 | 0.144..0.162 | 0.144..0.163 | 0.063/0.025 | 0.064/0.026 | 0.071/0.029 |
| Twerk (IK) | 0.406..0.434 | 0.404..0.434 | 0.433 | 0.038/0.048 | 0.042/0.055 | (window < clip) |
| Idle1Fix (IK) | 0.623..0.625 | 0.623..0.625 | 0.620..0.622 | 0.010/0.010 | 0.009/0.010 | 0.022/0.015 |
| Middle_Finger (IK) | 0.767..0.771 | 0.767..0.771 | 0.763..0.770 | 0.010/0.002 | 0.008/0.001 | 0.024/0.007 |

`ikErr` / `clamps` after the fix: `Clap` 0.000/0.000, 0/0 · `Idle1Fix` 0.000/0.000, 0/0 ·
`Middle_Finger` 0.000/0.000, 0/0 · `Prisyadka` w=0, 0/0 · `Twerk` err 0.05/0.11 m, clamps 14/14.
`Twerk`'s goal is beyond full reach on both sides (dist 0.59/0.63 m vs reach 0.561), so `clamps`
counts unreachable frames for the whole run (6..14 depending on how many frames the 1.2 s hold got)
and `err` is the residual shortfall at the sampled frame — not a solver failure.

`reachUtil` after: `Clap` 0.758..0.774/0.714..0.719 (source IK 0.781..0.790/0.722..0.726) ·
`Twerk` 0.959..0.999 · `Idle1Fix` 0.962/0.946 · `Prisyadka` **0.724/0.722** (source rotation-only
0.724/0.722, exactly) · `Middle_Finger` 0.938..0.954/0.784 (source IK 0.937..0.975/0.731).

Run-to-run spread on the phase-sensitive columns (travel, `handLR` extremes) is up to 0.02 m; the
`Clap` / `Idle1Fix` / `Middle_Finger` `handLR` and `reachUtil` values are identical run to run.

Read-out: the absolute arm pose error on the rotation-driven clips drops from 0.2-0.5 m to
0.005-0.06 m (5-10×), travel now matches the source's scaled travel to 1-2 cm, and the IK-driven
clips do not move (their hands are held at the baked targets; only the elbow plane and the hand's
roll change, which is the point).

The diagnostic counters that made this measurable are new log-only lines:
`[emote:rig] rest chain pos:… rot:…`, `[emote:rig] basis quat=…`, `[emote:rig] arm rest L/R …`
(bind vs source rest, the table above) and `[emote:rig] arm rest-frame conform (bind): …` (what was
applied). `PosePlayer.ApplyIkSide` also clears `Clamps` for a zero-weight side now, so the sample
line no longer reports the previous clip's clamp count (it used to show 14 for `Prisyadka`, which
drives no IK at all).

## Still not reproduced (current state)

* **`The_Griddy` arms**: LC drives them with both weights at 1 and a target that travels 1.009/0.949 m,
  and all of its arm rotations are static — so LC's visible motion is the IK. Not baked here (out of
  scope this pass), so the port's arms come from the body rotation only. Cheapest remaining win:
  add `The_Griddy` to `IK_CLIPS_EXTRA` and regenerate — same code path, one line, ~4 KB.
  Watch out: its `handLR` would jump from 0.562 to LC's IK value, and it is the clip with the largest
  body rotation, so it needs its own before/after review.
* **`Salute` left arm**: LC pins it at `LeftArm_target` (weight 1, target near the hip, 0.090 m of
  its own travel); the port leaves it at the authored pose. Baking Salute's L target would close it
  the same way `Middle_Finger` was closed.
* **`Shy` and `D_Clap`/`D_Middle_Finger`** are ported but not in the acceptance sweep; `Shy` is
  rotation-driven (weight 1 but a static target) and now benefits from the conform.
* **Arm length/basis bias**: the arm chain is 0.808/0.824 of LC's, so absolute positions (wrist lift,
  `pelvis→hand`) still sit 3-5 cm short after scaling, and `Idle1Fix`/`Middle_Finger` R `reachUtil`
  is ~0.05 high because the model's clavicle/chest proportions differ. Not fixable at rotation level.
* **Finger chains** keep their own bind anchors, so a finger's absolute direction carries the same
  kind of rest bias as the arm did (small: finger bones are short and the pose is in the hand's own
  rotation, which *is* conformed). Conforming all 51 bones would fix it and re-baseline the whole
  skeleton (spine, legs, pelvis) — that is the "not cheap" fix this pass deliberately avoided.

> **→ the fourth pass below closes the IK coverage**: it audits all 12 clips in one pass, enables
> `The_Griddy`, `Salute` (left), `Shy`, `D_Clap` and `D_Middle_Finger`, and corrects the `Shy`
> classification (weight 1 with a static target = IK-pinned pose, not rotation-driven) and the
> "The_Griddy's arm rotations are static" claim (they animate in the last 0.6 s of the loop).

## Verification (desktop locked — log + numbers + private RT camera only)

* `[emote:selftest] RESTORE CHECK … max bone change over 0.4s = 2.28deg` fires after every sweep
  (19x in `verify/after_final2.log`): the game's animation resumes when the lease is released.
* `[mirror:rt-dump] renders=90/180 …` continues (52 dumps) and the private verify camera
  (`[emote:shot] verify camera mask=0x00904001 rt=768x768`) keeps writing `emote_run*_f*.png`; the
  Prisyadka frames before/after the conform visibly go from arms-wide (`handLR` 0.60) to hands
  together in front of the chest (`handLR` 0.08), matching the numbers.
* No `PORT_PACK_INVALID`, no `EMOTE_LATE_WRITER`, no `EMOTE_OWNERSHIP_FAILED` in the final log.
* Pack compatibility: `cmp -n 1572484 emotes/emote.bin verify/emote.pre_gap.bin` → identical; the
  first 141 `manifest.txt` lines are byte-identical; the four untouched clips' `ik` offsets are
  unchanged (381505/381993/382481/384425/386369/389617/392865/392993).

Reproduce:

```bash
python3 tools/extract_emotes.py                       # pack (appends MF's IK arrays last)
python3 tools/source_metrics.py                       # source numbers, raw + IK
python3 tools/arm_rest_diagnosis.py <log> --json x.json   # rest bias + conformed model
grep -E "emote:(travel|sample|rig)\]" <log>           # port numbers, last sweep
./track.sh emotes build && ./track.sh emotes deploy    # hot reload; the sweep re-runs itself
```

---

# Fourth pass: IK coverage closed by one audit of all 12 clips

Corrections to the third pass, established by `tools/ik_audit.py` (new, see below):

* **`The_Griddy` is enabled now** (both weights 1, target travel 1.226/1.214 m) — the third pass
  left it off on request.
* **`Salute`'s left arm is enabled now** (weight L 1 / R 0; only L changes the pose). The left hand
  sits at the hip, 0.291 m from the pelvis vs the source's scale-corrected 0.297-0.301 m, instead of
  hanging at the side at 0.449-0.454 m.
* **`Shy` is not rotation-driven.** Its target travel is 0.028/0.047 m, but its weight is 1 on both
  sides, so LC's pose is *pinned by the IK* and the authored 34° left-upperarm curve is overridden.
  Enabling it is what matches LC (`handLR` 0.383..0.437 vs source 0.383..0.438).
* Three clips the sweep never played (`Shy`, `D_Clap`, `D_Middle_Finger`) are in the acceptance
  sweep now, so every enabled clip has port measurements.
* `The_Griddy`'s authored arm rotations are **not** static: 6 euler keys with a 93.6/95.2° swing,
  all of it in the last ~0.6 s of the 6.53 s loop. Before enabling IK the port therefore looked
  static-armed only for the first ~5.9 s (i.e. for every frame the old sweep measured). At weight 1
  the IK overrides that swing, which is why the whole-clip IK share below is 87/86%, not 100%.

Everything above still holds except where this section corrects it. `AlterumModelSwap.Contracts`,
`Mirror.cs` and `MirrorPlayerVisibility.cs` are md5-identical to the baseline
(`Contracts.cs ee94408c…`, `Mirror.cs 08b77a17…`, `MirrorPlayerVisibility.cs 3f33c824…`);
`PosePlayer.cs` was not touched in this pass.

## Gap 3 — the audit that stops the one-clip-at-a-time discovery

`tools/ik_audit.py` answers, for every clip in the pack at once:

* the **weight time series** per side from the authored `m_FloatCurves` (path CRC32 of
  `Rig 1/{Left,Right}Arm`, script GUID `c8047ffc`), 60 Hz over the clip — not a sample. No curve
  means the prefab default 1 (marked `*`).
* the **`{Left,Right}Arm_target` travel** as the bbox of the offset relative to `spine.003` (the
  exact float array the pack bakes) plus the node's own authored local travel.
* whether the **authored arm bone rotations** are static or animated (`max |q(t) - q(0)|` over
  `shoulder`, `arm.*_upper`, `arm.*_lower`, `hand`).
* the **wrist pose motion with and without the clip's own IK**, in the `spine.03` frame (body frame,
  so a spinning clip does not masquerade as arm motion), the IK pose offset, and the
  **IK share** of the motion = `(path length with IK - path length rotation-only) / path length with
  IK`.

Pose model = `source_metrics.SourceRig` (the pack's global rotation deltas + pelvis Y, no source
translation curves), so the numbers are directly comparable with both `source_metrics.py` and the
runtime log. For the 5 clips that already had baked IK arrays, the audit-computed target offset was
cross-checked against the pack: `max |delta| = 0.000000 m` for all of them.

### The 12-clip table (`python3 tools/ik_audit.py`)

| clip | w L | w R | target travel L/R (m) | authored arm rot L/R | wrist pose travel rot→IK L/R (m) | IK offset max L/R (m) | IK share L/R | class L/R |
|---|---|---|---|---|---|---|---|---|
| Clap | 1.00..1.00 | 1.00..1.00 | 0.082 / 0.024 | 0.0° / 0.0° | 0.000→0.061 / 0.000→0.014 | 0.217 / 0.256 | 100% / 100% | IK / IKpin |
| D_Clap | 1.00..1.00 | 1.00..1.00 | 0.082 / 0.024 | 0.0° / 0.0° | 0.000→0.061 / 0.000→0.014 | 0.217 / 0.256 | 100% / 100% | IK / IKpin |
| D_Middle_Finger | 1.00..1.00 | 1.00..1.00 | 0.827 / 0.839 | 0.0° / 0.0° † | 0.000→0.822 / 0.000→0.837 | 0.772 / 0.782 | 100% / 100% | IK / IK |
| D_Sign | 0.00..0.00 | 0.00..0.00 | 0.131 / 0.111 | 101.2° / 125.6° | 0.532→0.532 / 0.819→0.819 | 0 / 0 | 0% / 0% | rot(w=0) / rot(w=0) |
| Idle1Fix | 1.00..1.00 \* | 1.00..1.00 \* | 0.028 / 0.019 | 7.3° / 5.2° | 0.088→0.020 / 0.052→0.017 | 0.350 / 0.082 | 0% / 23% | IKpin / IKpin |
| Middle_Finger | 1.00..1.00 | 1.00..1.00 | 0.827 / 0.188 | 0.0° / 0.0° † | 0.000→0.822 / 0.000→0.176 | 0.772 / 0.197 | 100% / 100% | IK / IK |
| Prisyadka | 0.00..0.00 | 0.00..0.00 | 0.088 / 0.068 | 87.8° / 129.2° | 0.400→0.400 / 0.446→0.446 | 0 / 0 | 0% / 0% | rot(w=0) / rot(w=0) |
| Salute | 1.00..1.00 | 0.00..0.00 | 0.171 / 0.099 | 0.0° / 106.7° | 0.000→0.090 / 1.108→1.108 | 0.301 / 0 | 100% / 0% | IK / rot(w=0) |
| Shy | 1.00..1.00 | 1.00..1.00 | 0.028 / 0.047 | 34.0° / 0.0° | 0.233→0.024 / 0.000→0.044 | 0.218 / 0.207 | 0% / 100% | IKpin / IK |
| Sign | 0.00..0.00 | 0.00..0.00 | 0.007 / 0.007 | 0.0° / 25.6° | 0.000→0.000 / 0.282→0.282 | 0 / 0 | 0% / 0% | rot(w=0) / rot(w=0) |
| The_Griddy | 1.00..1.00 | 1.00..1.00 | 1.226 / 1.214 | 93.6° / 95.2° | 0.921→1.232 / 0.961→1.204 | 1.026 / 0.893 | 87% / 86% | IK+rot / IK+rot |
| Twerk | 1.00..1.00 | 1.00..1.00 | 0.063 / 0.097 | 0.0° / 0.0° | 0.000→0.113 / 0.000→0.127 | 0.279 / 0.526 | 100% / 100% | IK / IK |

`w` = `ChainIKConstraint.weight`, min..max over the whole clip; `*` = no curve (prefab default 1).
Every weight series in the table is constant (1 or 0), so there is no clip in the pack whose arm IK
turns on or off part-way through. `target travel` is the bbox of the offset relative to `spine.003`
over all frames (`The_Griddy`'s max-displacement-from-frame-1 measure is 1.070/1.007 m, the third
pass quoted 1.009/0.949 m for that style of metric — same motion, the bbox is the fuller number).
`class`: `IK` = the target produces the wrist motion, `IKpin` = weight on but the target is
body-fixed (IK sets the pose, not the motion), `rot(w=0)` = IK inert.

† `armRotSpread` includes `hand.*`, and `Middle_Finger`/`D_Middle_Finger` author an 81.5° `hand.L`
curve. A hand rotation moves the fingers, not the wrist: their shoulder/upper/lower spread is 0.0°,
which is why they classify as pure IK.

Read-out: **9 of the 12 clips have a non-zero weight somewhere** and are enabled; 10 have baked
arrays (Prisyadka's are inert at weight 0, kept for the byte prefix), and `Sign`/`D_Sign` have none
because nothing would ever act on them. The only one-sided enable is `Salute` (R weight 0).

## What was enabled, what was left off

| clip | weight | action | why |
|---|---|---|---|
| Clap, Twerk, Idle1Fix, Middle_Finger | 1 | already baked (passes 2-3) | unchanged |
| Prisyadka | 0/0 | baked, inert (kept) | byte-prefix compatibility; the solver is a no-op at weight 0 |
| Sign, D_Sign | 0/0 | **not** baked | nothing drives the arms; baking would add two arrays no runtime path can use |
| Salute | L 1 / R 0 | **enabled** | the left hand belongs at the hip; R is a no-op by weight |
| Shy | 1/1 | **enabled** | IK-pinned pose (target wobble 0.028/0.047 m), not rotation |
| The_Griddy | 1/1 | **enabled** | 1.2 m target travel, authored arms static for the first 5.9 s |
| D_Clap | 1/1 | **enabled** | the prop variant of Clap; it was playing the static-armed pose |
| D_Middle_Finger | 1/1 | **enabled** | both targets travel 0.83 m |

No new solver, no per-side flag: `PosePlayer.ApplyIk` already blends by `fade × bakedWeight` per
side, so a weight-0 side (Salute R) writes nothing and one clip's weight-0 bake is harmless.

## Code changes (pack + tooling + sweep, no runtime path change)

* `tools/extract_emotes.py`: `IK_CLIPS_NEW = ("D_Clap", "D_Middle_Finger", "Salute", "Shy",
  "The_Griddy")`, emitted after `IK_CLIPS_PRIMARY + IK_CLIPS_EXTRA` (explicit order concatenation,
  not a global sort) so every pre-existing array keeps its offset.
* `tools/source_metrics.py`: HOLDS table + a `travel(sweep …)` line that reproduces the runtime's
  `[emote:travel]` window exactly (same [0.35 s, hold] of run time, loop wrap / one-shot clamp).
  This encodes the third pass's window fix; the old `travel(post-fade)` line stays, but only the
  sweep line may be compared with the port.
* `mod/.../EmoteRuntime.cs`: `Steps` appended with `Shy`, `D_Clap`, `D_Middle_Finger` (the first 9
  entries are unchanged, so their sweeps are directly comparable with earlier logs). No solver,
  player or lease change; `PosePlayer.cs` is untouched.
* New `tools/ik_audit.py` (the audit) and `tools/sweep_compare.py` (the before/after/source table).

Pack: `emote.bin` 1,575,876 → 1,624,900 B, `manifest.txt` 143 → 153 lines (+10 `ik` lines).

## Before / after, measured, with the windows

Port columns come from two complete sweeps of the same `Steps` list: **before** = the previous pack
(`verify/before_ikall_matched.log`), **after** = the new pack (`verify/after_ikall_final.log`;
`after_ikall_final2.log` is a repeat run and `ikall_compare_run2.txt` its table). Source columns are
`source_metrics.py`'s window-matched variant, scaled to the port's arm length exactly like the
earlier passes (×0.816 `handLR`, ×0.808/0.824 travel).

**Windows.** Travel and window `handLR` = `[0.35 s, hold]` of run time, with the pose player's phase
(loop wrap / one-shot clamp); holds: Salute 1.6, Middle_Finger 1.2, Sign 1.6, D_Sign 1.6,
The_Griddy 1.6, Clap 1.6, Twerk 1.2, Prisyadka 2.0, **Idle1Fix 1.01** (the 0.6 m nudge cancels it at
+1.0 s), Shy 1.6, D_Clap 1.0, D_Middle_Finger 0.9. The source tool uses the same hold. Strip
columns are clip fractions (0.25/0.45/0.65/0.85), so they are frame-locked and not window-sensitive.

| clip | window `handLR` before → after (source) | travel L/R before → after (source) | `pelvisHandL` strips before → after (source) | `reachUtil` L/R after | `ikErr` / clamps |
|---|---|---|---|---|---|
| Salute | 0.848..0.920 → **0.713..0.993** (0.627..1.043) | 0.010/0.609 → **0.030/0.783** (0.070/0.901) ‡ | 0.449..0.454 → **0.291** (0.297..0.301) | 0.999 / 0.555..0.959 | 0.036 / 0.000 m, 19 / 0 |
| Shy | 0.455..0.473 → **0.383..0.437** (0.383..0.438) | 0.029/0.021 → **0.032/0.041** (0.036/0.048) | 0.542..0.555 → **0.515..0.519** (0.564..0.565) | 0.566..0.569 / 0.655..0.656 | 0.067 / 0.082 m (fade), 0 / 0 |
| The_Griddy | 0.562..0.562 → **0.318..0.581** (0.318..0.583) | 0.171/0.161 → **0.896/0.881** (0.912/0.888) | (only strip 0 is sampled; heartbeat 1.08 s: 0.422 → 0.385) | 0.974 / 0.964 (heartbeat) | 0.000 / 0.000 m, 0 / 0 |
| D_Clap | 0.154..0.154 → **0.144..0.162** (0.144..0.163) | 0.028/0.021 → **0.057/0.025** (0.070/0.029) | 0.637 → **0.567..0.596** (0.613..0.646) | 0.758..0.770 / 0.715..0.719 | 0.000 / 0.000 m, 0 / 0 |
| D_Middle_Finger | 0.463..0.463 → **0.452..0.453** (0.452..0.459) | 0.003/0.003 → **0.006/0.006** (0.024/0.027) | 0.290..0.319 → **0.735..0.788** (0.824..0.837) | 0.908..0.954 / 0.931..0.972 | 0.071 / 0.071 m (fade), 0 / 0 |

‡ `Salute`'s travel/`handLR` **window** columns are phase-sensitive, not IK-sensitive: the right hand
is mid-sweep at 0.35 s and the window's first frame moves by one or two frames between runs, worth
~0.05 m per frame. The right side has weight 0 and cannot be affected. Three after runs gave
`handR` travel 0.783 / 0.609 / 0.600 and `handLR` 0.713..0.993 / 0.853..1.007 / 0.863..1.006, while
the frame-locked strips were identical every time (0.901..0.925) and `pelvisHandL` 0.291 every time.
The strip columns are the evidence for Salute.

`ikErr` is the residual at the sampled frame: Salute's left target is 0.596 m from the shoulder
against a 0.561 m arm, so the port clamps 3.6 cm short on every w=1 frame (`clamps` counts frames,
not failures — same as Twerk, whose LC goal is 1.03–1.17× reach). Shy/D_Middle_Finger's 0.067-0.082 m
rows are the *fade-in* samples (w≈0.67-0.89, blend not complete); at w=1 both are 0.000 m.

### No regression on the clips that already matched

| clip | window `handLR` before → after (source ×0.816) | travel L/R before → after (source ×scale) | `reachUtil` L/R after | `ikErr` / clamps |
|---|---|---|---|---|
| Clap | 0.144..0.162 → **0.144..0.162** (0.144..0.163) | 0.065/0.025 → 0.064/0.025 (0.070/0.029) | 0.758..0.771 / 0.714..0.719 | 0.000 / 0.000 m, 0 / 0 |
| Twerk | 0.404..0.434 → **0.406..0.434** (0.407..0.436) | 0.042/0.054 → 0.037..0.041/0.048..0.051 (0.048/0.058) ‡ | 0.999 / 0.948..0.999 | 0.065 / 0.136 m, 10 / 10 |
| Idle1Fix | 0.623..0.625 → **0.623..0.625** (0.623..0.625) | 0.009/0.009 → 0.009/0.009 (0.014/0.007) | 0.963 / 0.946 | 0.000 / 0.000 m, 0 / 0 |
| Middle_Finger | 0.767..0.771 → **0.767..0.771** (0.766..0.774) | 0.008/0.001 → 0.007..0.009/0.001..0.002 (0.024/0.007) | 0.910..0.954 / 0.784..0.803 | 0.000 / 0.000 m, 0 / 0 |
| Prisyadka (w=0) | 0.080..0.092 → **0.080..0.092** (0.075..0.090) | 0.138/0.132 → 0.140/0.132 (0.150/0.149) | 0.724 / 0.722 | 0.000 / 0.000 m, 0 / 0 |
| Sign (w=0) | 0.334..0.497 → 0.334..0.501 (0.365..0.552) | 0.006/0.184 → 0.006/0.187 (0.009/0.208) | 0.713 / 0.630..0.636 | – (no IK) |
| D_Sign (w=0) | 0.305..0.781 → 0.310..0.783 (0.300..0.837) | 0.372/0.570 → 0.371..0.414/0.571..0.587 (0.425/0.668) ‡ | 0.846..0.893 / 0.846..0.932 | – (no IK) |

‡ The window travel of `Twerk` (0.25 s loop) and `D_Sign` (3.15 s one-shot) moves by ≤0.04 m between
runs on **identical pack bytes** (Twerk 0.042/0.054 before, 0.037/0.048 and 0.041/0.051 after;
D_Sign 0.371/0.571 and 0.414/0.587) — the window start lands on a different frame. Their `handLR`,
strip and pose numbers are stable.

No clip's numbers got worse: the four well-matching clips are byte-identical in the pack and measure
the same, and the two weight-0 rotation-driven clips (`Sign`, `D_Sign`) cannot be reached by the
solver. Nothing was reverted.

## Verification (desktop locked — log + numbers + private RT camera only)

* `verify/after_ikall_final.log`: no `PORT_PACK_INVALID`, no `EMOTE_LATE_WRITER`, no
  `EMOTE_OWNERSHIP_FAILED`; 23 `RESTORE CHECK` lines (last: max bone change 2.28-2.58° over 0.4 s,
  game animation resumes); 60 `[mirror:rt-dump]` lines; verify camera `mask=0x00904001 rt=768x768`.
* RT camera before/after frames (saved in `verify/shots_before/`, `verify/shots_after/`):
  `run5` (The_Griddy) arms hanging → both hands in front of the torso; `run11` (D_Clap) static
  hanging pose → hands together at chest height; `run1` (Salute) left arm adducted onto the hip.
* Pack compatibility, checked on the deployed file:
  * `cmp -n 1575876 emotes/emote.bin verify/emote.pre_ikall.bin` → identical (old `emote.bin` is a
    strict prefix of the new one);
  * `cmp -n 1572484 emotes/emote.bin verify/emote.pre_gap.bin` → identical (the third pass's pack is
    still a prefix, transitively);
  * `head -143 emotes/manifest.txt | cmp - verify/manifest.pre_ikall.txt` → identical;
    `head -141 … | cmp - verify/manifest.pre_gap.txt` → identical;
  * the 10 pre-existing `ik` offsets are unchanged (381505 … 393916), the 10 new ones start at
    393969;
  * `md5sum -c verify/md5_baseline.txt` → `Contracts.cs`, `Mirror.cs`, `MirrorPlayerVisibility.cs`
    OK.

## Reverting

* **Arm rest conform** (third pass, unchanged here): restore `verify/PosePlayer.pre_gap.cs` over
  `mod/AlterumModelSwap.Hot/PosePlayer.cs` (its `Bind` has no `ConformArmAnchors()` call), rebuild
  and deploy; that reproduces `verify/after_gap1.log`. The contained alternative is to delete just
  the `ConformArmAnchors();` line in `PosePlayer.Bind` (leaving the method unused) — the clavicle
  exclusion, hand conform and every number it produced stay exactly as documented.
* **Any new per-clip IK enable**: the "flag" is the presence of an `ik <clip> …` line in
  `manifest.txt`, produced from `IK_CLIPS_NEW` in `tools/extract_emotes.py`. Remove a name, rerun
  `python3 tools/extract_emotes.py`, and copy `emotes/{manifest.txt,emote.bin}` to
  `C:\AlterumT2\BepInEx\plugins\AlterumEmotes/`; deploy the dll once to force a reload (the pack is
  read at module init). Removing all five names reproduces the previous pack byte-for-byte.
  Fastest full revert: copy `verify/emote.pre_ikall.bin` and `verify/manifest.pre_ikall.txt` back to
  `emotes/` and to the install (no rebuild needed — the runtime ignores clips without an `ik` line).
* **The sweep extension** (Shy/D_Clap/D_Middle_Finger in `Steps`): restore
  `verify/EmoteRuntime.pre_ikall.cs`, rebuild and deploy. The pack is unaffected.

Reproduce this pass:

```bash
python3 tools/ik_audit.py --json verify/ik_audit.json     # the 12-clip audit table
python3 tools/extract_emotes.py                           # pack (new IK arrays appended last)
python3 tools/source_metrics.py                           # source numbers, window-matched
./track.sh emotes build && ./track.sh emotes deploy       # hot reload; the 12-clip sweep runs
python3 tools/sweep_compare.py --before verify/before_ikall_matched.log \
    --after verify/after_ikall_final.log --source verify/source_metrics.ikall.log
```

## What remains unverified / not reproduced

* **Full-clip choreography of the long clips.** The sweep holds 1.6 s of `The_Griddy` (6.53 s) and
  2.0 s of `Prisyadka` (6.75 s); the rest of each clip is only verified offline (the audit's whole
  clip columns) and by the source tool, not by runtime frames. The Griddy's late authored arm swing
  (5.9-6.5 s) is overridden by the IK at weight 1 but was never measured on the target rig.
* **Arm length / clavicle bias.** Absolute hand-to-pelvis distances still sit 3-5 cm short of the
  scale-corrected source on the enabled clips (`Shy` 0.515 vs 0.564, `D_Middle_Finger` 0.788 vs
  0.824, `Idle1Fix` 0.963 reachUtil vs 0.922 source-side); `Salute`'s hip target is structurally
  3.6 cm beyond the port's reach, so it clamps. Same cause as documented in the third pass, not a
  solver defect.
* **Finger chains** keep their own bind anchors; only the hand rotation is conformed.
* **No visual check on the locked desktop** beyond the private 768x768 RT camera frames listed
  above; the game's own mirror keeps rendering but was not inspected.

## Clean-up pass (after the port; no retargeting change)

`verify/CLEANUP.md` holds the full record. What it changed in behaviour, not in the port:

* The diagnostic sweep is **default-off and armed per run** (`selftest=1` in `emote.request`, or
  `ALTERUM_EMOTE_SELFTEST=1`). A hot reload during play no longer acquires the lease, writes bones
  or nudges the player. Reload evidence: `teardown writes=0 leaseHeld=False->False selftest=off`.
* The ownership lease is transactional and fails closed; a failed release keeps its key for a retry;
  pose-write exceptions stop playback instead of looping.
* The pelvis translation now fades in and out with the rotations and is restored on teardown. Extra
  request keys used as diagnostics: `native=1|0` (native clip on the added layer) and `baseline=1`
  (force the fade-out target to the pre-emote pelvis, for the "animator writes no translation" case).
* The diagnostic Animancer layer is reused and retired (`Layers.Count` trim), and the verify camera +
  RenderTexture are destroyed on teardown. Five consecutive reloads kept layers 5, cameras 0,
  RenderTextures 0 (the in-frame census reads 1 because `Destroy` is deferred).
* Generation changes compare controller/person identity, stop before the early return when the owner
  is lost, and commit a binding only after `Bind` succeeds.

Known and deliberately untouched: P2.8 LateUpdate ordering, P2.10 pack validation, P2.11 elbow
continuity, P2.14 fail-closed retargeting, P2.15 tooling loop-period mismatch.


---

# Native emotes: the game's own player animations in the same cycle

Everything in this section is on the same footing as the LC port: one entry point (`StartAt`),
one ownership lease, one write phase. Nothing here bypasses the lease.

## What was added

* Source A (the third-person model's own `PersonAnimancerComponent`): 41 clips across 5 layers,
  enumerated by walking `layer.States` (mixers recursed) and `GatherAnimationClips`. Only the two
  self-contained idle loops are offered: `Common_ThirdView_Movement_Idle` and
  `CommonAdventure_ThirdView_Movement_Idle`. The other 39 are locomotion blend-tree leaves or the
  first-person `Hands_FPS` clip.
* Source B (the player-character content the game has loaded): the `FemaleAdventure_*` body family
  (69 clips). Its 36 arms-only **hands-layer** clips (`_Idle`, `_Idle_ThrowPrepare`, `_IdleLowered`,
  `_Using*`, `_Purification`, `_Burning`, `_Attack_*`, `DoingSomething*`) are offered as upper-body
  emotes. Their `_TakingOut` transitions, the crawl/crouch movement set, and the
  kill/exile body sequences are rejected with reasons; the `Female_*`/`Male*` families are the same
  animations for the other body variants and are logged as duplicates.
* Total: **38 native emotes** (`native:<ClipName>`), list = 12 LC + 38, ids of the 12 unchanged.
  Full census/classification/verification table: `tracks/emotes/notes/native-emotes.md`.

## How a game clip is played

`PosePlayer.StartNative(clip, id, layerMask)`:

1. Binds a `NativeSampler`: a hidden scratch copy of the rig with the live model's bone names and
   avatar, an `Animator` (no controller) and a one-clip `PlayableGraph`. Unity's own humanoid
   sampling/retargeting evaluates the clip onto the scratch rig; the mod never interprets the
   clip's muscle data.
2. Measures which bones the clip actually drives (16 phases, rotation + translation), so a clip
   writes only those bones; the rest keep the pose the player had at entry. For an upper-body clip
   the write set is additionally intersected with the **game's own `Hands Mask` body parts**
   (`LeftArm,RightArm,LeftFingers,RightFingers`), i.e. arms layered over the body pose, not a body
   replacement. The pelvis is in neither set, so an upper-body emote writes no translation at all.
3. Takes the same lease as an LC clip (`DoingSomethingHandler.StartDoingSomething` +
   `DisableAnimancerAndAnimator` with one key object), then writes the faded pose in
   `PostAnimationUpdate` exactly like `Apply()` does for a ported clip, and hands the pose back
   with the same `RestorePoseNow` on release. `EmoteRuntime.SkeletonOwned` reports the single
   "we own the skeleton" condition (the merged host gates its jiggle solver on it).

Fail-closed: if the sampler cannot bind or the lease cannot be acquired, nothing plays, no bone is
written and the previous state is restored. The 0.2 s fade-in/out, the movement cancellation and
the generation/rebind path are shared with the LC clips.

## Entry points

* `F7`/`F9` cycle the combined list; `F6` toggles; `F8` toggles the selected native emote.
* `emote.request`: `clip=native:<Name>` starts one; `native=1|0` starts/stops the selected one;
  `cycle=N` exercises the exact `CycleNext` path (the desktop is locked, so no key can be pressed);
  `census=1` dumps the full layer/state/clip/bone census (including all 548 loaded clips);
  `probe=<Name>` samples one loaded clip and reports its drive mask; `nativesweep=1` runs the
  per-clip start/hold/stop/release/restore verification; `nativefilm=1` + `filmnames=a,b,...`
  records ClipRecorder showcase filmstrips.
