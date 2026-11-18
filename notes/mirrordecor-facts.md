# MirrorDecor / Alterum read-only reconnaissance

## 3. Alterum hook candidates (investigated first)

### Sources, method, and important evidence limit

Primary inputs:
- **A**: `/mnt/c/Program Files (x86)/Steam/steamapps/common/Alterum/BepInEx/interop/Assembly-CSharp.dll` (16,048,640 bytes).
- **M**: `alterum/tracks/merged/mod/AlterumModelSwap.Hot/Mirror.cs` (read only; existing code is not proof that its diagnosis is correct).
- **G**: `/mnt/c/Program Files (x86)/Steam/steamapps/common/Alterum/Alterum_Data/globalgamemanagers`, `TagManager`, read with UnityPy.

Environment: `DOTNET_ROOT=$HOME/.dotnet`; PATH prefixed with `$HOME/.dotnet:$HOME/.dotnet/tools`; ilspycmd 8.2.0.7535, UnityPy 1.25.4. No build, game launch/termination, runtime attachment, or mod-code edit was performed. Decompiler output used disposable RAM-backed `/dev/shm/mirrordecor-recon/`; this notes file is the sole retained output.

First command was `ilspycmd -p -o /dev/shm/mirrordecor-recon/alterum <A>`. It aborted with exit 70 after writing 1,081 files: `Error decompiling for 'InputSystem/ControlsStateMachine.cs'`, `System.ArgumentOutOfRangeException`, in `TypeSystemAstBuilder.ConvertTypeHelper`. Used `ilspycmd -l c <A>` followed by `ilspycmd -t <Full.Type.Name> <A>` for every candidate below. Generic names require metadata arity: `'Gameplay.PlayerSystem.PlayerModelInstantiator\u00601'` and `'Gameplay.PlayerSystem.PlayerModelInstantiatorInitializable\u00601'` (the `\u0060` here means a literal backtick). Without the backtick/1 ILSpy cannot find these types.

**Critical limitation:** A is an **Il2CppInterop wrapper assembly**, not the original managed gameplay implementation. Gameplay method bodies call `IL2CPP.il2cpp_runtime_invoke(...)`; their native logic is absent. This decompile establishes actual type names, signatures, field types, inheritance, and native metadata tokens. It does **not** establish whether native code writes `Renderer.enabled`, `Renderer.forceRenderingOff`, layers, or materials; nor does it establish an every-frame call chain. Statements about nine disabled skinned renderers / per-frame disabling are supplied task context, not independently confirmed by this static inspection. Do not mistake `CallerCount(0)` for a method never executing.

References such as `RenderersRenderingController.cs:95` below are line numbers in **the targeted `-t` output**, before ILSpy's update notice. Key excerpts are embedded below so they survive deletion of scratch output.

### Candidate types and exact patch targets

All types in this table are in namespace **`Gameplay.PlayerSystem`**, except the explicitly qualified camera types. Wrapper declarations include `unsafe`; Harmony target selection uses declaring type, method name, and parameter types, not `unsafe`. Several originally protected methods are exposed publicly by the wrapper: metadata field names retain `Protected`, as noted.

