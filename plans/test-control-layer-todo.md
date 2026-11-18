# Alterum deterministic test-control layer TODO

Last updated: 2026-11-18

## Final scoped delivery (15-minute closeout)

Camera/capture implementation is complete and deployed Hot-only. No further source iteration is planned.

- Final deployed SHA-256: `b764e5673db88009f05e344a0992891a62635c244d4a43a5204ea4117a960635` (all three approved slots match).
- Final build: 0 errors, 16 warnings (known/pre-existing).
- Cache probe: all 8 cases PASS. Model policy probe: `VERDICT OK`.
- Live QC registration: B accepted 26 rows; native processor command invocation tested.
- Found and fixed live IL2CPP `TargetException` on delegate invocation. QC still parses/validates commands; a row-identity-scoped `CommandData.Invoke` bridge invokes only this generation's registered handlers, with the access check intact.
- Live general capture via native QC invocation: PNG written, SHA-256 read back, atomic evidence sidecar written, image decoded at 640x360 with magenta=0.
- Capture evidence sidecars include the retained snapshot (PID/DLL/generation/scene/identities/assets/camera) and remote renderer/material census when available. These are synchronous post-render observations, not physical UI acceptance claims.
- Locked A rejected capture invocation; read-only snapshot worked in Lobby.
- Original PIDs/path/start times verified; no process was started or killed.
- B was already disconnected at Menu on arrival; remote ledger empty on both. Thus remote-body/live wardrobe acceptance was NOT performed and must not be reported as PASS.
- Physical AEZAKMI unlock on A was attempted but not confirmed. B's unlock was restored from its pre-existing process state, not freshly typed in this pass.
- Cancelled scenario code had already landed before cancellation. Its request entry, native registration/dispatch and tick were removed; private unreachable scaffolding remains, with no scenario command exposed.
- Evidence and build/deploy logs: `.temp/alterum-control-final/`.
- Deployment backups/manifests: `C:\AlterumTools\fix\deploy\control-final`.

The uncompleted UI/HUD/asset historical-proof items below are recorded limitations outside the final camera/capture closeout, not claimed completed.


This is the working checklist for the `alterum.test.*` control layer. Keep it current after every delegated implementation task and every live verification pass.

## Scope and invariants

- Keep the existing AEZAKMI/QcGate policy.
- Read-only diagnostics may run while locked.
- Setup, model selection, teleport, camera, and proof operations require unlock.
- Authoritative multiplayer mutations are host-only and must use existing game-owned paths.
- `select-model ... ui` must use the real wardrobe/EventSystem path.
- Protocol setup must have a distinct command name/variant from UI selection.
- Do not use walking or strafing for acceptance evidence.
- Use only the original approved live pair when deploying or verifying:
  - A: `C:\AlterumPinned\Alterum.exe`, PID 51976
  - B: `C:\AlterumB\Alterum.exe`, PID 35564, `-alterumInstance=b`
- Do not touch Steam, `C:\AlterumPinned-pristine`, `C:\Interop\*`, or `C:\AlterumTests\*`.
- Build Hot only, with shared compilation and server reuse disabled.
- Do not deploy until source review and build checks pass.

## Completed source slices

### Existing diagnostic foundation

- [x] Existing `qc.request` transport retained.
- [x] Existing Quantum Console reflection-only architecture retained.
- [x] Native QC runtime registration implemented through `LambdaCommandData` and `QuantumConsoleProcessor.TryAddCommand`.
- [x] Native rows are installed only after AEZAKMI unlock and removed during teardown/hot reload.
- [x] Native-row delegate conversion is failure-soft per command/arity.
- [x] Read-only commands remain available while locked.
- [x] `test.pose` reports effective body-first pose plus root pose and body-module state.
- [x] `test.identity [clientId]` reports canonical identity without fabricating unknown remotes.
- [x] `test.remote-list` reports accepted actors separately from actually built bodies.

### Wardrobe and model control

- [x] `test.wardrobe-list` reports database rows, labels, slugs, and native/custom classification.
- [x] `test.open-wardrobe` uses `WardrobeInputHandler.TryOpenWardrobe()`.
- [x] `alterum.test.select-model <slug> ui` uses the existing wardrobe tab/button/EventSystem machinery.
- [x] `alterum.test.select-model <slug> protocol` uses the existing authoritative helper, is host-only, and has distinct protocol provenance.
- [x] Selection and wardrobe opening are gated mutations.
- [x] `alterum.test.await-model <actor> <slug>` performs frame-based observation with timeout and last-state reporting.
- [x] `alterum.test.assert-model <actor> <slug>` emits structured identity-field PASS/FAIL output.

### Asset and deterministic setup diagnostics

