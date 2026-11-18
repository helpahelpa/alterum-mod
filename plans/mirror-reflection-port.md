# MirrorDecor static-camera port — implementation plan

## Scope and invariant
- Implement only in `mod/AlterumModelSwap.Hot`; no injected types, host rebuild, Contracts change, game restart, launch, or termination.
- Copy MirrorDecor's fixed child-Camera → RenderTexture → glass model, NOT planar-reflection mathematics. Keep the fixed Lobby pose `(-0.328,1.153,-12.273)`, yaw `41.06`, configured dimensions, frame, LobbyOnly, and zero colliders.
- Camera local position/rotation, projection settings, and mask are assigned once per mirror creation. Never track the viewer; explicit removal/recreation is the only way to choose another viewpoint.

## 1. Replace the reflection implementation (`Mirror.cs`)
- Delete Tick's main-camera lookup (including `Camera.main`), plane/viewer reflection calculations, `Vector3.Reflect`, `SetPositionAndRotation`, FOV/far-plane copying, per-frame mask copying, projection reset, oblique projection block, and `CameraSpacePlane`.
- Delete `_obliqueOk`, runtime `mainCam`, `extraLayer`, obsolete body-force counters/state, `ForceBodyRenderers`, `EnableBodyNow`, `TickPreCull`, and factory `_pending*` globals. Replace the misleading planar-reflection/MirrorDecor comment.
- Keep `cam.enabled=false`; keep `ModelSwapRuntime.LateUpdate → MirrorRuntime.Tick`. Tick does ONLY validity checking, hiding this mirror's glass/frame renderers, one `cam.Render()`, and restoring their exact previous enabled states in `finally` (plus bounded diagnostics).
- Render EVERY frame while the Lobby mirror exists. A fixed camera still needs fresh frames for moving/animating players, doors, lighting, and wardrobe changes; rendering once at creation would produce a frozen photograph.
- Do not temporarily force body visibility around Render: establish persistent, scoped visibility via the patches below before rendering. Never render recursively or register extra camera callbacks.
- Retain explicit Render initially because this installation already renders RTs through it; if URP rejects it, report a rendering failure rather than silently reintroducing tracking math. No pipeline fallback in this port.

## 2. Procedurally instantiate the fixed viewpoint
- Parent `AlterumMirrorCam` under `AlterumMirror` with `SetParent(root.transform,false)`; set `localPosition=(0,0,-0.10)` metres, `localRotation=Quaternion.identity`, `localScale=Vector3.one` exactly once.
- Here glass is local z=0 and root +Z faces into the room. The camera is 10 cm behind the glass looking along +Z, NOT looking back into the wall. This offset is an explicit Alterum choice, not a recovered MirrorDecor prefab transform.
- Set perspective, vertical FOV=65, near=0.01, far=1000, depth=0, Skybox clear, HDR=false, allowMSAA=false, targetTexture=rt; retain useOcclusionCulling=false initially to avoid wall/occlusion rejection of the body.
- Set aspect=`width/height` once even with a square RT, so stretching the texture onto the tall glass preserves proportions. No inherited oblique/custom projection or main-camera matrices.
- Keep root pose immutable during ordinary updates. Disable the `@MirrorToMe`/overlay relocation path for this fixed-Lobby mirror with an explanatory log; re-creation via `@Mirror` remains allowed.
- No AudioListener or camera stacking; keep this RT camera out of `FindGameCamera` (its existing targetTexture filter already does that).

## 3. Fixed mask, adapted to Alterum rather than LC's numeric constant
- Recommend deriving the mask ONCE from the explicitly selected `FindGameCamera()` result, never LC's `565909343` and never `Camera.main`.
- Offline `globalgamemanagers` layer strings confirm: 3 Ignore Render, 5 UI, 15 Handheld, 20 Player, 22 WorldSpaceUI, 23 TV_PlayerModelMesh, 24 FV_PlayerModelMesh, 30 Footstep, 31 HintLookable. All 32 slots are named; an excluded layer is not automatically unused.
- Both current logs show game mask `0x4DDB8F87`, including BOTH 23 and 24. Thus merely enabling the body on 23 exposes it to the owner's first-person camera; blindly copying LC's layer-23/layer-5 recipe is unsafe here.
- Select one mirror-only body layer at creation: try 31 first, then descending 8–30, accepting only a bit absent from every active screen-output gameplay camera and with no other active scene Renderer using it. Exclude UI=5, FV=24 and TV=23 explicitly; never relayer whole player roots/colliders/bones.
- Log the chosen slot AND its actual LayerToName value. If no safe slot exists, leave player state unchanged and log `[mirror:body-layer-unavailable]`; room-only rendering is a diagnosed failure, not completion.
- Freeze `mask=(selectedGameMask | bodyBit) & ~(1<<24) & ~(1<<5) & ~(1<<15)`; without ShowLocalPlayer omit bodyBit. Hide the mirror's own renderers during the pass instead of consuming another layer.
- The derived world mask and reused layer remain game-version brittle: log all 32 names/mask once, revalidate on mirror creation, and tear down/recreate if a replacement screen camera includes bodyBit. Do not update the mirror mask per frame.

