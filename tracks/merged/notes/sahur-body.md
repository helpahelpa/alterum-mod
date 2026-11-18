# Sahur body — CC0 mesh as a playable character, bound to the game's own skeleton

Source: **Tung Tung Tung Sahur** by **@BakaFoxArt**, Sketchfab BakaFoxAssets, **CC0 1.0**
(`models/sahur/dl/tttsahur/tttsahur/LICENSE.txt`, kept next to the archived asset; the archive
stays in `models/sahur/dl/tttsahur/`). No Blender, no Unity editor, no asset bundle: the FBX is
baked offline into an embedded binary and the `Mesh` is built in C# at runtime.

## 1. Bake — `tools/bake_sahur.py`

Reads the FBX with `models/sahur/fbxtool.py`, writes into the hot mod:

| file | size | contents |
|---|---|---|
| `sahur.mset` | 73,843 B | 8 rigid pieces, 3,939 vertices, 3,738 triangles: int16 positions over the exact bbox, uint16 UV, int8 normals, uint16 indices, per-piece byte bone region |
| `sahur_albedo.png` | 361,394 B | byte copy of the model's own albedo — the copy is also what removes the FBX's absolute `C:\Users\PC\...\albedo.png` material path (the runtime only ever sees embedded bytes) |

The source is 121,724 B of FBX; the blob is 60 % of that for the geometry and the texture is
carried as shipped.

### Region assignment (geometry only, no names — the source has 0 bones)

Landmarks measured on this mesh (FINDINGS §9.5), all centimetres:

* head — `y >= 118` (narrowest neck ring, 13.7 cm wide at y 116..122);
* arm — inside the shoulder weld band `100 <= y <= 133`: `|x| >= 11` **and** `|z| <= 8`;
  below it (`y < 100`): `|x| >= 16.5` (the hanging forearm/hand is at `|x| 20.3..25.3`, the robe
  hem only reaches `|x| 14`, so x alone suffices there). The `|z|` term is required: the robe's
  front/back faces reach `|x| 17.5` in the weld band, so `|x|` alone drags the torso into the arms;
* leg — `y < 62` (hip hem, hem bottom ring at y 66), side by the sign of x;
* torso — everything else;
* both eyeball components (65 v each, separate topology) go to the head bone.

`|x|` alone is not enough anywhere near the neck: at `y 104..116` the arm is `|x| 12.1..22.6` at
`|z| <= 6` while the robe's corners are at `|x| 17.5, |z| 11.4`.

Vertex counts per region (source vertices): torso 149, head 759 (incl. 130 eyeball), arm_l 315,
arm_r 315, leg_l 165, leg_r 165 (symmetric, as expected). No isolated outlier vertices needed
fixing.

### The split

