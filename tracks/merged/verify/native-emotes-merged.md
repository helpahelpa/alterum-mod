# Native emotes in the merged build — port, verification, evidence

Source track: `tracks/emotes/` (38 native Alterum clips + the 12 ported LC clips, notes in
`tracks/emotes/notes/native-emotes.md`). Target: `tracks/merged/mod/` — the tree that also carries
the jiggle suspension (`EmoteRuntime.SkeletonOwned` → `JiggleSolver.SetSuppressed`), the cosmetic
subtree exclusion in `PosePlayer.Walk`, and the cosmetics/proportions/cloth/prop work.

Deployed binary: `AlterumModelSwap.Hot.dll` md5 **831d3d09fda5afb9d072e4cac2d4cd2b** (21 378 560 B,
2026-11-12 12:32), identical on
* A `C:\Program Files (x86)\Steam\steamapps\common\Alterum\BepInEx\hot\`
* B `C:\AlterumB\BepInEx\hot\b\`

A was only hot reloaded (`[hot] generation 4 → 10`, process 57004 started 10:59:02 unchanged);
B was not running. `Contracts`/host are unchanged (no `AlterumModelSwap.Contracts` edit), so no
restart paths were touched. Rebuild/ redeploy: `tracks/merged/tools/deploy-hot.sh`.

Model during verification: `3P_ConcreteModel_Male_01(Clone)`, 155 bound transforms
(`cosmeticSkipped=4`), 20 probes, `legScale=0.802`. The emotes track ran on a 176-transform rig,
which is why the absolute restore/pose numbers differ slightly; the acceptance rule is the same.

## The 50-entry list

`[emote] pose player ready (12 LC clips + 38 game clips); F6 toggles, F9 cycles the 50-entry list,
F8 toggles the selected game clip`

```
[emote:native] catalogue: offered=38 (body=2 upper=36) ids=[native:Common_ThirdView_Movement_Idle,
native:CommonAdventure_ThirdView_Movement_Idle, native:FemaleAdventure_Hands_TP_…]
[emote:native] hands layer mask='Hands Mask' (arms-only layering)
```

Identical ids to the track. `cycle=49` + `cycle=1` drove the exact `CycleNext` path F9 calls and
logged **50 distinct ids** (`[emote] request-cycle: clip index k/50 -> '<id>'`), so the combined
list is one cycle, not two.

## Native clips — armed sweep (`nativesweep=1`), merged build

`verify/native_sweep_final.log`. Per clip: start, hold, stop, lease released, animancer/animator
re-enabled, restore motion over the 0.4 s after the hand-back, pose vs the pre-start pose, and the
merged-only masked-write guard (`bodyChannelsChanged` / `bodyMaxDev` = channels outside the clip's
layer mask that changed value). Last two columns are the same fields from the track's table.

| # | clip | family | start | hold | stop | released | animEnabled | restore (deg) | moved bones | poseVsEntry (worst) | bodyChannels / maxDev | pelvisΔ (m) | track restore / poseVsEntry |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `native:Common_ThirdView_Movement_Idle` | body | 1 | 1.60s | 1 | True | True | 1.77 | 18/20 | 6.23 (calf_l) | 0 / 0.000deg | 0.0117 | 1.73 / 0.37 |
| 2 | `native:CommonAdventure_ThirdView_Movement_Idle` | body | 1 | 1.60s | 1 | True | True | 1.85 | 18/20 | 4.10 (calf_l) | 0 / 0.000deg | 0.0129 | 1.73 / 4.17 |
| 3 | `native:FemaleAdventure_Hands_TP_AdrenalineMushroom_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.77 | 18/20 | 4.18 (calf_l) | 0 / 0.000deg | 0.0131 | 1.73 / 4.17 |
| 4 | `native:FemaleAdventure_Hands_TP_AdrenalineMushroom_Using` | upper | 1 | 1.60s | 1 | True | True | 2.15 | 20/20 | 1.31 (upperarm_l) | 0 / 0.000deg | 0.0019 | 2.09 / 1.31 |
| 5 | `native:FemaleAdventure_Hands_TP_Axe_Attack_1` | upper | 1 | 1.60s | 1 | True | True | 1.82 | 18/20 | 1.66 (upperarm_l) | 0 / 0.000deg | 0.0016 | 2.38 / 2.18 |
| 6 | `native:FemaleAdventure_Hands_TP_Axe_Attack_2` | upper | 1 | 1.60s | 1 | True | True | 1.88 | 17/20 | 2.46 (upperarm_l) | 0 / 0.000deg | 0.0024 | 1.71 / 2.29 |
| 7 | `native:FemaleAdventure_Hands_TP_Axe_Attack_Throw` | upper | 1 | 1.60s | 1 | True | True | 2.14 | 17/20 | 2.43 (upperarm_l) | 0 / 0.000deg | 0.0024 | 2.04 / 2.52 |
| 8 | `native:FemaleAdventure_Hands_TP_Axe_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 5.01 (calf_l) | 0 / 0.000deg | 0.0113 | 1.72 / 5.00 |
| 9 | `native:FemaleAdventure_Hands_TP_Axe_Idle_ThrowPrepare` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.45 (calf_l) | 0 / 0.000deg | 0.0123 | 1.69 / 4.14 |
| 10 | `native:FemaleAdventure_Hands_TP_CeremonialCandle_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.45 (calf_l) | 0 / 0.000deg | 0.0123 | 1.72 / 4.24 |
| 11 | `native:FemaleAdventure_Hands_TP_Crucifix_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.45 (calf_l) | 0 / 0.000deg | 0.0123 | 1.80 / 4.17 |
| 12 | `native:FemaleAdventure_Hands_TP_Crucifix_Purification` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.45 (calf_l) | 0 / 0.000deg | 0.0123 | 1.87 / 4.22 |
| 13 | `native:FemaleAdventure_Hands_TP_DoingSomething` | upper | 1 | 1.60s | 1 | True | True | 1.85 | 18/20 | 4.48 (calf_l) | 0 / 0.000deg | 0.0131 | 1.72 / 4.56 |
| 14 | `native:FemaleAdventure_Hands_TP_DoingSomething_CeremonialCandle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.69 / 4.14 |
| 15 | `native:FemaleAdventure_Hands_TP_DoingSomething_Rune` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.87 / 4.22 |
| 16 | `native:FemaleAdventure_Hands_TP_DoorKey_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.85 | 18/20 | 4.18 (calf_l) | 0 / 0.000deg | 0.0131 | 1.87 / 4.53 |
| 17 | `native:FemaleAdventure_Hands_TP_Flashlight_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.85 | 18/20 | 4.18 (calf_l) | 0 / 0.000deg | 0.0131 | 1.73 / 4.56 |
| 18 | `native:FemaleAdventure_Hands_TP_HealthFlask_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.87 / 4.14 |
| 19 | `native:FemaleAdventure_Hands_TP_HealthFlask_Using_Quick` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.45 (calf_l) | 0 / 0.000deg | 0.0123 | 1.87 / 4.53 |
| 20 | `native:FemaleAdventure_Hands_TP_HealthMushroom_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.45 (calf_l) | 0 / 0.000deg | 0.0123 | 1.80 / 4.56 |
| 21 | `native:FemaleAdventure_Hands_TP_HealthMushroom_Using` | upper | 1 | 1.60s | 1 | True | True | 2.15 | 20/20 | 1.86 (upperarm_l) | 0 / 0.000deg | 0.0021 | 1.70 / 0.90 |
| 22 | `native:FemaleAdventure_Hands_TP_HealthSyringe_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 5.78 (calf_l) | 0 / 0.000deg | 0.0148 | 1.87 / 5.76 |
| 23 | `native:FemaleAdventure_Hands_TP_HealthSyringe_Using_Quick` | upper | 1 | 1.60s | 1 | True | True | 1.84 | 19/20 | 4.45 (calf_l) | 0 / 0.000deg | 0.0123 | 1.87 / 4.53 |
| 24 | `native:FemaleAdventure_Hands_TP_Lantern_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.77 | 17/20 | 4.48 (calf_l) | 0 / 0.000deg | 0.0131 | 1.87 / 4.53 |
| 25 | `native:FemaleAdventure_Hands_TP_RitualDagger_Attack_1` | upper | 1 | 1.60s | 1 | True | True | 2.16 | 17/20 | 1.11 (upperarm_r) | 0 / 0.000deg | 0.0011 | 2.49 / 0.73 |
| 26 | `native:FemaleAdventure_Hands_TP_RitualDagger_Attack_2` | upper | 1 | 1.60s | 1 | True | True | 2.69 | 18/20 | 2.62 (clavicle_r) | 0 / 0.000deg | 0.0015 | 2.52 / 2.55 |
| 27 | `native:FemaleAdventure_Hands_TP_RitualDagger_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 5.27 (calf_l) | 0 / 0.000deg | 0.0116 | 1.87 / 5.30 |
| 28 | `native:FemaleAdventure_Hands_TP_Rope_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.73 / 4.56 |
| 29 | `native:FemaleAdventure_Hands_TP_Rune_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.87 / 4.14 |
| 30 | `native:FemaleAdventure_Hands_TP_SpellScrollPart_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.73 / 4.56 |
| 31 | `native:FemaleAdventure_Hands_TP_SpellScroll_Burning` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.72 / 4.17 |
| 32 | `native:FemaleAdventure_Hands_TP_SpellScroll_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.69 / 4.14 |
| 33 | `native:FemaleAdventure_Hands_TP_Spellbook_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.73 / 4.24 |
| 34 | `native:FemaleAdventure_Hands_TP_ThrowingDagger_Attack_Throw` | upper | 1 | 1.60s | 1 | True | True | 1.82 | 15/20 | 2.48 (lowerarm_r) | 0 / 0.000deg | 0.0026 | 1.84 / 2.05 |
| 35 | `native:FemaleAdventure_Hands_TP_ThrowingDagger_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.77 | 18/20 | 4.86 (upperarm_l) | 0 / 0.000deg | 0.0107 | 1.73 / 4.79 |
| 36 | `native:FemaleAdventure_Hands_TP_ThrowingDagger_Idle_ThrowPrepare` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.07 (calf_l) | 0 / 0.000deg | 0.0121 | 1.73 / 4.17 |
| 37 | `native:FemaleAdventure_Hands_TP_WalkieTalkie_Idle` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.72 / 4.17 |
| 38 | `native:FemaleAdventure_Hands_TP_WalkieTalkie_IdleLowered` | upper | 1 | 1.60s | 1 | True | True | 1.67 | 19/20 | 4.15 (calf_l) | 0 / 0.000deg | 0.0122 | 1.69 / 4.14 |
All 38: start=1, stop=1, released=True, animEnabled=True. Restore 1.67–2.69° (track 1.69–2.52°).
bodyChannelsChanged=0 and bodyMaxDev=0.000deg for every clip.

Where the merged numbers differ from the track's, the difference is measurement context, not the
clip: `restoreMotionMaxDeg` is the game's own animation resuming (a 155-transform rig at a different
frame rate), and `poseVsEntryMaxDeg` is dominated by where the lobby idle happened to be when the
clip started (`worst='calf_l'` in both). No clip regressed into a stuck/half-restored state, so
none was fixed or removed.

The one merged-context outlier worth naming: `native:Common_ThirdView_Movement_Idle`
poseVsEntry 6.23° vs 0.37° in the track, because this run started it while the character was
mid idle-transition (pre-pose != settled idle). It is a property of the entry pose, not of the
write path: restore 1.77°, released, animEnabled.

The same sweep was run twice on this tree (before/after an evidence-only counter change,
`verify/native_sweep_prevbuild.log` vs `verify/native_sweep_final.log`) and that is the honest noise
floor: restore changes by ≤0.64° per clip, poseVsEntry by ≤1.33° (`RitualDagger_Attack_2` restore
2.05 ↔ 2.69, `Common_ThirdView_Movement_Idle` poseVsEntry 4.90 ↔ 6.23). The track-vs-merged gaps
above are inside that band.

## 12 ported LC clips — armed sweep (`selftest=1`), merged build

`verify/lc_sweep_final.log`.

| # | clip | duration | loop | frames | stop reason | released |
|---|---|---|---|---|---|---|
| 1 | `Salute` | 1.783 s | False | 108 | selftest-window | True |
| 2 | `Middle_Finger` | 0.867 s | False | 53 | end(Middle_Finger) | True |
| 3 | `Sign` | 2.083 s | True | 126 | selftest-window | True |
| 4 | `D_Sign` | 3.150 s | False | 190 | selftest-window | True |
| 5 | `The_Griddy` | 6.533 s | True | 393 | selftest-window | True |
| 6 | `Clap` | 1.000 s | True | 61 | selftest-window | True |
| 7 | `Twerk` | 0.250 s | True | 16 | selftest-window | True |
| 8 | `Prisyadka` | 6.750 s | True | 406 | selftest-window | True |
| 9 | `Idle1Fix` | 4.033 s | True | 243 | movement(0.600m) | True |
| 10 | `Shy` | 2.500 s | True | 151 | selftest-window | True |
| 11 | `D_Clap` | 1.000 s | True | 61 | selftest-window | True |
| 12 | `D_Middle_Finger` | 0.867 s | False | 53 | end(D_Middle_Finger) | True |
`mapped=51/51 targetMissing=0` on all 12 START lines.
`[emote:selftest] RESTORE CHECK game animation running again: max bone change over 0.4s = 2.28deg`
(track: 2.32°).

## Jiggle suspension during a native emote

The merged arbitration is `ModelSwapRuntime.LateUpdate`:
`emoteOwns = _emote.SkeletonOwned` → `JiggleSolver.SetSuppressed(...)` → `LateTick` →
`_emote.PostAnimationUpdate`. Native playback sets exactly the same `Active`/`_stopping` flags as
the LC path (one ownership signal), so `SkeletonOwned` is true for the whole native run including
its 0.2 s fade-out. Observed for a native clip (`Axe_Idle`, one run):

```
[emote] START clip='native:FemaleAdventure_Hands_TP_Axe_Idle' (game clip) gen=1 len=3.000s loop=1
        bones=155 rotDriven=50 posDriven=1 sampler=playablegraph mask=[mask='Hands Mask'
        parts=LeftArm,LeftFingers,RightArm,RightFingers bones=38/52 mapped] maskedAllowed=38/155
        suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
