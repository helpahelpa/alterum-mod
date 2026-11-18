# Merged build manifest — streams A + C + D

Date: 2026-11-14. Base: `tracks/merged/mod`. Merge target: `tracks/merged/mod` (in place).
Instance: `C:\AlterumI`, instance id `i` (pid 66744). **Instance A (pid 29384) untouched, never reloaded.**

## Build

| field | value |
|---|---|
| command | `dotnet build -c Release -p:RefsDir=$HOME/assets/monorepo/alterum-data/refs/` in `mod/AlterumModelSwap.Hot` |
| exit status | **0** (`error CS` count 0, verified, not piped through `grep … \|\| true`) |
| errors | 0 |
| warnings | 11 (see below) |
| DLL md5 | `34ae1d371f7a5f8accfd52ea720dded1` |
| DLL size | 40,662,528 B |
| deploy target | `C:\AlterumI\BepInEx\hot\i\AlterumModelSwap.Hot.dll` (temp name + `Move-Item`, md5 re-read on the clone: match) |

Warnings (all pre-existing except the two SahurModel CS0168, which came in with stream A's file):
`MSB3245` unresolved assembly reference **`UnityEngine.SceneManagementModule`** (1) · `CS0168` LobbyProp.cs(1734) ·
`CS0168` SahurModel.cs(1554) · `CS0168` SahurModel.cs(1580) · `CS0169` ModelSwapRuntime `_nextCfgCheck` ·
`CS0169` ReelHud `_noteData` · `CS0414` EmoteRuntime `_shotFor` · `CS0649` JiggleProbe `ForceActive`, `ForceEnd` ·
`CS0649` ModelSpec `AnchorOverride` · `CS0649` ThirdPersonCameraRig `_veto`.
The unresolved `UnityEngine.SceneManagementModule` reference is the only reference warning; it is present in the
pre-merge baseline build too (measured before any file was copied).

## Per-file provenance

`md5` is the md5 **as merged** (i.e. after the wiring edits listed below).

| stream | file | md5 | note |
|---|---|---|---|
| A | DefaultHands.cs | fa5279e588687c87d9188dc7849d67b9 | new, byte-identical to A |
| A | EnhancementSettings.cs | 8264dafda7ee84acfd0836b0a3f9498b | new, byte-identical to A |
| A | CustomEnhancements.cs | ab6420d27c1071a89189fc92c08b7b0b | new, byte-identical to A |
| A | ModelEnhancementProfiles.cs | 127904b8239b8a0aab42050c68efa470 | new, byte-identical to A |
| A | SahurModel.cs | e9d85a9f85587887bad7d67a4ae0e2f9 | **A's content + F3/F2 wiring edit** |
| A | SahurRig.cs | f0419ebd71d5133aa2f2a653b819cf76 | **A's content + F1 wiring edit** |
| A | Proportions.cs | 4dd77b133fa655e283007fd263f2e6c6 | byte-identical to A |
| A | JiggleSolver.cs | 6973fe20afad823de0dd8a656186e713 | byte-identical to A |
| C | ModBindings.cs | e430b6fce218db7afd74548cae9343c7 | byte-identical to C |
| C | ModBindingsStore.cs | 39c04d3a2b2872bafeb87bf76ca37241 | byte-identical to C |
| C | ModPauseSettings.cs | 0c8d88276172ae973a2837dbe2ea8da9 | byte-identical to C |
| C | MenuProof.cs | a3b7de669ddf4c3c3f58c5d9f01ff5b8 | byte-identical to C |
| C | ModControlsUI.cs | 47b1155e4dcfeaa1bd93edbdf848854b | **C's content + row-creation fix (see below)** |
| C | PickerUI.cs | 1291f274d0dafd26bb3502daf36938c7 | byte-identical to C |
| C | EmoteRuntime.cs | 3b2dc8cbaa631ef5d58bab7856559234 | byte-identical to C |
| C | ClothesToggle.cs | be9345c2daf05bd6be445caa10144122 | byte-identical to C |
| C | PovToggle.cs | aaedd56d7781669c780a4010999441e6 | byte-identical to C |
| C | ScreenShot.cs | 7b61aa37fa49899bf902ea131d2ff10e | **C's content − ModBindings.Tick (moved)** |
| C | ClipRecorder.cs | cee77e1811391e2618d008f922ef5fb7 | byte-identical to C |
| D | LobbyNaming.cs | b44532f97c5e4ef0df4a89af201345d1 | new, byte-identical to D |
| D | ModelRoster.cs | 10b7c70c8095ce76ee5c8c2a590b058e | byte-identical to D |
| D | NordboRoster.g.cs | 43ab9be67632e964d9342f41368acfd4 | byte-identical to D |
| wire | ModelSwapRuntime.cs | 90c958202d92e9d436f9eab518e233ca | integrator wiring |
| wire | HotModule.cs | b1c5198c0be437cdcb4e2d4a831ec6ab | integrator wiring |
| tool | models/nordbo/baked/tools/gen_roster.py | 479f8f0f78feaa0dd7ae58c973cbbbeb | outside the mod tree; D wrote it in place, no `work/D` copy existed |
| doc | AlterumModelSwap.Hot.csproj | 4818a91ae4569cfc4ada5d8efcc504be | stale "(demo only)" claim fixed |
| doc | notes/wardrobe-variants2.md | f09bb84364390b5e3c12cb41eb875398 | suffixed labels fixed |

### Files each stream was *declared* to own but did not change (no merge needed, verified identical to base)
A: `SahurPreview.cs`, `ChestWeights.cs`, `ProportionShots.cs`.

### Rejected / not merged
**None.** No stream touched a file it did not own. Verified: `LobbyProp.cs`, `ReelLogic.cs`, `ReelDeck.cs`,
`ReelArt.cs`, `ReelEligible.cs`, `SpawnProbe.cs` are byte-identical to base in all three stream copies and in the
merged tree. No non-source (asset/csproj) differences existed in any stream copy.

## Wiring applied

1. `ModelSwapRuntime.cs` — `KeyLegend` constant deleted; `[keys]` is now one line from `ModBindings.Legend()`
   (wrapped in try/catch) and `OnGUI` draws `ModBindings.Legend()`. `PollPropPlaceKey` polls
   `ModBindings.Pressed("mod.reel.place", out t)` (was `kb.f4Key`); the trigger text goes into the `[reel] place`
   line and into `PlacePropAtPlayer`'s branch label. `EnhancementSettings.EnsureLoaded(_h)` is called immediately
   after `_proportions = new Proportions(_h)`. `JiggleSolver.Shutdown` already preceded `SahurModel.Dispose`
   (lines 895 / 902) and was left in that order.
2. `SahurModel.cs` — `PollKeys` reads `mod.sahur.body` / `mod.sahur.bat`; `SahurRig.cs` — `PollKeys` reads
   `mod.sahur.rig`. The ready lines print `ModBindings.KeyText(...Current)` instead of the `const Key`, so a rebind
   cannot leave a lying log line.
3. `HotModule.cs` — `LobbyNaming.Install(host)` right after `_host = host;` (before the runtime exists);
   `LobbyNaming.Tick(_host)` in `Update`; `LobbyNaming.Dispose()` in `Dispose` **before** `_runtime?.Dispose()`.
4. Optional move done: `ModBindings.Tick` left `ScreenShot.Tick` and is now the first statement of
   `HotModule.Update` (own try/catch), so the registry ticks **before** the per-feature pollers — removing a
   one-frame rebind lag — and keeps ticking if the screenshot path throws. It is called in exactly one place.

## Integrator fixes beyond the requested wiring (both needed to make stream C's check pass at all)

* **`ModControlsUI.Clone` — no row was ever created.** `row.actionReference = reference` runs the game's
  `UpdateBinding()` → `RebindActionUI.GetInputAction`, which throws `NullReferenceException` for an action in a
  runtime-built `InputActionAsset`; the catch then destroyed the clone. Measured: **all 34 rows, every generation**
  (`[bind:rows] clone for … failed` ×34). Fixed by writing the serialized fields (`row.m_Action`,
  `row.m_BindingId` — plain field stores, no game logic) and driving the two display refreshes ourselves. Rows now
  build: `built 34 rows … resolved=0/34 failed=0`, `RebindActionsCollection 26 -> 60`, `PreInitialize rows=6860`.
* **Row text was the donor's.** After the clone, `UpdateActionLabel`/`UpdateBindingDisplay` write the *donor's*
  text (every row showed the donor key `f`), so the label and key are re-asserted unconditionally afterwards:
  `"Alterum: " + action.Label` and `action.CurrentText`.

**Known limitation, not fixed:** `resolved=0/34` — the game's own `ResolveActionAndBinding` still throws for a
runtime-built asset, so the rows *display* the registry label/key but the game-native rebind click path does not
resolve them. Rebind-override plumbing (`ModBindings.SetOverride`, store, legend) is unaffected.

## Docs

`AlterumModelSwap.Hot.csproj`: the comment claiming the wardrobe label says `"(demo only)"` now states the row text
is the plain `Labubu` and that the marking lives in `DemoOnly`/`DemoReason` + the startup warning + the banner.
`notes/wardrobe-variants2.md`: all label cells and prose updated to the unsuffixed visible labels (`Labubu`,
`TC2 Scout` … `Peter Griffin`), with `AdoptNames` noted as the reload-adoption path. **Every
`!! NOT FOR DISTRIBUTION !!` / `NOT FOR DISTRIBUTION` banner is intact (csproj 2, notes 8, unchanged counts).**

## Verification table (clone `C:\AlterumI`, instance `i`)

| # | check | result | evidence |
|---|---|---|---|
| 1 | `[hot] generation … active` | **PASS** | `[hot] generation 1 active`, then further generations on reload |
| 2 | `[Error]` = 0 | **FAIL (historical) / PASS since the fix** | 7480 `[Error]` lines in the log, **all** `[bind:rows]` — the pre-fix clone failure (34 per rebuild attempt × every Controls-tab open). After the last reload (`[hot] generation 3 active`): **errors_after_gen3=0**, `built 34 rows` once. The runtime log is not error-free overall. |
| 3 | `grep -c SWAP` = 0 | **PASS** | case-sensitive `SWAP` count = 0 (a case-insensitive `swap` match is 19393 because `AlterumModelSwap` / `[swap:teardown]` contain it — the check must stay case-sensitive) |
| 4 | Menus — Controls shows mod rows | **PARTIAL** | 34/34 rows built and `active=True` in the live hierarchy, registered `26 -> 60`; the capture does **not** show them in frame (appended at the end of the scroll list, `MenuProof` does not scroll). `menu_controls2.png` 640×360 nonBlackPixels=124534 (54.1%) shows the Controls tab + native rows. |
| 5 | Menus — ESC menu, 2 toggles at defaults | **PASS** | `menu_esc.png` nonBlackPixels=221710 (96.2%); `ALTERUM: CHEST ENHANCEMENTS` and `ALTERUM: HIPS ENHANCEMENTS` visible in `menu_controls2.png`; `[enh] loaded chest=on hips=off` |
| 6 | Labels (D) — `TC2 Scout` … `Peter Griffin`, `Labubu`, no suffixes | **PASS** | roster log: `Labubu`, `Capuchino`, `TC2 Scout` … `TC2 Saxton Hale`, `Peter Griffin` — no suffix |
| 7 | Labels — 26 rows, no duplicates after hot reload | **PASS** | post-reload roster re-adopted every entry by name (`adopted the existing 'TC2 Scout' entry at index 15` …), roster count stayed 16 ours of 10 native rows (26) |
| 8 | Lobby name (D) — published metadata ends ` (Modded)` exactly once | **NOT VERIFIED** | prefix installed (`[lobby:name] installed: prefix on Steam.SteamLobbyManager.CreateLobby`) but the auto click-sequence never reached lobby creation on this clone within the box; no published-name line |
| 9 | Lobby name — `''` after creation without republish | **NOT VERIFIED** | same reason |
| 10 | Enhancements (A) — chest on/hips off on fresh config | **PASS** | fresh `com.user0.alterum.enhancements.i.cfg` created with defaults: `[enh] loaded chest=on hips=off … (created with the defaults)` → `[enh:apply] chest=on hips=off why=load` |
| 11 | Enhancements — applied to native + custom model, 4 combos, persistence | **NOT VERIFIED** | needs an `enh.request` run; out of time box |
| 12 | Hands (A) — held item on custom model + native control | **NOT VERIFIED** | needs a map run (`RequiredPlayerNumberToStartGame = 1` + readiness RPCs); not attempted |

## Failures / caveats (plain)

1. **`[Error]` = 0 is not met over the whole log** (7480 lines, all the same pre-fix `[bind:rows] clone … failed`).
   Since the fix the last generation logs 0 errors; the residue is historical in the same file. The build itself has
   0 errors.
2. **Controls rows are hierarchy-verified, not pixel-verified.** They are appended to the end of the scroll list
   and `MenuProof` has no scroll command, so no capture shows them.
3. **Native rebind resolution of the mod rows fails** (`resolved=0/34`) — see the limitation above.
4. **D's lobby-name check did not run** — the auto click sequence did not reach a created lobby on the clone.
5. **A's hands check did not run** — no map run was attempted, so this does **not** improve on A's state-only proof.
6. The clone `C:\AlterumI` is still running (pid 66744). It was created by `win/make-instance.ps1`; that script's
   last step failed copying the main install's `BepInEx/hot` because one file was locked by another instance —
   harmless here, the clone uses `hot/i/` only.
