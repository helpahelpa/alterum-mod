# Merged build — collision record

Source: `tracks/merged/mod/`. Base tree: `tracks/bones/mod/` (swap removal, newest Contracts,
jiggle/proportion/cloth work). Ported in from `tracks/emotes/mod/`: `EmotePack.cs`, `EmoteRuntime.cs`,
`PosePlayer.cs`, the Animancer references, `plugins/AlterumEmotes/` pack. Ported in from
`tracks/cosmetics/mod/`: `CosmeticPack.cs`, `CosmeticsRuntime.cs`, `CosmeticMarkers.cs`,
`PngDecoder.cs`, `PovToggle.cs`, the embedded `cosmetics.apack`, the cosmetic hooks in the two
mirror files and the `ClipRecorder` additions. No track tree was modified.

Build: `dotnet build -c Release` in `AlterumModelSwap.Contracts`, `AlterumModelSwap`,
`AlterumModelSwap.Hot` (in that order). Refs resolve through `$(AlterumDataDir)refs/`
(`~/assets/monorepo/alterum-data/refs/`), exactly like the tracks, guarded by `AlterumRefsCheck`.

## Shared-file collisions and resolutions

| file | collision | resolution |
|---|---|---|
| `ModelSwapRuntime.cs` | each track replaced the other's per-frame block and `LateUpdate`/`Dispose` | union, with one ordering block in `LateUpdate`: (1) suspend/keep the jiggle solve, (2) jiggle `LateTick` (bones: before the mirror renders), (3) emote `PostAnimationUpdate` (emotes: pose before the mirror renders), (4) mirror `Tick`, (5) emote bookkeeping, (6) jiggle probe. `Update` runs the emote runtime early, then cloth/proportions/jsolve/probes, then `PovToggle.Tick` → `ForcePovRefresh`, then the mirror placement, then `_cosmetics.Update()` after `Visibility.Update()`. `Dispose` order: emote (release lease) → jiggle probe/solver strip → cloth metrics → proportions restore → cloth sim → destroy mirror → cosmetics. `PlaceMirror` wires `MirrorRuntime.Cosmetics` + `SetMirrorCamera` before the first render. The bones `DumpModelTable` (no swap) is kept; the swap block does not exist. |
| `HotModule.cs` | bones ticked both helpers without guards, the other two only `ClipRecorder` | shared-tree union: `ScreenShot.Tick` and `ClipRecorder.Tick` each in their own try/catch, `ClipRecorder.Abort` in `Dispose`. |
| `Alterum.References.props` | bones added `MagicaClothV2`, emotes added `Kybernetik.Animancer` + `.FSM`, cosmetics none | union (27 references). |
| `AlterumModelSwap.Hot.csproj` | cosmetics added the embedded pack | bones/emotes csproj + `<EmbeddedResource Include="cosmetics.apack" LogicalName="AlterumModelSwap.Hot.cosmetics.apack">`; `Il2CppSystem.Core` kept. Host and Contracts csproj were identical everywhere and are unchanged. |
| `Contracts.cs` | bones removed the `ConfigSnapshot` swap fields; emotes/cosmetics added nothing | bones version (`0de8cdac…`). **Contracts change ⇒ host + Contracts dll replaced, game restarted.** |
| mirror + `ClipRecorder` | cosmetics modified files the other two copied verbatim | see the md5 table below. |

## md5 of the four shared files

| file | shared `mod/` | bones | emotes | cosmetics | merged |
|---|---|---|---|---|---|
| `ScreenShot.cs` | a164a70143029a7ffe7b88c5ce15dcb0 | same | **absent** | **absent** | a164a70143029a7ffe7b88c5ce15dcb0 |
| `ClipRecorder.cs` | 0c871778dacc10f3a9d77a8d740902b3 | same | same | 176b410b63fd9eee3fb265adf69db2f3 | cosmetics superset |
| `Mirror.cs` | 08b77a17c997ed1b8cb0998d35ec8cb6 | same | same | e633848eff28e5fadbe88a56ba639792 | cosmetics superset |
| `MirrorPlayerVisibility.cs` | 3f33c8244d41dde1901e8a9d5f48c179 | same | same | 0d7d2cebb6946448377334762c88791d | cosmetics superset |

