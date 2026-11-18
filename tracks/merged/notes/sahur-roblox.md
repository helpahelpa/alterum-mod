# Rigged Sahur variant (Roblox model) — bake, retarget, evidence

Second wardrobe entry next to the BakaFox one. Files: `mod/AlterumModelSwap.Hot/SahurRig.cs` (runtime),
`tools/bake_sahur_roblox.py` (bake), `tools/sahur_roblox_portrait.py` (portraits),
assets `AlterumModelSwap.Hot/sahur_roblox.{rset}`, `sahur_roblox_{albedo,normal,portrait,bust}.png`.

## Attribution (required, CC BY 4.0)

* **Tung Sahur Game 3D model (Roblox)** by **`alex.cgwarrior`**,
  https://sketchfab.com/3d-models/tung-sahur-game-3d-model-roblox-4cb52341b3bb4a59bd951e4e6b0ae0e6
* Licence: **CC BY 4.0**, https://creativecommons.org/licenses/by/4.0/ — author credited, changes below.
* **Modified:** 4K PBR maps downscaled to 1024² (base colour + normal), roughness folded into a constant
  URP smoothness (0.475, from the map's mean), AO dropped, skin weights reduced from ≤7 to the 4 strongest
  and renormalised, mesh mirrored in X to match the game's handedness. Source kept at
  `models/sahur/variants/tung-sahur-game-3d-model-roblox/`.
* The credit line is also in `SahurRig.cs`'s header, its `[sahurrig]` startup log line, and the csproj
  comment next to the embedded resources.

## Bake format (`sahur_roblox.rset`, magic `SAHRRG1`)

`tools/bake_sahur_roblox.py`; reads the FBX with `models/sahur/fbxtool.py`, no Blender/assimp/Unity.

| block | contents |
|---|---|
| header | version, flags (bit 0 = mesh mirrored X), counts, position bbox, uv bbox |
| names | 71 UTF-8 names, newline separated (diagnostics only — the runtime maps by anatomy) |
| bones | 71 × { parent i8 (−1 = root), flags, local pos 3×f32 (m), local rot 4×f32 } — **these locals ARE the bind pose** |
| submeshes | 8 × { triCount u32, material u8, islandKind u8 }; one material for all eight |
| vertices | int16×3 position (quantised over the bbox), uint16×2 uv (quantised over the uv bbox, u goes past 1 → Repeat), int8×3 normal, uint8×4 bone index, uint8×4 weight |
| indices | uint16×3 per triangle, contiguous per submesh |
| clip | u16 frames, f32 duration, int16×4 rotation delta per bone per frame, int16×3 hips translation per frame (mm) |

Facts the bake prints and checks:

* 3,097 source verts / 6,162 tris → **3,711 emitted verts** (split by `(vertex, uv, normal)`; only the real
  seams duplicate), 8 islands = body(+head) 1790 tris, arms 962+962, eyes 576+576, legs 468+468, **bat 360**.
* bind pose: **58/71 bones from the cluster `TransformLink` matrices**, the 13 bones without geometry from the
  node chain. The local accumulation reproduces `TransformLink` to 6.7e-16. This matters: the file's *rest pose*
  differs from its *bind pose* (worst 19.1 cm, at `LeftLeg`), so a node-chain-only bind draws the eyes and the
  knees wrong. `TransformLink` is the pose the mesh was skinned in, and it is what the mesh matches.
* weights: ≤7 influences in the source, **4 strongest kept and renormalised** — mean mass dropped 0.02 %,
  worst 10.1 % on 68 vertices (the shoulder/hip bands). Why 4: Unity's classic `Mesh.boneWeights` (BoneWeight)
  path is what an injected IL2CPP module can set without `NativeArray<BoneWeight1>` plumbing, and `skinWeights`
  is `Bone4`. `SkinQuality.Bone4` is set explicitly on the renderer.
* clip: `Armature|Walk`, **2.3667 s, 72 frames at 30 fps, in place** (hips travel −4.6…+4.6 cm), frame 0 ==
  identity by construction (deltas are relative to frame 0, so a rest/bind mismatch cannot displace the mesh at
  t=0). Largest joint swings: foot 68°, hands/fingers 65° (accumulated world deltas — the fingers inherit the
  arm swing).
* textures: base 4096² → **1024² (537,888 B)**, normal 4096² → **1024² (435,528 B)**, roughness mean 0.525 →
  constant smoothness 0.475, AO dropped.
* **payload: rset 159,412 B + albedo 537,888 B + normal 435,528 B + 2 portraits (27,640 + 49,465 B) =
  1,209,933 B ≈ 1.15 MB** added to the hot assembly (23.8 MB dll total).

## Runtime skeleton and retarget

`SahurRig` builds the 71 bones as real `Transform`s under `SahurRigged_Skeleton` (child of the player model
root), plus the `SkinnedMeshRenderer` on `SahurRigged_Body` (layer 6, 8 submeshes, **8 materials**, `boneWeights`
= the baked 4-influence values, `mesh.bindposes` = `inverse(yaw · local chain)` computed from the baked locals,
`RecalculateTangents()` for the normal map). Height 1.807 m mesh / 1.793 m skinned in the idle pose, feet on the
floor (root y + 0.015).

Yaw is decided at runtime from anatomy, not hardcoded: the model's own `LeftHand` bind position accumulated in
its own space vs the game's `hand_l` in the player-root space. Both are −X for this bake (the mesh is mirrored in
X because the source's L/R is the other way round from the game's), so yaw = 0 and the model faces the game's +Z.