| Full type / inheritance | Exact method signature(s) / native metadata token | What the decompile establishes; hiding mechanism |
|---|---|---|
| `Gameplay.PlayerSystem.RenderersRenderingController : RenderingController` | `public unsafe virtual void DisableRenderers()` — **100683022 / 0x06004D0E**; `public unsafe override void OnDisableRendering()` — 100683021; inverse `public unsafe virtual void RestoreRenderers()` — 100683023 and `OnEnableRendering()` — 100683020 | Strongest named final renderer-disabling seam. `Queue<UnityEngine.Renderer> _cachedActiveModelRenderers` and `virtual bool TryGetRenderers(out Il2CppReferenceArray<UnityEngine.Renderer> modelRenderers)` (100683019). `DisableRenderers` / `RestoreRenderers` were protected virtual in native metadata. **Actual property writes unknown**. Sources: `RenderersRenderingController.cs:13-54,58-114`. |
| `Gameplay.PlayerSystem.PlayerModelRenderersRenderingController : RenderersRenderingController` | **Inherits `DisableRenderers()`; does not declare its own disabling override**. Declares `public unsafe override bool TryGetRenderers(out Il2CppReferenceArray<UnityEngine.Renderer> modelRenderers)` — 100682990; `public unsafe virtual void OnModelSetuped(PlayerModelSetuper modelSetuper, PlayerModelData previousModelData, PlayerModelData newModelData)` — 100682988; `public unsafe virtual void OnModelDespawned(PlayerModelSetuper modelSetuper)` — 100682989 | Model-specific renderer collection, exposes `ComponentsGrabber _renderComponentsGrabber` and `Il2CppReferenceArray<Renderer> _cachedModelRenderers`. Useful for identifying which instance owns the local body's renderer set. **Does not prove renderer count, writes, or visibility state.** Sources: `PlayerModelRenderersRenderingController.cs:13-59,93-149`. |
| `Gameplay.PlayerSystem.ProxyRenderingController : RenderingController` (sealed) | `public unsafe override void OnDisableRendering()` — **100683016**; `public unsafe override void OnEnableRendering()` — 100683015; `public unsafe void Initialize(RenderingController renderingController)` — 100683013; `public unsafe void Deinitialize()` — 100683014 | Exposes `RenderingController _renderingController` and `bool IsInitialized` / `_IsInitialized_k__BackingField`. Likely forwards rendering changes to its assigned target, but native forwarding call itself is opaque. Both callbacks originally protected virtual. No direct renderer array here. **Hiding implementation unknown.** Sources: `ProxyRenderingController.cs:10-28,45-58,85-144`. |
| `Gameplay.PlayerSystem.FirstViewProxyRenderingControllerInitializable : PlayerModelInstantiatorInitializable<FirstViewPlayerModel>` | `public unsafe override void OnInitialize(FirstViewPlayerModel playerModel)` — **100682797**; `public unsafe override void OnDeinitialize(FirstViewPlayerModel playerModel)` — 100682798 | Exposes `ProxyRenderingController _proxyRenderingController`, `Animancer.StringAsset _renderingControllerName`, `string _cachedRenderingControllerName`. **Initialization/binding seam, not a proven renderer-disabling method.** Original callbacks protected virtual. Wrapper does not show `Update`, `LateUpdate`, `OnDisable`, or `OnEnable` in this class. Merely setting this MonoBehaviour's `enabled=false` is not evidence that an already-bound proxy was deinitialized. Sources: `FirstViewProxyRenderingControllerInitializable.cs:11-23,25-67,69-103`. |
| `Gameplay.PlayerSystem.ThirdViewProxyRenderingControllerInitializable : PlayerModelInstantiatorInitializable<ThirdViewPlayerModel>` | `public unsafe override void OnInitialize(ThirdViewPlayerModel playerModel)` — **100682881**; `public unsafe override void OnDeinitialize(ThirdViewPlayerModel playerModel)` — 100682882 | Same three proxy/name fields as first-view initializer, but takes the third-person body model. Do **not** assume the first-view-named initializer controls the third-person body just from its name. Inspect both sides' actual proxy assignments in future runtime diagnostics. **No property writes visible.** Sources: `ThirdViewProxyRenderingControllerInitializable.cs:11-23,25-67,69-103`. |
| `Gameplay.PlayerSystem.RenderingController : UnityEngine.MonoBehaviour` | `public unsafe void DisableRendering(Il2CppSystem.Object key)` — **100683036**; `public unsafe void EnableRendering(Il2CppSystem.Object key)` — 100683035; `public unsafe void ForceUpdateRenderingState()` — 100683037; `public unsafe virtual void OnDisableRendering()` — 100683033 | Exposes `Il2CppSystem.Collections.Generic.HashSet<Il2CppSystem.Object> _disableKeys`, `bool IsRenderingEnabled`, events `RenderingEnabled`, `RenderingDisabled`, `RenderingStateChanged`. **Key-based visibility gate exists at interface/data level.** Native rules/event order hidden. Skipping the leaf callback versus preventing insertion of the local-view disable key are different interventions. Sources: `RenderingController.cs:13-53,100-129,235-300`. |
| `Gameplay.PlayerSystem.MultiRenderingController : UnityEngine.MonoBehaviour` | `public unsafe void DisableRendering(Il2CppSystem.Object key)` — **100682918**; `public unsafe void EnableRendering(Il2CppSystem.Object key)` — 100682917; `public unsafe bool TryGetSubRenderingController(string renderingControllerName, out RenderingController renderingController)` — 100682916 | Exposes a name→`RenderingController` dictionary (`_subRenderingControllers`, `SubRenderingControllers`) and list `_subRenderingControllersRefs`; each entry has `Animancer.StringAsset RenderingControllerName` and `RenderingController RenderingController`. Provides a way to find the mesh controller without guessing the serialized string name. **No native fan-out body visible.** Sources: `MultiRenderingController.cs:14-55,88-129,149-189`. |
| `Cameras.SwitchActionProcessors.PlayerModelDisplayer : PlayerPOVControllerProcessor` (namespace `Cameras.SwitchActionProcessors`) | `public unsafe override void OnEnableFirstPersonViewModeInternal()` — **100693291**; `public unsafe override void OnEnableThirdPersonViewModeInternal()` — 100693292; `public unsafe virtual void OnModelSetuped(PlayerModelSetuper modelSetuper, PlayerModelData previousModelData, PlayerModelData newModelData)` — 100693290 | Exposes `PlayerModelReferences _modelReferences`, `MultiRenderingController FirstPersonRenderingController`, `MultiRenderingController ThirdPersonRenderingController`. **Most semantically specific first-/third-person switching seam**, but switching logic opaque. Both internal callbacks originally protected virtual. Sources: `Cameras.SwitchActionProcessors.PlayerModelDisplayer.cs:11-29,31-103,119-152`. |
| `Gameplay.PlayerSystem.ModelPersonAnimancerController : UnityEngine.MonoBehaviour` | `public unsafe void DisableAnimancerAndAnimator(Il2CppSystem.Object key)` — **100682629**; inverse is spelled **`public unsafe void EnabledAnimancerAndAnimator(Il2CppSystem.Object key)`** — 100682630; `public unsafe void OnAnimancerAndAnimatorEnabledStateChanged(bool previousValue, bool newValue)` — 100682634; `public unsafe void UpdateAnimancerAndAnimatorEnabledState()` — 100682635; `public unsafe void Initialize(PlayerModel playerModel)` — 100682627 | Animation/Animator enable state is separately managed by `SettableSafeVariableWrapper<bool> _isAnimancerAndAnimatorEnabledSafeSettable`, `bool IsAnimancerAndAnimatorEnabled`, `CurrentPersonAnimancerComponent`, and enabled/disabled events. **Not a demonstrated renderer visibility controller**. Could matter to a body frozen/not skinning after visibility is fixed; do not equate Animator disable with Renderer disable. Sources: `ModelPersonAnimancerController.cs:16-80,172-200,232-280,417-461,499-521`. |
| `Gameplay.PlayerSystem.PlayerModelPersonAnimancerController : ModelPersonAnimancerController` | `public unsafe virtual void OnPlayerCreated(Player player)` — 100682662 | Adds player-create integration and `GrabProcessorsOnAwake`, not a renderer-hiding override. Source: `PlayerModelPersonAnimancerController.cs:9-19,49-87`. |
| `Gameplay.PlayerSystem.ThirdViewPlayerModel : PlayerModel` | No declared renderer-disabling method. `public unsafe void AttachToModel(UnityEngine.GameObject objToAttachToModel)` — 100683075 | The **body model component**, not the controller that hides it. Its inherited `MeshesGrabber`, `RootGrabber`, and `MultiRenderingController` identify subtrees/controllers. Contains bones, character customization, cloth, hitbox and other body data; no declared `Update` or layer-hiding callback. Sources: `ThirdViewPlayerModel.cs:17-85,601-611`; `PlayerModel.cs:14-48,125-168,245-289`. |

**Token caution:** tokens above are Il2CppInterop's `GetIl2CppMethodByToken` arguments (native/original metadata lookup), not necessarily reflection tokens of the generated wrapper DLL. The exact declaring type + signature is the Harmony target. Verify support for patching these native-backed methods in the existing BepInEx/Harmony IL2CPP environment; a managed transpiler cannot recover the absent native method body.

### How hiding works — what is and is not established

- **Confirmed statically:** there is a renderer-specific disable/restore class; it holds a queue of previously active renderers. There are separate keyed rendering controllers, named subcontrollers, proxies, model-instantiation initializers, POV switching processors, and animation enable/disable controllers.
- **Not confirmed from A:** `Renderer.enabled = false` versus `forceRenderingOff = true` versus layer/material/shader changes inside any gameplay method. No user-provided runtime observation was re-measured. A queue named `_cachedActiveModelRenderers` is consistent with enabled-state caching but does not prove it.
- **Layer trick alone is not sufficient IF the reported Renderer.enabled=false state is accurate.** A camera culling mask cannot make a disabled renderer draw. A controller interception or equivalent correct state restoration is needed in addition to a mirror-only layer. Even then, this static evidence does not explain why immediate pre-`Camera.Render()` restoration failed; additional possible blockers include a later native visibility operation, animator/skinning state, LOD, render-pipeline behavior, or culling. These are hypotheses, not findings.
- A generic/global skip of `DisableRenderers()` or `DisableRendering(key)` risks showing other players/first-person arms/dead or intentionally hidden models. Any experiment should scope by local owner and actual body renderer/controller identity, preserve unrelated disable keys, and retain main-camera layer exclusion.
- The provided `Mirror.cs` searches components beneath the body for a name containing `FirstViewProxy` and sets `MonoBehaviour.enabled=false`. The candidate of that name is an **initializer**; its exposed interface has no per-frame renderer method. Disabling it after initialization need not undo references held by `ProxyRenderingController` elsewhere. This explains why that particular attempt is not a reliable interception, without claiming it is the proven failure cause.