`ScreenShot.cs` exists only in the shared tree and the bones tree (the other two tracks never
carried it); it is byte-identical where present.

The emotes/cosmetics trees did **not** keep the mirror byte-identical:
* `Mirror.cs`: ORs `1<<23` into the mirror camera mask, holds `CosmeticsRuntime`, and wraps
  `reflectCam.Render()` in `CosmeticsRuntime.BeginMirrorView()` (restored in `finally`).
* `MirrorPlayerVisibility.cs`: skips marker-carrying renderers in the body scan and in
  `Snapshot()` (`[mirror:body-cosmetic-skipped]`), exposes `Current` and
  `TryMeasureNativeHide()`.
* `ClipRecorder.cs`: additive `look=head|body`, `lookoffset`, pinned `eye`/`lookat`, camera census,
  invariant vector formatting. No behaviour change to the existing request keys.

The merged tree takes those supersets because they are required for a hat to render in the mirror
at all (and for the requested `[mirror:body-cosmetic-skipped]` / 9-of-9 body evidence). The merged
mirror is therefore **not** byte-identical to `mod/`; the difference is exactly the cosmetic
exclusion + mirror view scope + layer-23 mask bit. Everything else in the mirror is untouched.

## Emote vs jiggle: sole-writer decision

`JiggleSolver` writes `chest_02_l/r` additively from `LateUpdate`; `PosePlayer` writes the whole
skeleton from the same phase while an emote (or its 0.2 s fade-out) is active. Two writers on the
same leaves was rejected: additive jiggle on an owned pose would also break the solver's
"was this value ours" bookkeeping.

Decision: the solve is **suspended** while `EmoteRuntime.SkeletonOwned` is true
(`PosePlayer.Writing = Active || _stopping`), and resumed afterwards.
`ModelSwapRuntime.LateUpdate` calls `JiggleSolver.SetSuppressed(...)` before `LateTick`.
Suspension writes no transform; it forgets the recorded writes so a stale offset is never
subtracted from the emote pose, and re-seeds the particles on release. Logged as
`[jsolve:gate] suspended reason=emote: N leaf write(s) forgotten …` / `[jsolve:gate] resumed …`.

## Function-key collision (not in the task list, found during the merge)

Emotes used F6/F7/F8; cosmetics used F6 (overlay) and F7 (POV). Merged map, logged as `[keys]`:

F5 clothes toggle | F6 emote toggle | F7 POV third-person | F8 emote native-clip diagnostic |
F9 emote next clip | F10 screenshot | F11 clip recorder | F12 cosmetics panel.

Request files (`emote.request`, `pov.request`, `clip.request`, `shot.request`, `solver.request`,
`clothsim.request`) are unchanged and are the automation path.

## Clothes toggle (added after the merge)

`ClothesToggle.cs` hides the worn garments on every player model in the scene (local third- and
first-person body, all wardrobe mannequins, any other player model) with `Renderer.forceRenderingOff`
and restores the recorded pre-hide value on `on`. It is a pure per-renderer flag: `enabled`, layers,
bounds, the mirror body binding's `isolated/driven` census, the first-person policy and the
chest-weight clones are untouched. The tick runs after the mirror's `Visibility.Update()`, so a
wardrobe-switch `Rebind` (which writes `forceRenderingOff=false`) is re-asserted in the same frame.

F5 toggles it; `clothes.request` (`off`/`on`/`toggle`/`dump`/`state`/`mirror=<tag>`) is the
unattended path. Classification is a name-token rule (keep wins over garment, unmatched kept)
logged with the full per-model renderer table. State is not persisted across a hot reload: a new
generation starts clothed and scrubs any leftover garment flag. Verification, captures and the
per-body findings: `shots/clothes-toggle/` (`README.md`, `evidence.log`).