- [x] `alterum.test.assets` reports cache entries, leases, liveness, bodies, and retirement state.
- [x] `alterum.test.assert-assets` checks observable cache/body invariants without mutating state.
- [x] `alterum.test.points` reports grounded named points and authority/player prerequisites.
- [x] `alterum.test.point <name>` is host-only and uses `PlayerTeleportator.TeleportOnServer`.
- [x] Grounded points currently include only `lobby-spawn` and `lobby-mirror`.
- [x] Unstable or ungrounded points such as `wardrobe-room` and `remote-body` were deliberately omitted.

### Proof capture

- [x] `alterum.test.capture-remote <clientId> <tag>` validates the actor/body/tag and reuses `RemoteModels` deterministic capture logic.
- [x] Remote captures report the written artifact and SHA-256.
- [x] Existing camera/RenderTexture restoration and failure cleanup are preserved.

### Build status

- [x] Each completed slice has built successfully with zero errors.
- [x] The known `UnityEngine.SceneManagementModule` reference warning remains unresolved.
- [x] Existing warning set is otherwise pre-existing; do not treat it as a control-layer regression.
- [x] No control-layer slice has been deployed or live-verified yet.

## In progress

- [x] `alterum.test.assert-image <path> <maxMagentaPixels>`
  - Restrict reads to an existing PNG under `ScreensDir`.
  - Reject path traversal, malformed arguments, missing files, malformed PNGs, and invalid thresholds.
  - Use the existing PNG decoder.
  - Report dimensions, SHA-256, total pixels, magenta count, and PASS/FAIL.
  - Remain read-only and available while locked.
  - Register through native QC with the correct arity.

Current delegated task: none. Final camera/capture review completed and fixes deployed.

## Remaining source work

### Required parity and lifecycle commands

- [x] Add `alterum.test.close-wardrobe` through the native wardrobe close lifecycle, not by fabricating UI state.
- [x] Decide whether to retain compatibility aliases (`test.pose`, etc.) or rename/add canonical `alterum.test.*` forms for:
  - [x] pose
  - [x] identity
  - [x] remote-list
  - [x] wardrobe-list
  - [x] open-wardrobe
  - [x] Compatibility aliases are retained and route to the same handlers; canonical commands are documented.

### Camera and capture orchestration

- [x] Add `alterum.test.camera-remote` using the existing deterministic camera path.
  - Reports the shared remote-shot framing without permanently moving or rendering the game camera.
  - Uses `TryResolveRemoteShotView`; actual capture uses the same placement.
- [x] Add a general `alterum.test.capture` using the existing proof/camera code without creating a second renderer.
  - Gated proof write; uses the current game camera without changing its pose.
  - Validates safe tags, reports artifact path/bytes/SHA-256, and restores target texture/render state/resources on all paths.
- [x] Every successful PNG capture emits an atomic evidence sidecar containing snapshot metadata, image hash/magenta count, camera information and available renderer/material census. Unavailable facts remain explicit; post-render observation is labelled.

### State and scenario control — de-scoped

- [x] `alterum.test.snapshot` already exists and is retained as a useful read-only artifact for capture evidence.
- [—] `alterum.test.scenario` is intentionally de-scoped by the user; do not implement.
- [—] A generalized command-id/process-generation state machine is intentionally de-scoped; keep existing per-command IDs/timeouts where already implemented.
- [x] Keep setup, real-user simulation, observation, and proof operations visibly separated in the commands already shipped.

### Named points — de-scoped expansion

- [x] `lobby-spawn` grounded and implemented; retain as-is.
- [x] `lobby-mirror` grounded and implemented; retain as-is.
- [—] Do not spend further work grounding or implementing `wardrobe-room`.
- [—] Do not spend further work grounding or implementing `remote-body`.
- [x] Do not add coordinates merely because they look useful in one capture.

## Required correctness review before accepting source completion

Implementation reports and successful builds are not acceptance evidence. These issues need explicit review/fixes before deployment:

- [ ] Replace the current `select-model ... ui` fallback sequence (`ClickedSafe` / `ClickedSafeReferenced` / `ClickedUnsafe` invocation) with actual native input/EventSystem processing. Direct event/handler invocation is NOT the required UI acceptance path, even if the model changes. Do not mark this capability accepted yet.
- [ ] Verify `assert-model` checks actual HUD and wardrobe surfaces, rather than merely comparing canonical-derived expected rows to themselves. Report unobserved surfaces as unverified, not PASS.
- [x] Harden `assert-image` path containment against parent-directory junctions/symlinks, not only lexical `..` escapes and a reparse point on the PNG itself. Source review: every directory component between the screens dir and the PNG is checked for a reparse point (`link-in-path`); the PNG's own attributes are still checked. Live rejection case still to verify.
- [x] Review capture camera restoration on every exception path, including exceptions during `Camera.Render()`. Source review: the remote-shot path restores camera pose, `targetTexture` and `RenderTexture.active` in `finally` blocks and writes the PNG through a temp sibling + rename; the general capture path restores `RenderTexture.active` only when it was saved, and also writes via temp + rename. Live exception-path capture still to verify.
- [ ] Scope asset assertions accurately: aggregate lease counts and current-generation snapshots do not prove per-body ownership or previous-generation eviction history.