### Where the local body lives / exposed component path

The decompile provides the following structural access path (not a claim about the current live object names or transforms):

```text
local Gameplay.PlayerSystem.Player (Unity.Netcode.NetworkBehaviour)
  GetRefs<Gameplay.PlayerSystem.PlayerModelReferences>()
    .ModelMainObject                      : PlayerModelMainObject
      .ModelSetuper                       : PlayerModelSetuper
        .ThirdModelSetuper                : ThirdViewPlayerModelSetuper
          .PlayerModel                    : ThirdViewPlayerModel (inherited generic property)
            .gameObject                   : actual instantiated third-person body root
            .MeshesGrabber                : ChildrenComponentsGrabber
            .RootGrabber                  : ChildrenComponentsGrabber
            .MultiRenderingController     : MultiRenderingController
          .ModelParent                    : Transform (actual spawn parent)
```

Exposed raw fields for the same chain: `PlayerModelReferences._modelMainObject`; `PlayerModelMainObject._modelSetuper`; `PlayerModelSetuper._thirdModelSetuper`; `PlayerModelInstantiator<ThirdViewPlayerModel>._playerModel`, `_modelParent`. The instantiator also exposes `_initializablesParent`, `_initializables`, `PlayerModelInitialized`/`PlayerModelDeinitialized` events.

Related direct handles on `PlayerModelReferences`:
- `.ThirdPersonModelRenderingController` / `._thirdPersonModelRenderingController` — aggregate third-person controller.
- `.FirstPersonModelRenderingController` / `._firstPersonModelRenderingController` — separate first-person aggregate.
- `.ThirdPlayerModelPersonAnimancerController` / `._thirdPlayerModelPersonAnimancerController` — separate third-person animation controller.
- `.PlayerPOVController` / `._playerPOVController`.

Sources: `Player.cs:16,166-179,400-409`; `PlayerModelReferences.cs:19,81-94,141-229,291-304,351-439`; `PlayerModelMainObject.cs:27-40,72-85`; `PlayerModelSetuper.cs:114-143,230-258`; `ThirdViewPlayerModelSetuper.cs:9`; `PlayerModelInstantiator\u00601.cs:182-240,267-296`.

Distinguish **instance** from **prefab**: `PlayerModelData.ThirdViewPlayerModel` (and `_thirdViewPlayerModel`) is on a ScriptableObject model-data asset, not evidence of the instantiated local body. Prefer `ThirdModelSetuper.PlayerModel`.

M currently finds a scene-backed `ThirdViewPlayerModel` whose ancestor (up to 8 levels) has a name containing `Owner: 0`; this is an existing heuristic, **not a reliable general ownership API or an independently observed live hierarchy**. Source M, `MakeLocalPlayerVisible`. A `Player` is a Netcode `NetworkBehaviour`; ownership should be established through the actual local player/Netcode ownership, rather than assuming client ID zero is local. No process was inspected to obtain the exact live GameObject path, transform, number of renderers, or active/LOD state.

Spawn/setup hooks for an LC-style setup intervention:
- `Gameplay.PlayerSystem.PlayerModelSetuper.SetupThirdViewModel(PlayerModelData modelData)` (100683005), and `SetupModel(PlayerModelData modelData)` (100683003); wrappers `PlayerModelSetuper.cs:402-436`.
- `Gameplay.PlayerSystem.PlayerModelInstantiator<ThirdViewPlayerModel>.InstantiateAndInitialize(ThirdViewPlayerModel modePrefab)` (100682966; argument really is spelled `modePrefab` in output), `PlayerModelInstantiator\u00601.cs:402-424`.
- `Gameplay.PlayerSystem.ThirdViewProxyRenderingControllerInitializable.OnInitialize(ThirdViewPlayerModel playerModel)` / first-view counterpart above.
- `Gameplay.PlayerSystem.PlayerModelRenderersRenderingController.OnModelSetuped(...)` above.

A setup postfix can retrieve the instantiated body and establish a mirror-only layer; it will **not by itself** neutralize a later renderer-disable callback or keyed disable state. A has no declared LODGroup field on `PlayerModel` or `ThirdViewPlayerModel`, and these wrappers do not establish whether the current body has a Unity `LODGroup`. Do not assert LC's LOD fix is applicable without checking the actual instantiated subtree.

### Layer constants, layer indices, and culling masks

`Constants.LayersNames` exposes string fields: `Default`, `IgnoreRaycast`, `Player`, `Ragdoll`, `Hitbox`, `IgnoreJump`, **`FV_PlayerModelMesh`**, `OverlayWorldSpaceUI`, `TargetCollider`, `Handheld`, `ShockwaveMaskable`. A only contains getters/setters reading native static fields; it does not include string values or layer-number literals. Source: `Constants.LayersNames.cs:8-30,116-128,186-200`.

To resolve names/indices without running the game, G's serialized `TagManager.layers` was read. Relevant **verified asset** indices:

| Index | Layer name | Single-bit mask |
|---:|---|---:|
| 3 | `Ignore Render` | `0x00000008` |
| 10 | `PlayerIgnore` | `0x00000400` |
| 11 | `Ragdoll` | `0x00000800` |
| 14 | `Hitbox` | `0x00004000` |
| 15 | `Handheld` | `0x00008000` |
| 20 | `Player` | `0x00100000` |
| **23** | **`TV_PlayerModelMesh`** | **`0x00800000`** |
| **24** | **`FV_PlayerModelMesh`** | **`0x01000000`** |

All 32 layer slots are named in this build (no unnamed free layer). Full index order from G:

```text
0 Default; 1 TransparentFX; 2 Ignore Raycast; 3 Ignore Render;
4 Water; 5 UI; 6 InteractableLookable; 7 Transparent;
8 OverlayWorldSpaceUI; 9 InteractableTransparent; 10 PlayerIgnore; 11 Ragdoll;
12 PostProcess; 13 IgnoreJump; 14 Hitbox; 15 Handheld;
16 Lightblocker; 17 Primitive; 18 Soundblocker; 19 UnstableGround;
20 Player; 21 PrimitiveOccluder; 22 WorldSpaceUI; 23 TV_PlayerModelMesh;
24 FV_PlayerModelMesh; 25 Invisible Collider; 26 ShockwaveMaskable; 27 UIMask;
28 UIMaskable; 29 TargetCollider; 30 Footstep; 31 HintLookable.
```

