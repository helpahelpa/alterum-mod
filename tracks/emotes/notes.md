# BetterEmotes → Alterum: reconnaissance notes

Read-only recon. No game launched, nothing built, no existing mod file touched.
Scratch lives in `alterum/tracks/emotes/` (this dir): `ref/` (zip + unzip),
upstream source and decompiled references were kept outside the repo (see
`~/assets/monorepo/alterum-data/research-archive/`).

Sources of truth used:
- Shipped package 1.5.8: `ref/unz/` (`BetterEmotes.dll` 60 KB, `BetterEmotes/animatorbundle` 216 KB,
  `BetterEmotes/animationsbundle` 1.87 MB, README/CHANGELOG/icon). Thunderstore API
  `https://thunderstore.io/api/experimental/package/KlutzyBubbles/BetterEmotes/` → `latest.version_number = 1.5.8`.
- Upstream C# + Unity project: GitHub `KlutzyBubbles/lc-better-emotes` `main`
  (`src/lc-better-emotes-main/BetterEmote/**.cs`, `.../Unity/moreemotes/*.anim`). The DLL decompile
  (`decomp/`) matches the source, so the source is quoted rather than the decompile.
- Bundles inspected with UnityPy 1.25.4; `ilspycmd 8.2.0.7535` for the interop.

---

## 1. What the reference mod actually does

**One sentence:** it does *not* implement emotes itself — it **replaces the player's
`RuntimeAnimatorController` with a bigger copy of Lethal Company's `metarig` controller** whose
`EmotesNoArms` state machine has extra `Dance3…Dance9` / `WriteSign` / `ShowSign` states, then drives
the game's *existing* animator integer parameter `emoteNumber`.

That is the whole trick. No `AnimationClip.Play`, no `AnimatorOverrideController` per-clip overrides,
no Playables graph, no IK-authored poses at runtime.

### 1.1 Triggering

- **Keybinds** via `Rune580-LethalCompany_InputUtils` (`Utils/Keybinds.cs : LcInputActions`), one
  `InputAction` per emote (`Middle_Finger`, `Clap`, `Shy`, `Griddy`, `Twerk`, `Salute`, `Prisyadka`,
  `Sign`, plus `SignSubmit`, `SignCancel`, `EmoteWheel`, `EmoteWheelNextPage`,
  `EmoteWheelPreviousPage`, `EmoteWheelController`). Bindings are *generated* at plugin load from
  config defaults (`Settings.DefaultInputList[i].keyboard/controller`) and then appear in the game's
  own rebind screen — the mod never draws a keybind UI.
- **Emote wheel**: `AssetScripts/EmoteWheel.cs` (a MonoBehaviour on an instantiated prefab
  `Resources/MoreEmotesMenu.prefab`, parented to `Systems/UI/Canvas` in `RoundManager.Awake` postfix).
  8 blocks/page, mouse-angle selection around screen centre (`ignoreRadius`/`stopRadius` derived from
  aspect), `alterPage(±1)` on scroll, gamepad right-stick variant. Release of the wheel key →
  `EmoteKeybindPatch.onEmoteKeyWheelCanceled` → `player.PerformEmote(context, id)`.
- **Direct keys** (defaults 3..0) call `PerformEmote(context, emoteID)` directly.
- Emote id space (`Utils/EmoteDefs.cs`): `1 Dance, 2 Point` (vanilla), `3 Middle_Finger, 4 Clap,
  5 Shy, 6 Griddy, 7 Twerk, 8 Salute, 9 Prisyadka, 10 Sign`; **1000-offset doubles**
  (`1003` double middle finger, `1004` double clap) and `1010` = Sign-with-text.

### 1.2 How animation is played ("the mechanism")

`Patches/EmotePatch.cs`, all on LC's `PlayerControllerB`:

| Patch | Purpose |
|---|---|
| `Start` postfix | `AddComponent<CustomAudioAnimationEvent>` on `ScavengerModel/metarig`, `AddComponent<CustomAnimationObjects>`, cache `movementSpeed` as `LocalPlayer.BaseSpeed`, spawn the Sign prop |
| `Update` prefix | mirror `performingEmote` into `playersPerformingEmotes[id]`; on falling edge reset IK weights |
| `Update` postfix | **swap `playerBodyAnimator.runtimeAnimatorController`** to `local` (owner) or `others` (non-owner, non-VR) on the first frame; restore `movementSpeed`; apply griddy/prisyadka speed multipliers; move `localArmsRotationTarget` to the "free arms" clone when the emote needs detached arms |
| `PerformEmote` **prefix, returns false** | full replacement of vanilla: cooldown (`timeSinceStartingEmote >= Settings.EmoteCooldown`, default 0.2 s), `CheckConditionsForEmote()`, then `playerBodyAnimator.SetInteger("emoteNumber", id); performingEmote = true; StartPerformingEmoteServerRpc(); syncAnimator?.UpdateEmoteIDForOthers(id)` and side effects (legs material for Prisyadka, sign input field for Sign) |
| `CheckConditionsForEmote` postfix | for griddy/prisyadka and for sign: a **stricter** condition set (no special-interact anim, not dead, not jumping, for griddy also `moveInputVector.x == 0` and not sprinting/crouching; for sign `!isWalking`) |
| `StopPerformingEmoteServerRpc` postfix | `SetInteger("emoteNumber", 0)`, sync 0 to others, `CurrentEmoteID = 0`, re-enable badges |
| (`RoundManager.Awake`, `HUDManager.EnableChat_performed`/`SubmitChat_performed`) | wheel + sign-UI prefab spawn; block chat while the sign text field is open |

Controller acquisition (`Plugin.loadAnimatorControllers`):

```csharp
EmotePatch.local  = new AnimatorOverrideController(bundle.LoadAsset<RuntimeAnimatorController>("Assets/MoreEmotes/NEWmetarig.controller"));
EmotePatch.others = new AnimatorOverrideController(bundle.LoadAsset<RuntimeAnimatorController>("Assets/MoreEmotes/NEWmetarigOtherPlayers.controller"));
```

The `AnimatorOverrideController` wrapper is inert here (no `overrides` set) — it is just a controller
swap. `others` is a slimmed controller (no transition-name table, fewer states) used for remote bodies.

