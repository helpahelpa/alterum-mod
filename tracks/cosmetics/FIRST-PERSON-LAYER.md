# First-person local-cosmetic layer — merge note

Feature: hide the local player's cosmetics from the screen camera in first person while the mirror
camera keeps rendering the same objects; third person and other players' hats are unchanged.

## Patch

`first-person-local-layer.patch` applies to the current `tracks/merged/` checkout:

```bash
cd alterum/tracks
patch -p0 --forward < cosmetics/first-person-local-layer.patch
```

Dry-run checked against `merged/mod/...`; the patched `CosmeticsRuntime.cs` + `Mirror.cs` were
copied into a throwaway build directory and compiled (0 errors). `Mirror.cs`'s existing
`SyncCosmeticLayer`/`CosmeticsInUse` layer-23 gate and the request-file path are untouched.

## Files and functions

### `AlterumModelSwap.Hot/Mirror.cs`

* `MirrorFactory.LayerRenderability(IHostContext log)` — new internal accessor over the existing
  cached 32-layer `RenderableLayers` probe. The cosmetics runtime must choose a layer this URP
  pipeline actually renders; sharing the probe keeps both subsystems from disagreeing and keeps
  the 32-render probe to once per generation.

### `AlterumModelSwap.Hot/CosmeticsRuntime.cs`

* Constants/fields: `FallbackLocalCosmeticLayer = 6`, `PreferredLocalCosmeticLayer = 6`,
  `MirrorBodyLayer = 14`, `CameraScanSeconds`; `_localLayer`, `_layerScene`, `_screenMasks`,
  `_hideAll`, `_screenLayerApplied`, `_nextCameraScan`. `CameraMaskState` stores a screen camera's
  pristine mask.
* `EnsureLocalCosmeticLayer()` — selects the local layer once per scene: shared renderable map,
  live-renderer census (mod cosmetics excluded by marker/ancestor), screen-mask union, exclusions
  (`0,2,5,14,15,23,24,27`). Prefers layer 6 if renderable, unused, outside every screen camera;
  otherwise the documented priority order. Re-probes on scene change and restores the old bit.
* `ApplyScreenCameraMasks()` / `TrackScreenCamera()` / `RestoreScreenCameraMasks()` — write only
  the local layer bit (`1 << _localLayer`) on every `MirrorFactory.ScreenCamera`; present in
  third person, absent in first person and on native body hides. Pristine masks are restored on
  dispose/re-probe. Layer 23 is never touched.
* `EnsureMirrorLocalLayer()` — the mod-owned mirror camera always carries the local bit.
* `ApplyScreenState()` — replaces the old `SetInstanceVisible(instance, screen)` POV toggle with
  `hideAll = _foreignHide && _pov != FirstPerson` (native death/cutscene/equipment hides still
  hide both cameras) plus the camera-mask policy.
* `BeginMirrorView()` — now a mask assertion, returns `null`; the mirror no longer needs a
  renderer-enable/restore scope. `SetMirrorCamera()` ensures the bit and logs.
* Removed: `ApplyMirrorState`, `CaptureRendererState`, `RestoreRendererState`, `EndMirrorScope`,
  `MirrorScope`, and the scope/pool fields. `MirrorRuntime.Tick`'s `using (BeginMirrorView(...))`
  is valid with a null scope.
* `CreateInstance()` / `BuildNode()` — create the local cosmetic root/nodes on the local layer;
  new instances start enabled unless a native body hide is active.
* `Dispose()` — restores the tracked screen-camera masks.
* `LogCameraMasks()` — logs the local layer, its bit, the screen/mirror membership, and layer 23.

The proof/capture additions (`RunProof` matrix, `DumpProofMatrix`, `DumpProofCase`,
`DumpMirrorWithPlayer`, `DumpMainCamera`, `TryHatScreenBounds`, `MeasureDiff`) are **not** in the
patch. They are verification-only and live in `cosmetics/mod/AlterumModelSwap.Hot/CosmeticsRuntime.cs`;
the merged build keeps its own proof path. If wanted, they can be copied over the existing
`RunProof`/`DumpProof` region.

## Integration notes

* The merged `Mirror.cs` gates layer 23 with `SyncCosmeticLayer`; the local layer is handled by
  `CosmeticsRuntime` and is independent. `_baseMask` captured before the local bit is added, so
  `SyncCosmeticLayer` leaves the extra bit alone.
* Other players' cosmetics remain on layer 23. The screen mask keeps `1<<23` in both POVs
  (`layer23InMask=True` in every proof case); no path removes it.
* `MirrorPlayerVisibility` (layer 14 body binding) is unchanged. The marker/ancestor check keeps
  the local hats out of its sweep.
* `builtin.partyhat`, the `.apack` loader, and the `MegaBook Page Back` material clone path are
  untouched.
