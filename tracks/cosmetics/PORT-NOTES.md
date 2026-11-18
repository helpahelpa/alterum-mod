# Cosmetics port — implementation notes (track `cosmetics`)

Status: **21/21 MoreCompany built-in cosmetics extracted offline and rebuilt at runtime against
URP; local-only; hat verified attached to the head bone and rendering in both camera masks.**
Evidence below is from this track's install (`C:\AlterumT3`, instance `t3`), generations 5–7.

## 1. Offline extraction (`tools/extract_cosmetics.py`, UnityPy 1.25.4)

Input: the two UnityFS bundles carved out of `MoreCompany.dll` 1.14.0
(`unpack_bundle_3110260` = `morecompany.cosmetics`, `unpack_bundle_70132` = `morecompany.assets`).

| thing | count | notes |
|---|---:|---|
| cosmetics (`CosmeticInstance`) | 21 | 18 HAT, 1 CHEST (`outoforder`), 1 HIP (`gunholster`), 1 R_LOWER_ARM (`watch`) |
| unique meshes | 33 | 93,319 verts / 481,710 indices; vertex data lives in the bundle's `.resS` stream, decoded through `MeshHandler` (UnityPy's plain `read()` leaves `m_Vertices` empty) |
| materials | 48 | 28 reachable from cosmetic renderers; HDRP/Lit |
| textures | 23 | decoded from DXT1-crunched / DXT5-crunched / BC7 / DXT1 to RGBA8, re-encoded as PNG (14.0 MB) |
| prefab transforms | 150 | full descendant TRS chain kept, incl. non-uniform scales |

Output: `mod/AlterumModelSwap.Hot/cosmetics.apack` — 20.6 MB, magic `ACPK1`, u32 manifest length,
JSON manifest, then 4-byte-aligned blobs (vertices/normals/tangents/uv0/u32 indices, PNG textures).
Embedded into the hot assembly, so `track.sh cosmetics build && deploy` stays a single atomic step.

### How the placement data was recovered
* Every cosmetic prefab's descendant TRS chain is exported verbatim (`pos/rot/scale` per node,
  e.g. party hat `Party_Hat` y=1.3932, z=-0.064, scale 0.43, `Cone`/`Sphere` children with their own
  scales). Nothing is collapsed into one guessed offset, and the prefab root's authoring-scene TRS
  is recorded but never applied (MoreCompany overwrites it at runtime too).
* **Source anatomy**: `morecompany.assets` ships Lethal Company's real rig (`testoverlay.prefab` →
  `ScavengerModel/metarig/spine.004`) with the LOD1 `SkinnedMeshRenderer`. Head-weighted vertices
  (weight ≥ 0.5 on `spine.004`) transformed by that bone's bindpose give the source head box in the
  head bone's local space: width 0.40216, top 0.370123, centre (x −0.0021, z 0.0135). The bone's
  rotation relative to the model root is ≈identity, so that frame is "model-aligned" (Y up).
* **Authoring unit scale** (`authoringUnitScale` = 2.0626): MoreCompany's prefabs were authored on a
  scaled clone of that rig — their internal proportions are only consistent if every length is
  ~2.06× the LC model's units (median of `lowest geometry point / head top` over the on-top hats;
  the party hat alone gives 0.796/0.370 = 2.15). The manifest therefore stores the source head box
  **in authoring units** (width 0.8351, top 0.7657). MoreCompany's runtime `0.38` multiplier is not
  used anywhere.

## 2. Runtime rebuild (`CosmeticPack.cs`, `PngDecoder.cs`, `CosmeticsRuntime.cs`)

* `Mesh`: `new Mesh()`, `indexFormat = UInt32` only when needed, `vertices/normals/tangents/uv`
  from `Il2CppStructArray`, `subMeshCount` + `SetTriangles(..., submesh, false)`, `RecalculateBounds()`.
  Meshes are cached per source mesh id and shared between cosmetics (eyeballs reuse one mesh 8×).