**Netcode** is minimal and reuses the game's own replication: vanilla `StartPerformingEmoteServerRpc`
already replicates `emoteNumber` to everyone; `Netcode/SyncAnimatorToOthers.cs` (a `NetworkBehaviour`
injected with Ozone's `RuntimeNetcodeRPCValidator`) adds `UpdateCurrentEmoteIDServerRpc`/
`ClientRpc(int)` purely because the *custom* ids (>2) are not what vanilla would send.

### 1.3 First person vs third person

No first-person animation work at all. The mod keeps the body model in sync by
`AssetScripts/LocalPlayer.SpawnLegs()` — instantiating a `plegs.prefab` ("first person legs") under
the animator and re-pointing `FistPersonLegs`, so the visible legs match the third-person body. Arms
are handled by duplicating `localArmsRotationTarget` (`freeArmsTarget`) and switching to it for
emotes that need the arms away from the camera (`LocalPlayer.IsArmsSeparatedFromCamera`).

### 1.4 Movement locking / cancellation / speed

- The player is **not** frozen. LC's own rules stop the emote when the state machine changes
  (`CheckConditionsForEmote` returning false); the mod only *tightens* those rules per emote.
- Two emotes intentionally move: griddy `movementSpeed = BaseSpeed * 0.5`, prisyadka `* 0.34`.
  `movementSpeed` is re-asserted from the cached base every `Update` postfix.
- Cancellation paths: (a) releasing the wheel outside the ring with `stopEmote` → `performingEmote=false`
  + `StopPerformingEmoteServerRpc()`; (b) any behaviour change that fails `CheckConditionsForEmote`;
  (c) pressing another emote (ids differ → state transition);
  (d) `timeSinceStartingEmote` reset gives a per-emote cooldown.
- Double-emote toggle: re-pressing the same id while `performingEmote` adds/subtracts the 1000 offset.

### 1.5 Bundle contents (the "content" half)

`animatorbundle` (UnityFS, `unityVersion 2022.3.9f1`, serialized `2022.3f1`): 66 `AnimationClip`
(64 unique), 2 `AnimatorController`, 1 `AssetBundle`. Clips are Lethal Company's own set re-exported
(`Walk`, `CrouchWalk`, `Jump`, `HoldOneHandedItem`, …) plus a duplicate `Dance1`/`Dance2`.
**The emote clips are not in this bundle** — the controllers reference them *externally*
(`m_FileID = 1`) and they live in:

`animationsbundle` (same Unity version): 12 `AnimationClip` — `Middle_Finger` (0.87 s),
`Clap` (1.00 s, loop), `D_Clap`, `Shy` (2.50 s), `The_Griddy` (6.53 s), `Twerk` (0.25 s),
`Salute` (1.78 s), `Prisyadka` (6.75 s), `Sign` (2.08 s), `D_Sign`, `D_Middle_Finger`,
`Idle1Fix`; plus 3 `AudioClip` (single claps), 187 GameObject (wheel HUD, sign, legs prefabs),
162 MonoBehaviour, 24 Texture2D, 5 Material, 4 Shader, 1 Mesh.

Controller state/layer names survive in the packed controller's string table (`m_TOS`), which is how
the graph below is known:

- Layers: `Base Layer`, plus one upper-body layer whose **sub-state-machines** are
  `EmotesNoArms`, `UpperBodyEmotes`, `SpecialAnimations`, `HoldingItemsRightHand`,
  `HoldingItemsBothHands` (state strings in the table read `<machine>.<state>`, e.g.
  `Base Layer.Walk`, `EmotesNoArms.Dance1`).
- `EmotesNoArms` sub-graph: `defaultState`, `Dance1`…`Dance9`, `Dance3_1`, `Dance4_1`, `WriteSign`,
  `ShowSign`.
- AnyState transition names `AnyState -> Dance3`, `AnyState -> Dance4_1`, `AnyState -> ShowSign`, …
- Parameters seen: `emoteNumber` (int), `crouching`, `hinderedMovement`, `notInSpecialAnim`,
  `animationSpeed`, `tiredAmount`, `GravityWeight`, `useTZPItem`, `shovelHit`, etc.

**Inferred, not proven:** that each `AnyState -> DanceN` edge is gated on `emoteNumber == N` and the
return edge on `emoteNumber == 0`. The state machine is stored as a compiled blob; UnityPy cannot
decode transitions, so the gating conditions come from the naming plus `SetInteger("emoteNumber", id)`.

---

## 2. Is the reference content reusable in Alterum? **No.** (direct answer)

Two independent blockers, either one fatal:

**(a) Unity version.** Both bundles are `UnityFS 5.x`, `unityVersion 2022.3.9f1`, serialized-file
format tagged `2022.3f1`. Alterum is Unity **6000.3.16f1** (`Alterum_Data/resources.assets` header
says `6000.3f1`). Asset bundles are not cross-major-version compatible: the `AnimationClip`
compiled-clip layout and the packed `AnimatorController` blob are version-specific native
serialization. A 2022 bundle is not expected to `AssetBundle.LoadFromFile` into a 6000 player at all,
and `AnimatorController` is the least likely type to survive. Treat this as "cannot load", not
"needs testing".

**(b) Rig binding, which is the real killer.** LC's emote clips are **Generic** clips — they are
bound to transform paths by CRC32, not to humanoid muscles. Decoded from the shipped `Dance1`
(`m_ClipBindingConstant.genericBindings`, `typeID = 4` = Transform), the paths are:

```
spine                                  2463100623
spine/spine.001                             254801669
spine/spine.001/spine.002                  4095213152
spine/spine.001/spine.002/spine.003          674009458
spine/thigh.L                                49267413
spine/thigh.R                              4175495094
```

i.e. LC's metarig root has a `spine` child which holds `spine.001` and `thigh.L`. Alterum's rig is
**different**: `pelvis → spine.001 → spine.002 → spine.003`, `thigh.L` under `pelvis`, plus
`spine.004`, `toe.L/R`, `pelvis.L/R_end`, `hand.*_end`. Generic clips would animate nothing (paths
resolve to no transform). Contrast with Alterum's own clips, which are **Humanoid** (see §3.3).

The repo's `.anim` sources are also *name-scrubbed* (`path: path_0xDB36EA2A_lIuHqpN`), so even the
text source cannot be re-targeted by name.

**Conclusion: the port is mechanism-only.** Content must be either (i) a clip already in Alterum,
(ii) a Humanoid clip from elsewhere, or (iii) a clip authored against Alterum's bone names.

Note what *is* portable: the idea, the id scheme, the per-emote condition table, the wheel UX, and
the "swap/extend the player's animation graph and drive one int" approach. Also portable as *design*
data: clip durations/loop flags in §1.5.

---

## 3. What Alterum gives us

### 3.1 The animation stack

- `Kybernetik.Animancer.dll` + `Kybernetik.Animancer.FSM.dll` in `BepInEx/interop/`. The player
  `Animator` has **no controller** (`3P_ConcreteModel__Template`'s Animator: `m_Controller = null`) —
  Animancer drives everything.
- `Animations.ExtendedAnimancer.Core.ExtendedAnimancerComponent : Animancer.AnimancerComponent`
  (adds `Pause`/`Unpause`), and
  `Animations.ExtendedAnimancer.Core.LayeredAnimancerComponent<TLayerType> : ExtendedAnimancerComponent`
  with an indexer `AnimancerLayer this[TLayerType]` and `Dictionary<TLayerType,int> _layerTypeToIndex`.
- `Animations.ExtendedAnimancer.PersonAnimancer.PersonAnimancerComponent :
  LayeredAnimancerComponent<PersonAnimancerComponent.PersonLayers>`, with
  `enum PersonLayers { BaseLayer, LeftHand, RightHand, BothHands, Legs }` — **5 layers**.
- `Gameplay.PlayerSystem.PlayerModelPersonAnimancerController : ModelPersonAnimancerController`
  (`MonoBehaviour`) owns it:
  - `CurrentPersonAnimancerComponent` (get), `Initialize(PlayerModel)`, `Deinitialize()`,
    `IsAnimancerAndAnimatorEnabled`, `DisableAnimancerAndAnimator(object key)` /
    `EnabledAnimancerAndAnimator(object key)` (note the typo in the shipped name),
    `RegisterProcessor/RemoveProcessor(IPlayerModelPersonAnimancerControllerProcessor)`,
    events `Initialized`, `Deinitialized`, `AnimancerAndAnimatorEnabled/Disabled`.
  - subclass adds `OnPlayerCreated(Player)` (a `IPlayerCreatedHandler`).
- Playing a clip is the ordinary Animancer API (`Kybernetik.Animancer.dll`, decompiles cleanly):
  `AnimancerComponent.Layers : AnimancerLayerList` with `AnimancerLayer Add()`, `SetMinCount(int)`,
  `AnimancerLayer this[int]`; `AnimancerLayer.Play(AnimationClip clip, float fadeDuration,
  FadeMode mode = FadeMode.FixedSpeed) : AnimancerState`, `Play(AnimationClip)`,
  `IsPlayingClip(AnimationClip)`, `StopWithoutWeight()`, `CreateState(AnimationClip)`,
  `SetMask/SetAdditive`, `SetDebugName`. `AnimancerComponent.Play/Stop/TryPlay/IsPlaying` are the
  same one-liners on layer 0.

### 3.2 Getting the local player's third-person animancer

From `Gameplay.PlayerSystem.Player : Unity.Netcode.NetworkBehaviour` (`IsLocalPlayer`, `IsOwner`,
`GetRefs<T>() where T : class`):

```
Player.GetRefs<PlayerModelReferences>()
  .ThirdPlayerModelPersonAnimancerController      // PlayerModelPersonAnimancerController
      .CurrentPersonAnimancerComponent            // PersonAnimancerComponent
        [PersonLayers.BaseLayer]                  // AnimancerLayer
```
(`PlayerModelReferences` also exposes `FirstPlayerModelPersonAnimancerController`,
`ThirdPersonModelRenderingController`, `ThirdViewAnimationConfig`, `DoingSomethingHandler`,
`MovementBlendingHandler`.) Re-acquire after `PlayerModelSetuper.SetupThirdViewModel(...)` —
the model is re-instantiated on wardrobe change / respawn.

### 3.3 Content already in the game — and it is Humanoid

`Alterum_Data/resources.assets` (Unity 6000.3.16f1) contains **40 `AnimationClip`s**, 20 of them
personality/gesture idles from a POLYGON-style pack:

```
A_POLY_IDL_Bored_FootTap_Masc        A_POLY_IDL_Bored_KickGround_Masc
A_POLY_IDL_Bored_SlumpBack_Masc      A_POLY_IDL_Bored_SwingArms_Masc
A_POLY_IDL_CheckWatch_Femn           A_POLY_IDL_HandsOnHips_Base_Loop_Femn
A_POLY_IDL_HandsOnHips_Grumpy_Loop_Femn   A_POLY_IDL_Inspect_Feet_Femn
A_POLY_IDL_Inspect_Legs_Femn         A_POLY_IDL_Inspect_Torso_Femn
A_POLY_IDL_Look_L_Behind_Masc        A_POLY_IDL_Look_R_Behind_Masc
A_POLY_IDL_PickNose_Loop_Masc        A_POLY_IDL_PlayWithHair_OneHand_TuckBehindEar_Femn
A_POLY_IDL_PlayWithHair_TwoHands_FluffUp_Femn  A_POLY_IDL_Posture_Shy_Loop_Femn
A_POLY_IDL_Posture_Slumped_Loop_Femn  A_POLY_IDL_Pray_CrossChest_Femn
A_POLY_IDL_Pray_Standing_Loop_Femn    A_POLY_IDL_Yawn_Femn / _Masc
```
plus `Skeleton_Pose_TwoHandsShackles_Sitting`, `FlipAnimation`, `Recorded(1..2)`,
`FemaleAdventure_Body_TP_SettingsExample`, `Female_Body_TP_DaggerInstantKill_{Start,End}_Victim`,
`Hands_FP_*`, `Start_/Loop_/EndKill|Release_Att|Vic`.

**These are Humanoid clips.** Evidence: every one has `m_EulerCurves/m_PositionCurves/m_FloatCurves`
empty and 130 `genericBindings` entries all with `path = 0`, `typeID = 95` (`Animator`), `attribute`
= 7…136 — that is Unity's humanoid muscle-curve encoding, not transform paths.

The rig is Humanoid too: `Avatar "RiggedSkeletonAvatar"` has 20 human bones mapped
(`pelvis→Hips, thigh.L→LeftUpperLeg, shin.L, foot.L, spine.001→Spine, spine.002→Chest,
spine.003→UpperChest, neck, head, shoulder/upper_arm/forearm/hand .L/.R`) over 35 skeleton entries:
`root, pelvis, pelvis.L/R(+_end), spine.001..004, shoulder.L/R, upper_arm/forearm/hand .L/R(+_end),
neck, head(+_end), thigh/shin/foot/toe .L/R(+_end), SM_Skeleton_LOD0`.

→ Humanoid clips **retarget** onto any Humanoid avatar, so Alterum's own POLY_IDL clips (and any
Mixamo/free humanoid clip) can be played on the player model with zero authoring. That is the
content answer for the port, and it is much better than BetterEmotes gives us.

**Unverified:** whether the *player* model's avatar is that Humanoid one. `3P_ConcreteModel__Template`
/`1P_ConcreteModel__Template` have an `Animator` whose `m_Avatar` PPtr cannot be trusted from a
typettree read in this build (it resolves to nonsense like `Texture2D:Clowds 2`), and the corpse
model `3P_ConcreteModel_Corpse_Skeleton` does use `RiggedSkeletonAvatar`. Read `animator.avatar.isHuman`
at runtime before relying on retargeting.

### 3.4 State machines we could integrate with instead of bypassing

- `Gameplay.PlayerSystem.ASMCConditionState` (`MonoBehaviour`): `CanEnterStateFrom(ASMCConditionState)`,
  `OnStateEnter()`, `OnStateExit()`, `Initialize(PersonAnimancerComponent)`, `Order`,
  `PersonAnimancer` (protected).
- `ASMCConditionStateMachine : ConditionStateMachine<ASMCConditionState>` with `DefaultState`,
  `OnBeforeStateEnter`/`OnAfterStateExit`, `LateUpdate`, `IsAnimancerAvailable`.
- `ASMCStatesLinker` (on the player model) holds `_stateMachine` and `ASMCConditionState[] _states` —
  the discovery point for existing states (`WalkingASMCConditionState`,
  `ThirdViewHandheldASMCConditionState`, `DoingSomethingASMCConditionState`, …).
- The game already has a "playing a one-off animation" notion:
  `DoingSomethingASMCConditionState` with `AnimatorStateMachineController _doingSomethingASMC` and
  `float _afterDoingSomethingDelaySec`, plus `DoingSomethingHandler.StartDoingSomething(object key)` /
  `StopDoingSomething(object key)` / `IsDoingSomething` / `StateChanged`. This is the clean,
  game-native way to say "the player is busy doing something" and have the ASMC play the clip — but it
  requires an `AnimatorStateMachineController` asset, i.e. authored content.

### 3.5 Input: can we add a keybind with no UI?

Yes, three levels of effort:

1. **No registration at all** — poll `UnityEngine.InputSystem.Keyboard.current[Key.F6].wasPressedThisFrame`
   (or a pad button) from our own pump `Update`. `Unity.InputSystem` is already referenced by the
   existing mod (`Alterum.References.props:74`). Fine for a first step, invisible to the options menu.
2. **Our own `InputAction`** created in code (`new InputAction(binding: "<Keyboard>/f6")`, `.Enable()`),
   callback-based, no UI. Same assembly.
3. **Rebindable row in the game's settings** — the game's controls page is
   `HWG.Settings.Controls.RebindActionUI` / `RebindActionData(string PreviousDisplayName,
   string DisplayString, string DeviceLayoutName, string ControlPath)`, with
   `InputSystem.Core.KeyBinding` / `NetworkKeyBinding` and `Services.RebindActionsCollection`;
   the runtime input wrapper is `PInput.PlayerInput : PlayerInputBase` (`MovementControls`,
   `PlayerActionsControls`, `AlwaysEnabledControls` (has `FButtonsActions`), `UIControls`,
   `InputActionAssets`, `GetInputAction(InputActionReference)`, `GetBindingName(...)`).
   Feasible, but it means building a row into a scene-owned settings view — phase 2+.

Note the injected-type constraint the existing mod already works around:
`AlterumModelSwap/PumpBehaviour.cs` is the only il2cpp-injected `MonoBehaviour` (registered with
`ClassInjector`), it forwards `Update/LateUpdate/OnGUI` into the reloadable assembly. Any wheel/HUD
controller we add needs the same treatment, and its `Update` never hot-reloads.

### 3.6 UI framework (for a wheel later)

Already proven in this repo (`AlterumModelSwap.Hot/ModelSwapRuntime.cs:406-440`,
`ActivateOrClick`): walk `Resources.FindObjectsOfTypeAll(Il2CppType.Of<MonoBehaviour>())`, match
`PathOf(go.transform)`, then

- `UI.Elements.Button`: `IsInteractable`, `CanClick()`, `Click(PointerEventData)`,
  `ClickedSafe` / `ClickedSafeReferenced` / `ClickedUnsafe` (invoke manually after `Click`),
- `UI.Elements.Tab`: `Activate()`.

So a radial menu is a *later* layer on top of a working emote, not a prerequisite. No wheel exists to
copy; BetterEmotes' wheel prefab cannot be reused (2022 bundle, and it is built for LC's `Text`-based
UGUI + `Systems/UI/Canvas`).

