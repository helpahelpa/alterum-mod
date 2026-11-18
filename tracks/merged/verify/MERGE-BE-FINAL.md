# Stream B + E merge, final build, and the four outstanding checks

Addendum to `MERGE-MANIFEST.md`. Merge target `tracks/merged/mod`. Verification slot: the clone
(`C:\AlterumI`). **Instance A (pid 29384) was never reloaded or written to; no deployment was made**
(see §5 — the gate did not go green).

## 1. Merge provenance

Both streams existed. `work/B` and `work/E` were each forked from a different base, which decides what
counts as "their" file:

* **B is on the pre-A/C/D base.** Its tree still has the old `KeyLegend`, `f4Key`, `PickerUI` etc., so
  every base-vs-merged difference in B's copy is *not* B's change.
* **E is on the post-A/C/D merged tree** (it already carries `ModelSwapRuntime` wiring `90c958…`,
  D's roster, A's enhancement files, C's registry). E adds exactly one file and one wiring block.

| stream | file | md5 as merged | note |
|---|---|---|---|
| B | `ReelPlacements.cs` | `72e5c51072443cf66a0d041580903182` | **new, byte-identical to B** |
| B | `ReelInteraction.cs` | `a843b5c87b86fb2dde9c7228fd7e356f` | new, B + the one fix in §3.2 |
| B | `LobbyProp.cs` | `5a009412a23a28a75a6f2ecfd1402189` | **byte-identical to B** (base == merged before this merge) |
| B | `ModelSwapRuntime.cs` | `4b6856dfe9f8fb4daeb5fc50767a77ac` | merged wiring + B's `ReelPlacements.Tick` block |
| E | `QuestAdmin.cs` | `1b55eb4cde191ffc51ae08b773522612` | **new, byte-identical to E** |
| E | `HotModule.cs` | `43349d9577e85fb1011e38f7ae5ad87e` | **byte-identical to E** (whole file) |

**Rejected (not merged):** `work/B/…/AlterumModelSwap.Hot.csproj` and `work/E/…/csproj`. Both are
older than the merged one: B's lacks the PInput reference *and* the doc fix, E's lacks only PInput.
The merged csproj is a strict superset, and `EnableDefaultCompileItems=true`, so neither new `.cs` file
needs a csproj entry.

Wiring applied, all four of B's and one of E's:
1. `ModelSwapRuntime.Update` — `ReelPlacements.Tick` inside a try/catch, before the place-request block
   (byte-identical to B's block; verified by extracting both).
2. `LobbyPropRuntime.MoveTo` / `CreateAt` — present in B's `LobbyProp.cs`, which is copied whole.
3. `LobbyPropRuntime.PollReelRequest` — `ReelPlacements.HandleRequest` right after the `place=here` branch.
4. `ReelInteraction.EnsureBuilt` — called from `ReelPlacements` at both booth-creation sites
   (`:508` live booth, `:581` newly created booth).
5. `HotModule.Update` — `QuestAdmin.Tick(_host)` after `UiDriver.Tick`, so `quest.request` is served.

The diff of merged against B's copy of `ModelSwapRuntime.cs` contains **only** the A/C/D wiring
(registry legend, `EnhancementSettings.EnsureLoaded`, `ModBindings.Pressed("mod.reel.place")`); nothing
of B's block is lost or altered.

## 2. Build

| field | value |
|---|---|
| command | `dotnet build -c Release -p:RefsDir=$HOME/assets/monorepo/alterum-data/refs/` in `mod/AlterumModelSwap.Hot` |
| exit status | **0** (final build; `error CS` count 0, read from the raw log, no pipe, no `|| true`) |
| warnings | **16** — `MSB3245` ×1 (unresolved `UnityEngine.SceneManagementModule`, also in the pre-merge baseline), `CS0168` ×5, `CS0169` ×1, `CS0414` ×3, `CS0649` ×3, `CS8073` ×1, plus the duplicate roll-up |
| DLL | 40,778,240 B, md5 `77a2c7fee782bdad98aafc26a5e91440` |

The new `CS8073` is stream B's `LobbyProp.cs(1787)` — `Bounds != null` on a non-nullable `Bounds` field
inside a log string. Harmless (the true branch always runs; nothing depends on it), left as B wrote it
to keep the file byte-identical. E's `QuestAdmin.cs` adds **zero** warnings.

**Eight builds were run, not one.** The first was the merge build (exit 0); the other seven are
verification-blocking fixes, each measured before it was written (see §3). Every one exited 0 with 0
errors.

## 3. Defects the checks exposed, and the fixes

None of these were introduced by this merge; all were present in the tree the manifest declared green,
and each one made a required check impossible. Each was measured first, then fixed, then re-measured.

### 3.1 `[Error]` = 0 was never true — 34 NREs per generation (rows)
`RebindActionUI.OnEnable` → `UpdateBinding` → `GetInputAction` ran *inside* `Object.Instantiate`, before
`_playerInput` could be seated. Counts by generation: 7480 (gen 1), 6835 (2), 6902 (3), then exactly
**34 per generation from 4 to 13** — i.e. since the rebind fix, not before it. The manifest's
"`[Error]` = 0 since the fix" was measured after the rows were built, not over the generation.

Fix (`ModControlsUI.Clone`): instantiate the donor with its `RebindActionUI` disabled, seat the fields,
then enable the row once so `OnEnable` runs with a valid input. 34 → 12.

### 3.2 "Interaction Provider Not Constructed" — one per booth build
The cloned prefab's provider was never `Construct()`ed, so its `Awake` logged an `[Error]` on
activation. Fix (`ReelInteraction.PrepareProvider`, called while the clone is still under the inactive
holder): run the game's own `ResetInteractionInitialization`/`InitializeInteraction`, and if that leaves
it unconstructed, hand the lookable the stock provider and drop the dead component. The first attempt
used `Destroy`, which defers to end of frame and left the error in place; `DestroyImmediate` on the
inactive clone removes it. Measured: `provider settle[0] … constructed=False interaction=null`,
`-> stock '…/LobbyStatue Lookable Interactable/Interaction' key='e'`, `dropped the unconstructed clone
provider`. 1 → 0.

### 3.3 The remaining 12 NREs — rows the game cannot bind
Of the 34 rows, 12 always throw in `UpdateBinding`: 2 chord rows (`mod.clothes.all`, `mod.emote.cycle.prev`)
and 10 digit/arrow rows (`mod.picker.down`, `slot1..slot9`). Measured via `menu.request: diag` — the
throwing rows report `DisplayString=<null>` while a working row reports `f1`. Fix: call the game's own
`UpdateBindingDisplay` first (it walks the identical `UpdateBinding` path) and enable the row **only if
it survives**. Result: 12 → **0**, and every one of the 34 rows keeps the registry label and key text.

### 3.4 Rows could not build at all outside the Lobby — 1 `[Error]` per generation
`AddActionMap` throws `InvalidOperationException: Cannot add, remove, or change elements of
InputActionAsset PlayerActionsControls while one or more of its actions are enabled`. Measured in the
Menu scene and in a map, and in the Lobby on a fresh boot. Fix (`ModControlsUI.BuildAsset`): record
which actions are enabled, `asset.Disable()`, edit, then re-enable exactly those in a `finally`.
Measured after the fix: `disabled 'PlayerActionsControls' for the edit (30 action(s) were enabled;
restored after)` → `built 34 rows … resolved=34/34 failed=0`, `[Error]` = 0.

### 3.5 New request command (for the hands check)
`hands.request grant=<substr>` grants an item through `PlayerInventoryUtilities.AddItemOrDropIfNoSpace`
— the same call `ReelLogic.Grant` uses to pay a reel win out — and logs the held state immediately
after, because "granted into the inventory" and "actually in the hand" are different things. It lists
all 55 loaded `InventoryItemData` names when nothing matches.

## 4. The four checks

Final clone state, generation 3/4 (Lobby, `C:\AlterumI`, my build live):

| constraint | value |
|---|---|
| `[Error]` since `[hot] generation 4 active` | **0** |
| `grep -c SWAP` | **0** |
| reel | `cells=64 blank=22 item=21 damage=10 debuff=6 double=5` |
| wardrobe | 26 rows (`16 of 26 PlayerModelData rows are ours`) |
| rows | `built 34 rows … resolved=34/34 failed=0` |

### Check 1 — Lobby ` (Modded)` — **PASS**

Created a **private** lobby and read it back from Steam, not from the creator field:

```
[lobby:name] create: calling CreateLobby(4, 'Testlobby-I', Private)
[lobby:name] create lobby: field='Testlobby-I' -> published='Testlobby-I (Modded)' hit=#1
[lobby:name] read: id=109775241840603763 name='Testlobby-I (Modded)' tag='' started='' dmods=''
                     members=1/5 owner=us
```

`CurrentLobby.GetData("name")` is `'Testlobby-I (Modded)'` — present, exactly one suffix, **not `''`**,
which settles stream D's open question. The value is set in the Harmony prefix on the `lobbyName`
argument, i.e. before the async state machine captures it, so no republish was needed.

Caveats, stated plainly: the click on the UI **Create** button was bypassed — the dropdown could not be
driven unattended (its items are template clones at identical paths), so the lobby would have been
created *Public* through the button. `create=` calls the same `SteamLobbyManager.CreateLobby(4, name,
Private)` the button calls, from managed code. The name field was filled with `setname=` first, so the
input path was exercised. One warning was logged: `gave up confirming 'Testlobby-I (Modded)' after 30s
(no lobby we created)` — the reconcile path did not accept the lobby as ours, though the published
value is correct.

### Check 2 — Enhancement matrix — **PARTIAL**

Fresh boot; the clone's selected model is `Sahur (Classic)`, **kind=Rigid** (`RestDeform`), so the
rigid arm of the matrix is measured and the rigged arm is not:

```
[enh:matrix] sahur-classic  kind=Rigid  plannedChest=RestDeform plannedHips=RestDeform
             scale=1.18/1.12 profile=table  measured=chest=RestDeform:on,hips=RestDeform:off,bones=0,verts=3939
[enh:custom] apply chest=off hips=off why=enhancement:request records=1
             [sahur-classic(chest=RestDeform:off,hip=RestDeform:off,bones=0,verts=3939)]
```

Not obtained: the four combinations as four distinct measured rows (only `off/off` and the worn
`on/off` were captured before the box closed), the rigged model's four (needs a wardrobe selection
change through the UI), before/after captures, persistence across a reload, and the repeated-apply
cumulative-deformation count. The mechanism in the code is re-derive-from-original (every apply reads
the recorded original array; `MaxDeltaY` is per-apply), but **that is not a measurement here**.

### Check 3 — Hands on a held item — **BLOCKED** (the check that matters most)

An item could not be armed, in a map or in the lobby. Three separate blockers, each measured:

1. **In the lobby** — `grant` reached the game's own API and it threw:
   `[hands:grant] item='Axe_02_Chopping_Weapon_InventoryItemData' threw=True Il2CppException:
   System.NullReferenceException`. The lobby player has no run inventory, so
   `PlayerInventoryUtilities.AddItemOrDropIfNoSpace` cannot resolve one.
2. **In a map (Lair of Occultists, entered with `req=1` + `start`)** — the player cannot be walked and
   the nearest item interactables are out of reach of the looker's 4 m sphere: `[ui:near] radius=25
   interactables=86`, nearest item `4.33m '[Tier-2 KEY] GetItemToPlayersInGroup Interactable'`, then
   `8.73m InteractableHandheld 'Exit Spell Part A'`. Everything within reach is a player-carry or the
   Frisk interaction.
3. **The map generation itself degraded to ~1 frame per minute**, so the `grant` request was never even
   polled. `Time.unscaledTime` advances per frame with `maximumDeltaTime` clamping, so a 0.5 s poll
   interval needs minutes of wall clock. Measured: `hands.request` sat unconsumed for 25 s and 120 s;
   `ui.request scene` answered ~10 minutes late; the hot reloader (a different MonoBehaviour) still
   reacted in under a second, so the module object was alive — the frame loop was not.

So the hands census with an item (`held=… item=True`, grip distance, 1P and 3P on native / rigged /
rigid) was **not taken**. What the merged build does prove is the *empty-handed* path stream A
measured, and that the hands request file is served (`[hands] ready`).

Side finding that forced the restart: the solo-forced map also logged a game-side
`ArgumentException: … Key: 0` from `SceneHandle.GameLoadingScreen.CreatePlayerStatuses()`, and the
map → Lobby return (`spawn.request lobby` → `LoadSceneOnServer('Lobby')`) never completed. The clone
was killed (pid 66744) and relaunched from `C:\AlterumI` as pid 31940; instance A was not touched.

### Check 4 — Controls rows in frame — **PARTIAL**

`MenuProof` canvas-to-RT path, 640×360:

```
[menu:shot] tag='ctrl4' 640x360 canvases=34 nonBlackPixels=124440 pct=54.0
            file='C:\AlterumI\BepInEx\screens\menu_ctrl4.png'
```

Rows are built and resolved in this generation (`built 34 rows … resolved=34/34`), and this capture
exercised the canvas-to-RT path at 640×360 — but it is **not** a capture of the Controls page with the
mod rows in frame: the shot covered all 34 canvases, and the rows are appended to the end of the
Controls scroll list, which `MenuProof` cannot scroll (`scroll=` is not a command, and resizing cannot
reveal content clipped by the viewport). The follow-up attempt that narrowed the root
(`canvas=Master Canvas`) was not executed by the mod before the box closed.

### Spot-checks

* **Reel native prompt in a map — BLOCKED, not a regression.**
  `ReelInteraction.EnsureBuilt` returns early on `ReelLogic.IsMapRun` (by design: the interactable is
  built from the Lobby's statuette template). In the map the reel's *booth* was created and its deck
  initialised (`cells=64`), and the interactable reported
  `state built=False 'stale (booth recreated)' source=scene template '…/LobbyStatue Lookable
  Interactable'` — the template is a Lobby-scene object and does not exist in a map. So no interactable,
  hence no prompt, in a map run. The prompt itself is proven in the Lobby by stream B.
* **Saved placement restores — PASS.**
  Seeded the version-2 store with the three anchors (`necropolis-1`, `necropolis-2`, `lair-1`; `lair-1`
  marked `isDefault` because B's seed marked none, so nothing could ever auto-restore) and entered the
  Lair map:
  ```
  [reel:place] store '…\alterum-reel-placements.json' loaded 3 placement(s)
               maps=[Necropolis_GameMapData|Necropolis Map Scene(2) LairOfOccultists_GameMapData|Lair Of Occultists Map Scene(1)]
  [prop] Reel of Fortune placed pose=(-34.35, 0.58, -2.48) euler=(0,89.1,0)
  [reel:place] restored default 'lair-1' into a new booth … pos=(-34.35,0.582,-2.48) yaw=89.1
  [reel:place] restore skipped: generation … already restored (marker)      <- next generation
  ```
  Exact seeded anchor, and the once-per-generation marker holds across a hot reload.

## 5. Deployment to instance A — **NOT DONE**

The gate says deploy only if Tasks 1–2 are green. Task 2 is not: check 3 is blocked, checks 2 and 4 are
partial. **No file was written to A.** Verified after the fact: pid 29384 alive (CPU 83,773 s), and
`…/Alterum/BepInEx/hot/AlterumModelSwap.Hot.dll` md5 `eadea60e55537ec705c895089f07eb13`, 40,680,448 B,
mtime 08:40 — i.e. **A is running a build that predates this session's work** (my build is
`77a2c7fe…`). Whoever owns A's DLL should note that it is not the tree documented here.

The clone is left on the final build (`C:\AlterumI\BepInEx\hot\AlterumModelSwap.Hot.dll` =
`77a2c7fe…`, generation 4, pid 31940). Its previous DLL is at
`AlterumModelSwap.Hot.dll.cloneroot-backup` (`55cfa2d3…`), the pre-restart one at
`hot/i/AlterumModelSwap.Hot.dll.prev`.

## 6. Not proven

1. Hands with a held item, in any scene. No item could be armed; the three blockers are in §4/check 3.
2. Enhancement matrix completeness: rigged model's four combinations, the four combinations as
   distinct rows on the rigid model, captures, reload persistence, repeated-apply accumulation.
3. Controls mod rows visible in a capture (hierarchy-verified only; no scroll command).
4. `[Error]` = 0 in a **map** generation: it is 0 in the Lobby (gen 3/4) but the map generation logs the
   game-side `CreatePlayerStatuses` `ArgumentException: Key: 0` caused by starting a 4-player map solo.
5. The map is unusable on this clone at present (~1 frame/minute), which is what blocked checks 3 and
   the map spot-check; the cause was not diagnosed.
6. `LobbyNaming`'s reconcile warning (`gave up confirming … after 30s`) — the name is correct in Steam,
   but the self-check did not clear.