* `Texture2D`: PNG is decoded in-process (`PngDecoder`, zlib/DeflateStream + PNG unfilter, rows
  flipped to Unity's bottom-up raw order) and uploaded with `LoadRawTextureData` + `Apply(true, true)`
  with a box-filtered mip chain. `ImageConversion.LoadImage` is unusable in this interop build: its
  `Il2CppStructArray<byte>` overload wraps the data in `Il2CppSystem.ReadOnlySpan` and throws
  `MissingMethodException: ReadOnlySpan.GetPinnableReference` at runtime.
* Material: `Universal Render Pipeline/Lit`, cloned from a game material (`MegaBook Page Back`) to
  keep working variants; `_BaseMap`, `_BaseColor`, `_Metallic`, `_Smoothness`, emission off.
  HDRP normal/mask maps are intentionally omitted (`COSMETIC_MATERIAL_APPROX` in the log) — no way to
  validate the swizzle visually with the desktop locked; base colour + lit shading is the deliverable.
* Fit (per model, logged once): `f = targetHeadWidth_world / sourceHeadWidth_authoring`
  (= 0.2404 for the current body), cosmetic root world scale = f, rotation = `modelRoot.rotation *
  R_src`, position = `head.position + hatRot * (targetBox − sourceBox*f)` (in hat-local axes). The
  target box is measured from Alterum's own head-weighted vertices (`Sheriff_Head`, 13,263 verts,
  width 0.2005, top 0.1953) through the head renderer's bindposes; a neck→head ratio fallback exists
  and is logged (`[cosmetic:fit-fallback]`) if a mesh is ever not CPU-readable.
* Socket: HAT → `ThirdViewPlayerModel.Bones.Head` (calibrated). HIP → `Bones.Pelvis`,
  R_LOWER_ARM → `Bones.RightWrist`, CHEST → `Bones.Pelvis` with `[cosmetic:slot-approx]` warnings —
  Alterum has no typed chest/forearm socket, these three are approximations.
* Selection: local only, CSV `BepInEx/config/cosmetics.txt` (default `builtin.partyhat`), F6 overlay
  with one toggle per cosmetic (Input System keyboard; legacy `UnityEngine.Input` throws in this
  game). No `CustomizationOptionRegistry`/RPC/network changes.

## 3. Mirror + screen visibility

* Marker: `CosmeticMarkers` (pointer registry owned by the hot generation) instead of a host-injected
  `CosmeticTag` component. Injected types must live in the stable host assembly and would force a
  host/Contracts rebuild + restart; the task forbids that unless unavoidable, and this achieves the
  same predicate with no host change. `MirrorPlayerVisibility` consults it live in the body scan and
  in `Snapshot()`.
* Cosmetics live on layer 23 (`TV_PlayerModelMesh`). The screen camera mask already contains it
  (`0x4DDB8F87`) and the mirror camera mask is explicitly OR-ed with `1<<23` (`0x4CDB4F87`).