## 4. RenderTexture and glass
- MirrorDecor reference: square 2000×2000 ARGB32, depth-backed, 8× MSAA, no mips; camera allowMSAA=false. Treat its RT AA and camera flag separately, not as proof that this game's URP will resolve 8×.
- Ship hot constants `Resolution=1536`, `Samples=1`; construct explicit ARGB32 + 24-bit depth, square dimensions, antiAliasing=1, useMipMap=false, autoGenerateMips=false, bilinear filtering, Clamp wrapping. Check Create/IsCreated and log actual settings.
- Current RT is 1024×2048 at 0.9×1.9: 2.10M pixels, roughly 16 MiB color+32-bit depth. 1536² is 2.36M/~18 MiB (12.5% more pixels); 2000² is 4M/~30.5 MiB at 1× (91% more pixels).
- A 2000² 8× target is roughly 244 MiB color+depth plus ~15 MiB resolved color, backend-dependent, with substantially more sample bandwidth; two game instances multiply the cost. Do not ship 8× just for asset parity.
- Keep `Universal Render Pipeline/Unlit`, white tint, opaque surface, existing ordinary 0..1 quad UVs; bind rt to `_BaseMap` and `_MainTex` when present, scale=(1,1), offset=(0,0). No U/V flip, negative scale, HDRP material, or camera-dependent UV/projection shader.
- Fail visibly/log if no compatible texture shader is available; do not quietly settle on an untextured Lit material. Preserve frame material and no-shadow settings.

## 5. Player visibility is a persistent hot-owned module
- Add `MirrorPlayerVisibility.cs` with a small managed interface: Attach(localPlayer,bodyLayer), Rebind(), Dispose(); keep renderer/controller/LOD snapshots and Harmony ownership private. Use non-generic engine lookups and TryCast; SkinnedMeshRenderer is a Renderer, not a MonoBehaviour.
- Resolve the owner via CurrentPlayerProvider and ancestry; if unavailable, inspect ancestor NetworkObject.IsOwner using non-generic lookup. Never assume name `Owner: 0`: B's current log correctly resolves `Owner: 1`.
- Find only that owner's ThirdViewPlayerModel and its concrete renderers; record originals before changes. Preserve inactive wardrobe alternatives and noncurrent LODs; do not SetActive every renderer indiscriminately.
- Move only current body renderer GameObjects to bodyLayer; keep first-view hands on their native layers (24/15), excluded by the mirror mask. Disable applicable body LODGroups with saved state, select their LOD0 renderer set, suppress lower-LOD duplicates; set body skinned updateWhenOffscreen=true and clear forceRenderingOff for the selected body renderers.
- Fix the native hiding SOURCE, not another late enable loop: Harmony-prefix `Gameplay.PlayerSystem.RenderingController.DisableRendering(Il2CppSystem.Object key)` and skip ONLY requests to this owner's cached third-person renderer controller(s) whose key is the identified local first-person-hide proxy. All other controllers/keys must run normally.
- Identify the key by comparing native pointers against the owner's ProxyRenderingController instances whose `_renderingController` targets those cached body controllers; inspect `_disableKeys` during Attach. Do not suppress visibility for remote players, menus, wardrobe previews, death/despawn or unrelated hide keys.
- Remove an already-applied matching hide key once via `controller.EnableRendering(proxyKey)` so the controller's internal state and renderer state agree; record suppressed keys for restoration. Then enable the selected renderers once after layer isolation, not before Camera.Render.
- Add a postfix to `PlayerModelSetuper.SetupModel(PlayerModelData)` that marks this owner's binding dirty; rebind on the next Update after all native callbacks finish. Initial Attach must also discover already-spawned bodies so hot reload does not depend on another spawn.
- These interop method/field names are confirmed against the game's interop assembly; wrappers do NOT establish which concrete proxy/key hides the third-person body. Log the matched native identities and key type; if no scoped match exists, emit `[mirror:body-proxy-unmatched]` and treat body visibility as unfinished, never broaden the patch globally.
- Use a unique Harmony ID per hot generation (GUID suffix), explicit patch targets, no injected MonoBehaviour. Defer installation/Attach to the new runtime's first Update: HotLoader.Initialize runs BEFORE old.Dispose, so constructor-time mutation would race old-state restoration.
- Dispose order: disarm patch handlers; stop/destroy mirror; unpatch ONLY this generation's Harmony ID in finally; restore surviving body renderer layers/flags, LOD state and suppressed native hide keys; clear static references/caches. Dispose must also undo partially completed installation.
- Make `ModelSwapRuntime.Dispose` perform visibility teardown as well as DestroyMirror; mirror removal/scene changes restore body state too. Never call global UnpatchAll without an owner ID; no delegates/events may retain a retired hot assembly.

