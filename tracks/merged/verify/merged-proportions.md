# Proportions pass 5 merged into the merged build — verification

Source: `tracks/bones/` (Proportions.cs, ChestWeights.cs, ProportionShots.cs, the `LateTick` hook).
Target: `tracks/merged/mod/` — the tree that also carries the emote/jiggle lease, the cosmetics
runtime, the third-person camera rig and the lobby prop. Install A
(`.../steamapps/common/Alterum`), hot reload only, desktop locked (request files + RenderTexture).

Deployed: `AlterumModelSwap.Hot.dll` md5 **980955cbda13279ae34685c56cda119a** (21 431 808 B),
identical in A's `BepInEx/hot/` and B's `BepInEx/hot/b/`. Deploy:
`tracks/merged/tools/deploy-hot.sh` (build + atomic rename). A was only hot reloaded
(`[hot] generation 38 → 42`, process 10752 unchanged); host and `AlterumModelSwap.Contracts` were
not touched, so no restart.

Model during the proportions checks: the local player is `3P_ConcreteModel_Female_03_Diana`
(female rig ⇒ the jiggle solver is live, `rigs=1`), plus spawned frozen `PropCaptureBody` clones of
`Female_3_Diana` and `Female_2_Freya`.

## What the merge needed

| file | change | why it is not a straight copy |
|---|---|---|
| `Proportions.cs` | replaced with the bones version | merged tree only had the pass-2 build (leaf 1.45 / hips 1.22, no cage, no chest weights). Shipped constants: leaf **1.55**, cage **1.22581** (total 1.90), hips **1.50**. |
| `ChestWeights.cs` | new, verbatim | — |
| `ProportionShots.cs` | new, verbatim | consults only `Proportions` + `ChestWeights` + `IHostContext`; no merged-only dependency |
| `ModelSwapRuntime.cs` | `ProportionShots.Tick(_h, _proportions)` after `_proportions.Tick(_t)` | the capture rig is inert unless `propcap.request` exists |
| `ModelSwapRuntime.cs` | `_proportions?.LateTick()` **after `_emote.PostAnimationUpdate`, before `_mirrorRt.Tick()`** | **reconciled**: in the bones tree this ran first, before `JiggleSolver.LateTick`. Here `PosePlayer` writes the whole skeleton from `PostAnimationUpdate` while a clip is active (`posDriven=5` on the native path), so a first-position call would be undone by the emote write every frame. It must be the last skeleton write before the mirror render. Measured: `latePins=632` on `spine_05`, `clavicle_l/r`, `neck_01` — the live animator does rewrite those positions (up to 30 mm between verifications) and this hook puts them back. |
| `ModelSwapRuntime.cs` | nothing else | no `Dispose` change needed: `_proportions?.Dispose()` already runs before the mirror teardown, and `Proportions.Restore` now also restores the garment meshes. The emote lease, cosmetics, tp camera and prop are untouched. |

`AlterumModelSwap.Contracts` was **not** modified.

## 1. Strap-junction hole at the shipped value (Diana, frozen rig, 720×1080, 3.7×)

Frozen + soloed `Name=PropCaptureBody` (spawn clone of `Female_3_Diana`), capture camera, pose
frozen, jiggle solver off (rest shape), `chestfix=1` vs `chestfix=0` at the shipped 1.90.

`tools/patcheck5.py` (union garment footprint, per-state strap mask, hem blobs excluded):

| state | skin px on the tank top | components |
|---|---|---|
| authored weights (`chestfix=0`) | **177** | y=262..270 (the strap-junction hole, **53 px**) + y=418..445 (hem, 124 px) |
| fixed, shipped (`chestfix=1`) | **124** | y=418..445 only (hem, 124 px) |

The chest component is **0 px** with the fix and 53 px without it; the remaining 124 px are the
tank top's hem over the belt, present identically in both states and unchanged by the value
(pass 5 measured the same constant at 246 px on its own camera). `shots/proportions-merged/`
`d-1.90-fx{0,1}-*`, `ship-3.7x.png` (authored vs fixed, same crop, 3.7×), `*-pokepix.png`.

`[chest:apply] mesh='Jane_Undershirt' verts=2232 band=[-0.075,+0.095] fade=0.030 halfWidth=0.160
rim=0..0.012 rims=106 candidates=916 changed=901 noSkin=13 maxShift=0.372 meanShift=0.083
normalised=ok(sum=1,<=4) mesh=Jane_Undershirt->Jane_Undershirt(Clone) (clone)`

## 2. Freya is a no-op — confirmed, not assumed