[jsolve:gate] suspended reason=emote: 0 leaf write(s) forgotten, chest writes stop here - the emote
        owns the skeleton while it (and its fade-out) writes
…
[emote] STOP reason=unbind clip='native:FemaleAdventure_Hands_TP_Axe_Idle' t=0.31s released=True
        fadeOut=0.20s native=1 maskedAllowed=38/155 bodyChannelsChanged=0 bodyMaxDev=0.000deg
[emote:native] sampler disposed reason=stop writes=138
[jsolve:gate] resumed reason=animation: rigs=0, particles re-seeded from the animated pose
```

(verbatim in `verify/evidence_single_native_clip.txt`; that run was cut by a hot reload, hence
`reason=unbind`.)

The same pair appears once per native clip in the sweep (38 ×) and once per LC clip in the LC sweep
(12 ×); the sweep also samples the flag from the solver side while the emote holds the skeleton:

```
… VERIFY clip='native:…' … jsolveSuppressedStart=False mid=True afterRelease=False …
… STOP  clip='native:…' … jsolveSuppressed=True …
```

Honest limitation: in this lobby the jiggle solver has **no attached rig** (`rigs=0`), because the
local body is the male model, which has no `chest_02_l/r` leaves and no MagicaCloth — `Attach`
finds nothing to jiggle and returns. The gate transitions, the forgotten-write count and the
re-seed therefore have the same (vacuous) content as they do for the LC clips in this build
(`SHOWCASE.md` shows the same `rigs=0`); the suspension *contract* is exercised, the leaf
bookkeeping has nothing to forget. Proving it against live chest leaves needs the local player on
one of the female bodies; that needs a wardrobe switch, which the locked desktop cannot do.

## Upper-body zero-write check (merged-specific)

The native write loop now leaves a masked-off channel untouched instead of re-writing its entry
value: `allowed=false → continue` (only the guard reads it). So for the 36 upper-body clips, pelvis,
spine, neck, head, thigh, calf, foot, ball and every other non-arm channel is never assigned. The
merged tree has other post-animation writers (proportions leaves, jiggle when present, cloth, the
cosmetics fit), so the run also measures it: every native frame compares each masked-off channel
with its entry value and counts distinct deviating bones plus the worst deviation.

Result over all 36 masked clips + the two body clips (no mask → nothing masked, 155/155 allowed):

```
bodyChannelsChanged=0    bodyMaxDev=0.000deg     (all 38 clips, sweep and manual runs)
[emote:native] t=… wrote=38 posBones=0 allowed=38/155 bodyChannelsChanged=0 bodyMaxDev=0.000deg
```

`wrote=38` = the 38 arm/finger bones the `Hands Mask` allows (`bones=38/52 mapped`), `posBones=0`.

## Regression gates (same build, A)

| gate | evidence |
|---|---|
| mirror body binding | `[mirror:body-check] owner='LobbyPlayer_Separated \| Owner: 0' bound=True renderers=9 controllers=2 keys=0 isolated=9 driven=9 suppressedKeys=0 passedKeys=0 foreignKeysLeftInPlace=0 …` (mirror frames 90 and 180 of the last generation) |
| mirror cosmetics, first-person rule | `[cosmetic:view] why=update pov=FirstPerson … fit=True screen=False mirror=True hideAll=False localLayer=6 instances=1 renderers=2`, `[cosmetic:layer] selected=6 name='InteractableLookable' … reason=unused/outside-screen … renderable=True inScreenMask=False` |
| mirror cosmetics, layer 23 for others | `[cosmetic:layer] mirror mask 0x4CDB4F87 -> 0x4CDB4FC7 added localLayer=6 ('InteractableLookable') layer23=True`; `[cosmetic:layer] screen camera 'Lobby Global/Main Camera' pristineMask=0x4DDB8F87 tracked=1`; `[clip] mask showcase … bodyLayers=[14,23]` for a third-person showcase capture |
| prop, scroll settling | `[reel] settled symbol=2 code=KEY name=key cell=1 targetCell=1 targetSymbol=2 u=0.00000 centre=1.000 match=True total=3.200s grants=0` |
| prop, visibility | `[prop:view] verdict=VISIBLE` earlier in this session; the last probe reads `verdict=PARTLY_OCCLUDED(6/16 'spine_01/spine_02/spine_03/neck_01/neck_01 [Hitbox]')` because the character had walked to within 0.93 m of the board — every blocker is the player's own hitbox, no room geometry |
| cosmetic material template | `[cosmetic:material] id=28 name='Party_Hat_DefaultMaterial_BaseMap' … template='MegaBook Page Back' …` |
| no swap path | `grep -c SWAP LogOutput.log` = 0 |
| clean teardown (mid-native-playback reload) | `[emote:teardown] id=1 writes=138 leaseHeld=True->False skeletonOwned=True animatorEnabled=true selftest=on layers=5->5 verifyCams=1 verifyRts=1 samplerRigs=1 nativeOffered=38` followed by generation 9; the next reload reports `samplerRigs=0 verifyCams=0 verifyRts=0` (the previous line's 1s are the same-frame `Object.Destroy` the sampler issued). `[swap:teardown] … emoteLeases=0` |
| no sampler leak across the sweeps | after 38 native clips + 4 captures + 12 LC clips: `[emote:teardown] … samplerRigs=0` |
| mirror/prop/cosmetics untouched | the only edited files are `EmoteRuntime.cs`, `PosePlayer.cs`, `ModelSwapRuntime.cs` (key legend text only) and `JiggleSolver.cs` (one read-only getter); `NativeSampler.cs`/`NativeCensus.cs` added. `CosmeticsRuntime.cs`, `PropView.cs`, `LobbyProp.cs`, `ReelArt.cs`, `Proportions.cs`, `JiggleProbe.cs`, the mirror files and `Contracts.cs` were not opened for writing |

## Captures (RenderTexture only, desktop locked)

`shots/native-emotes/` — 3 native upper-body clips + 1 body idle from `ClipRecorder`
(`view=showcase`, 640×360, 2 fps, 6 frames, `yaw=135 dist=2.6 lift=0.10`, third-person POV), plus
one 768×768 verify-camera still per clip from the sweep:

| clip | family | filmstrip | frames | still |
|---|---|---|---|---|
| `native:CommonAdventure_ThirdView_Movement_Idle` | body | `body_CommonAdventure_Idle_strip.png` | `body_CommonAdventure_Idle/frame_000{1..6}.png` | `body_CommonAdventure_Idle_still.png` |
| `native:FemaleAdventure_Hands_TP_Axe_Idle` | upper | `upper_Axe_Idle_strip.png` | `upper_Axe_Idle/…` | `upper_Axe_Idle_still.png` |
| `native:FemaleAdventure_Hands_TP_Spellbook_Idle` | upper | `upper_Spellbook_Idle_strip.png` | `upper_Spellbook_Idle/…` | `upper_Spellbook_Idle_still.png` |
| `native:FemaleAdventure_Hands_TP_HealthFlask_Using_Quick` | upper | `upper_HealthFlask_Using_Quick_strip.png` | `upper_HealthFlask_Using_Quick/…` | `upper_HealthFlask_Using_Quick_still.png` |

Frame-to-frame mean |Δ| over the 6 frames: body idle 0.75, axe idle 0.37, spellbook idle 0.43,
flask use 0.84 (0 = frozen; the arms move, the masked body does not).

## What the merge had to adapt

1. `EmoteRuntime` kept only one ownership mechanism. The merged tree's stage-1 diagnostic (an extra
   Animancer layer, `AcquireLayer`/`RetireLayer`/`NativeTick`, F8) was **removed**; F8 now toggles
   the selected native clip through `StartAt(PackCount + _nativeSel)`, and the key legend/overlay
   string were updated. A clip reaches the skeleton only via `PosePlayer` under the lease.
2. `PosePlayer` is the track's native code on the merged (pre-IK-pass) player: `StartNative`,
   `NativeSampler`, `ComputeBodyPartMask`/`BodyPartOf`, `NativeApply`, `ProbeNativeClip`,
   `DisposeSampler`, native-aware `Elapsed`/`Duration`/`Stop`/`AbortPlayback`/`CheckLateWriter`, plus
   `NativeClip`/`NativeMode` and the masked-write guard. The merged `Walk` cosmetic-subtree skip is
   kept untouched, so cosmetic roots never enter `_order` and therefore never enter the scratch rig
   either.
3. Masked write rule tightened for the merged tree: masked-off channels are not assigned at all
   (the track re-wrote their entry value). With additional post-animation writers in this tree,
   "did anything move them" is measured per run instead of assumed (`bodyChannelsChanged`).
4. `JiggleSolver.IsSuppressed` added as a read-only getter (no behaviour change) so the sweep can
   print the solver's own view of the arbitration next to the emote's.
5. Native runs now bump `PosePlayer._writeFrames` so `[emote:teardown]` reports writes for
   native-only sessions.
6. `Rebind`, `Dispose` and `PollRequestFile` reset/clean the native catalogue, sweep, film and
   `_nativeSel`; `Dispose` also counts leftover `AlterumEmoteSampleRig` objects.
7. Request keys added: `census`, `nativesweep`, `nativefilm`, `filmnames`, `probe`, `cycle`; `clip=`
   resolves native ids (`native:<Name>` or a bare catalogue name) through `TryIndexOfEmote`.
8. `NativeSampler.cs` and `NativeCensus.cs` copied verbatim from the track.