---

## 4. Recommended minimal first step

**"One emote, one key, local model only."** No bundle, no UI, no netcode, no wheel.

1. **Pump** `Update`: if `Keyboard.current[Key.F6].wasPressedThisFrame` → toggle emote.
2. **Resolve** local `Player` (via `NetworkBehaviour.IsLocalPlayer`), `GetRefs<PlayerModelReferences>()`,
   `ThirdPlayerModelPersonAnimancerController.CurrentPersonAnimancerComponent`; bail if null.
3. **Content** — take a clip that is already in memory rather than authoring one:
   `Resources.FindObjectsOfTypeAll(Il2CppType.Of<AnimationClip>())` and pick by name
   (`A_POLY_IDL_HandsOnHips_Base_Loop_Femn`, `A_POLY_IDL_Posture_Shy_Loop_Femn`,
   `A_POLY_IDL_CheckWatch_Femn` — all Humanoid, so they retarget). Log what is actually found; do not
   hardcode a name until the runtime list is confirmed. (Fallback if the list is empty:
   `Resources.Load<AnimationClip>("…")` with a path found later, or build a `AnimationClip` at runtime
   with `SetCurve` against the known bone paths `pelvis`, `spine.001`, … — we now know all 35 names.)
4. **Play** on a *new* layer so the ASMC cannot immediately stomp it:
   `var layer = personAnimancer.Layers.Add(); layer.SetDebugName("Emote"); layer.Play(clip, 0.25f);`
   Hold ~clip length (`AnimancerState.Length`, `IsPlaying`, `IsLooping` all exist), then
   `layer.SetWeight(0)` / `layer.Stop()` (`AnimancerNode`) and `DestroyStates()`. Do not touch layers
   0–4 (`BaseLayer…Legs`).