Gated mode (same file, no other change): the hide is now decided **per garment** from a measured
body-coverage number instead of per model. Per model, the `keep` renderers whose name is a body part
form the skin set; each garment's vertices are sampled in world bind pose and a sample is *covered*
when a skin vertex lies within 30 mm; the garment hides when ≥80 % of it is covered (`samples=600`,
search exact to 50 mm). Decisions and the 10/20/30/40/50 mm shares are logged per garment per model
(`coverage`, `dump`), and `tolerance=`/`threshold=`/`samples=` retune at runtime. F5 = gated,
Shift+F5 = hide everything; `clothes.request` gained `gated`/`all`/`coverage`/`tolerance`/
`threshold`/`samples` (`off` still means unconditional). Live: 2 of 38 garments hide
(`Kiona_Undershirt` 0.938, `Female_02` `Hat` 0.895); every model that only has a body fragment
stays dressed (local `Female_01`, Diana, Ryan, James, Male_01/02/03, Sarah, 1P). Captures, the
per-garment table and the `analysis.txt` component/hole verdicts: `shots/clothes-toggle-gated/`
(driver `tools/clothes-gated-verify.sh`).

## Deployed state

* A (`…/common/Alterum`): merged host + Contracts in `BepInEx/plugins/`, merged hot dll in
  `BepInEx/hot/`, emote pack in `BepInEx/plugins/AlterumEmotes/`.
* B (`C:\AlterumB`): merged host + Contracts in `BepInEx/plugins/`, merged hot dll in
  `BepInEx/hot/b/`. B was not running; it needs no restart to pick this up.
* Contracts differ from what A last ran (swap fields removed) ⇒ A was started fresh with the merged
  host; no in-place restart of a running instance was needed.

## Native emotes merged in (from `tracks/emotes/`)

