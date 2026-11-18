# Cosmetics track — reconnaissance: MoreCompany (reference) vs Alterum (target)

Read-only reconnaissance. Nothing outside this directory was created or modified; no build, no
game launch/termination, no runtime attachment. Decompiler scratch output went to
`/dev/shm/mc-full/`, `/dev/shm/alterum-cos*/` (disposable). Retained artifacts here:
`dl/mc.zip`, `dl/mc.json`, `unpack/` (Thunderstore zip contents); the targeted decompile used to
MoreCompany types), `unpack_bundle_70132` / `unpack_bundle_3110260` (carved embedded bundles).

Environment: `DOTNET_ROOT=$HOME/.dotnet`, PATH prefixed with `$HOME/.dotnet:$HOME/.dotnet/tools`;
ilspycmd 8.2.0.7535; UnityPy 1.25.4 (system python3).

---

## 1. Reference mod inventory

Thunderstore API: latest version **1.14.0**, `download_url`
`https://thunderstore.io/package/download/notnotnotswipez/MoreCompany/1.14.0/`,
single dependency `BepInEx-BepInExPack-5.4.2100`. Zip is 10.5 MB.

```
README.md, manifest.json, icon.png, BepInEx/plugins/MoreCompany.dll   (10,451,968 bytes)
```

**No loose asset bundles ship in the zip.** The DLL carries two embedded .NET resources that are
Unity bundles (carved out by parsing the `UnityFS\0` headers, `flags=0x243`, compressed-blocks
info present):

| Resource name | offset in DLL | bytes | Unity | Contents |
|---|---:|---:|---|---|
| `MoreCompany.Resources.morecompany.assets` | 70,132 | 3,040,123 | 2022.3.9f1 | UI prefabs + TMP/fonts + the "display guy" scavenger model |
| `MoreCompany.Resources.morecompany.cosmetics` | 3,110,260 | 7,319,308 | 2022.3.9f1 | **all built-in cosmetics** |

Loaded via `BundleUtilities.GetResourceBytes` + `AssetBundle.LoadFromMemory`
(`MainClass.Awake`, `MainClass.cs:100-118` in the targeted decompile), and user `.cosmetics`
files are loaded with `AssetBundle.LoadFromFile` from a recursive scan of the BepInEx
`plugins` tree (`MainClass.RecursiveCosmeticLoad`).

`morecompany.cosmetics` object census: `Mesh` 33, `Material` 49, `Texture2D` 45, `Sprite` 21,
`GameObject` 150, `Transform` 150, `MeshRenderer` 70, `MeshFilter` 70, `AnimatorController` 2,
`AnimationClip` 2, `Shader` 5, `MonoScript` 3.

`morecompany.assets` object census: `GameObject` 132, `RectTransform` 57, `Canvas` 1,
`CanvasRenderer` 42, `Mesh` 4, `Material` 8, `Texture2D` 21, `Sprite` 16,
`SkinnedMeshRenderer` 3 (LOD1/2/3 of the display model), `LODGroup` 1, `Camera` 1,
`Shader` 6, `Font` 2, `AnimationClip` 2, `AnimatorController` 1.

Cosmetics-relevant managed types (whole list is only ~70 classes; the rest of the size is assets):

```
MoreCompany.Cosmetics.CosmeticApplication   (MonoBehaviour, the attach/runtime component)
MoreCompany.Cosmetics.CosmeticInstance      (MonoBehaviour, per-cosmetic marker/data)
MoreCompany.Cosmetics.CosmeticGeneric       (base class for bundle+path declared cosmetics)
MoreCompany.Cosmetics.CosmeticRegistry      (static: registry, selection, GUI, persistence)
MoreCompany.Cosmetics.CosmeticType          (enum)
MoreCompany.Cosmetics.ParentType            (enum)
MoreCompany.CosmeticPatches                 (Harmony: dead body / masked enemy / quick menu GUI)
MoreCompany.CosmeticSyncPatch               (Harmony: netcode named messages)
MoreCompany.Behaviors.SpinDragger           (UI-only: drag-to-rotate the preview model)
MoreCompany.MainClass                       (config, bundle load, save-file path)
```

Everything else in the DLL is player-count/lobby/UI-scaling work — explicitly out of scope.

---

## 2. The cosmetics data model

**A cosmetic = a prefab in a Unity AssetBundle with a `CosmeticInstance` component.**

```csharp
// MoreCompany.Cosmetics.CosmeticInstance (verbatim decompile, 14 lines, whole class)
public class CosmeticInstance : MonoBehaviour
{
    public CosmeticType cosmeticType;
    public string cosmeticId;
    public Texture2D icon;
}

// MoreCompany.Cosmetics.CosmeticType
public enum CosmeticType { HAT, WRIST, CHEST, R_LOWER_ARM, HIP, L_SHIN, R_SHIN }   // HAT=0 ... R_SHIN=6

// MoreCompany.Cosmetics.ParentType
public enum ParentType { Player, DeadBody, MaskedEnemy, DisplayGuy }
```

Registration is purely by bundle scan — no manifest, no ScriptableObject, no JSON:

```csharp
// CosmeticRegistry.LoadCosmeticsFromBundle (CosmeticRegistry.cs:59-92)
foreach (string text in bundle.GetAllAssetNames()) {
    if (!text.EndsWith(".prefab")) continue;
    GameObject val = bundle.LoadPersistentAsset<GameObject>(text);       // also sets hideFlags = DontUnload...
    CosmeticInstance component = val.GetComponent<CosmeticInstance>();
    if (component == null) continue;                                     // prefabs without it are ignored
    // skipped if MainClass.disabledCosmetics (csv config) contains component.cosmeticId
    // skipped (warning) if cosmeticInstances already has that id
    cosmeticInstances.Add(component.cosmeticId, component);
}
```

