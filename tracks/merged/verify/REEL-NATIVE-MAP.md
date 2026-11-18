# Reel: one interaction path, in maps (E native)

Tree: `tracks/merged/mod` (hot only). Hot-deployed to `C:\AlterumB\BepInEx\hot\b`, run as
`-alterumInstance=b`; generations 1→8, the final three reloads exercised the rebuild path.
Nothing was written to the pin, `-pristine`, `C:\Interop`, `C:\AlterumTests` or Steam.

## 1. The two NREs, and what was actually null

**`InteractionProvider<T,TData>.InitializeInteraction()` NRE.** The cloned lookable's own
`TapInteractionProvider` was never `Construct()`ed: `Construct(IPlayerMediator)` is private, takes
the local player's mediator, and only the player's own prefab goes through it. Its `_interaction` /
`_interactionData` are null, so `InitializeInteraction()` (which reads `_interactionData`) is a null
dereference — and the old skill *called* it (`PrepareProvider` / `WireProvider` step 1) on every
build and on every 0.5 s re-wire. This was our bug on every build; it is not called any more. The
clone's dead provider is now `DestroyImmediate`d while the clone is still under the inactive holder
(so its own Awake never logs "Interaction Provider Not Constructed" → no `[Error]`), and the
lookable is pointed at a provider the game constructed for this player.

**`InteractableInputHandler.OnInteractionPerformed()` NRE.** That method dereferences
`_previousLookableInteraction` / `_previousLookableInteractable`; the handler fills them only while
the looker holds a lookable. The unattended `press=e` fallback invoked it unconditionally, so a press
made with nothing looked at NREd inside the game's code. The fallback now requires a bound
interaction and otherwise logs: `press: not delivered to the reel - the input handler has no
interaction bound, i.e. no lookable is active` (verified at 7 m, accepted unchanged, no exception).

The third error the task would have hit next was found while verifying in a map, and is not an NRE
of ours: the altar template's `ShowAnywayPostExcluder.FillConstraintsToExclude` throws when the
lookable is in the looker's radius, because the clone's post-excluder still references the template
constraints that get stripped. It only appeared when a booth was cloned from *another booth's*
interactable; `FindInteractablePrefab` now skips anything under `AlterumReelOfFortune`, so every
booth clones the pristine scene template (and each gets its own component set, not a clone of a
clone). 1392 `[Error]` lines before that filter, 0 after.

## 2. The native interactable in maps

* `ReelInteraction` is one instance per booth (`LobbyPropRuntime.Interaction`), so two anchors are
  two interactables with their own collider, look centre, WS-UI anchor and `InteractedLocally`
  subscription.