## Required build and static checks

Run after every source slice:

```bash
MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
~/.dotnet/dotnet build \
tracks/merged/mod/AlterumModelSwap.Hot/AlterumModelSwap.Hot.csproj \
-c Release -v q --nologo \
-p:UseSharedCompilation=false -nodeReuse:false
```

Also run the existing relevant checks before deployment:

- [x] ModelAssetCache real-path probe: all eight cases pass.
- [x] ModelPolicyProbe reports `VERDICT OK`.
- [ ] Review only intended control-layer files in the diff; preserve pre-existing dirty work.
- [x] Confirm output is `bin/Release/AlterumModelSwap.Hot.dll`.
- [x] Record final SHA-256 before deployment.

## Live deployment and acceptance

Do not do this until the source checklist and build checks are green.

- [x] Deploy only the Hot DLL atomically with per-slot backups using the approved deployment script.
- [x] Confirm the same final SHA-256 in all three approved Hot slots.
- [ ] Preserve the original live pair and verify PID/path/session identity before and after deployment.
- [ ] Unlock the real Quantum Console with AEZAKMI through the real session-1 input path.
- [ ] Confirm native QC rows appear in the live command table after unlock.
- [ ] Confirm `status` reports the native test-row count.
- [ ] Confirm read-only diagnostics work while locked.
- [ ] Confirm locked mutation/proof commands are refused.
- [ ] Confirm host/client behavior: host may use `point` and protocol selection; client receives explicit refusal.
- [ ] Confirm `alterum.test.points` and `alterum.test.pose` before moving anything.
- [ ] Use `alterum.test.point lobby-mirror` or another grounded point; never walk/strafe.
- [ ] Run native wardrobe open through the real lifecycle.
- [ ] Select a custom model through `alterum.test.select-model <slug> ui`.
- [ ] Await and assert the model identity.
- [ ] Capture the remote body and run `alterum.test.assert-image`.
- [ ] Select another custom/native model through the UI path.
- [ ] Await and assert again.
- [ ] Run `alterum.test.assets` and `alterum.test.assert-assets`.
- [ ] Confirm zero magenta pixels and healthy remote materials/renderers.
- [ ] Verify A's own model remains unchanged on B's screen.
- [ ] Restore the approved final player model and lobby point.
- [ ] Capture final proof artifacts and record hashes/manifests.
- [ ] Close any temporary test instances; leave only the original pair.

## Failure-case matrix

- [ ] Locked `point` rejected without movement.
- [ ] Locked wardrobe open/selection/capture rejected as designed.
- [ ] Client `point` rejected without movement.
- [ ] Unknown point rejected without movement.
- [ ] Unknown model/slug rejected without mutation.
- [ ] Closed or unattached wardrobe rejected by UI selection.
- [ ] Unknown client rejected by identity/capture commands.
- [ ] Pending/native/no-body remote rejected by remote capture.
- [ ] Invalid capture tag rejected without file creation.
- [ ] Path traversal rejected by image assertion.
- [ ] Parent-directory junction/symlink rejected by image assertion (`link-in-path`).
- [ ] Missing/malformed PNG rejected by image assertion.
- [ ] Asset pressure and retired-cache cases remain PASS.
- [ ] Duplicate await registration is idempotent.
- [ ] Hot reload removes native rows and does not leave stale delegates.

## Separate follow-up work, not part of this control-layer slice

- [ ] Mandatory instance-label guard deployment/routing.
- [ ] Portable release packaging.
- [ ] Shared Windows-profile isolation.
- [ ] Composite portrait-layer projection completion.
- [ ] Fresh yt-dlp extraction verification.
- [ ] QC `[gate] Activate/0` audit/error investigation.
- [ ] Remaining unrelated audit/script findings.
- [ ] Remaining reel/music lifecycle coverage outside this test-control layer.

## Definition of done

The control layer is done when:

1. Every required command is implemented through both `qc.request` and the real native Quantum Console path.
2. Mutating/proof commands obey AEZAKMI and host restrictions.
3. UI selection has been physically verified through the native wardrobe/EventSystem path.
4. Deterministic model, asset, capture, and image assertions pass on the original live pair.
5. Camera/proof artifacts produce screenshots, hashes, and the retained snapshot manifest.
6. The original pair is restored to the approved final state and no temporary instances remain.