**No actual first-person camera culling mask was resolved.** Layer names strongly suggest separate TV/FV model meshes, but do not prove which main camera excludes which bit, nor that an extra layer alone solves hiding. `StaticGameData.LayerMaskData` exposes `UnityEngine.LayerMask LayerMask` but no fixed value or first-person-specific association (`StaticGameData.LayerMaskData.cs:10-34`). The candidate controller/model wrapper declarations do not expose a dedicated hiding culling-mask field. Future runtime work should read actual active camera masks, actual renderer layers and the subcontroller assignments rather than infer masks from names.

M currently selects a non-main-camera-visible layer by scanning down from 31 (except 2/4/5), calls it a free layer, and recursively moves the body. **Excluded by that camera does not mean unused by the game**; G confirms those bits already have gameplay names.

### Embedded decompiled evidence

The following are verbatim method bodies from targeted A output (except omission of surrounding attributes/other members). They demonstrate the native-code visibility limit directly.


**Gameplay.PlayerSystem.RenderersRenderingController.cs:95-102**

```csharp
	public unsafe virtual void DisableRenderers()
	{
		IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this);
		IntPtr* ptr = null;
		Unsafe.SkipInit(out IntPtr intPtr);
		IntPtr intPtr2 = IL2CPP.il2cpp_runtime_invoke(IL2CPP.il2cpp_object_get_virtual_method(IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)(object)this), NativeMethodInfoPtr_DisableRenderers_Protected_Virtual_New_Void_0), IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this), (void**)ptr, ref intPtr);
		Il2CppException.RaiseExceptionIfNecessary(intPtr);
	}
```

**Gameplay.PlayerSystem.ProxyRenderingController.cs:136-143**

```csharp
	public unsafe override void OnDisableRendering()
	{
		IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this);
		IntPtr* ptr = null;
		Unsafe.SkipInit(out IntPtr intPtr);
		IntPtr intPtr2 = IL2CPP.il2cpp_runtime_invoke(NativeMethodInfoPtr_OnDisableRendering_Protected_Virtual_Void_0, IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this), (void**)ptr, ref intPtr);
		Il2CppException.RaiseExceptionIfNecessary(intPtr);
	}
```

**Gameplay.PlayerSystem.FirstViewProxyRenderingControllerInitializable.cs:83-91**

```csharp
	public unsafe override void OnInitialize(FirstViewPlayerModel playerModel)
	{
		IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this);
		IntPtr* ptr = stackalloc IntPtr[1];
		*ptr = IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)(object)playerModel);
		Unsafe.SkipInit(out IntPtr intPtr);
		IntPtr intPtr2 = IL2CPP.il2cpp_runtime_invoke(IL2CPP.il2cpp_object_get_virtual_method(IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)(object)this), NativeMethodInfoPtr_OnInitialize_Protected_Virtual_Void_FirstViewPlayerModel_0), IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this), (void**)ptr, ref intPtr);
		Il2CppException.RaiseExceptionIfNecessary(intPtr);
	}
```

**Cameras.SwitchActionProcessors.PlayerModelDisplayer.cs:133-140**

```csharp
	public unsafe override void OnEnableFirstPersonViewModeInternal()
	{
		IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this);
		IntPtr* ptr = null;
		Unsafe.SkipInit(out IntPtr intPtr);
		IntPtr intPtr2 = IL2CPP.il2cpp_runtime_invoke(IL2CPP.il2cpp_object_get_virtual_method(IL2CPP.Il2CppObjectBaseToPtr((Il2CppObjectBase)(object)this), NativeMethodInfoPtr_OnEnableFirstPersonViewModeInternal_Protected_Virtual_Void_0), IL2CPP.Il2CppObjectBaseToPtrNotNull((Il2CppObjectBase)(object)this), (void**)ptr, ref intPtr);
		Il2CppException.RaiseExceptionIfNecessary(intPtr);
	}
```

### Ranked most-promising patch targets

1. **`Gameplay.PlayerSystem.RenderersRenderingController.DisableRenderers()`**, prefix limited to the local instantiated third-person body's `PlayerModelRenderersRenderingController`. Closest named leaf to the disabled renderers; the declaring type is the base class, **not** `PlayerModelRenderersRenderingController`. This is the best narrow candidate for testing whether preventing native disabling from happening at all (rather than trying to restore just before a render) fixes the issue. References: candidate table and embedded `RenderersRenderingController.cs:95-102`.
2. **`Cameras.SwitchActionProcessors.PlayerModelDisplayer.OnEnableFirstPersonViewModeInternal()`**, plus its `OnModelSetuped(...)` as a setup/rebinding observation point. Best semantic hook to prevent the local POV operation from suppressing just the third-person meshes. A blanket skip may also prevent normal first-person model setup; preserve the intended first-person controller behavior. References: displayer table row and embedded method.
3. **`Gameplay.PlayerSystem.ProxyRenderingController.OnDisableRendering()`** or **`RenderingController.DisableRendering(Il2CppSystem.Object key)`**, restricted to the verified local-body proxy/target and, if possible, the specific local-POV disable key. `Initialize(RenderingController)` exposes the binding seam. This is preferable to globally enabling everything, but requires knowing actual proxy identity and disable-key ownership. References: proxy/base controller rows and embedded proxy method.
4. **Third-view spawn/setup postfix:** `PlayerModelSetuper.SetupThirdViewModel(PlayerModelData)` / `PlayerModelInstantiator<ThirdViewPlayerModel>.InstantiateAndInitialize(ThirdViewPlayerModel)` / `ThirdViewProxyRenderingControllerInitializable.OnInitialize(ThirdViewPlayerModel)`. Good for a persistent body handle, renderer cache, and mirror-only layer assignment (and LOD checks if one actually exists). **Not enough alone** if the game later disables the renderers. The first-view initializer is a separate candidate only if its actual binding proves relevant. References: local-body chain, setup-hook list, initializer rows.
5. **`ModelPersonAnimancerController.DisableAnimancerAndAnimator(Il2CppSystem.Object)`** or `UpdateAnimancerAndAnimatorEnabledState()`: secondary investigation only if the body remains non-drawing or frozen after the renderer-visibility seam is neutralized. This controller concerns animation state; no renderer-hiding implementation was demonstrated. Reference: animation row.

**Plain conclusion: the exact hiding mechanism and native caller chain are inconclusive from this interop assembly.** The candidate signatures and owner-body access path are concrete; layer indices 23/24 are concrete from assets; whether the code uses `enabled`, `forceRenderingOff`, another mechanism, or an additional LOD/skinning/culling blocker is not established. Further work needs native-code analysis or carefully scoped runtime instrumentation; neither was performed here.

