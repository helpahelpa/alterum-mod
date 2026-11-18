# bones track — recon notes

Research task: implement "scale up breast + hip bones on **all** Alterum player models, with
jiggle", modelled on `FoxGod/ThiccCompanyModelReplacement` (Lethal Company), and remove the
existing Diana→Freya model swap.

Everything below is static analysis (decompiled assemblies + UnityPy on shipped assets).
Confidence is marked where it matters: **[fact]** = read directly, **[inference]** = reasoned
from shipped data, could not be confirmed without running the game.

Artifacts in this directory:

| path | what |
|---|---|
| `tc.zip`, `tc/` | Thunderstore package 2.0.1, unpacked (`ThiccCompany2.0.dll` + 11 suit PNGs) |
| `thicc.bundle` | the 21 MB AssetBundle carved out of the DLL's embedded resource |
| `tc_decomp/` | ilspycmd project of the reference mod (15 files, 220 lines) |
| `mrapi.zip`, `mrapi/`, `mrapi_decomp/` | ModelReplacementAPI 2.3.6 (the jiggle engine lives here) |
| `acsharp/classes.txt` | `ilspycmd -l c` on the game's interop `Assembly-CSharp.dll` (5834 types) |
| `interop/` | decompiled interop stubs: `PlayerModelSetuper`, `ThirdViewPlayerModel`, `PlayerModelData`, `SafeGameObject`, `MagicaCloth`, `ClothSerializeData` |
| `explore_shared.py` | read-only UnityPy helper for the game's `sharedassets0.assets` (reusable) |

---

## 1. What the reference mod actually does

**[fact] It does not scale any bone.** It ships 11 complete replacement body models and hangs a
spring-physics component on their extra breast/butt bone chains.

* BepInEx plugin `foxgod.ThiccCompanyModelReplacement`, assembly `ThiccCompany2.0.dll`.
  Only real code: register 11 `BodyReplacementBase` subclasses with
  `ModelReplacementAPI.RegisterSuitModelReplacement(suitName, type)` and (optionally)
  `RegisterModelReplacementDefault`. Each subclass is 13 lines:
  `Assets.MainAssetBundle.LoadAsset<GameObject>("DefaultOrange")` etc.
  `Harmony.PatchAll()` is called but the assembly contains **no** patch classes.
* The asset bundle `thicccompany_v2_0_0bundle` is an embedded DLL resource
  (`Assembly.GetManifestResourceStream`), Unity **2022.3.9f1**, 21,024,826 bytes, at file
  offset 7392. Contents: 11 model prefabs (`DefaultOrange`, `CatLightRed`, `FoxWolfBlue`,
  `RaccoonPurple`, `DragonBlack`, `DevilRed`, `FishLightBlue`, `DogGreen`, `BunnyPink`,
  `CowWhite`, `HorseYellow`), 11 `Body` skinned meshes (10.5k–23.6k verts, 61–86 bones),
  856 GameObjects, 496 MonoBehaviours, 4 MonoScripts.