`LoadPersistentAsset<T>` (`Utils/AssetBundleExtension.cs`) is just `bundle.LoadAsset(name)` plus
`hideFlags = HideFlags.DontUnloadUnusedAsset` (value 32). Then `bundle.Unload(false)` is called,
i.e. assets are kept alive by that flag and by the dictionary references.

A second, code-first path exists for the built-in bundle: `CosmeticGeneric` subclasses declare
`gameObjectPath`/`cosmeticId`/`textureIconPath` and `LoadFromBundle` instantiates the prefab path,
`AddComponent<CosmeticInstance>()`, fills fields, and registers. Built-in cosmetics are *not*
polled this way; they ship as prefabs with the component already attached (see below).

**21 built-in cosmetics**, id + `cosmeticType` read from the `CosmeticInstance` typetrees in
`morecompany.cosmetics`:

| cosmeticId | type | | cosmeticId | type |
|---|---|---|---|---|
| builtin.partyhat | 0 HAT | | builtin.hardhat | 0 |
| builtin.tophat | 0 | | builtin.denimhat | 0 |
| builtin.santahat | 0 | | builtin.rudolph | 0 |
| builtin.jesterhat | 0 | | builtin.propellerhat | 0 (has Animator) |
| builtin.kittyears | 0 | | builtin.bunnyears | 0 |
| builtin.plaguemask | 0 | | builtin.mimemask | 0 |
| builtin.fancypants | 0 (moustache+monocle) | | builtin.fancyglasses | 0 |
| builtin.partyglasses | 0 | | builtin.3dglasses | 0 |
| builtin.eyeballs | 0 (has Animator) | | builtin.slimething | 0 |
| builtin.outoforder | **2 CHEST** | | builtin.watch | **3 R_LOWER_ARM** |
| builtin.gunholster | **4 HIP** | | | |

Icons are `Texture2D`/`Sprite` at `assets/morecompanyassets/cosmetics/icons/*.png`.
`WRIST` (=1) is declared in the enum but **no built-in cosmetic uses it and
`CosmeticApplication.ParentCosmetic` has no case for it** — a `WRIST` cosmetic would log
`Failed to find transform of type: WRIST`. `L_SHIN`/`R_SHIN` likewise unused.

### Per-cosmetic prefab shape (this is the "offset/scale data")

Every cosmetic prefab is 2–4 transforms: a bare root carrying `CosmeticInstance`, then one
intermediate "Pivot"/`model_0`/named node that holds the **placement offset, rotation and scale**,
then the mesh child(ren). Example, `assets/morecompanyassets/cosmetics/partyhatcosmetic.prefab`:

```
PartyHatCosmetic                 (CosmeticInstance: HAT, builtin.partyhat)
└── Party_Hat     localPos (0, 1.3932, -0.0640)  rot (-0.0687,0,0,0.9976)  scale 0.43
    ├── Cone      mesh "Cone"   mat Party_Hat_DefaultMaterial_BaseMapMetallic  scale 100
    └── Sphere    mesh "Sphere" mat Party_Hat_DefaultMaterial_BaseMap   pos (0,-0.993,-0.982) scale 21.16
```

Other examples (intermediate node TRS as authored):

| prefab | intermediate node | localPos | localScale |
|---|---|---|---|
| tophat | `TopHatTotal` | (0, 1.3910, -0.0310) | 0.48 |
| hardhat | `HardHat` (mesh directly, no pivot) | (0, 1.1070, 0.4990) | (20.2, 9.76, 26.06) |
| santahat | `model_0` | (-0.018, 0.868, 0.017) | 0.023 |
| denimhat | `model_0` | (0.012, 0.903, 0.158) | (1.6, 1.48, 1.48) |
| jesterhat | `Bufoon Hat` | (-0.012, 0.962, -0.009) | (56.5, 59.24, 7.09) |
| gunholster (HIP) | `model_0` | (-0.2206, -4.014, 1.25) | 0.05 |
| watch (R_LOWER_ARM) | `Pivot` | (-0.036, 0.767, 0.061) | 0.434 |

**The prefab root's own baked TRS is irrelevant and is overwritten at runtime** (roots still carry
their authoring-scene position, e.g. `(415.15, 247.49, 150.43)`). Only the intermediate node's
local TRS and the mesh child scales matter.

---

## 3. Attachment mechanism

`CosmeticApplication` is added to the **metarig** GameObject of a player model
(`PlayerControllerB.transform.Find("ScavengerModel").Find("metarig")`), or to a dead-body root, a
masked-enemy's `ScavengerModel/metarig`, or the wardrobe-preview model. Bone lookup is by
hardcoded relative path in `Awake`, evaluated **once**:

```csharp
// CosmeticApplication.Awake (CosmeticApplication.cs:29-45)
Transform val   = transform.Find("spine") ?? transform;
chest           = val.Find("spine.001").Find("spine.002").Find("spine.003");
head            = chest.Find("spine.004");
lowerArmRight   = chest.Find("shoulder.R").Find("arm.R_upper").Find("arm.R_lower");
hip             = val;                                     // -> "spine"
shinLeft        = val.Find("thigh.L").Find("shin.L");
shinRight       = val.Find("thigh.R").Find("shin.R");
if (parentType == ParentType.DisplayGuy) CosmeticRegistry.UpdateCosmeticsOnDisplayGuy(startEnabled:false);
RefreshAllCosmeticPositions();
```

Verified against the display model's actual rig in `morecompany.assets`
(`assets/morecompanyassets/testoverlay.prefab` → `Canvas/GlobalScale/CosmeticsScreen/ObjectHolder/
ScavengerModel/metarig`, metarig localScale 0.56):