`tracks/emotes/` added 38 native Alterum clips next to the 12 ported LC clips (50-entry cycle,
`clip=native:<Name>`) against a `PosePlayer` that has since diverged (the track's later IK pass).
The port therefore takes the track's native *subsystem* and not its IK work:

| file | change |
|---|---|
| `NativeSampler.cs` | added verbatim from the track (scratch rig + one-clip `PlayableGraph`, humanoid retargeting, per-bone drive measurement, `SampleAnimation` fallback). |
| `NativeCensus.cs` | added verbatim (read-only layer/state/clip census for `census=1`). |
| `PosePlayer.cs` | native playback on the merged player: `StartNative` (same `AcquireLease`, same `RestorePoseNow`, same 0.2 s fade), `NativeApply` in the existing `PostAnimationUpdate`, `ComputeBodyPartMask`/`BodyPartOf`, `ProbeNativeClip`, `DisposeSampler`, native-aware `Elapsed`/`Duration`/`Stop`/`AbortPlayback`/`CheckLateWriter`, `NativeClip`/`NativeMode`. `Walk`'s cosmetic-subtree skip is untouched. |
| `EmoteRuntime.cs` | native catalogue (`NativeEmote`, curated body manifest, `FemaleAdventure_Hands_TP_*` upper family, `ClassifyNative`/`ClassifyPlayerClip`), `PackCount`/`TotalEmotes`/`IdAt`/`TryIndexOfEmote`, a combined `CycleNext` (F9) and `StartAt` that dispatches to `StartNative`, `nativesweep`, `nativefilm`, `census`, `probe`, `cycle`. The stage-1 Animancer-layer diagnostic (`AcquireLayer`/`RetireLayer`/`NativeTick`, old F8) is **removed** — one ownership mechanism only; F8 now toggles the selected native clip. |
| `JiggleSolver.cs` | `IsSuppressed` read-only getter (evidence only; no behaviour change). |
| `ModelSwapRuntime.cs` | key legend text only (`F8 native emote`, `F9 cycle emote`); the emote/jiggle ordering block is unchanged. |

Deliberate divergence from the track: a bone the clip's layer mask disables is **not assigned at
all** (the track re-wrote its entry value each frame). The merged tree has more post-animation
writers, so the run also counts masked-off channels that deviate from their entry value
(`bodyChannelsChanged`/`bodyMaxDev`, printed per native run and per sweep line) instead of trusting
the mask.

The native path takes the merged arbitration unchanged: `StartNative` sets the same `Active` then
`_stopping` flags, so `SkeletonOwned` (→ `JiggleSolver.SetSuppressed` → forgotten writes → re-seed
on release) covers native playback exactly as it covers LC playback. There is no second ownership
mechanism and no bypass.

Verification (all 50 clips, jiggle gate, zero-write, regression gates, captures):
`verify/native-emotes-merged.md`, logs `verify/native_sweep_final.log`, `verify/lc_sweep_final.log`,
captures `shots/native-emotes/`. Deploy: `tools/deploy-hot.sh` (build + atomic mv to A and B's
`hot/`; hot reload only).

## Proportions pass 5 merged in (from `tracks/bones/`)

Shipped constants: chest leaf **1.55**, chest cage **1.22581** (total 1.90), hips **1.50**.

| file | change |
|---|---|
| `Proportions.cs` | replaced with the bones version: cage scaling (`ScaleCage`, `IsChestChain`, the `cage` / `cage-comp` / `cage-trunk` roles), `LateTick`, `ReapplyAll`, `SetScales`/`SetChestFix`/`SetClavicleGrow`, the `ChestFixes` list per rig, `breastscale`/`hipscale`/`chestscale`/`clavgrow`/`chestfix` request keys |
| `ChestWeights.cs` | new: runtime garment-weight rewrite (skin-following weights in a chest band measured along the skeleton's own axes, 0–12 mm rim ramp off the welded boundary loops, weights normalised to sum 1 / ≤4 influences, mesh cloned and the clone destroyed on restore) |
| `ProportionShots.cs` | new: the capture/probe rig (`spawn`, `freeze`, `solo`, `partsall`, `hideparts`, `measure`, `weights`, `chestdump`, `chestfix`, `breastscale`/`chestscale`, …). Inert unless `BepInEx/screens/propcap.request` exists |
| `ModelSwapRuntime.cs` | `ProportionShots.Tick(_h, _proportions)` right after `_proportions.Tick(_t)`; `_proportions?.LateTick()` in `LateUpdate` |

**The one collision that had to be reconciled**: `LateTick` re-asserts the pinned cage-child local
*positions* and must run after the last skeleton writer. In the bones tree it ran first (before
`JiggleSolver.LateTick`) because nothing else wrote the skeleton there. In the merged tree
`PosePlayer.PostAnimationUpdate` writes the whole skeleton while a clip is active (the native path
drives 51 rotations and 5 positions, `spine_05`/`clavicle_l/r`/`neck_01` among them), so a
first-position call would be undone by the emote write in the same frame. It now runs between
`_emote?.PostAnimationUpdate(_emoteDt)` and `_mirrorRt.Tick()` — the last write before the mirror
draws. The live rig shows it doing real work: `[bounds:dump] … spine_05 … posDelta=0.0300
latePins=632`, same for `clavicle_l/r` and `neck_01`.

`Dispose` needed no change (proportions already restore before the mirror dies); `Proportions.Restore`
now also restores the garment meshes, so a reload logs `[chest:restore] … clone destroyed` for
every rig it reweighted. Contracts untouched.

Verification (`verify/merged-proportions.md`, sweeps `verify/merged_native_sweep.log` /
`verify/merged_lc_sweep.log`, captures `shots/proportions-merged/`): dropped-strap hole 0 px at
1.90 (53 px with the authored weights), Freya a confirmed no-op, shoulder/height invariance exact
against the same rig at 1.0, hips 0.2990 m, pivot→leaf 0.0603 m, jiggle gate suspend/re-seed with
`rigs=1` (live leaves, the old male-body limitation is gone), 38+12 clips all
start/stop/`released=True`, clones torn down on three reloads, mirror 8/8, tp-cam 7 candidates,
fp mask `0x4DDB8F87`, prop `VISIBLE` + `[reel] settled … match=True`, cosmetics `MegaBook Page
Back`, `grep -c SWAP` = 0. Known gap: Sarah's garment is skipped on this prefab (no `*body*`-named
skin renderer), so only Diana/Kiona are reweighted.