5. **Exit conditions** for step 1: stop on movement input, on any `ASMCConditionState` change, or after
   a fixed 3 s. That is enough to eyeball "does the body move".
6. **Gate**: only run when `ModelPersonAnimancerController.IsAnimancerAndAnimatorEnabled` and the model
   is not `Deinitialized`; re-resolve after `PlayerModelSetuper` re-instantiates the model.

Build note (read-only observation, do not apply here): `refs/Kybernetik.Animancer.dll` is already
copied into `~/assets/monorepo/alterum-data/refs/` but is **not** referenced in `Alterum.References.props`;
`UnityEngine.AnimationModule` *is* (line 98). Porting needs one added `<Reference Include="Kybernetik.Animancer">`.

Then, in order: (a) per-emote ids + cooldown + the condition table from §1.2, (b) netcode
(server RPC → clients play the same clip on the replicated body), (c) keybinds in options, (d) wheel.

---

## 5. Explicitly *not* determined

- The transition conditions inside either BetterEmotes controller (compiled blob; only names known).
  The "`AnyState -> DanceN` gated on `emoteNumber == N`" mapping is inference.
- Whether the 2022 bundle *fails* on load in Unity 6 vs. loads partially — not tested, by design
  (no game launched). Recommendation stands regardless because of the Generic-clip binding problem.
- Whether Alterum's player avatar is Humanoid (`Animator` typetree reads are unreliable in this build;
  `RiggedSkeletonAvatar` is Humanoid and is used by the 3P corpse model, which is suggestive only).
- Whether `Layers.Add()` survives the ASMC/`PersonAnimancerComponent` re-initialisation, and whether the
  graph is rebuilt (`Initialized` event fires again) on model re-instantiation.
- Whether the POLY_IDL clips are reachable by `Resources.Load` path, or only via objects already
  referenced in memory. Which asset references them was not traced.
- Whether the ASMC writes layer weights every `LateUpdate` (if it does, a higher layer must be
  re-weighted after it, or `AnimancerComponent.Evaluate()` ordering matters).