A triangle's region is the **majority of its three vertices' regions**; every triangle corner
then becomes an output vertex of that piece, keyed by `(source vertex, corner UV)`. A boundary
vertex therefore exists once per piece it touches — that duplication *is* the cut, and it makes a
triangle spanning two bones structurally impossible. Nothing is added or removed, so the union of
the pieces is exactly the source surface. 130 boundary triangles (4 touch three regions), 2,071
duplicated vertex copies (3,939 output vs 1,868 source vertices — most of the growth is the
source's own UV seam split, 3,828 UV pairs for 1,868 vertices).

| piece | bone | verts | tris |
|---|---|---|---|
| torso | `spine_01` | 332 | 300 |
| head | `head` | 1,285 | 1,248 |
| arm_l | `upperarm_l` | 828 | 647 |
| arm_r | `upperarm_r` | 823 | 647 |
| leg_l | `thigh_l` | 257 | 328 |
| leg_r | `thigh_r` | 250 | 328 |
| eye_l | `head` | 82 | 120 |
| eye_r | `head` | 82 | 120 |

The bake re-reads what it wrote and verifies: no emitted triangle has vertices in two regions, no
piece range overlaps or leaves a vertex uncovered, the position decode is inside one quantisation
step, UV decode inside 1/65535, and every triangle keeps the source winding (32 source slivers of
area < 5e-5 cm² are excluded — their normal is numerical noise in the source too).

`--piece-shot` renders the pieces flat-coloured with `models/sahur/render.py`:
`shots/sahur/bake_pieces_front.png`, `bake_pieces_side.png`.

## 2. Runtime — `SahurModel.cs` (new file, no other file's logic changed)

Embedded as `AlterumModelSwap.Hot.sahur.mset` / `.sahur_albedo.png` (two `EmbeddedResource`
lines). At load: parse, dequantise, `positions * 0.01`, yaw 180° about Y (auto-decided: the
source faces −Z and its left is +X; `hand_l` sits at `x = -0.285` in the model root's space, so
the rotation is required and the arms cannot end up crossed), then `Mesh.vertices/uv/normals`,
`boneWeights` (1.0 to exactly one bone), 8 submeshes, `bindposes = bone.worldToLocalMatrix *
rendererRoot.localToWorldMatrix` at bind. Renderer: `SkinnedMeshRenderer`, `bones` = the game's
own 6 bones from `ThirdViewPlayerModel.Bones._allBones`, `layer 6`.

The local body is resolved the same way the cosmetics runtime does it (`CurrentPlayerProvider` →
`MirrorPlayerVisibility.Under`), so only the owner's third-person model is touched.

Body hide/restore: all `Renderer`s under the player model root are snapshotted before our objects
exist, then `enabled = false` (never destroyed, never `forceRenderingOff`); re-asserted once a
second because the mirror's body sweep and the garment pass write their own snapshots; restored to
the recorded value on off/dispose/model-gone.

Layers/POV: layer 6 is the mod's local-cosmetic layer, whose existing per-camera policy is
already "third person + mirror, never first person". The renderers are also registered with
`CosmeticMarkers` (renderer + root) so the mirror's layer-14 sweep and the cosmetic census treat
them as ours — the layer-14 body isolation and its census are untouched.

Bat: 4 primitives under `hand_r` (handle 8-sided 0.017→0.019 m × 0.36 m, tapered barrel
0.046→0.060 m × 0.50 m, two bands), separate togglable part.

Control: **F3** body, **F4** bat; `sahur.request` in the screens dir accepts `on|off|toggle`,
`bat=on|off|toggle`, `dump=1`, `probe=1`, `view=front|back|side[:orthoSize]`, `nobody=1`,
`ref=1`, `bindtest=<bone>`, `mirror=<tag>`. `MiniJson`-free, line-oriented, deleted on pickup.

## 3. Evidence and what is still wrong

Bake-side: verified as above; the piece palette render shows head/torso/arms/legs cut at the
measured landmarks.

Runtime-side (in `BepInEx/LogOutput.log`, generation 31-36):

* `[sahur:build] verts=3939 tris=3738 submeshes=8 bones=spine_01,head,upperarm_l,upperarm_r,thigh_l,thigh_r height=1.796m scale=0.01 yaw=180`
* `[sahur:body] snapshot 78 renderer(s) ... hid 42` then `restored 42 ... (why=dispose)` — hide and
  restore both fire, on toggle-off and on hot-reload dispose.
* `[sahur:probe] weights readback: n=3939 [0]=b0/w1.00 [500]=b1/w1.00 [3900]=b1/w1.00` — the rigid
  weights survive the upload.
* `[sahur:probe] BakeMesh verts=3939 ... actual y -0.013..1.788 (1.800 m)` — **measured in-game
  height 1.800 m** (source 179.62 cm), feet at the player root's y, top 6 cm above the game's own
  `head` bone position (the mask is taller than the bone).

**Bug, not fixed: the skinned render comes out at ~1/3 scale.** In the capture rig (private ortho
camera, layer 6 only, unlit copy of the albedo) the body measures 0.595 × 0.278 m against a
declared 1.80 × 0.51 m, while a plain `MeshRenderer` of the *bat* in the same frame is the correct
0.96 m and a plain `MeshRenderer` of the body mesh at the same transform did not draw at all
(`view=ref` frame is empty). Binding every vertex to `pelvis` alone (`bindtest=pelvis`), where the
skin matrix is provably `root.localToWorld` (bindpose and bone matrices cancel), still renders at
0.595 m — so the fault is inside the skinning path of this IL2CPP build (bindposes/bone array
convention), not the mesh, the material, the camera, the weights or the bake. `BakeMesh` reports
the correct 1.800 m through the same buffers, which is why the height above can be trusted even
though the drawn result cannot.

Next step for whoever picks this up: dump `smr.bones[i].localToWorldMatrix` and
`smr.sharedMesh.bindposes[i]` for i=0 and compare their product against `root.localToWorld`,
then try (a) setting `bindposes` from `bone.worldToLocalMatrix * root.localToWorldMatrix` with the
renderer placed under a scale-1 wrapper, and (b) a two-bone `Unassigned` test mesh of known size to
isolate whether this Unity build applies the renderer's own matrix to skinned output.

### Captures (`shots/sahur/`)

| file | what |
|---|---|
| `bake_pieces_front.png`, `bake_pieces_side.png` | the 8 pieces, flat-coloured, offline |
| `shot_main_sahur_tp_stand_...png` | game camera, third person, body hidden — the scene is a dark map, so this proves little beyond "the frame renders" |
| `sahur_view_front.png`, `_back.png`, `_side.png` | the live skinned body in the capture rig (shows the scale bug; the bat is correct in `_side.png`) |
| `sahur_view_nobody.png`, `sahur_view_ref.png` | negative controls (empty frames) |

**Not obtained:** mirror captures — this install is on `Lair Of Occultists Map Scene`, and the
mirror booth is lobby-only (`cfg MirrorLobbyOnly`), so no mirror camera exists to dump. Emote
captures (`The_Griddy`, `Twerk`) were not taken because the body renders at 1/3 scale; the emote
runtime itself is untouched and drives the same bones.

## Shared files touched (nothing else)

* `AlterumModelSwap.Hot/AlterumModelSwap.Hot.csproj` — two `EmbeddedResource` lines after the
  `reel_pool_icons.png` entry (an earlier version of this block landed on the `reel_icons.png`
  line and was lost when that line was renamed; re-added on the current one).
* `AlterumModelSwap.Hot/ModelSwapRuntime.cs` — three additive spots: `SahurModel.Tick(log, dt)` in
  `Update` immediately after `ClothesToggle.Tick`, `SahurModel.Dispose(_h)` in `Dispose`
  immediately before `ClothesToggle.Shutdown`, and two lines appended to the `KeyLegend` string.
  `ReelLogic.cs`, `ReelArt.cs`, `LobbyProp.cs` untouched.

## Session state

All work was hot-reload only on install A (`BepInEx/hot/AlterumModelSwap.Hot.dll`, generations
31-43); `win/run-game.ps1` was never used and no process was killed from here. The instance's log
stops at 05:25:27 with no error and no `ErrorLog` entry, and `tasklist` shows `Alterum.exe` gone —
i.e. the session ended while this work was in progress (its last known state had the Sahur body
active, which dies with the process). Nothing is left hidden or half-replaced on disk.
`tools/deploy-hot.sh` was **not** used (it also writes `C:\AlterumB`); the dll was copied to A's
hot dir with the tmp+`mv -f` pattern only.

---

# 2026-11-13 — the "1/3 scale" bug, and Sahur as a wardrobe model

## The bug was never a scale

The body did not render small: **only its first submesh rendered at all**. The mesh has one
submesh per rigid piece (8) and the renderer was given a single material
(`sharedMaterials = [Body]`). A renderer with fewer materials than submeshes draws only the
submeshes that have a material, so the only thing on screen was piece 0 — the **torso**, whose
box is 27.9 × 69.6 × 29.5 cm. That is exactly the 0.278 × 0.595 m silhouette measured in the
capture rig: the width matched the torso's X extent to the millimetre, which is what gave it away.

Measured frames, same camera, same frame, ortho size 1.02 (2.04 m over 360 px):

| frame | drawn height |
|---|---|
| 1 material for 8 submeshes (`mats=1`, reproduces the old build) | **0.589 m** |
| 8 materials (`sahur_view_front`) | **1.802 m** (mesh extent 1.796 m) |

The fix is one line in `SahurModel.Build`: `sharedMaterials` gets `asset.Pieces` entries. The
reference path (`view=ref`) was given the same treatment, which is why that frame draws now too.

## The decisive experiments (all in one generation, `sahur.request`)

`mats=1` logged both renderers and reproduced/cured the symptom; `mats=all` copied the game's own
body-renderer values one at a time onto ours and captured after each:

| source of the value | ours | game body | step captures |
|---|---|---|---|
| submeshes / materials | 8 / **8** | 8 / **8** | `mat1` 0.589 m → `mat8` 1.802 m |
| rootBone | `pelvis` | `spine_03` | drawn 1.796 m; only the renderer's *bounds* moved (0.71,1.89,0.84 → 1.00,1.94,1.01) |
| quality | Auto | Auto | unchanged (1.802 m) |
| localBounds | 1.264×0.488×0.555 | 0.507×0.328×0.340 | unchanged |
| updateWhenOffscreen | True | False | unchanged |
| forceMatrixRecalculationPerRender | False | False | unchanged |

None of bindposes/rootBone/quality/localBounds/updateWhenOffscreen changes the drawn size, so the
skinning path was never at fault, and `BakeMesh`'s 1.800 m was right all along. (The bindpose
formula comparison against the game's head renderer differs by 0.56, but the game's bindposes were
authored in the prefab, in a child renderer's own bind space, so that number is not comparable —
the CPU skin measurement and the drawn result now agree, which is the check that matters.)