Spawn `Female_2_Freya`, same method. Her `body_Optimized` is found, but it has **no vertices in the
chest band** (her shirt is closed and *is* the bust surface), so every band candidate finds no skin
to sample — which is exactly the pass-5 reason, now observed here rather than assumed:

```
[chest:apply] model='PropCaptureBody' body='body_Optimized' no garment mesh needed a chest reweight
[chest:skip] mesh='Shirt' reason=no-verts-changed (candidates=596, noSkin=596)
```

0 garment vertices changed. Her garment-only renders at 1.90 with the fix on and off are
**byte-identical** (`f-1.90-fx1-shirt-three.png` = `f-1.90-fx0-shirt-three.png`,
md5 `1affa58d8f5f717060cee03078dbb2db`), the chest band of the two full renders differs by **0 px**,
and `patcheck5.py` reports the same 48 px / 3 blobs for both states (the collar skin, not through
the shirt).

## 3. Invariants — measured, on the shipped build

`propcap measure=1`, one frozen pose, same rig, shipped vs natural (all three knobs 1.0):

| measure | natural (1.0/1.0/1.0) | shipped (1.55/1.226/1.50) | Δ |
|---|---|---|---|
| shoulder joint width (`upperarm_l..r`) | 0.2896 m | **0.2896 m** | 0.0000 |
| height (head → lowest bone) | 1.6012 m | **1.6012 m** | 0.0000 |
| hip width (thigh roots) | 0.1993 m | **0.2990 m** | ×1.500 |
| hip half width | 0.0997 m | **0.1495 m** | ×1.500 |
| chest pivot → leaf | 0.0490 m | **0.0601 m** | ×1.226 |

*Absolute* values vs pass 5's table: hip width **0.2990 m matches exactly**; shoulder joint width
and height differ from pass 5's 0.2925 / 1.5972 by 3 mm / 4 mm because the frozen pose here is the
local player's idle copied onto the clone (`posecopy=1`), not a wardrobe preview idle. The
*invariance* claim is exact in the same pose: shoulder width and height are unchanged to the
4th decimal against the same rig at 1.0/1.0/1.0.

Rest length at the shipped cage, on the live local rig (`solver.request dump=1`):
`chest_02_l orig pos 0.0492` → ×1.22581 = **0.0603 m** (the task's number; the pose-copied clone
reads 0.0601 because its pose has a 0.0490 rest). Both are the cage-scaled rest, no retune.

## 4. Jiggle: suspends on emote start, re-seeds on release — with live leaves

The standing limitation of the previous merged verification (male local body ⇒ `rigs=0`, vacuous
gate) **does not apply any more**: the local body is `Female_03_Diana`, the solver is attached with
`rigs=1 leaves=2`. One native clip (`Axe_Idle`) and one LC clip (`Salute`) verbatim:

```
[emote] START clip='native:FemaleAdventure_Hands_TP_Axe_Idle' (game clip) … suspended=True
        lease=DoingSomething+DisableAnimancerAndAnimator
[jsolve:gate] suspended reason=emote: 2 leaf write(s) forgotten, chest writes stop here - the emote
        owns the skeleton while it (and its fade-out) writes
[emote] STOP reason=… clip='native:…Axe_Idle' t=0.00s released=True fadeOut=0.20s native=1
        maskedAllowed=38/186 bodyChannelsChanged=0 bodyMaxDev=0.000deg
[jsolve:gate] resumed reason=animation: rigs=1, particles re-seeded from the animated pose
```

`[jsolve:gate] suspended` / `resumed … re-seeded … rigs=1` appears once per clip in both sweeps
(38 + 12), and the rate line keeps `rigs=1 leaves=2` throughout
(`[jsolve:rate] enabled=1 rigs=1 leaves=2 frames=… peakDev=4.369deg peakOff=7.49mm`).

## 5. The 50-clip emote sweep against the merged proportions

Both own-documented paths, run on the merged build (install A, live lobby):

* `nativesweep=1` → `verify/merged_native_sweep.log`: **38/38**
  `start=1 stop=1 released=True animEnabled=True bodyChannelsChanged=0 bodyMaxDev=0.000deg`,
  restore motion **1.66–2.17°** (previous merged run 1.67–2.69°), `poseVsEntry` 1.01–5.63°,
  `[emote:nativesweep] COMPLETE clips=38`.
* `selftest=1` → `verify/merged_lc_sweep.log`: **12/12** start/stop/`released=True`, stop reasons
  identical to the pre-merge run (`selftest-window`, `end(Middle_Finger)`, `movement(0.600m)`,
  `end(D_Middle_Finger)`), `[emote:selftest] RESTORE CHECK game animation running again: max bone
  change over 0.4s = 2.24deg` (previous merged 2.28°, track 2.32°), `sequence complete`.