* Screen policy: `equipped && fit && bodyActive && !foreignHide && POV==ThirdPerson`, reasserted every
  frame (the game's `SimpleRenderingController` caches predate the hat, so it cannot gate it).
  Mirror policy: same without the POV term. `MirrorRuntime.Tick()` wraps `reflectCam.Render()` in
  `CosmeticsRuntime.BeginMirrorView(camera)`, which enables mirror-eligible renderers and restores
  the exact screen state afterwards, so a first-person screen never leaks a hat into the mirror or
  vice versa.

## 4. Evidence (this install, generation 7 unless noted)

```
[cosmetic:pack] resource='AlterumModelSwap.Hot.cosmetics.apack' bytes=20622292 cosmetics=21 meshes=33 materials=48 textures=23 sourceHeadWidth=0.8351 sourceHeadTop=0.7657 authoringUnitScale=2.0626
[cosmetic:head-mesh] measured from 'Sheriff_Head'/'Sheriff_Head' headWeightedVerts=13263 boxMin=(-0.0992,-0.0589,-0.1112) boxMax=(0.1012,0.1953,0.1219)
[cosmetic:fit] fitScale=0.24005 sourceBox(width=0.8351 top=0.7657 cx=0.0001 cz=0.0244) targetBox(width=0.2005 top=0.1953 cx=0.0010 cz=0.0054) shift=(0.00,0.01,0.00) headBone='head'
[cosmetic:mesh] id=118 name='Cone' verts=52 tris=46 submeshes=1 uv=True normals=True tangents=True
[cosmetic:texture] id=95 name='Party_Hat_DefaultMaterial_BaseMap' size=2048x2048 png=118224 rawChain=22369620 mips=12/12
[cosmetic:material] id=29 shader='Universal Render Pipeline/Lit' template='MegaBook Page Back' baseMap=Cosmetic_Party_Hat_DefaultMaterial_BaseMap
[cosmetic:bind] id=builtin.partyhat bone='head' boneWorld=(5.31,1.73,-7.57) hatWorld=(5.31,1.74,-7.57) relBone=(0.00,0.01,0.00) localScale=(0.24,0.24,0.24) worldBounds(center=(5.30,2.08,-7.57) size=(0.26,0.29,0.26))
[cosmetic:load] id=builtin.partyhat type=0 meshes=2 verts=562 tris=766 renderers=2 layer=23 (TV_PlayerModelMesh)
[cosmetic:mask] layer=23 (TV_PlayerModelMesh) bit=0x00800000 screenCameras=1 screenCamerasIncludingLayer=1 mirrorCamera=AlterumMirror/AlterumMirrorCam mirrorIncludesLayer=True renderers=2 marked=2
[cosmetic:mask] screen masks: 'Lobby Global/Main Camera'=0x4DDB8F87+
[cosmetic:mirror-mask] camera='AlterumMirror/AlterumMirrorCam' mask=0x4CDB4F87 layer23=True
[cosmetic:mirror-scope] enter camera='AlterumMirror/AlterumMirrorCam' mask=0x4CDB4F87 layer23InMask=True instances=1 renderers=2 enabledForMirror=True
[mirror:body-cosmetic-skipped] renderers=11 of 20 carry the cosmetic marker; excluded from the body sweep   (all-21 run)
[mirror:body-bound] body='3P_ConcreteModel_Male_01(Clone)' selected=9/9 controllers=2 keys=0 layer=14 (Hitbox)
[cosmetic:view] pov=ThirdPerson bodyActive=True foreignHide=False fit=True screen=True mirror=True
[cosmetic:view] pov=FirstPerson bodyActive=True foreignHide=False fit=True screen=False mirror=True
[cosmetic:proof] label=screen_thirdperson mask=0x4DDB8F87 layer23InMask=True renderersEnabled=2/2 coverage=45.54%
[cosmetic:proof] label=mirror mask=0x4CDB4F87 layer23InMask=True viaMirrorScope=True renderersEnabled=2/2 coverage=58.92%
[cosmetic:proof] label=screen_firstperson mask=0x4DDB8F87 layer23InMask=True renderersEnabled=0/2 coverage=43.48%
[cosmetic:disposed] roots=1 markers=0 meshesDestroyed=2 materialsDestroyed=2 texturesDestroyed=1
```

The proof sequence is opt-in (`ALTERUM_COSMETIC_PROOF=1`, and `ALTERUM_COSMETIC_OVERLAY=1` opens the
F6 panel without a keypress); the evidence above was produced with it enabled, the shipped default
leaves the POV alone.

All 21 equipped at once: 21 roots, 37 renderers, no `load-failed`/`texture-failed`
(`shots/proof/all21_mirror.png`). Proof PNGs (`shots/proof/`): `final_mirror_partyhat.png` shows the
textured party hat on the local head through the mirror mask inside the mirror scope;
`cosmetic_proof_screen_thirdperson_*.png` shows it with the screen mask; the
`screen_firstperson` dump shows the first-person arms with no hat (negative control).

The mirror's own RT dump (`mirror_static_g*_{90,180}.png`) renders the lobby from its fixed
viewpoint; the player is not inside that fixed view in this instance, so it does not show the body —
the mirror-mask proof render above is the pixel evidence for the mirror path (same mask, same scope,
same camera-as-render call site).

## 5. Not done / limitations

* HDRP normal, mask (metallic/AO/smoothness) and emissive maps are omitted; transparent slots
  (eyeballs' sclera) render opaque. Logged as `COSMETIC_MATERIAL_APPROX`.
* The two Generic child animations in the reference prefabs (propeller spin, eyeball blink) are not
  played; those items are static.
* CHEST/HIP/R_LOWER_ARM sockets are approximations (see §2).
* `0.38`-free fit is derived from a measured authoring-unit scale; residual uncertainty is in that
  single number (2.06 vs 2.15 depending on estimator). Hat proportions look correct in the dumps.
* Not verified: death/cutscene gating beyond the mirror's `IsRenderingEnabled` signal, remote players
  (out of scope), real desktop pixels (session 1 is locked; all evidence is logs + RT dumps).

---

## 6. Third-person toggle, CHEST socket, demo re-record (later generation)

### 6.1 `PovToggle` (was the demo-only `DemoPov`)

`DemoPov.cs` and its `ALTERUM_COSMETIC_THIRDPERSON` env / `thirdperson.flag` gating are **gone**.
The same code path is now the user-facing feature `PovToggle.cs`, called from
`ModelSwapRuntime.Update` before the cosmetics update; on a change it forces
`CosmeticsRuntime.ForcePovRefresh()` so the transition and its `[cosmetic:view]` line stay adjacent.

* Key: **F7** (F6 = cosmetics overlay, F11 = clip recorder).
* Request file: `<ScreensDir>/pov.request`, deleted on pickup, one word:
  `on|third|thirdperson|1`, `off|first|firstperson|0`, `toggle`. (The desktop is locked, so this is
  the unattended trigger.)
* Sticky: an unreachable controller keeps the request pending; a mode the game changes back is
  re-asserted at most every 0.5 s, re-assert log at most every 2 s.
* Guarded: no local player / no `PlayerPOVController` / a switch that throws is a logged no-op
  (`[pov] unavailable: ...`), never an exception into the game loop.
* Logs: `[pov] ready ...`, `[pov] request ...`, `[pov] applied ...`,
  `[pov] third-person ON (was FirstPerson) ...` / `[pov] third-person OFF (was ThirdPerson) ...`,
  then the cosmetics runtime's `[cosmetic:view] ... screen=? mirror=?` for the same frame.

Verified (generation 41, request-file path):
```
[pov] request third-person (from pov.request)
[pov] applied third-person (controller='.../Player POV Controller')
[pov] third-person ON (was FirstPerson) controller='...' owner='LobbyPlayer_Separated | Owner: 0'
[cosmetic:view] why=update pov=ThirdPerson ... screen=True mirror=True instances=1 renderers=2
[pov] request first-person (from pov.request)
[pov] third-person OFF (was ThirdPerson) ...
[cosmetic:view] why=update pov=FirstPerson ... screen=False mirror=(game hides the body: foreignKeys=2)
```
The F7 key branch is implemented with the same `Keyboard.current[...]wasPressedThisFrame` pattern as
F6/F11 but could not be exercised (locked desktop: no input).

Measured lobby fact: the lobby has exactly one enabled camera (`Lobby Global/Main Camera`, fov 70,
mask `0x4DDB8F87`) and it sits at the player's eyes in **both** POVs (`camPos=(5.35,1.8,-7.57)`),
so this build's "third person" enables the body/IK state without pulling the screen camera back in
the lobby. The cosmetic screen policy and both camera masks follow the POV correctly.

### 6.2 CHEST socket

MoreCompany's own CHEST socket is the third spine bone (`chest =
spine.spine.001.spine.002.spine.003`), i.e. **`spine_03`** here; `chest_01_*` are female-only breast
bones and are not a torso socket. `ResolveSocket(CHEST)` now resolves `spine_03` from
`ThirdViewPlayerModelBones.AllBones` with preference `spine_03 → spine_02 → spine_01 → spine_04`
(logged as `[cosmetic:socket]`; a one-off `[cosmetic:socket-scan]` lists the spine bones and their
world positions). `HIP → Pelvis` is unchanged.

The accessory keeps its baked prefab chain and the calibrated root rotation; what it does not keep
is the old pelvis position. A measured socket clearance puts the item's *centre* at the body's
torso surface (2 cm out), because the sheriff vest's front is ~0.28 m ahead of `spine_03` while the
placard is only ~0.12 m ahead of its root and would otherwise be buried in the vest. The
measurement only uses skinned body renderers at the item's height whose horizontal span is < 0.8 m
(the shirt AABB spans the arms and would overstate the chest by ~0.6 m), and is logged:
```
[cosmetic:socket-scan] body='3P_ConcreteModel_Male_01(Clone)' bones=[spine_01@(...,1.175,...); spine_02@(...,1.318,...); spine_03@(...,1.456,...)] pelvis=(...,1.038,...) neck=(...,1.612,...)
[cosmetic:socket] id=builtin.outoforder type=2 bone='spine_03' reason=CHEST = MoreCompany's spine.003 socket (no chest_01_* on male rigs)
[cosmetic:socket-offset] id=builtin.outoforder bone='spine_03' torso='Vest' torsoFront=-4.806 itemCenter=-5.062 shift=0.276m forward=(-1.00,0.00,-0.04) candidates=[Sheriff_Head:-4.89 Shirt_Optimized_1:span1.11 Tie_Optimized:-4.89 Vest:-4.81 Hair:-4.93 Hair_cap:-4.94]
[cosmetic:bind] id=builtin.outoforder bone='spine_03' ... worldBounds(center=(5.06,1.51,-7.59) size=(0.29,0.40,0.27))
```
Re-capture (`demo/out/still_outoforder_chest_1080.png`, `crop_outoforder_after.png`, and the
same-pose before/after side-by-side `demo/out/oo_before_after.png`): **yes** — the placard now hangs
from the rope around the neck, on the chest, above the belt and tie; before it hung in front of the
crotch. Orientation is unchanged on purpose: parenting to the bone with the bone's own rotation
flips the plate to face backwards (tested), because the prefab chain's local axis is the calibrated
hat frame. Consequence: the plate is rigid to `spine_03` in position but inherits the head-frame
rotation, so an independent chest twist is not followed.

### 6.3 Capture additions (`ClipRecorder`, additive request keys only)

* `look=head|body` (default body): `head` anchors the orbit on the head bone instead of the body
  bounds centre and looks at it — the showcase look point was fixed at body-centre +
  0.15*extents.y, which could never frame a tight head shot from ~1.3 m.
* `lookoffset=x,y,z`: metres added to the look point.
* `eye=x,y,z` + `lookat=x,y,z`: pin the camera pose in world space; both or neither. With a pinned
  pose nothing derives from body bounds, so an equipped/unequipped pair is directly comparable.
* `[clip] start`, `[clip] showcase` and a new `[clip] camera census` (view=main) log the resulting
  pose/rotation/mask.

### 6.4 Demo

`demo/out/cosmetics_partyhat_showcase.mp4` re-recorded in the new POV at 640x360, request
`fps=11 seconds=8` on generation 45; the recorder achieved **11.00 fps** (88 frames, wall 8.00 s) and
the file is encoded at 11 fps (`r_frame_rate=11/1`, duration 8.000 s): nominal == achieved, playback
is real time. The old file was 15 fps nominal for 8.42 fps achieved (~0.56x).

Head still: `demo/out/still_head_closeup.png` (`look=head lookoffset=0,0.15,0 dist=1.3 fov=40
lift=0.10 yaw=auto`), hat tip and chin both inside the frame.
Pinned pair: `demo/out/pair_hat_pinned_A.png`, `demo/out/pair_none_pinned.png`, diff
`demo/out/pair_pinned_diff_head.png`, measured by `demo/pinned_pair_diff.py`:
hat zone 3.84x the same-equipment control, face 0.79x, torso 0.19x — the change is confined to the
hat/head, and the torso (0.19x, ~1 % of pixels) shows the pinned pose held.

---

## 7. First-person local-cosmetic layer (per-camera, no renderer toggling)

The first-person screen used to hide the local hat by toggling `Renderer.enabled`; that is a shared
object, so the mirror either lost the hat or needed a render-scope dance. The local hat now lives on
a dedicated layer and only camera masks differ:

* `CosmeticsRuntime.EnsureLocalCosmeticLayer()` picks a layer once per scene from the mirror's
  shared URP renderability probe, requiring **no live renderer** on it and preferring one outside
  every screen-camera mask. Lobby probe (`generation 3`): renderable
  `[0,2,4,6,7,9,10,11,14,15,20,23,24,27]`, census `0=11865 1=2 3=45 7=2 23=9 24=1 31=1`, so
  **6 = `InteractableLookable`** is renderable, unused and absent from `0x4DDB8F87`.
* The local player's cosmetic roots/renderers are created on that layer (`CreateInstance`,
  `BuildNode`); layer 23 and other players' cosmetics are untouched.
* `ApplyScreenCameraMasks()` writes only bit `1<<6` on screen cameras: present in `ThirdPerson`,
  absent in `FirstPerson` and on native body hides. Pristine masks are recorded and restored on
  dispose / scene re-probe. The mirror camera gets/keeps bit 6 unconditionally
  (`SetMirrorCamera`, `BeginMirrorView`, `EnsureMirrorLocalLayer`).
* Layer 23 stays in the screen mask in **both** POVs, so foreign hats keep rendering in first
  person (`layer23InMask=True` in every proof case).
* `BeginMirrorView` is now a no-op mask assertion; the renderer snapshot/scope machinery is gone.

Four-case pixel proof (`ALTERUM_COSMETIC_PROOF=1`, same proof-camera pose, hat renderers enabled
`2/2` in every case, hat vs hatless in one frame; numbers independently re-measured from the PNGs):

| case | mask | local bit | diff px (>2) | mean | max |
|---|---|---|---:|---:|---:|
| first person / screen | `0x4DDB8F87` | absent | **0** | 0.0000 | 0 |
| first person / mirror | `0x4CDB4FC7` | present | **13,433** | 31.2860 | 198 |
| third person / screen | `0x4DDB8FC7` | present | **15,739** | 33.9299 | 142 |
| third person / mirror | `0x4CDB4FC7` | present | **14,020** | 31.1845 | 201 |

Live mirror RT (mirror moved 2.6 m in front of the player for the capture): FP **8,543** diff px,
TP **8,899**. `shots/first-person-hat/README.md` lists every file and the full probe/evidence.