```
spine
├── spine.001 → spine.002 → spine.003
│   ├── shoulder.L → arm.L_upper → arm.L_lower → hand.L → finger1..5.L
│   ├── shoulder.R → arm.R_upper → arm.R_lower → hand.R → ... , ServerItemHolder
│   ├── spine.004  (HEAD) → spine.004_end
│   ├── RightArm_target / LeftArm_target
│   ├── LevelSticker (MeshRenderer), BetaBadge (MeshRenderer)
├── thigh.L → shin.L → foot.L ; thigh.R → shin.R → foot.R
```

So `CosmeticType → bone`: HAT→`spine/spine.001/spine.002/spine.003/spine.004` (head),
R_LOWER_ARM→`.../spine.003/shoulder.R/arm.R_upper/arm.R_lower`, HIP→`spine` (lowest spine, not a
real hip), CHEST→`spine.003`, L_SHIN/R_SHIN→`spine/thigh.*/shin.*`.

Placement is **position-copy then re-parent**, not local offsets:

```csharp
// CosmeticApplication.ParentCosmetic (CosmeticApplication.cs:155-193)
cosmeticInstance.transform.position = val.position;
cosmeticInstance.transform.rotation = val.rotation;
cosmeticInstance.transform.parent   = val;             // Unity keeps world TRS -> root local pos ~= 0
```

Consequences: the root ends up at the bone origin with the *bone's* orientation; all authored
offset/rotation lives in the intermediate node; scale survives because Unity's `parent` setter
preserves world scale (root's localScale is divided by the bone's lossy scale).

Then a **hard-coded uniform scale fudge is applied afterwards**, in both attach paths:

```csharp
foreach (CosmeticInstance spawnedCosmetic in cosmeticApplication.spawnedCosmetics)
    spawnedCosmetic.transform.localScale *= 0.38f;      // CosmeticSyncPatch.cs:50, CosmeticPatches.cs:34
// CosmeticRegistry.COSMETIC_PLAYER_SCALE_MULT = 0.38f (CosmeticRegistry.cs:57)
```

The wardrobe preview divides instead (`UpdateCosmeticsOnDisplayGuy`: `localScale /= 4.3f`,
`CosmeticRegistry.cs:250-268`). These two numbers are **empirical, LC-model-specific** and were
tuned by eye; they are not derivable from the prefab data. Do not port them — calibrate on
Alterum's head bone.

No additive/offset API, no per-cosmetic slot exclusivity: `ApplyCosmetic` only refuses a duplicate
*id* (`spawnedCosmeticsIds`), so N hats can be stacked on one head simultaneously.

Visibility rules (`UpdateAllCosmeticVisibilities`, `CosmeticApplication.cs:112-146`):

```csharp
if (parentType == Player)   active = MainClass.cosmeticsSyncOther.Value && !isLocalPlayer;  // hide your own
if (parentType == DeadBody) active = MainClass.cosmeticsDeadBodies.Value;
if (parentType == MaskedEnemy) active = MainClass.cosmeticsMaskedEnemy.Value;
if (parentType == DisplayGuy)  active = true;
foreach (...) if (c == null) continue;
   if (c.cosmeticType != CosmeticType.HAT || !detachedHead) c.gameObject.SetActive(active);
```

Note the local player's own cosmetics **are applied to their own body but then deactivated** —
that is the whole "you don't see your hat in first person" story.

---

## 4. Selection storage and propagation

Local selection:

```csharp
CosmeticRegistry.locallySelectedCosmetics : List<string>    // ids, e.g. "builtin.partyhat"
CosmeticRegistry.ToggleCosmetic(string id)                  // add/remove + SyncCosmeticsToOtherClients()
CosmeticRegistry.IsEquipped(id) / GetCosmeticsToSync()      // GetCosmeticsToSync filters to known ids
MainClass.WriteCosmeticsToFile()                            // one id per line, File.WriteAllText
MainClass.ReadCosmeticsFromFile()                           // File.ReadAllLines -> the list
```

Save path: `Application.persistentDataPath + "/morecompanycosmetics.txt"` (or
`morecompanycosmetics-<profile>.txt` when `Per Profile Cosmetics` is on). The
`cosmeticsPerProfile` `SettingChanged` handler writes a *different* filename
(`MCCosmeticsSave[-profile].mcs`) than the one read at startup — an upstream inconsistency, not
something to copy.

Propagation to other players — Netcode `CustomMessagingManager` named messages, JSON/CSV payloads,
server relayed (`CosmeticSyncPatch.cs`):

| message | direction | payload |
|---|---|---|
| `MC_SV_SyncCosmetics` | client → server (id 0) | `ulong playerClientId`, `string` csv ids, `bool requestAll` |
| `MC_CL_ReceiveCosmetics` | server → all | `ulong playerClientId`, `string` csv ids |
| `MC_CL_ReceiveAllCosmetics` | server → one client | `string` JsonConvert of `Dictionary<int,List<string>>` |

Server keeps `MainClass.playerIdsAndCosmetics : Dictionary<int, List<string>>` (also used to clone
onto dead bodies / masked enemies), clears it on `ConnectClientToPlayerObject`, and replies with the
full JSON when `requestAll` is set. `NetworkDelivery` is Reliable(2) unless the buffer exceeds 1300
bytes, then 4 (ReliableFragmentedSequenced). Apply side is
`UpdateCosmeticsForPlayer(clientId, ids)`: find `allPlayerScripts[id]`, get/add
`CosmeticApplication` on `ScavengerModel/metarig`, `ClearCosmetics()`, `ApplyCosmetic(id,
startEnabled:false)` per id, `localScale *= 0.38f`, then
`UpdateAllCosmeticVisibilities(clientId == thisClientPlayerId)`.

Lobby/scene hooks that make this work are inside the player-count half of the mod (array resizing,
`PlayerControllerB.ConnectClientToPlayerObject` postfix registering the message handlers,
`GameNetworkManager.SetInstanceValuesBackToDefault` postfix unregistering them). Out of scope here,
except that the *registration point* pattern is worth copying.