## 1. Decompiled mod source (removed)

The six verbatim decompiled source files that were pasted here have been removed from the
repository; the findings they support are stated above and in section 2. The decompiled tree
is kept outside the repo at `~/assets/monorepo/alterum-data/research-archive/`.

## 2. Prefab facts (UnityPy)

Primary source: `~/assets/monorepo/alterum-data/mirrordecor/x/BepInEx/plugins/MirrorDecor/mirror.assets` (157,098 bytes). Read using `UnityPy.load`, `ObjectReader.read_typetree()`, and `UnityPy.helpers.MeshHelper.MeshHandler.process()` for decoded mesh verification. No Unity asset was saved or modified.

The bundle contains a Unity **2022.3.9f1** serialized file `CAB-92e5bcd7c890d752b5a704ebfda92f75`, its `.resS` and `.resource` data. Path IDs below are **signed 64-bit integers**; do not round them through a JavaScript Number. `m_FileID=0` means same serialized file; the one external serialized-file identifier is `Library/unity default resources`.

### Two distinct objects: lowercase `mirror` versus uppercase `Mirror`

The AssetBundle container maps:
- `assets/mirror/prefabs/mirror.prefab` → GameObject **`Mirror`**, pathID `6682092864339339923` (the actual spawned decoration root).
- `assets/mirror/meshes/mirror.fbx` → GameObject **`mirror`**, pathID `357557403661491495`, and Mesh **`Mirror`**, pathID `-2535983733840845896` (imported model asset, separate from the prefab hierarchy).
- `assets/mirror/unlockables/mirror/mirror.asset` → UnlockableItemDef MonoBehaviour `8767229826648621415`; `unlockable.prefabObject` references uppercase `Mirror` above.

**Critical scale distinction:** uppercase prefab root `Mirror.localScale = (1, 1, 1)`. Lowercase imported-model root `mirror.localScale = (100, 100, 100)`. They are **not** parent/child; do not multiply the spawned prefab by the FBX root's 100× scale.

### Transform hierarchy (complete)

Every Transform in the bundle is accounted for here. Child order comes from serialized `m_Children`; indentation is the actual parent relation:

```text
Mirror                       [spawned prefab root; layer 8]
├── PlacementCollider        [layer 8]
└── MirrorFrame              [layer 8]
    └── MirrorMesh           [layer 8]
        └── Camera           [layer 8]

mirror                       [separate imported FBX root; layer 0; no children]
```

All six GameObjects have `m_IsActive=true`. Rotations below are exact stored **quaternions `(x,y,z,w)`**, not Euler angles. Float spellings are lossless Python float representations of the serialized values. Transform local position/rotation/scale are relative to the parent shown above.

**Mirror** — GameObject `6682092864339339923`, Transform `-4755498954608607793`; parent Transform `0`.

- localPosition = `(0.0, 0.0, 0.0)`
- localRotation = `(0.0, 0.0, 0.0, 1.0)`
- localScale = `(1.0, 1.0, 1.0)`

**PlacementCollider** — GameObject `-6706047082920360020`, Transform `-4883286474679987084`; parent Transform `-4755498954608607793`.

- localPosition = `(0.0, 2.630000114440918, 0.0)`
- localRotation = `(0.0, 0.0, 0.0, 1.0)`
- localScale = `(1.0, 1.0, 1.0)`

**MirrorFrame** — GameObject `4285399111577793174`, Transform `-3177217087294547308`; parent Transform `-4755498954608607793`.

- localPosition = `(-0.003188316710293293, -0.012000000104308128, 0.0)`
- localRotation = `(-0.5026111006736755, -0.5026111006736755, -0.4973751902580261, 0.4973751902580261)`
- localScale = `(1.0, 1.0, 1.0)`

**MirrorMesh** — GameObject `-4768814683219268011`, Transform `1623861954514604087`; parent Transform `-3177217087294547308`.

- localPosition = `(0.014999999664723873, -0.024000000208616257, 1.3990000486373901)`
- localRotation = `(0.9961947202682495, 0.0, 0.0, 0.087155781686306)`
- localScale = `(-0.12999999523162842, 0.25999999046325684, 0.25999999046325684)`

**Camera** — GameObject `4817720351877269211`, Transform `7369106326630759353`; parent Transform `1623861954514604087`.

- localPosition = `(0.0, 0.0, 0.0)`
- localRotation = `(-0.7199417948722839, -0.005774702411144972, 0.020391276106238365, 0.6937108635902405)`
- localScale = `(-1.0, 1.0, 1.0)`

**mirror (separate FBX)** — GameObject `357557403661491495`, Transform `-7765896775854609164`; parent Transform `0`.

- localPosition = `(-0.0, 0.0, 0.0)`
- localRotation = `(-0.7071067094802856, -3.0908616110991716e-08, 3.090862321641907e-08, 0.7071068286895752)`
- localScale = `(100.0, 100.0, 100.0)`

### Camera relative to the actual mirror root — reproduction warning

The Camera is **not a direct child of `Mirror`**. Its immediate local position `(0,0,0)` is at the **MirrorMesh origin**, reached via `Mirror → MirrorFrame → MirrorMesh → Camera`. The Camera's exact immediate local quaternion and negative X scale are listed above. MirrorMesh also has a negative X scale and a 2:1 nonuniform scale; preserving just Camera's quaternion and setting its parent directly to the root would lose important transforms.

The safest procedural reproduction is to recreate all three child TRS transforms exactly. Root `Mirror` itself is identity in the prefab; placement can then move/rotate that root. In particular, **do not drop either negative-X scale** or the material UV crop described below.

For convenience only, the following is a **derived** composite matrix `T_Frame R_Frame S_Frame · T_Mesh R_Mesh S_Mesh · T_Camera R_Camera S_Camera`, mapping Camera-local points to uppercase Mirror-root space. Calculated in double precision from the stored values, normalizing each quaternion; rounded for display, not another serialized asset field:

```text
[ 0.008409134947,  0.054700459009, -0.254041613273, -0.027188316919]
[-0.005758671646,  0.254140942237,  0.054531226470,  1.387080417194]
[ 0.129950643984, -0.000064353789,  0.004287692500,  0.000349300162]
[ 0.000000000000,  0.000000000000,  0.000000000000,  1.000000000000]
```

Derived Camera origin in Mirror-root coordinates: `(-0.027188316918909546, 1.3870804171936963, 0.00034930016186772786)`.
Derived rotation-only forward (`q_Frame * q_Mesh * q_Camera` applied to +Z): `(-0.9770831638140901, 0.20914845700326304, -0.03956531213324805)`.
Derived rotation-only up: `(0.21038638851933034, 0.977151418710561, -0.03021046900877666)`.