* The 4 MonoScripts are the whole "physics" stack:
  `JigglePhysics.JiggleRigBuilder`, `JigglePhysics.JiggleSettings` (from
  **ModelReplacementAPI**, which vendors naelstrof's *UnityJigglePhysics*),
  `ModelReplacement.AvatarBodyUpdater.OffsetBuilder` and `RotationOffset` (MRAPI's skeleton
  remap). **No DynamicBone, no MagicaCloth, no Unity Cloth.** (README changelog v1.1.0:
  "Replaced Dynamic Bones with the more optimized and natively supported Unity Jiggle Bones" —
  "natively supported" = shipped inside MRAPI.)

### 1.1 Rig layout (per model, all 11 identical in structure)

```
<Model>                                  localScale 107.1–107.6  (export scale, uniform)
  Armature
    Hips                                 (humanoid pelvis bone)
      Root_Butt                          ← JiggleRigBuilder #1
        Left_butt  → Left_butt_1
        Right_butt → Right_butt_1
      Spine
        Chest
          Neck → Head                    ← Ear rigs on animal variants
          Root_Breasts                   ← JiggleRigBuilder #2
            Left_Breast_1  → Left_Breast_2
            Right_Breast_1 → Right_Breast_2
          Left shoulder → Left arm → Left elbow
  Body                                   SkinnedMeshRenderer, root bone = Armature/Hips
```

**[fact] Every bone in every model has `localScale = (1,1,1)`.** The thickness is *modelled into
the mesh*; nothing is scaled at runtime. So the "scale the bones" part of our feature has **no
precedent in the reference** — it is a technique we are choosing, not copying.

### 1.2 Jiggle wiring (exact, from the bundle's serialized components)

39 `JiggleRigBuilder` components over 11 models. Per model: one on `Root_Breasts`, one on
`Root_Butt`, plus ears/tail on animal variants. Common fields of all 39:
`ignoredTransforms = []`, `colliders = []`, `wind = (0,0,0)`, `levelOfDetail = null`,
`debugDraw = false`. The rig discovers bones by **recursing the subtree** (see 1.3), so one
component covers both left/right breast chains.

`JiggleSettings` ScriptableObjects baked in the bundle, referenced by the rigs:

| setting asset | used by | gravity | friction | angleElasticity | blend | airDrag | lengthElasticity | elasticitySoften | radius |
|---|---|---|---|---|---|---|---|---|---|
| `BreastJiggleSettings` | all 11 `Root_Breasts` | 0.10 | 0.40 | 0.60 | 1.0 | 0.01 | 0.10 | 0.10 | 0 |
| `SoftbodyJiggleSettings` | all 11 `Root_Butt` | 0.10 | 0.20 | 0.50 | 1.0 | 0.01 | 0.10 | 0.10 | 0 |
| `Tail Jiggle` | cat/dog/fox/horse/dragon tails | 0.10 | 0.40 | 0.40 | 0.65 | 0.01 | 0.0 | 0.0 | 0 |
| `Whale Tail Jiggle` | cow/devil/fish tails | 0.10 | 0.80 | 0.50 | 0.50 | 0.01 | 0.0 | 0.0 | 0 |
| `BunnyTail Jiggle` | bunny | 0.10 | 0.70 | 0.60 | 0.65 | 0.01 | 0.0 | 0.0 | 0 |
| `EarJiggle` | animal ears | 0.10 | 0.60 | 0.45 | 0.65 | 0.01 | 0.01 | 0.01 | 0 |

`radiusCurve` is the default `(0,1)→(1,0)` on all of them; `radiusMultiplier = 0` disables
collision entirely (hence `colliders = []`). Editing these two rows gives the "thicc" feel:
butt is deliberately *less* damped (friction 0.2) than breasts.

### 1.3 The jiggle algorithm (UnityJigglePhysics, vendored in ModelReplacementAPI 2.3.6)

`mrapi_decomp/JigglePhysics/*.cs` — ~1500 lines of plain C#, **no external dependencies**.

* `JiggleRigBuilder` (`[DefaultExecutionOrder(200)]`): fields `rootTransform`,
  `jiggleSettings`, `ignoredTransforms`, `colliders`, `wind`, `levelOfDetail`, `debugDraw`.
  `Initialize()` → `CreateSimulatedPoints` walks `rootTransform` depth-first and creates a
  `JiggleBone` per child transform, then appends one synthetic leaf `JiggleBone(null, parent)`
  for every leaf so the chain has a direction to aim at. `CalculateNormalizedIndex()` gives each
  point a 0..1 position along the chain (used for collision radius only).
* Update driver: `JiggleRigHandler` injects itself into Unity's **PlayerLoop at
  `PostLateUpdate`** (`PlayerLoop.GetCurrentPlayerLoop().InjectAt<PostLateUpdate>(…)`) and calls
  `builder.Advance(Time.deltaTime)` for every enabled rig. `Advance` accumulates real time and
  runs `UpdateJiggle` in `Time.fixedDeltaTime` steps, capped at
  `maxCatchupTime = fixedDeltaTime * 4`; then `Pose(data.blend)`.
* Integration (`JiggleBone.NextPhysicsPosition`) is Verlet with local-space velocity:
  `p' = p + (p - pPrev - localVel)*(1-airDrag) + localVel*(1-friction) + Physics.gravity*(gravityMultiplier*dt²)`,
  where `localVel = particleVel - parentParticleVel` (so it is *relative to the animated parent*
  — the rig follows the body without being dragged by it).
* Constraints, per step, in order: `ConstrainLengthBackwards` (only when colliders exist),
  `ConstraintPass` → `ConstrainAngle` + `ConstrainLength` (each a `Vector3.Lerp` toward the
  parent-relative rest direction/length with weight `elasticity * pow(clamp01(dist/len), elasticitySoften*2)`),
  optional `CollisionPass` (sphere vs. `Physics.ComputePenetration`).
* Write-back (`PoseBone(blend)`): the *simulated point* becomes the bone's **position**
  (`transform.position = Lerp(animated, simulated, blend)` for non-root) and the bone is
  **rotated** so that its child points along the simulated direction
  (`Quaternion.FromToRotation(childAnimated-childActual, childSim-childActual)`).
  Bone **scale is never touched**.
* Survival across model changes: nothing special — the component lives on the model prefab, so
  it is created/destroyed with the model instance. `MatchAnimationInstantly`,
  `PrepareTeleport`/`FinishTeleport`, `OnDisable→PrepareTeleport` exist to avoid explosions when
  a rig is disabled, teleported or re-enabled (LOD, pooling, view switching).
* `JiggleRigLOD` (ScriptableObject) can scale the settings with distance; unused here (null).

**[inference]** Porting cost if we want this exact behaviour: copy those 11 files into the hot
assembly, delete the `JiggleRigLOD`/`Collider` branches (or keep them; `UnityEngine.Physics` is
already referenced by the mod) and keep the PlayerLoop injection. No Lethal-Company-specific
API is used. It compiles against plain UnityEngine + `Il2CppSystem`/`Il2CppInterop` types, with
one caveat: under IL2CPP, `Transform` is an interop wrapper — all of this code is normal Unity
API surface, so it should work unchanged (it is the same calls the mod already makes).

---

## 2. What Alterum gives us

### 2.1 The model table (static, from `Alterum_Data/sharedassets0.assets`)

`PlayerModelData` assets → prefabs, resolved by scanning each asset's raw bytes for the
`ThirdViewPlayerModel` / `FirstViewPlayerModel` component path-ids **[fact]**:

| PlayerModelData asset | 3P prefab | 1P (hands) prefab | breast bones | MagicaCloth breast |
|---|---|---|---|---|
| `Female_1_Emma_PlayerModelData` | `3P_ConcreteModel_Female_01` | `1P_ConcreteModel_Female` | `Chest_01_l/r` → `Chest_02_l/r` | yes |
| `Female_2_Freya_PlayerModelData` | `3P_ConcreteModel_Female_02` | `1P_ConcreteModel_Female` | `Chest_01_l/r` → `Chest_02_l/r` | yes |
| `Female_3_Diana_PlayerModelData` | `3P_ConcreteModel_Female_03_Diana` | `1P_ConcreteModel_Female_Black` | `chest_01_l/r` → `chest_02_l/r` | yes |
| `Female_4_Kiona_PlayerModelData` | `3P_ConcreteModel_Female_04_Kiona` | `1P_ConcreteModel_Female` | `chest_01_l/r` → `chest_02_l/r` | yes |
| `Female_5_Sarah_PlayerModelData` | `3P_ConcreteModel_Female_05_Sarah` | `1P_ConcreteModel_Female` | `chest_01_l/r` → `chest_02_l/r` | yes |
| `Male_1_Leo_PlayerModelData` | `3P_ConcreteModel_Male_01` | `1P_ConcreteModel_Male` | **none** | no |
| `Male_2_Thomas_PlayerModelData` | `3P_ConcreteModel_Male_02` | `1P_ConcreteModel_Male` | **none** | no |
| `Male_3_Alex_PlayerModelData` | `3P_ConcreteModel_Male_03` | `1P_ConcreteModel_Male` | **none** | no |
| `Male_4_James_PlayerModelData` | `3P_ConcreteModel_Male_04_James` | `1P_ConcreteModel_Male` | **none** | no |
| `Male_5_Ryan_PlayerModelData` | `3P_ConcreteModel_Male_05_Ryan` | `1P_ConcreteModel_Male_Black` | **none** | no |

Consequences for the feature as specified:

1. **"breast bones of ALL player models" is only possible on the 5 female models.** The male
   rigs have no chest/breast bones at all (no bone named `chest*`/`breast*`; verified by
   walking all 10 skeletons). Nothing to scale; adding bones would require editing skinned
   meshes (bindposes + weights), i.e. an asset pipeline, not a mod.
2. **There is no hip/butt bone either.** No bone named `hip*`, `butt*`, `glute*` on any model.
   The only hip-ish bones are `pelvis` (humanoid root of the body, parent of `spine_01` and both
   `thigh_*`) and `thigh_l/r`. Scaling `pelvis` scales the **whole body** (children inherit
   scale), so "wider hips" means `pelvis.localScale = s` **plus** counter-scaling its children
   (`spine_01`, `thigh_l`, `thigh_r`, and the helper holders `Carriable Interactable Model
   Parent`, `Lantern Holder`) to `1/s`. That widens the leg attachment points without inflating
   the torso.
3. **Bone names differ in case between rig generations**: `Chest_01_l` (Emma, Freya) vs
   `chest_01_l` (Diana, Kiona, Sarah). Match case-insensitively.
4. **Exact paths** — two rig generations, otherwise identical:
   Diana / Kiona / Sarah:
   `root/pelvis/spine_01/spine_02/spine_03/spine_04/chest_01_l/chest_02_l`
   `root/pelvis/spine_01/spine_02/spine_03/spine_04/chest_01_r/chest_02_r`
   Emma / Freya (no `spine_03`, no `spine_04`):
   `root/pelvis/spine_01/spine_02/Chest_01_l/Chest_02_l`
   `root/pelvis/spine_01/spine_02/Chest_01_r/Chest_02_r`
   hips on all ten models: `root/pelvis` (children `spine_01`, `thigh_l`, `thigh_r`).
   All `localScale = 1` in the shipped assets **[fact]**.
5. Those chest bones are really skinned: `Diana_Optimized_Body` (64 bones) and the clothing
   meshes (`Diana_Pants_full`, `Jane_Undershirt`, `Diana_Belt`, `Diana_Pouch`) all list
   `chest_01_l/02_l/01_r/02_r` and `pelvis/thigh_l/thigh_r` in their bone arrays **[fact]**.
   So scaling them deforms body **and** clothes.
6. **First-person models have no chest bones.** `1P_ConcreteModel_*` is a hands-only prefab:
   74 transforms total, 62 under `root` (`pelvis`, `spine_01..03`, `thigh/calf/foot` +
   `*_twist_*`, hitbox and holder objects) and exactly one SkinnedMeshRenderer
   (`Hands_08_Mesh`). Nothing to scale, nothing visible. → **first-person needs no work**
   (and the existing `SwapFirstView` config is therefore also moot once the swap goes).

### 2.2 Alterum already ships MagicaCloth2 — including breast jiggle

**[fact]** `Alterum_Data` is Unity **6000.3.16f1**; `BepInEx/interop/MagicaClothV2.dll` is an
Il2CppInterop proxy for `MagicaCloth2.*` (namespace `MagicaCloth2`, 368 types), i.e. the plugin
is compiled into `GameAssembly.dll` and fully callable from a mod.

Every 3P prefab has a cloth root:

```
<Model>/Magica Cloth                     [MonoBehaviour: GObject.SafeGameObject]
   Breast                                [MonoBehaviour: MagicaCloth]      ← 5/5 female models
   Hair                                  [MonoBehaviour: MagicaCloth]      ← Emma/Kiona/Sarah/Freya
   Bang / Skirt / Raincoat               [MonoBehaviour: MagicaCloth]      ← per model
```

`GObject.SafeGameObject` = ref-counted active-state holder
(`_enableOnDefault`, `_keys`, `TrySetActive(Object key, bool)`, `ApplyActiveState`). It is the
game's mechanism for keeping cloth alive while the model `GameObject` gets toggled — exposed on
the model as `ThirdViewPlayerModel._magicaClothSafeGameObject` /
`get_MagicaClothSafeGameObject`, with an extra `_currentMagicaClothSafeGo` field.

Component class per child GameObject came from parsing the MonoBehaviour header
(PPtr + `m_Enabled` + PPtr + name) and resolving the `MonoScript` in
`globalgamemanagers.assets` — a built player has no type trees, so the usual UnityPy
`read()` returns nothing for these.

**What the `Breast` component is** — [inference, high confidence] a **BoneCloth** rig, not a
mesh cloth: its serialized data contains `rootBones` PPtrs at the very front
(`chest_01_l`, `chest_01_r` for all five models) and **no Renderer PPtr of any kind** anywhere
in the 2396-byte payload (checked against all 273 renderers in the file), whereas the
hair/skirt/raincoat components do contain their `SkinnedMeshRenderer` PPtr at offset 44
(`Hair_1_Cutted`, `Skirt`, `Raincoat`, `Hair_full`) plus a `Mesh` reference. A second PPtr block
in the breast payload lists exactly `chest_01_r, chest_02_r, chest_01_l, chest_02_l` (the
collision-bone list). So:

* the game already simulates the 2-bone breast chains (same shape as the reference mod's
  `Left_Breast_1→2`),
* the cloth drives those bone transforms, so its output is *added on top of* animation,
* **bone scale is untouched by MagicaCloth** → scaling `chest_02_l/r` (the leaf) grows the
  breast volume *without invalidating the cloth's rest lengths*, because the simulated chain
  points (`chest_01_l`, `chest_02_l` origins) do not move. Scaling `chest_01_l/r` (the root)
  *does* move `chest_02_*` → the virtual mesh's rest lengths would be stale and the cloth would
  fight the new pose until rebuilt.

The exact numeric tuning of the shipped `Breast` cloth **could not be decoded statically**
(`ClothSerializeData` has no type tree in the build; hand-decoding ~2.4 kB of nested structs
with variable-length lists is not reliable). It is trivially readable at runtime instead:
`magicaCloth.serializeData.gravity / .damping / .radius /
.angleRestorationConstraint.stiffness / .angleLimitConstraint.limitAngle / .blendWeight`.

### 2.3 Is MagicaCloth a viable jiggle path?

**Yes, and for the 5 female models it is already done — reuse, don't add.** Available API on
`MagicaCloth2.MagicaCloth` (from the interop assembly):

```
serializeData (ClothSerializeData), serializeData2, process
Initialize(), BuildAndRun(), DisableAutoBuild(), SetParameterChange(), ResetCloth(bool),
SetTimeScale(float), AddForce(Vector3, float, ClothForceMode), SetSkipWriting(bool),
ReplaceTransform(Il2CppSystem.Collections.Generic.Dictionary<string, Transform>),
GetUsedTransform() → HashSet<Transform>, GetCenterPosition(),
GetOriginalMesh(Renderer), GetCustomMesh(Renderer), GetCustomBones(Renderer)
```

```
ClothSerializeData: clothType (ClothType.MeshCloth|BoneCloth), sourceRenderers, rootBones,
  connectionMode, updateMode, animationPoseRatio, gravity, gravityDirection, gravityFalloff,
  damping, radius, blendWeight, stablizationTimeAfterReset,
  angleRestorationConstraint{useAngleRestoration, stiffness, velocityAttenuation, gravityFalloff},
  angleLimitConstraint{useAngleLimit, limitAngle, stiffness},
  inertiaConstraint, tetherConstraint, distanceConstraint, triangleBendingConstraint,
  motionConstraint, colliderCollisionConstraint, selfCollisionConstraint,
  colliderList, collisionBones, cullingSettings, reductionSetting, customSkinningSetting,
  normalAlignmentSetting   (+ public ctor)
```

Costs / caveats if we *add* cloth (e.g. hip cloth on males, or on `pelvis`):

* MagicaCloth2 builds its virtual mesh asynchronously (`RuntimeBuildAsync`, Burst jobs) the
  first time the component is enabled; a fresh BoneCloth on 2–4 bones is milliseconds, but it
  happens on the frame the model spawns. `DisableAutoBuild()` + `BuildAndRun()` gives control.
* BoneCloth needs a valid root-bone hierarchy and (MagicaCloth2 requirement) the root bone's
  parent; MeshCloth needs a **readable** mesh (`Mesh.isReadable`) — built-in body meshes are
  not necessarily readable, and `GetOriginalMesh` implies the game keeps renderer meshes around.
* Any cloth we add must sit under a `SafeGameObject`-style holder, otherwise the game's
  rendering-controller hides (`RenderersRenderingController.DisableRenderers`, already
  documented in `MirrorPlayerVisibility.cs`) will disable it and MagicaCloth will rebuild on
  every re-show.
* `ReplaceTransform(Dictionary<string,Transform>)` exists precisely for model swapping — it can
  re-point a cloth at a new instance's bones, which is how we would survive
  `PlayerModelSetuper.SetupModel` replacing a model without a rebuild.
* Don't stack a spring *and* MagicaCloth on the same bones: both write bone rotation each frame.

**Recommendation, in order of risk:**
1. Scale bones only (`chest_02_*` for volume, `pelvis`+counter-scale for hips). Zero new
   physics; the shipped breast cloth then jiggles a bigger breast for free. Cheapest and most
   likely to look right.
2. If more bounce is wanted: nudge the existing `MagicaCloth.serializeData` (lower
   `angleRestorationConstraint.stiffness`, higher `damping`/`inertiaConstraint`) +
   `SetParameterChange()`/`ResetCloth()` on the 5 existing breast rigs.
3. Only if (1)+(2) is not enough, port UnityJigglePhysics (~1500 lines, MIT, no deps) into the
   hot assembly and drive `chest_02_*`/`pelvis` with it — but then disable/neutralise the
   shipped `Breast` MagicaCloth on those bones or they will fight. This is the only route that
   matches the reference mod's feel exactly, and it is the only route that works for **males**
   (no bones → still impossible without mesh editing).

### 2.4 Where to hook (concrete Alterum types)

| thing | type (namespace) | notes |
|---|---|---|
| per-player model swap / spawn | `Gameplay.PlayerSystem.PlayerModelSetuper` | `public void SetupModel(PlayerModelData)`; also public events `ModelSetup` (`Action<PlayerModelData,PlayerModelData>` old→new), `ModelDespawned`, `BeforeModelDespawn`; fields `_firstModelSetuper`, `_thirdModelSetuper`, `_playerModelData`, `_isSetup`. **Our mod already Harmony-patches `SetupModel` as a postfix** (`MirrorPlayerVisibility.InstallPatches`) → the bone pass belongs there (drop the `Under(instance.transform, _owner)` filter to cover *all* players). Subscribing to `ModelSetup` is the non-Harmony alternative. |
| 3P model instance | `Gameplay.PlayerSystem.ThirdViewPlayerModel` | fields include `_bones`, `_characterCustomizationController`, `_hitboxes`, `_magicaClothSafeGameObject`, property `MagicaClothSafeGameObject`; base `PlayerModel` owns `_rootGrabber`, `_meshesGrabber`, `_multiRenderingController` |
| bone access | `ThirdViewPlayerModelBones : PlayerModelBones` | `_root,_pelvis,_leftWrist,_rightWrist,_neck,_head` + **`_allBones : List<Transform>`** + `AllBones` property. Diana's list has 111 entries and *does* contain `pelvis`, `thigh_l/r`, `chest_01_l/02_l/01_r/02_r` → no hierarchy walk needed, just filter by name. |
| 1P model | `FirstViewPlayerModel`, `FirstViewPlayerModelBones` | `_bones` only (root/pelvis/wrists); nothing to scale |
| model catalog | `PlayerModelData` | `_gender`, `_modelTypeData`, `_modelNameLocalizedString`, `_bustPortraitSprite`, `_portraitLayerSet`, `_wardrobeCameraData`, `_firstViewPlayerModel`, `_thirdViewPlayerModel`, `_index`, `_isInitialized` — the swap writes four of these |
| keeping helpers alive | `GObject.SafeGameObject` | `TrySetActive(key, bool)` |
| renderer hiding (conflict to respect) | `RenderingController` / `RenderersRenderingController` | already documented in `MirrorPlayerVisibility.cs` |
| ragdolls (risk) | `Gameplay.Ragdolls.ThirdViewPlayerModelRagdollInitializator`, `BonesPositionsRagdollActiveStateHandler`, `MeshBonesSwitcher` | **[not investigated]** they re-pose/copy bones; a scaled/jiggled skeleton in a ragdoll may need its own handling |

### 2.5 Removing the Diana→Freya swap

All of it is in `tracks/bones/mod/` (`= mod/`): the swap is *only* `DumpAndSwap()` plus its
config plumbing. Nothing else reads `Swap*`, and `Mirror*`/`ScreenShot` are unaffected.

1. `AlterumModelSwap/Plugin.cs`
   * L23–27 — delete the five field declarations
     (`SwapEnabled`, `SourceContains`, `TargetContains`, `SwapFirstView`, `SwapPortrait`).
   * L95–99 — delete the five `_cfgFile.Bind("Swap", …)` lines (config section `[Swap]`
     disappears with them).
   * L201–205 — delete the five `ConfigSnapshot` initialisers
     (`SwapEnabled`, `SourceContains`, `TargetContains`, `SwapFirstView`, `SwapPortrait`).
2. `AlterumModelSwap.Contracts/Contracts.cs` — L11–16, the `// Swap` block in `ConfigSnapshot`
   (`SwapEnabled`, `SourceContains`, `TargetContains`, `SwapFirstView`, `SwapPortrait`).
   Contracts is shared code: per `tracks/README.md` rule 4, copy the rebuilt
   `AlterumModelSwap*.dll` into the track's `plugins/` and restart the install.
3. `AlterumModelSwap.Hot/ModelSwapRuntime.cs`
   * L108 — the call site `if (!_dumpedData && _t > 3f) _dumpedData = DumpAndSwap();`
     and L55 `private bool _dumpedData;`.
   * L941–1015 — `DumpAndSwap()`. Two separable pieces:
     - L945–967: the `PlayerModelData` dump (diagnostics only, keeps working without swap).
     - L969–1013: **the actual swap** — find `src`/`tgt` by name substring, then
       `src._thirdViewPlayerModel = tgt._thirdViewPlayerModel` (L986–988),
       `src._firstViewPlayerModel = tgt._firstViewPlayerModel` (L996–998),
       `src._portraitLayerSet = tgt._portraitLayerSet` and
       `src._bustPortraitSprite = tgt._bustPortraitSprite` (L1007–1008).
     Suggested end state: keep a renamed dump-only method (or drop it), delete the rest, and
     leave the file with no reference to `_cfg.Swap*`.
4. Nothing to delete in `AlterumModelSwap.Hot/Mirror.cs`, `MirrorPlayerVisibility.cs`,
   `ScreenShot.cs`, `HotModule.cs`, `AlterumModelSwap/HotLoader.cs`, `PumpBehaviour.cs`.
5. Leftover `BepInEx/config/com.user0.alterum.modelswap*.cfg` files keep a stale `[Swap]`
   section afterwards — harmless (BepInEx ignores unknown keys), but worth noting.

Also relevant to the feature: `DumpAndSwap`/`RenderWardrobeSlots` already prove the wardrobe
keeps **every** `ThirdViewPlayerModel` instantiated under
`Lobby Global/Wardrobe/Wardrobe Scene/Models`, which is a ready-made way to eyeball all ten
models at once (no gameplay needed).

---

## 3. Things I could not determine statically

* Numeric parameters of the shipped `MagicaCloth` components (no type tree in the player
  build). Read them at runtime from `MagicaCloth.serializeData`.
* Whether `Chest_01/Chest_02` are *animated* by the humanoid clips (they are in the skeleton
  and in `_allBones`, but I did not decode `AnimationClip` curves). If they are animated, a
  spring/MagicaCloth writes on top of that (that is how BoneCloth works anyway).
* Whether the body/clothing meshes are `isReadable` (decides whether we could ever attach a
  *MeshCloth* to the hip region, as opposed to BoneCloth).
* Runtime consequences for ragdolls/corpses: `ThirdViewPlayerModelRagdollInitializator`,
  `MeshBonesSwitcher`, `Gameplay.Corpse.CorpseModelSetuper` were listed but not studied.
* Whether `pelvis`-scale + counter-scaled children survives the game's own renderer-hiding and
  wardrobe preview pipelines (needs a real run; the mirror harness already exists for that).

## 4. Reproduction

```bash
# reference package
curl -s "https://thunderstore.io/api/experimental/package/FoxGod/ThiccCompanyModelReplacement/"
curl -sL -o tc.zip "https://thunderstore.io/package/download/FoxGod/ThiccCompanyModelReplacement/2.0.1/"
unzip -o tc.zip -d tc
export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH
ilspycmd -p -o tc_decomp tc/ThiccCompany2.0.dll

# the bundle is the DLL's embedded resource: carve from the "UnityFS" magic to size
python3 - <<'PY'
import struct
d=open('tc/ThiccCompany2.0.dll','rb').read(); off=d.index(b'UnityFS')
i=off+8; ver=struct.unpack('>I',d[i:i+4])[0]; i+=4
for _ in range(2): i=d.index(b'\0',i)+1
size=struct.unpack('>q',d[i:i+8])[0]
open('thicc.bundle','wb').write(d[off:off+size])
PY

# jiggle engine
curl -sL -o mrapi.zip "https://thunderstore.io/package/download/BunyaPineTree/ModelReplacementAPI/2.3.6/"
unzip -o mrapi.zip -d mrapi && ilspycmd -p -o mrapi_decomp mrapi/ModelReplacementAPI.dll

# game side
AC="/mnt/c/Program Files (x86)/Steam/steamapps/common/Alterum/BepInEx/interop/Assembly-CSharp.dll"
ilspycmd -l c "$AC" > acsharp/classes.txt
ilspycmd -t Gameplay.PlayerSystem.ThirdViewPlayerModel "$AC" > interop/ThirdViewPlayerModel.cs
python3 explore_shared.py        # UnityPy over sharedassets0.assets + globalgamemanagers.assets
```