Config (`MainClass.cs:78-83`): `Cosmetics/Show Cosmetics`, `Show On Dead Bodies`,
`Show On Masked Enemy`, `Default Cosmetics`, `Per Profile Cosmetics`, `Disabled Cosmetics` (csv).

---

## 5. Harmony patches (cosmetics-relevant only)

| patch | target | purpose |
|---|---|---|
| `CosmeticPatches.SpawnDeadBody` | `PlayerControllerB.SpawnDeadBody` postfix | clone the dead player's ids onto `deadBody.transform`; `detachedHead = true` for death animations 4/5 |
| `CosmeticPatches.SetEnemyOutside` | `MaskedPlayerEnemy.SetEnemyOutside` postfix | clone mimicking player's ids onto `ScavengerModel/metarig`; refresh the enemy's `skinnedMeshRenderers`/`meshRenderers` arrays |
| `CosmeticPatches.ToggleQuickMenu` | `QuickMenuManager.OpenQuickMenu` **and** `CloseQuickMenu` postfix | show/hide `cosmeticGUIGlobalScale/ActivateButton`, hide `CosmeticsScreen` on close |
| `CosmeticSyncPatch.ConnectClientToPlayerObject` | `PlayerControllerB.ConnectClientToPlayerObject` postfix | register the 3 named-message handlers; send own selection with `requestAll:true` |
| `CosmeticSyncPatch.SetInstanceValuesBackToDefault` | `GameNetworkManager.SetInstanceValuesBackToDefault` postfix | unregister the 3 handlers |
| `QuickmenuVisualInjectPatch` | `QuickMenuManager.Start` postfix | `CosmeticRegistry.SpawnCosmeticGUI(mainMenu:false)` (plus out-of-scope player-list work) |
| `MenuManagerLogoOverridePatch` | `MenuManager.Awake` postfix | `SpawnCosmeticGUI(mainMenu:true)` (plus logo/LAN, out of scope) |

Third-party coupling worth knowing: MirrorDecor ships
`[HarmonyPatch(typeof(CosmeticApplication), "UpdateAllCosmeticVisibilities")] [HarmonyPostfix]`
and re-enables `spawnedCosmetics` on layer 23 by walking `__instance.spawnedCosmetics`,
`__instance.parentType`, `__instance.detachedHead` (see
`../notes/mirrordecor-facts.md` §"CosmeticPatch.cs"). So `spawnedCosmetics`, `parentType`,
`detachedHead` are de-facto public API in the LC ecosystem.

---

## 6. UI (what MoreCompany's wardrobe actually is)

Two bundles objects drive it (`MainClass.LoadAssets`, `MainClass.cs:197-209`):

- `cosmeticGUIInstance` = `assets/morecompanyassets/testoverlay.prefab`
- `cosmeticButton` = `assets/morecompanyassets/cosmeticinstance.prefab`

`testoverlay.prefab` hierarchy (abridged, actual names):

```
TestOverlay
├── Canvas                (Canvas + CanvasScaler + GraphicRaycaster)
│   └── GlobalScale  scale 2                       (re-parented under "Systems/UI/Canvas/" in-game)
│       ├── CosmeticsScreen  (starts inactive)
│       │   ├── Blackout        RawImage + CanvasGroup + Button (click-outside to close)
│       │   ├── ObjectHolder
│       │   │   └── ScavengerModel  [LODGroup, scale 288.4]
│       │   │       ├── LOD1 (SkinnedMeshRenderer, active) / LOD2 / LOD3
│       │   │       └── metarig [Animator, scale 0.56]  <- real LC rig, CosmeticApplication host
│       │   └── Spot Light (Light, intensity set to 30 in-game)
│       │   ├── CosmeticsHolderBG / CosmeticsHolderBorder (RawImage / Image)
│       │   ├── Scrollbar, CosmeticsHolder [ScrollRect + Mask] -> Content [GridLayoutGroup + ContentSizeFitter]
│       │   ├── ExitButton, SpinAreaButton [SpinDragger + Button]
│       │   └── EnableButton / DisableButton (+ "DisableCosmeticsText")
│       └── ActivateButton (always-visible in-game tab opener)
└── CanvasCam (Camera + HDAdditionalCameraData)
```

`cosmeticinstance.prefab` (the grid cell): root `Image + Button`, children
`Text (TMP)`, `Icon` (RawImage), `Deselected` (RawImage), `Selected` (RawImage).

`CosmeticRegistry.PopulateCosmetics` (lines 185-240) clears `Content`, instantiates one button per
`cosmeticInstances` entry, sets `Icon.texture = instance.icon`, wires `onClick` to
`ToggleCosmetic(id)` + overlay swap + `WriteCosmeticsToFile()` + `UpdateCosmeticsOnDisplayGuy(true)`.
`SpawnCosmeticGUI` also adds a `CosmeticApplication` to the preview `metarig`
(`parentType = DisplayGuy`) and layers its cosmetics to layer 5 (UI) via `RecursiveLayerChange`.
`SpinDragger` is a drag-to-spin `IPointerDownHandler/IPointerUpHandler` with inertia.

Cost of this UI in a port: it is a bespoke 130-GameObject canvas built for LC's HDRP + uGUI +
TMP. Not reusable (see §7); the *shape* (preview model + grid of icon buttons + spin area) is the
part worth copying.

---

## 7. Are MoreCompany's assets reusable in Alterum? **No.**