* `EnsureBuilt` runs every frame from the booth's `Tick`; the old `if (ReelLogic.IsMapRun(out _))
  return;` guard is gone. It defers (one line per 5 s) until a provider that resolves the local
  player's `TapInteraction` exists — in a map the player can spawn seconds after the booth.
* Provider choice is ranked: under the player and not an inventory/handheld item first, then any
  component under the player, then a scene provider; only providers that already resolve an
  interaction with key `e`. Observed: `.../Frisk Interactable Model Parent/Frisk The Player
  Interactable/Interaction`, `key='e'`. A respawn replaces that component, so the 2 Hz `ProviderGuard`
  re-resolves on death/destroy.
* The template's *object* gates are stripped, not just the AlwaysFalse refusal: the sacrifice-altar
  template carries two `RoleInteractionConstraint`s (Altered only) which refused every other player —
  that is what left the lookable `situative` and its prompt in the `Error` view — plus the altar's
  own `CooldownConstraint`. The generic player gates (in-range, obstacle, can-interact, knockout,
  dead) stay.

## 3. One handler for E

`LobbyPropRuntime.PollReelKey` returns immediately when `ReelInteraction.HandlesKey` is true and logs
once per booth: `E is handled by the game's interaction handler (native interactable live); the
raw-key fallback is off for this booth`. The raw-key path survives only for a booth whose interactable
could not be built. `reel.request` (spin/place/save/load/startmap) is unchanged.

## 4. Evidence (instance b, Lair Of Occultists, two saved anchors)

```
02:51:24  [reel:spawn] 'lair-1' pos=(-34.35,0.582,-2.48) collider=(-34.14,2.54,-2.48)/(0.36,1.60,1.01) lookCentre=(-34.35,2.18,-2.48)
02:51:24  [reel:spawn] 'lair-2' pos=(-25.25,0.567,-2.34) collider=(-25.04,2.53,-2.33)/(0.36,1.60,1.01) lookCentre=(-25.25,2.17,-2.34)
02:51:24  [reel:spawn] saved=2 spawned=2 failed=0 expected=2 live=2 names=[lair-1,lair-2]
```

Both booths, one request each:

```
[reel:act] booth[1] uiState=Active  provider='.../Frisk The Player Interactable/Interaction' key='e' subscribed=True
[reel:act] booth[2] uiState=Attraction (not looked at) provider=<same component> key='e' subscribed=True
```

Pull from reel 1 (stood 1.6 m in front, looking at it):

```
[reel:act] native interaction accepted n=1
[reel] native interact why=InteractedLocally interactor='.../BASE/Interactable/Interactor'
[reel] spin start trigger=interact spins=1 ... targetType=blank pull=True
[reel] result text='Nothing. It keeps your item.'
[reel:ui] drawn message shown: active=True enabled=True ... tex=360x26
```

Pull from reel 2 (request addressed `stand=1.6@2`):

```
[reel:act] stand dist=1.60 pos=(-23.65,0.57,-2.31) booth=(-25.25,0.57,-2.34)
[reel:act] native interaction accepted n=2      (n=1 was a refused bet: a rune is not bettable)
[reel] native interact why=InteractedLocally
[reel] spin start trigger=interact spins=1 ... targetType=damage pull=True
[reel] result text='You lost 42 HP'
[reel:ui] drawn message shown: active=True ... tex=192x26
```

Prompt states (same run, `state`): looking `uiState=Active`, presenter `shown=True owner=<our WS UI>
button='e' name='interact'`; looking away (`dot=-0.983`) `uiState=Attraction shown=False`; out of
range (6.64 m) `uiState=Disabled shown=False`.

Checks over the whole run: `InitializeInteraction` NRE 0, `OnInteractionPerformed threw` 0,
`grep -c SWAP` 0, deck `cells=64 blank=22 item=21 damage=10 debuff=6 double=5`, `[Error]` since the
final generation 0, two reels per map, no reel/spawn/build line at all while in the lobby.

## 5. Physical keypress

Not provable here: the desktop is locked (`LogonUI` running in session 1), and the synthetic
`KeyboardState` event this mod queues is not delivered to the game's action (logged:
`press: no device event delivered`). The accepted pulls came through
`InteractableInputHandler.OnInteractionPerformed` — the handler's own bound callback, the method a
real E press invokes — after the game's looker had promoted the booth itself. Everything downstream
of the key (looker → `Active` → handler binds `e` → `Interact` → `InteractedLocally` → spin) is
therefore proven; only the device-level event delivery is not.

## 6. Left broken / not exercised

* `[reel:ui] notify tier=toast failed: NRE` and `[reel:ui] looking ... via=drawn
  hud='fallback: game prompt threw: NRE'` — both pre-existing (present at 01:42, before this work)
  and confined to the *fallback* tiers of `ReelHud` (`GameNotifications.ShowNotificationStatic`,
  `ActionPromptsController.ShowOrChangePrompt`). The drawn HUD message and the native world-space
  prompt both work, so nothing user-visible depends on them yet. Warning level, not `[Error]`.
* Death→respawn of the player object was not triggered (no harness kills the player). The path it
  exercises — the provider component being destroyed with the player root — is the `ProviderGuard`
  case, and three hot reloads (which also recreate every booth and provider) ran clean.
* Map → lobby: still no unattended path (map exit is an in-world stage transition), so "leave and
  re-enter a map" was exercised as lobby → map (once) plus three generations, not as a round trip.