The composite linear matrix has nonzero off-diagonal terms in `AᵀA` (nonorthogonal scaled axes), so it contains **shear**. It cannot in general be represented by one ordinary child TRS without changing the matrix. Also, a transform matrix is **not itself a Camera view/projection matrix**; Camera behavior with inherited scale is engine-specific. The retained hierarchy and serialized camera projection settings, not an assumed flattening, are the reproduction source of truth. No live render was used to verify the result.

### Camera — complete serialized component

Camera component pathID `8599835811914204669`, GameObject `4817720351877269211`. Full `read_typetree()` output follows (the actual serialized keys contain spaces: `field of view`, `near clip plane`, `far clip plane`; there is no literal `field_of_view` key):

```json
{
  "m_GameObject": {
    "m_FileID": 0,
    "m_PathID": 4817720351877269211
  },
  "m_Enabled": 1,
  "m_ClearFlags": 1,
  "m_BackGroundColor": {
    "r": 0.1921568661928177,
    "g": 0.3019607961177826,
    "b": 0.4745098054409027,
    "a": 0.0
  },
  "m_projectionMatrixMode": 1,
  "m_GateFitMode": 2,
  "m_Iso": 200,
  "m_ShutterSpeed": 0.004999999888241291,
  "m_Aperture": 16.0,
  "m_FocusDistance": 10.0,
  "m_FocalLength": 50.0,
  "m_BladeCount": 5,
  "m_Curvature": {
    "x": 2.0,
    "y": 11.0
  },
  "m_BarrelClipping": 0.25,
  "m_Anamorphism": 0.0,
  "m_SensorSize": {
    "x": 36.0,
    "y": 24.0
  },
  "m_LensShift": {
    "x": 0.0,
    "y": 0.0
  },
  "m_NormalizedViewPortRect": {
    "x": 0.0,
    "y": 0.0,
    "width": 1.0,
    "height": 1.0
  },
  "near clip plane": 0.009999999776482582,
  "far clip plane": 1000.0,
  "field of view": 65.0,
  "orthographic": false,
  "orthographic size": 5.0,
  "m_Depth": 0.0,
  "m_CullingMask": {
    "m_Bits": 565909343
  },
  "m_RenderingPath": -1,
  "m_TargetTexture": {
    "m_FileID": 0,
    "m_PathID": -4158285611232656452
  },
  "m_TargetDisplay": 0,
  "m_TargetEye": 3,
  "m_HDR": false,
  "m_AllowMSAA": false,
  "m_AllowDynamicResolution": false,
  "m_ForceIntoRT": false,
  "m_OcclusionCulling": true,
  "m_StereoConvergence": 10.0,
  "m_StereoSeparation": 0.02199999988079071
}
```

Key facts:
- Enabled (`m_Enabled=1`); perspective (`orthographic=false`); FOV **65°**; near **0.009999999776482582**; far **1000**; depth **0**.
- `m_ClearFlags=1` (Unity CameraClearFlags.Skybox at the Camera API level). HDRP has its **own** `clearColorMode`, recorded below; do not treat this field alone as the effective HDRP clear behavior.
- `m_CullingMask.m_Bits=565909343` = **`0x21BB175F`**; enabled bit indices **[0, 1, 2, 3, 4, 6, 8, 9, 10, 12, 16, 17, 19, 20, 21, 23, 24, 29]**. This matches `Plugin.MirrorCameraCullingMask` exactly. This mask belongs to **Lethal Company**, not Alterum; do not reuse it as an Alterum camera mask.
- Target RT is **`m_TargetTexture`**, `m_FileID=0`, `m_PathID=-4158285611232656452` → RenderTexture `Mirror`, not the Texture2D or material of the same name.
- Camera `m_HDR=false`, `m_AllowMSAA=false`, `m_AllowDynamicResolution=false`, `m_OcclusionCulling=true`. These Camera flags are distinct from RT sampling settings and HDRP frame settings.

### Camera's HDRP additional component — complete serialized fields

MonoBehaviour pathID `5227701305024177694` references MonoScript `-404987181731620603`: class `UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData`, assembly `Unity.RenderPipelines.HighDefinition.Runtime`. It is enabled. This is **not** MirrorScript. Full data is preserved to avoid silently missing HDRP-specific viewpoint/rendering settings:

```json
{
  "m_GameObject": {
    "m_FileID": 0,
    "m_PathID": 4817720351877269211
  },
  "m_Enabled": 1,
  "m_Script": {
    "m_FileID": 0,
    "m_PathID": -404987181731620603
  },
  "m_Name": "",
  "clearColorMode": 0,
  "backgroundColorHDR": {
    "r": 0.02500000037252903,
    "g": 0.07000000029802322,
    "b": 0.1899999976158142,
    "a": 0.0
  },
  "clearDepth": 1,
  "volumeLayerMask": {
    "m_Bits": 1
  },
  "volumeAnchorOverride": {
    "m_FileID": 0,
    "m_PathID": 0
  },
  "antialiasing": 0,
  "SMAAQuality": 2,
  "dithering": 0,
  "stopNaNs": 0,
  "taaSharpenStrength": 0.5,
  "TAAQuality": 1,
  "taaHistorySharpening": 0.3499999940395355,
  "taaAntiFlicker": 0.5,
  "taaMotionVectorRejection": 0.0,
  "taaAntiHistoryRinging": 0,
  "taaBaseBlendFactor": 0.875,
  "taaJitterScale": 1.0,
  "physicalParameters": {
    "m_Iso": 200,
    "m_ShutterSpeed": 0.004999999888241291,
    "m_Aperture": 16.0,
    "m_FocusDistance": 10.0,
    "m_BladeCount": 5,
    "m_Curvature": {
      "x": 2.0,
      "y": 11.0
    },
    "m_BarrelClipping": 0.25,
    "m_Anamorphism": 0.0
  },
  "flipYMode": 0,
  "xrRendering": 1,
  "fullscreenPassthrough": 0,
  "allowDynamicResolution": 0,
  "customRenderingSettings": 0,
  "invertFaceCulling": 0,
  "probeLayerMask": {
    "m_Bits": 4294967295
  },
  "hasPersistentHistory": 0,
  "screenSizeOverride": {
    "x": 0.0,
    "y": 0.0,
    "z": 0.0,
    "w": 0.0
  },
  "screenCoordScaleBias": {
    "x": 0.0,
    "y": 0.0,
    "z": 0.0,
    "w": 0.0
  },
  "allowDeepLearningSuperSampling": 1,
  "deepLearningSuperSamplingUseCustomQualitySettings": 0,
  "deepLearningSuperSamplingQuality": 0,
  "deepLearningSuperSamplingUseCustomAttributes": 0,
  "deepLearningSuperSamplingUseOptimalSettings": 1,
  "deepLearningSuperSamplingSharpening": 0.0,
  "fsrOverrideSharpness": 0,
  "fsrSharpness": 0.9200000166893005,
  "exposureTarget": {
    "m_FileID": 0,
    "m_PathID": 0
  },
  "materialMipBias": 0.0,
  "m_RenderingPathCustomFrameSettings": {
    "bitDatas": {
      "data1": 140666587840333,
      "data2": 13763000512783810584
    },
    "lodBias": 1.0,
    "lodBiasMode": 0,
    "lodBiasQualityLevel": 0,
    "maximumLODLevel": 0,
    "maximumLODLevelMode": 0,
    "maximumLODLevelQualityLevel": 0,
    "sssQualityMode": 0,
    "sssQualityLevel": 0,
    "sssCustomSampleBudget": 20,
    "msaaMode": 1,
    "materialQuality": 0
  },
  "renderingPathCustomFrameSettingsOverrideMask": {
    "mask": {
      "data1": 0,
      "data2": 0
    }
  },
  "defaultFrameSettings": 0,
  "m_Version": 9,
  "m_ObsoleteRenderingPath": 0,
  "m_ObsoleteFrameSettings": {
    "overrides": 0,
    "enableShadow": 0,
    "enableContactShadows": 0,
    "enableShadowMask": 0,
    "enableSSR": 0,
    "enableSSAO": 0,
    "enableSubsurfaceScattering": 0,
    "enableTransmission": 0,
    "enableAtmosphericScattering": 0,
    "enableVolumetrics": 0,
    "enableReprojectionForVolumetrics": 0,
    "enableLightLayers": 0,
    "enableExposureControl": 1,
    "diffuseGlobalDimmer": 0.0,
    "specularGlobalDimmer": 0.0,
    "shaderLitMode": 0,
    "enableDepthPrepassWithDeferredRendering": 0,
    "enableTransparentPrepass": 0,
    "enableMotionVectors": 0,
    "enableObjectMotionVectors": 0,
    "enableDecals": 0,
    "enableRoughRefraction": 0,
    "enableTransparentPostpass": 0,
    "enableDistortion": 0,
    "enablePostprocess": 0,
    "enableOpaqueObjects": 0,
    "enableTransparentObjects": 0,
    "enableRealtimePlanarReflection": 0,
    "enableMSAA": 0,
    "enableAsyncCompute": 0,
    "runLightListAsync": 0,
    "runSSRAsync": 0,
    "runSSAOAsync": 0,
    "runContactShadowsAsync": 0,
    "runVolumeVoxelizationAsync": 0,
    "lightLoopSettings": {
      "overrides": 0,
      "enableDeferredTileAndCluster": 0,
      "enableComputeLightEvaluation": 0,
      "enableComputeLightVariants": 0,
      "enableComputeMaterialVariants": 0,
      "enableFptlForForwardOpaque": 0,
      "enableBigTilePrepass": 0,
      "isFptlEnabled": 0
    }
  }
}
```

Notable additional fields: `clearColorMode=0`, `clearDepth=1`, `antialiasing=0`, `flipYMode=0`, `invertFaceCulling=0`, `customRenderingSettings=0`, `volumeLayerMask.m_Bits=1`, `probeLayerMask.m_Bits=4294967295`. Custom frame-settings override mask is all zero. Effective inherited HDRP pipeline defaults, exposure, and runtime fallback behavior cannot be determined from this prefab alone; raw enum values are preserved rather than assuming another pipeline's meanings.

### RenderTexture — complete settings

RT `Mirror`, pathID `-4158285611232656452`; bundle key `assets/mirror/materials/mirror.rendertexture`:

```json
{
  "m_Name": "Mirror",
  "m_ForcedFallbackFormat": 4,
  "m_DownscaleFallback": false,
  "m_IsAlphaChannelOptional": false,
  "m_Width": 2000,
  "m_Height": 2000,
  "m_AntiAliasing": 8,
  "m_MipCount": -1,
  "m_DepthStencilFormat": 94,
  "m_ColorFormat": 8,
  "m_MipMap": false,
  "m_GenerateMips": true,
  "m_SRGB": false,
  "m_UseDynamicScale": false,
  "m_BindMS": false,
  "m_EnableCompatibleFormat": true,
  "m_EnableRandomWrite": false,
  "m_TextureSettings": {
    "m_FilterMode": 1,
    "m_Aniso": 0,
    "m_MipBias": 0.0,
    "m_WrapU": 1,
    "m_WrapV": 1,
    "m_WrapW": 1
  },
  "m_Dimension": 2,
  "m_VolumeDepth": 1,
  "m_ShadowSamplingMode": 2
}
```