1. **Engine version lock.** Both bundles are built by **2022.3.9f1** (bundle header `unityRevision`).
   Alterum is **6000.3.16f1** (verified: `GameAssembly.dll`, `UnityPlayer.dll`,
   `Alterum_Data/globalgamemanagers`, `level0`, and every game bundle header in
   `Alterum_Data/StreamingAssets/aa/StandaloneWindows64/*.bundle` carry `6000.3.16f1`).
   AssetBundles are not forward-loadable across Unity versions; `AssetBundle.LoadFromMemory` of a
   2022 bundle in a 6000 player fails. There is no supported conversion at runtime.
2. **Render pipeline mismatch, even ignoring (1).** Every cosmetic material uses
   `HDRP/Lit` (plus a Shader Graph `EyeballShaderGraph`), i.e. High Definition RP. Alterum ships
   Universal RP only (interop contains `Unity.RenderPipelines.Universal.*` and no HighDefinition
   assembly; our own code uses `Shader.Find("Universal Render Pipeline/Unlit")`). HDRP materials
   and shader graphs cannot render in URP.
3. **String/binding coupling.** Cosmetic prefabs carry `MonoScript` references to
   `morecompanycosmetics/scripts/cosmeticinstance.cs` and `spindragger.cs`, and the UI bundle
   references LC's `Metarig.controller`. Even a successful mesh extraction would need the scripts
   and controller rebuilt.

**Consequence: author our own cosmetic meshes.** Two viable sources, both already covered by
`alterum/plans/cross-version-asset-porting.md` §4.1/§4.4:
(a) procedurally build simple geometry at runtime (cheapest, no external data), or
(b) extract engine-neutral mesh buffers (positions/normals/uvs/indices) + textures from the 2022
bundle offline with UnityPy, ship them as data, and rebuild `Mesh`/`Material` at runtime against
URP. Option (b) is the only way to *look* like MoreCompany's hats, and is legal/portable because
no Unity-serialized asset crosses the version boundary — only numeric arrays.

---

## 8. What Alterum already gives us

All Alterum evidence below is from
`/mnt/c/Program Files (x86)/Steam/steamapps/common/Alterum/BepInEx/interop/Assembly-CSharp.dll`
(Il2CppInterop wrapper — **type/field/method signatures only, native bodies are
`il2cpp_runtime_invoke` calls and are opaque**), plus our own mod source (read-only).

### 8.1 Head/attachment bones — good news

```csharp
// Gameplay.PlayerSystem.PlayerModelBones : MonoBehaviour
Transform _root, _pelvis, _leftWrist, _rightWrist;        // + properties Root, Pelvis, LeftWrist, RightWrist

// Gameplay.PlayerSystem.ThirdViewPlayerModelBones : PlayerModelBones
Transform _neck, _head;  List<Transform> _allBones;       // + properties Neck, Head, AllBones
// Gameplay.PlayerSystem.FirstViewPlayerModelBones : PlayerModelBones   (adds nothing)

// Gameplay.PlayerSystem.ThirdViewPlayerModel : PlayerModel
ThirdViewPlayerModelBones _bones;  // property Bones
CharacterCustomizationController _characterCustomizationController;  // property CharacterCustomizationController
void AttachToModel(GameObject objToAttachToModel);
// Gameplay.PlayerSystem.PlayerModel (base)
ChildrenComponentsGrabber _rootGrabber, _meshesGrabber;  MultiRenderingController _multiRenderingController;
```

So the third-person body exposes **`Bones.Head`, `Bones.Neck`, `Bones.AllBones` plus base
`Pelvis`, `LeftWrist`, `RightWrist`, `Root`** — a cleaner, typed equivalent of LC's
`spine.004` / `arm.*_lower` / `spine` paths. No named "hat" or accessory socket type exists;
`HandheldSystem.Implementation.ActionProcessors.HandheldHolder` /
`ModelHoldersCollection.GetHolder(HandheldHolderData)` is the game's holder abstraction, but it is
built for handheld items (holders, IK, hinge joints) and is overkill for a hat.

Reaching it: our mod already resolves the local body with
`Resources.FindObjectsOfTypeAll(Il2CppType.Of<ThirdViewPlayerModel>())` +
`Under(model.transform, owner)` (`MirrorPlayerVisibility.Rebind`). `Bones` may be null until the
model is set up — rebind on `PlayerModelSetuper.SetupThirdViewModel`/`OnModelSetuped` or on a
`Bones == null` retry.

### 8.2 The game's own wardrobe / outfit system — network of 4 layers

**Layer 1 — customization options (data):**

```csharp
CharacterCustomization.CustomizationOptionSO : ScriptableObject           // base has no exposed fields
CharacterCustomization.BoolCustomizationOptionSO : CustomizationOptionSO  // bool _invert
CharacterCustomization.CustomizationOptionRegistry : ScriptableObject
    List<CustomizationOptionSO> _options;  int Count;
    bool TryGetIndex(CustomizationOptionSO, out ushort);  bool TryGetOption(ushort, out CustomizationOptionSO);
CharacterCustomization.CustomizationValue    // struct: ulong RawLow, RawHigh; static Of<T>(), T As<T>(), FromRaw()
```

The wire/index identity of a customization is a **`ushort` index into the registry list**, and the
value is a 16-byte raw blob with type-specialised `Of<T>/As<T>` helpers.

**Layer 2 — appliers on the model:**

```csharp
CharacterCustomization.ICustomizationApplier { CustomizationOptionSO Option; CustomizationValue DefaultValue; void Apply(CustomizationValue); }
CharacterCustomization.ToggleObjectCustomizationApplier : MonoBehaviour, ICustomizationApplier
    BoolCustomizationOptionSO _option;  Il2CppReferenceArray<GameObject> _targetObjects;  bool _defaultValue;
CharacterCustomization.RenderingControllerCustomizationApplier : MonoBehaviour, ICustomizationApplier
    BoolCustomizationOptionSO _option;  Il2CppReferenceArray<RenderingController> _controllers;  bool _defaultValue;
CharacterCustomization.CharacterCustomizationController : MonoBehaviour
    List<ICustomizationApplier> _appliers;  Dictionary<CustomizationOptionSO, CustomizationValue> _currentValues;
    void Apply(CustomizationOptionSO, CustomizationValue);
    bool TryGetCurrentValue(...);  bool TryGetDefault(...);
    IReadOnlyDictionary<ushort, CustomizationValue> ResolveFullCustomization(registry, known);
```