## Wardrobe entry

* The list is `PlayerModelDatabase.Models` (a static `PlayerModelData[]`; 10 game entries:
  `Female_1_Emma … Male_5_Ryan`). `WardrobeOptionalViewController` builds one
  `WardrobeModelButtonedTab` per entry, each `Initialize(PlayerModelData)`.
* Selection is index-based and native: tab -> `ModelSelected(clientId, index)` ->
  `WardrobeController.OnModelSelectedServerRpc` -> `PlayersAppearanceManager.SetBaseModelIndex` ->
  `ApplyEffectiveModel` -> `PlayerModelSetuper.SetupModel(Models[index])`. `PlayersModelSessionSaver`
  writes the index to PlayerPrefs, so it also persists across sessions.
* `SahurWardrobe.cs` clones `Male_4_James_PlayerModelData` at runtime, renames it `Sahur`, gives it
  the rendered portrait (`PlayerModelData._bustPortraitSprite`, an empty `PortraitLayerSet` clone)
  and appends it -> **index 10 of 11**. Adopts/prunes by the name marker so hot reloads do not
  stack duplicates. The spawned donor body is hidden by `SahurModel` exactly like the normal body,
  so the visible model is Sahur and the F3 toggle drives the same module (no second path).
* Portraits: `tools/sahur_portrait.py` rasterises `sahur.mset` (same 180° yaw, same 0.01 scale as
  the runtime) with alpha into `sahur_portrait.png` (256×320) and `sahur_bust.png` (256×256), both
  embedded.
* The wardrobe UI itself is lobby-only, so the tab list could not be captured from the mission
  instance. `SahurWardrobe.Attach` dumps every tab (path, data, text, every `Image` child with its
  sprite/size) and takes one full-screen shot (`sahur_wardrobe_lobby.png`) the first time the view
  appears, so the next lobby visit produces the evidence automatically. A tab missing from the
  game's list is inserted through the wardrobe's own prefab + `TabGroup.AddTab`.

Requests added to `sahur.request`: `mats=1|all|<step>`, `human=1|0`, `wardrobe=dump|apply|revert|hud|hudshot`.