No clip was left mid-restore and no `[Error]`/exception line was produced after the deploy. The
widened leg roots do not disturb the IK clips: `mapped=51/51 targetMissing=0` on every LC START and
no IK warning in either sweep.

## 6. Chest-weight clones are torn down

Three reloads, same dll (`iterate.sh hot` / `deploy-hot.sh`, generation 39 → 40 → 41 → 42):

```
[chest:restore] mesh='Jane_Undershirt' original sharedMesh restored, clone destroyed (changed=901)
[chest:restore] mesh='Kiona_Undershirt' original sharedMesh restored, clone destroyed (changed=896)
[chest:restore] mesh='Jane_Undershirt' original sharedMesh restored, clone destroyed (changed=901)
[bounds:disposed] generation=com.user0.alterum.proportions.… patchesRemoved=True
[hot] generation 4N active
[chest:apply] mesh='Jane_Undershirt' … mesh=Jane_Undershirt->Jane_Undershirt(Clone) (clone)
```

No leaked meshes: over the whole session 17 clones were created and 14 destroyed — the balance is
exactly the 3 live clones of the current generation — and **no apply ever found a `(Clone)` as its
source** (`mesh=Jane_Undershirt->…`, never `Jane_Undershirt(Clone)->…`), i.e. the authored
`sharedMesh` was re-pointed before each re-apply rather than a clone of a clone.
One reload (the first) had nothing to restore because a capture had left `chestfix=0` at that
moment; the two reloads after a clean re-apply each restored all three meshes.
A spawned capture rig tears down the same way: `[propcap:release]` → `[bounds:restore] rig=
'PropCaptureBody' reason='model gone'` (its chest fix, if any, is restored before the nodes).

## 7. No regressions

| gate | evidence in the deployed build (generation 42) |
|---|---|
| mirror body binding | `[mirror:body-check] bound=True renderers=8 controllers=2 isolated=8 driven=8 suppressedKeys=0 passedKeys=0 …` |
| tp-cam candidates / restore | `[tp-cam:init] generation=1 candidates=7 …` + 7 `[tp-cam:candidate]` lines; `[tp-cam:pick] … candidate=elevated-close`; body bit restored via `[bodyview:veto] cleared -> third-person body admission allowed again` |
| first-person masks | `[bodyview:screen] pov=FirstPerson … masks='Lobby Global/Main Camera'=0x4DDB8F87-.` (bit 14 absent), third person `0x4DDBCFC7+o`; `[cosmetic:mask] screen masks: 'Lobby Global/Main Camera'=0x4DDB8F87+.` |
| prop visible | `[prop:view] verdict=VISIBLE` (first person, `propDist=5.09m`, `[prop:occl] blocked=0/16`) |
| prop scroll settling | `[reel] settled symbol=2 code=KEY name=key cell=11 targetCell=11 targetSymbol=2 u=0.62500 centre=1.000 match=True total=3.200s grants=0` |
| cosmetics template | `[cosmetic:material] id=28 name='Party_Hat_DefaultMaterial_BaseMap' … template='MegaBook Page Back' …`; `[cosmetic:load] id=builtin.partyhat … layer=23` |
| no swap path | `grep -c SWAP LogOutput.log` = **0** |

## Still broken / differences worth knowing

1. **Sarah's tank top is not reweighted on this prefab.** `ChestWeights.Apply` picks the skin
   renderer by name (`*body*`); the `Female_05_Sarah` rig on this install has no such renderer
   (`Sarah_Belt/Chain/Pants/Shirt/Hands/Head/Hairstyle`), so it logs
   `[chest:skip] no body/skin SkinnedMeshRenderer found` and leaves her garment authored. Pass 5
   reported Sarah 901/2232, so that prefab must have had a `*body*` skin mesh. Not fixed here: it
   needs a selection rule (e.g. “largest renderer carrying chest_02 weights”) rather than a
   one-line change, and it changes which meshes get rewritten. Diana and Kiona are covered.
2. Freya's “no verts in the chest band” holds, but through `noSkin=596` on the band candidates
   rather than an empty band (§2) — the same end state, a different log path than pass 5's wording.
3. `[jsolve:gate] rig='PropCaptureBody' disabled: Il2CppException` at a capture teardown is the
   expected path (the clone is destroyed while the solver holds it); it is followed by
   `[bounds:restore] … reason='model gone'` and a cloth write-back restore.