So an existing "outfit" option today is a bool that toggles a set of **GameObjects** or a set of
**RenderingControllers** on the model. That is exactly the mechanism a hat-toggle would need —
Alterum's own customization already knows how to show/hide meshes on a player model.

**Layer 3 — networking (server-authoritative):**

```csharp
PersistentData.PlayersCustomizationNetworkData : NetworkSingleton<PlayersCustomizationNetworkData>
    Dictionary<ulong, Dictionary<ushort, CustomizationValue>> _serverCustomizations, _localCustomizations;
    events CustomizationUpdated / CustomizationRemoved / CustomizationOptionRemoved
    SaveCustomization(ulong, ushort, CustomizationValue);
    UpdateCustomizations(ulong, IReadOnlyDictionary<ushort,CustomizationValue> toSet, IReadOnlyList<ushort> toRemove);
    SyncFullStateRpc(clientIds[], optionIndices[], rawLows[], rawHighs[]);   ApplyDeltaRpc(...);   RemoveClientRpc(ulong);
    RequestFullStateRpc();  SendFullStateTo(ulong);  GetPlayerCustomizations(ulong);
CharacterCustomization.Networking.NetworkedCharacterCustomizer : NetworkBehaviour
    CharacterCustomizationController _controller;  CustomizationOptionRegistry _registry;  PlayersAppearanceManager _playersAppearanceManager;
    RequestCustomization(option, value);  RequestCustomizationRpc(ushort optionIndex, ulong rawLow, ulong rawHigh);
    ApplyStoredCustomizations();  TryApply(ushort, CustomizationValue);
CharacterCustomization.Networking.CustomizationValueCodecRegistry   // byte id <-> ICustomizationValueCodec, per value type
CharacterCustomization.Networking.CustomizationDataInstaller : MonoInstaller   // Zenject binding of the registry asset
```