## 6. Configuration and lifetime
- No new host binds or ConfigSnapshot fields: keep resolution, samples, FOV, offset, near/far and mask policy as hot constants. Existing Width/Height/Frame/LobbyOnly/ShowLocalPlayer flow from host binds through ConfigSnapshot unchanged.
- Enable existing `[Mirror] ShowLocalPlayer=true` in each intended instance's cfg for acceptance (currently false). Host config watching already carries that bool; hot code must react to changes by teardown/recreation, including restoring body state when false.
- Leave the host's stale “does not work” bind description/default correction for a separately authorized host restart; do not rebuild host or Contracts to fix wording. Scene change, re-place, config reapply, and hot Dispose must share the same idempotent cleanup path.
- On every creation failure release RT, meshes, materials, root/camera and any body attachment; disable camera/detach target before Release/Destroy. Unity Destroy is deferred, so old mirror renderers must be disabled immediately.

## 7. Diagnostics and acceptance risks
- Read each install's `BepInEx/LogOutput.log`, preserving existing logs. Add `[mirror:static-created]` with world/local pose, mask, FOV/aspect, RT settings, colliders=0; add one `[mirror:render-ok]` after the first successful render.
- Black/frozen glass: log `[mirror:render-failed]` with full exception, shader/RT state and failure count (first occurrence then rate-limited); URP unsupported Render messages may also appear from Unity. Stop repeated failing renders; never label a failed pass successful.
- Missing body: `[mirror:body-bound]` must report owner identity, selected renderer count (known baseline nine, not a hard requirement), proxy/controller/key identities, active/enabled/forceOff, bounds, mesh/material and layer. Distinguish no safe layer, unmatched proxy, inactive model and out-of-frustum bounds.
- Wall/sky-only view is possible with a behind-wall lens and no oblique clipping; emit `[mirror:camera-placement]` with fixed pose and a one-time wall-intersection diagnostic. Calibrate only the creation-time offset constant, never follow the viewer or restore oblique math.
- Duplicate bodies/first-person clipping: log failed layer isolation or LOD selection; restore and disable the visibility attachment. A wrong owner on B must be reported, not silently mapped to owner 0.
- Harmony may reject detours referencing a collectible assembly: catch/log `[mirror:patch-failed]` with the full exception and roll back. This is a hard visibility blocker; do not claim parity or silently move injected types into hot/modify host.
- Log `[mirror:disposed]` with patch removal/restoration counts. Repeated generation errors, callbacks from an old GUID, or duplicate mirrors indicate incomplete cleanup; log `[mirror:restore-failed]` per failed cleanup step, continuing the rest.

## 8. Ordered execution checklist (for the implementing agent)
1. Preserve both processes; inspect the named hot files and recon wrappers; add the bounded diagnostics and visibility module with generation-specific cleanup first.
2. Implement scoped proxy-key suppression, initial owner/body discovery, model-change rebind, layer/LOD snapshots and restoration; wire the existing ShowLocalPlayer bool and config-change teardown.
3. Replace factory camera/RT/material setup with the constants above; remove reflection math and obsolete body hacks; reduce Tick to the guarded static-camera render pass.
4. Disable live mirror relocation; retain fixed Lobby placement, resource teardown and scene filtering. Confirm by inspection there is no post-creation camera-transform/projection/mask assignment.
5. Only when implementation is authorized: run `./iterate.sh hot` (never other iterate modes); copy the same hot DLL to B's watched `C:\AlterumB\BepInEx\hot\b\AlterumModelSwap.Hot.dll` if testing B. Do not deploy host/Contracts or run launch/stop scripts.
6. Validate on both existing instances: turn/move the player, animate/change wardrobe, approach/leave mirror. Viewpoint stays fixed while RT/body animation changes; owner's full body appears only in mirror, hands do not, remote player remains unaffected.
7. Toggle ShowLocalPlayer, remove/recreate mirror, and hot-reload twice; verify layers/LOD/hide keys restore and only the current generation has patches. Check scene reentry when naturally available, without forcing a restart.
8. Require `[mirror:static-created]`, `[mirror:render-ok]`, `[mirror:body-bound]` and clean disposal on A/B before declaring success; capture an optional one-shot RT dump with RenderTexture.active restored and temporary Texture2D destroyed in finally. Missing body is not an acceptable room-only completion.