- Serialized size **2000×2000**, sample count **8** (`m_AntiAliasing=8`). Camera `allowMSAA=false` does **not** change what the RT asset serializes; no runtime sample count was measured.
- `m_ColorFormat=8` → **R8G8B8A8_UNorm**; `m_DepthStencilFormat=94` → **D32_SFloat_S8_UInt** (decoded using UnityPy's `GraphicsFormat` enum). `m_SRGB=false`.
- 2D texture (`m_Dimension=2`), volume depth 1; mipmaps off, generate-mips flag true, mip count -1; no dynamic scale, no random write, no bind-MS.
- Filter mode 1 (bilinear), anisotropy 0, wrap U/V/W 1 (clamp), bias 0.
- **Runtime resize differs:** `Plugin.Awake` calls `UpdateMirrorRes(Config.resolution.Value)`, which clamps to **8..1024** and calls `Resize` (Release + set width/height). Default config is **512**, hence nominal default runtime dimensions 512×512, not the stored 2000×2000. This does not prove when the engine recreates the RT or actual runtime allocation format. See full Plugin/Config source in section 1.

### Every MeshRenderer: materials, shader names, and texture mapping

Shader names were resolved from **Shader.m_ParsedForm.m_Name**, because the top-level Shader `m_Name` is empty. All three renderer materials reference Shader pathID **`-9131526356163373820`**, parsed name **`HDRP/Lit`**. The bundle also contains unreferenced-by-these-materials fallback shaders `Hidden/Core/FallbackError` (`-2383429423479903051`) and `Hidden/HDRP/FallbackError` (`5150962942331123426`); those are not the materials' selected shader names.

| Object | MeshRenderer pathID | Slot 0 material name / pathID | Shader | MeshFilter reference |
|---|---:|---|---|---|
| `MirrorMesh` | `-9004081922731439884` | `Mirror` / `-3355599039596493090` | `HDRP/Lit` | FileID `1`, PathID `10209` |
| `MirrorFrame` | `7254180153445717387` | `Mirror` / `-5922000198467761970` | `HDRP/Lit` | FileID `0`, PathID `-2535983733840845896` |
| `mirror` | `4168077519901960293` | `DefaultHDMaterial` / `-2300892688861324716` | `HDRP/Lit` | FileID `0`, PathID `-2535983733840845896` |

All three MeshRenderers serialize `m_Enabled=true`, `m_CastShadows=1`, `m_ReceiveShadows=1`, and `m_RenderingLayerMask=257` (rendering/light layers, **not** GameObject layer/camera culling mask). There are no MeshRenderers on the prefab root `Mirror`, Camera, or PlacementCollider.

**Non-null material textures and their exact UV transforms:**

- Material `Mirror` / `-3355599039596493090`:
  - `_BaseColorMap` → `RenderTexture Mirror` / `-4158285611232656452`; scale `(0.5, 1.0)`, offset `(0.25, 0.0)`.
  - `_MainTex` → `RenderTexture Mirror` / `-4158285611232656452`; scale `(0.5, 1.0)`, offset `(0.25, 0.0)`.
- Material `Mirror` / `-5922000198467761970`:
  - `_BaseColorMap` → `Texture2D Mirror` / `-5035613583872723821`; scale `(1.0, 1.0)`, offset `(0.0, 0.0)`.
  - `_MainTex` → `Texture2D Mirror` / `-5035613583872723821`; scale `(1.0, 1.0)`, offset `(0.0, 0.0)`.
- Material `DefaultHDMaterial` / `-2300892688861324716`:
  - All serialized texture slots are null.
- Material `Mirror2` / `-9151769425768873304`:
  - All serialized texture slots are null.

The **MirrorMesh display material crops the middle half of the RT horizontally**: both `_BaseColorMap` and `_MainTex` have scale `(0.5,1)` and offset `(0.25,0)`. This is important when reproducing the aspect/viewpoint. These UV scale/offset values are positive; there is no negative-U material flip here. The negative-X scales are in the hierarchy instead. Material `Mirror2` is present in the bundle but **not assigned to any of the three MeshRenderers**. Material `Mirror` on `MirrorFrame` is a different material asset than material `Mirror` on `MirrorMesh`; names alone are ambiguous.

`MirrorMesh`'s MeshFilter points to **external built-in resource** `Library/unity default resources`, pathID `10209`. The supplied bundle does not contain that external mesh. It was **not resolved to a specific primitive/name**; do not assume it is the embedded frame mesh or label it Quad without further evidence. In contrast, MirrorFrame and lowercase imported mirror both reference the embedded Mesh named `Mirror` below.

### MirrorScript MonoBehaviour — empty confirmed

The root `Mirror` has MonoBehaviour pathID `6770711070285901688`, referencing MonoScript `-8810123544800974425`. MonoScript identifies **namespace `MirrorDecor`, class `MirrorScript`, assembly `MirrorDecor.dll`**. The complete serialized MonoBehaviour is:

```json
{
  "m_GameObject": {
    "m_FileID": 0,
    "m_PathID": 6682092864339339923
  },
  "m_Enabled": 1,
  "m_Script": {
    "m_FileID": 0,
    "m_PathID": -8810123544800974425
  },
  "m_Name": ""
}
```

**No custom serialized fields at all**: only `m_GameObject`, `m_Enabled`, `m_Script`, `m_Name`. This agrees with the complete empty C# class in section 1. Its lack of serialized settings is not an unresolved type-tree issue; the concrete type tree was successfully decoded.

### Mesh `Mirror` — counts, topology and bounding box

Embedded Mesh pathID **`-2535983733840845896`**; referenced by MirrorFrame and lowercase imported model `mirror`, **not** MirrorMesh's external mesh.

- Serialized vertex count **152**; MeshHandler decoded **152 vertices**.
- One submesh; topology **0 (Triangles)**; index count **324**, hence **108 triangles**. Index format 0 (UInt16), index-buffer length 648 bytes; decoded indices range 0..151.
- Serialized mesh-local AABB center = `(0.0, 0.22006383538246155, 1.4014755487442017)`.
- Serialized mesh-local AABB extents = `(0.7519050240516663, 0.5523285865783691, 1.401893973350525)`.
- Derived from serialized AABB: min = `(-0.7519050240516663, -0.3322647511959076, -0.0004184246063232422)`; max = `(0.7519050240516663, 0.7723924219608307, 2.8033695220947266)`; size = `(1.5038100481033325, 1.1046571731567383, 2.80378794670105)`.
- Independent decoded-vertex min = `(-0.7519050240516663, -0.33226478099823, -0.00041852635331451893)`; max = `(0.7519050240516663, 0.7723924517631531, 2.8033695220947266)`. Tiny differences from center±extents are floating-point rounding.
- Submesh AABB matches the serialized mesh AABB exactly. Mesh compression 0. These coordinates are **mesh-local**, not prefab/root/world-space dimensions; apply MirrorFrame's transform for the prefab, not the separate FBX root's 100× scale.
- Vertex data was successfully read from the bundled `.resS` stream (offset 699072, size 4864), so counts/bounds are not merely guessed from an inaccessible external resource.

Full serialized submesh record and mesh AABB:

```json
{
  "m_SubMeshes": [
    {
      "firstByte": 0,
      "indexCount": 324,
      "topology": 0,
      "baseVertex": 0,
      "firstVertex": 0,
      "vertexCount": 152,
      "localAABB": {
        "m_Center": {
          "x": 0.0,
          "y": 0.22006383538246155,
          "z": 1.4014755487442017
        },
        "m_Extent": {
          "x": 0.7519050240516663,
          "y": 0.5523285865783691,
          "z": 1.401893973350525
        }
      }
    }
  ],
  "m_LocalAABB": {
    "m_Center": {
      "x": 0.0,
      "y": 0.22006383538246155,
      "z": 1.4014755487442017
    },
    "m_Extent": {
      "x": 0.7519050240516663,
      "y": 0.5523285865783691,
      "z": 1.401893973350525
    }
  },
  "m_StreamData": {
    "offset": 699072,
    "size": 4864,
    "path": "archive:/CAB-92e5bcd7c890d752b5a704ebfda92f75/CAB-92e5bcd7c890d752b5a704ebfda92f75.resS"
  }
}
```

### Unresolved / not measured (do not fill these gaps by assumption)

- Alterum's actual native property writes and call order, per-frame behavior, current active-camera masks, live owner-body transform/hierarchy, LODGroup presence, and actual root/body renderer count: not inferable from the inspected wrappers/assets; no live attachment was done.
- External built-in mesh `FileID=1, PathID=10209`: not supplied by this bundle, not resolved.
- Effective runtime HDRP exposure/frame defaults, GPU texture format/sample fallback, and final appearance: not verified by launching/rendering in Unity or a game.
- No source-based dynamic reflection calculation exists in the empty MirrorScript; the static prefab camera/transform/material configuration is what was actually found. Whether any external Lethal Company component alters those transforms at runtime was not tested.