**Layer 4 — appearance overrides (priority stack, used by systems that must temporarily change a
player's look):**

```csharp
Gameplay.Appearance.AppearanceOverride      // key, priority, Nullable<ushort> modelIndex, nickname, customizationOverrides
Gameplay.Appearance.PlayerAppearanceState   // base model index + base customizations + ordered overrides;
                                            // EffectiveModelIndex / EffectiveCustomizations / TryGet*IfChanged
Gameplay.Appearance.PlayersAppearanceManager : NetworkBehaviour
    Dictionary<ulong, PlayerAppearanceState> _statesByClientId;
    SetBaseModelIndex(ulong, ushort);  SetBaseCustomization(ulong, ushort, CustomizationValue);  SetCustomization(ulong, CustomizationOptionSO, CustomizationValue);
    AddOrUpdateOverride(ulong, AppearanceOverride);  RemoveOverride(ulong, object key);
    PublishBaseAppearanceSnapshot() / PublishBaseAppearanceSnapshotRpc(...);  WaitForBaseAppearanceSnapshotAsync(int version);
    ApplyEffectiveModel / Nickname / Customizations;  RestoreAllBaseAppearances()
```

**The wardrobe itself selects the body *model*, not cosmetics:**

```csharp
Gameplay.Wardrobe.WardrobeController : NetworkBehaviour
    PlayersAppearanceManager _appearanceManager;  WardrobeOptionalViewController _wardrobeOptionalViewController;
    Construct(PlayersAppearanceManager);  void OnModelSelectedServerRpc(ulong localClientId, ushort chosenModelIndex);
UI.MasterLayer.Lobby.WardrobeOptionalViewController
    Action<ulong,ushort> ModelSelected;  WardrobeModelDisplayer _modelDisplayer;  WardrobeModelRotator _modelRotator;
    TabGroup _modelsButtonsTabGroup;  WardrobeModelButtonedTab _wardrobeModelButtonedTabPrefab;
    IsModelIsTakenAlready(ushort);  OnSelectButtonClicked();  SelectModelButtonForLocalPlayer();  UpdateNicknames()
Gameplay.Wardrobe.WardrobeModelDisplayer
    Dictionary<PlayerModelData, ThirdViewPlayerModel> _models;  DisplayModel(PlayerModelData);  InstantiateModels()
Gameplay.Wardrobe.WardrobePlayerModel  // camera data for the preview
```

`WardrobeModelDisplayer` keeps a **live dictionary of instantiated `ThirdViewPlayerModel`s** to
preview — i.e. the wardrobe already instantiates real player bodies client-side, which is a ready
credible place to show a cosmetic preview.

### 8.3 Can we extend the game's outfit system instead of replacing it?

Technically yes, structurally awkward. To add a hat as a *native* customization option you would
need all of: a `CustomizationOptionSO` instance appended to `CustomizationOptionRegistry._options`
(public `List<>`, so an `Add` is possible with a runtime `ScriptableObject.CreateInstance`), an
applier component living on every `ThirdViewPlayerModel` (the game's models are prefab instances
from bundles — adding components at runtime per instance is possible but must be redone on every
model setup/swap), a codec registered in `CustomizationValueCodecRegistry` for the value type, and
a UI entry in the wardrobe (the model tab list is built from `PlayerModelData`, and *customization*
options have a lobby/settings UI we have not located in this pass).

Verdict: **do not extend it for an MVP.** The registry/applier/network plumbing is index-based and
server-authoritative, so injecting an unregistered index risks desync, and none of the three
existing pieces gives us "an arbitrary mesh parented to the head bone". Keep the native system for
body models, and keep cosmetics as a separate, mod-owned layer. The one native seam worth reusing
later is `CharacterCustomizationController._appliers` + `ToggleObjectCustomizationApplier`
(a bool option that toggles GameObjects) if we ever want a hat to follow the same
persist/network path as the game's own options.

### 8.4 Our mod's procedural-GameObject pattern is sufficient

`Mirror.cs` already does everything a rigid hat needs:

```csharp
ctrl.Root = new GameObject("AlterumMirror");
var mf = ctrl.Root.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
var mr = ctrl.Root.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
mf.sharedMesh = BuildQuad(w, h);
var shader = Shader.Find("Universal Render Pipeline/Unlit");
var mat = new Material(shader) { name = "AlterumMirrorMat" };
mr.sharedMaterial = mat;  NoShadows(mr);                       // ShadowCastingMode.Off, receiveShadows=false
...
private static Mesh BuildQuad(float w, float h) {
    var mesh = new Mesh { name = "AlterumMirrorQuad" };
    mesh.vertices  = new Il2CppStructArray<Vector3>(new[] { ... });
    mesh.uv        = new Il2CppStructArray<Vector2>(new[] { ... });
    mesh.triangles = new Il2CppStructArray<int>(new[] { ... });
    mesh.RecalculateBounds();
    return mesh;
}
```

So a cone/cylinder/box hat is a ~40-line `Mesh` builder in the same style; only `uv`/`RecalculateNormals`
and (optionally) `RecalculateTangents` are new. Materials must be created from a URP shader; the
repo's porting plan notes `Shader.Find` may return a non-null shader whose needed variants were
stripped, so validate visually and prefer `new Material(<existing game URP material>)` if a
template is available.

### 8.5 Interaction with `MirrorPlayerVisibility` — must be handled

`MirrorPlayerVisibility` (our mod, read-only) enumerates the local body's renderers wholesale:

```csharp
var bodyRenderers = _body.GetComponentsInChildren(Il2CppType.Of<Renderer>(), true);   // :128
...
for (int i = 0; i < bodyRenderers.Length; i++) Snapshot(bodyRenderers[i]?.TryCast<Renderer>()); // :248
// RendererState: Isolated = renderer.gameObject.layer != 24 && renderer.gameObject.layer != 15;
//                Drive    = Isolated && renderer.gameObject.activeInHierarchy;
```

`Snapshot` captures layer/enabled/forceRenderingOff/shadow state and the module then **isolates
body renderers onto a mirror-only layer and drives their enabled state** so the body shows up only
in the mirror. A hat mesh parented under the head bone is a child renderer of `_body`, therefore:

- it **will be swept into that set on the next `Rebind()`** (layer 23 `TV_PlayerModelMesh` ≠ 24/15),
  and the hat will end up visible *only in mirrors*;
- conversely, if it were placed on layer 24 (`FV_PlayerModelMesh`) or 15 (`Handheld`) to dodge
  isolation, that layer choice still has to be reconciled with what the first-person and main
  cameras render (see the layer table in `../notes/mirrordecor-facts.md`: 23 = `TV_PlayerModelMesh`,
  24 = `FV_PlayerModelMesh`, 15 = `Handheld`; the actual camera culling masks were **not**
  resolved in that pass either).

Mitigation options, in order of preference:
1. Give cosmetics a marker component (e.g. `CosmeticTag : MonoBehaviour`) and add
   `&& renderer.GetComponent(Il2CppType.Of<CosmeticTag>()) == null` to `MirrorPlayerVisibility`'s
   snapshot loop — smallest, explicit, and keeps mirror logic untouched otherwise.
2. Attach outside `_body` (a sibling root that copies the head bone's world TRS in `LateUpdate`);
   avoids the sweep but pays a per-frame sync and loses exact skinning-aligned motion.
3. Put cosmetics on a layer the snapshot skips and validate both cameras.

Also note the reverse direction: the game hides the local body in first person via
`SimpleRenderingController` disable keys whose rendered groups were observed at runtime as
`Meshes/Other Meshes` (11 renderers) and `Meshes/Head Extras Meshes` (3 renderers)
(MirrorPlayerVisibility header comment). Those renderer sets are gathered through
`Utilities.ComponentsGrabbers.ChildrenComponentsGrabber` (`_recursiveSearch`, `_includeInactive`)
by `SimpleRenderingController.EnsureCachedRenderers()`, i.e. **cached on demand, not per frame**.
A hat added after that cache would not be hidden with the body when the player goes first-person,
so the cosmetics module must gate its own visibility (e.g. copy the enabled state of a body
renderer, or watch `Cameras.SwitchActionProcessors.PlayerPOVController`). Whether the "Head Extras
Meshes" grabber would pick up a new head child on a later `EnsureCachedRenderers()` call is
**unverified** — if it does, the hat would follow body visibility for free, which is worth one
runtime experiment.

---

## 9. Recommended minimum viable cosmetic

Local-only, one procedurally built hat on the head bone:

1. **Data model (data-driven, tiny JSON next to the mod, not AssetBundles):**
   `[{ "id":"alterum.partyhat", "kind":"hat", "bone":"head", "offset":[0,0.10,0],
       "euler":[0,0,0], "scale":[1,1,1], "shape":"cone", "color":"#E03131" }]`.
   `kind` mirrors MoreCompany's `CosmeticType` (keep `hat` and `chest`/`hip`/`wrist` for later).
   `shape` selects a procedural builder; `bone` maps to `ThirdViewPlayerModel.Bones.Head`
   (plus `Neck`, `Pelvis`, `LeftWrist`, `RightWrist` from the base class).
2. **Attachment:** `var hat = new GameObject("Cosmetic_"+id); hat.transform.SetParent(head, false);`
   then apply per-cosmetic `offset/euler/scale`. Author the mesh in *head-local* units and
   calibrate against the real bone scale at runtime (log `head.lossyScale` and a `Bounds` of the
   head's skinned renderer once) — do **not** port MoreCompany's 0.38 / ÷4.3 fudge factors.
3. **Mesh:** `BuildCone/BuildBox/BuildCylinder` in the style of `MirrorFactory.BuildQuad`
   (`Il2CppStructArray` vertices/uvs/triangles, `RecalculateBounds`, `RecalculateNormals`);
   `MeshFilter`+`MeshRenderer` via `AddComponent(Il2CppType.Of<T>()).TryCast<T>()`; material from a
   URP shader/template; `ShadowCastingMode.Off` optional (a hat should probably cast).
4. **Selection & persistence:** one selected id in our existing config/JSON; toggle through a
   minimal in-game UI. For the UI, the *cheapest correct* choices are (a) an `OnGUI` panel (the
   module already exposes `OnGUI` in `HotModule`), or (b) a runtime-built uGUI canvas with a
   `RawImage` per cosmetic (icon = generated swatch or a baked 32×32 texture), a `Button`, and the
   same selected/deselected overlay idiom as MoreCompany. Do not attempt the LC 3D preview model
   in an MVP.
5. **Visibility gating (required):** hide the hat when the local body is hidden
   (first person / cutscene / death). Simplest robust rule: each `LateUpdate`, set the hat's
   `Renderer.enabled` to the current enabled state of a chosen body renderer (e.g. the head's
   `SkinnedMeshRenderer`), or query the POV controller.
6. **Interaction with the mirror:** either exclude cosmetic renderers from
   `MirrorPlayerVisibility`'s snapshot with a marker component (preferred), or place them on a layer
   that mirrors' sweep skips — and then verify the main camera still draws them.
7. **Explicitly dropped from MoreCompany:** player-count/lobby work, custom messaging
   (`MC_SV/CL_*`), the CSV/JSON relay, dead-body and masked-enemy cloning, per-profile save,
   `.cosmetics` bundle loading, the 3-D preview wardrobe, `SpinDragger`, `ParentType`/`DisplayGuy`
   paths, HDRP materials, `detachedHead` special-casing.
8. **Keep from MoreCompany's design:** prefab-like per-cosmetic record (id + type + icon + local
   TRS), one registry dictionary keyed by id, bone chosen by `cosmeticType`, `List<string>
   equipped`, and a single `UpdateAllCosmeticVisibilities`-style function — those are the parts
   other mods (MirrorDecor) ended up depending on.

If the hats should eventually look like MoreCompany's: switch step 3 from "procedural" to
"UnityPy-extracted numeric mesh + texture, rebuilt at runtime", per
`plans/cross-version-asset-porting.md` §4.1/§4.4. No Unity-serialized MoreCompany asset can be
loaded as-is.

---

## 10. Flagged unknowns / not determined

1. **`ChangeOutfit`** — `Assembly-CSharp.dll` metadata contains
   `NativeMethodInfoPtr_ChangeOutfit_Private_Void_Il2CppReferenceArray_1_Renderer_0`, i.e. a private
   `void ChangeOutfit(Il2CppReferenceArray<Renderer>)`, whose neighbouring field names are
   `get_Renderers` (`IReadOnlyList<Renderer>` and its abstract override) and
   `TryGetRenderers` (`Il2CppReferenceArray<Renderer> byref`). It belongs to some
   `IRenderersCollection`-implementing type in the `RenderersRenderingController` family, but it was
   **not** found in `RenderersRenderingController`, `PlayerModelRenderersRenderingController`,
   `SimpleRenderingController`, `MultiRenderingController` or the 30 `CharacterCustomization.*` /
   `Gameplay.Wardrobe.*` / `Gameplay.Appearance.*` types decompiled here. No `ChangeOutfit` type
   exists; there is no `Outfit` type in Alterum at all. Treat "the game's outfit swap" as
   *customization-option + renderer-set swapping*, and the exact owner of `ChangeOutfit` as
   unidentified (a full-project decompile of Alterum aborts, and enumerating ~5,956 types one by one
   was out of scope).
2. **Alterum has no cosmetics/hat concept.** No type name in the interop contains `Cosmetic`,
   `Accessory`, `Wearable` or `Outfit` (case-insensitive search over all 5,956 public type names
   returns 0; `Hat` matches only inside `Chat…`/`Settings…` names). What exists is body **model** selection
   (wardrobe) and boolean customizations that toggle GameObjects or RenderingControllers. What
   concrete options exist (hair styles, scars, …) lives in ScriptableObject assets inside
   Addressables bundles; enumerating them was not attempted (the English localization table stores
   GUID-referenced values in a separate shared-data asset, so a quick text dump did not yield the
   option names).
3. **Camera culling masks / actual layer behaviour for 15, 23, 24** remain unresolved (inherited
   from the mirrordecor recon). Necessary if cosmetics are made to dodge the mirror sweep by layer.
4. **`ThirdViewPlayerModel.Bones` population timing** (null before `SetupModel`?) was not observed
   at runtime; our mirror code already treats model setup as asynchronous.
5. **Whether the game's "Head Extras Meshes" grabber would adopt a newly added head child** on a
   later `EnsureCachedRenderers()` is unverified (§8.5).
6. **`CustomizationOptionSO`'s base fields** are not exposed by the interop wrapper (empty class,
   only `BoolCustomizationOptionSO._invert` visible), so the exact data an option carries
   (icon? localization key? display name?) is unknown.
7. All Alterum method *bodies* are `il2cpp_runtime_invoke` stubs — the existence and signature of
   every API above is confirmed; the runtime semantics of e.g.
   `PlayersAppearanceManager.ApplyEffectiveCustomizations` are not.