Per mapped pair, every frame (LateUpdate, after game animation + emote pose + proportion cage, before the mirror
renders):

```
delta    = (rootRotInv * gameBone.rotation) * inverse(gameBind[k])     // accumulated world rotation delta
ours     = delta * (yaw * bindWorld)                                  // world
local    = inverse(parentWorld) * ours
hipsPos  = bindPos + rootRotInv * (gamePelvis - gameBindPelvis)        // root follows position
```

`gameBind` is not "the first frame" any more: it is read from **`mesh.bindposes` of the 8 body SMRs** under the
model root (`inverse(bindpose).rotation` is the bone's bind rotation in that renderer's frame). Capturing the
live first frame instead cost a permanent ~90° arm offset whenever the model respawned while the animator was
still warming up. 20/20 mapped bones resolve from bindposes; the live-first-frame fallback logged 0.

**Bone map (anatomy, 20 pairs):**

| game | model | note |
|---|---|---|
| pelvis | Hips | + position delta |
| spine_01 / spine_03 / spine_05 | Spine / Spine1 / Spine2 | 5 game spine joints over 3 model ones: 1st, 3rd, 5th |
| neck_01 | Neck | |
| head | Head | |
| clavicle_l/r | LeftShoulder / RightShoulder | |
| upperarm_l/r | LeftArm / RightArm | |
| lowerarm_l/r | LeftForeArm / RightForeArm | |
| hand_l/r | LeftHand / RightHand | |
| thigh_l/r | LeftUpLeg / RightUpLeg | |
| calf_l/r | LeftLeg / RightLeg | |
| foot_l/r | LeftFoot / RightFoot | |

**Unmapped game bones** (logged at bind): `spine_02`, `spine_04`, `neck_02`, `chest_01_l/r`, `chest_02_l/r`,
plus every garment bone. No counterpart in a 3-spine Mixamo rig; their motion is not applied.

**Not driven on the model side:** the other 51 bones inherit their parent's delta, which is what makes the
fingers follow their hand, the toes follow the foot, the eyes/mouth follow the head — and **`BIT` follow
`RightHand`**. That is the bat socket: the bat island (360 tris, 100 % weighted to `BIT`) sits 0.082 m from
`RightHand` in the bind and is carried by it for free (`[sahurrig:bat]` + every emote capture). No rigid
re-parenting was needed.

**Proportions system:** `Proportions.cs` scales `pelvis`, `clavicle_l/r`, `chest_02_l/r` — game bone names.
This model owns its skeleton, so **the proportion system does not affect it at all**: no breast/hip scaling is
applied, and none of its bone transforms are touched by that pass.

## Verification

Engine state: A (`…/common/Alterum`), hot generation after the last deploy, **0 `[Error]` lines in the current
generation** (409 earlier ones are the pre-fix `IndexOutOfRangeException` storm from this same session, and 2
unrelated pre-existing ones: one clip-recorder deadline, one `Update threw` NullReference before my work).
`grep -c SWAP` = 0. Reel unchanged: `[reel] strip 8192x160 cells=64 … deck=[item=38 damage=12 debuff=8
double=6]`, placed, spun, `settled card=55 … match=True`.

Captures in `shots/sahur-roblox/` (640×360, private layer-6 camera):

| file | shows |
|---|---|
| `01_compare_floor_and_height.png` | model next to a real `Female_2_Freya` body on the same floor; 1.81 m vs her 1.57 m head height |
| `02_mid_twerk.png` | mid-`Twerk` — crouched, legs splayed, mesh deformed (not rigid) |
| `03_mid_the_griddy.png` | mid-`The_Griddy` — bent-over frame |
| `04_walk_clip_side.png` | the baked Mixamo walk playing (`walk=on`), mid-stride, bat horizontal in the hand |
| `05_hud_portrait_roblox.png` | the bottom-right HUD portrait slot with the rigged entry applied |
| `05b_hud_portrait_classic.png` | the same slot with the classic entry applied — visibly different |
| `06_classic_still_works_front.png` | the BakaFox body still selectable and unchanged |

## The bottom-right HUD portrait

`wardrobe=hud` now also dumps the descendant `Image` sprites of
`HUD/Portrait(Clone)/Controller/Background/Portrait Image`. Selecting the rigged entry gives
`sprite='Sahur_Portrait_sahur_roblox_bust'`, selecting the classic gives
`sprite='Sahur_Portrait_sahur_bust'` — the slot is fed by `PlayerModelData.BustPortraitSprite`, it follows the
selection, and the two portraits are visibly different (orange tall wooden head vs dark mask,
`05_hud_portrait_roblox.png` / `05b_hud_portrait_classic.png`). An earlier capture looked unchanged because the
selection had not been re-applied through `SetBaseModelIndex` at the time; a model selection is what refreshes it,
not the map load.

Still not proven in-game: the wardrobe **tab list** (per-entry icon + label) needs the lobby — in a map there is
no `WardrobeOptionalViewController`, so only the data registration, the `BustPortraitSprite` slot, the selection
path and both bodies were exercised here.

Not fixed / still looks wrong: the `smr.bounds` Unity reports is a Hips-based culling box (centre ~2 m), so a
log line framing by it would be wrong — the capture rig frames from the player root instead. `~9 %` of UV
corners have u > 1 (the atlas wraps); it renders correctly with the default Repeat wrap mode but a Clamp setting
would smear those triangles. The model's arms read darker than the robe in the unlit capture material — that is
the albedo region those UVs sample, not a shading fault, but it is worth a second look in lit conditions.
