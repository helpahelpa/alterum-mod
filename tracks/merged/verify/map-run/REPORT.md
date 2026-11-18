# Map run — custom model reverted to native (fixed) + gap closure

Date 2026-11-15, host = `C:\AlterumPinned` (pid 10672), client = `C:\AlterumB` (pid 41836),
map = `Lair Of Occultists Map Scene`, session `109775242094364446`.
Final hot dll `dd27a587da93ba7ebe604277a65bede5` on both (== the built file).

## Cause (one line)

On entering a map the game's own spawn path re-applies the model index it captured at **lobby
join**, after the wardrobe selection was applied, and the mod followed that row:
`PlayersModelSetuper.SpawnPlayerModelOnServer` reads `_playersModelsIndexes[client]` (a
NetworkDictionary written once per membership) and calls
`PlayersAppearanceManager.SetBaseModelIndex`. Nothing writes the current selection back into that
dictionary, so the map replays the join-time value. Host had picked 24 (`TC2 Saxton Hale`), the
map re-applied 12 (`Golubiro`, still a custom row); the client had rolled 16
(`TC2 Soldier`), the map re-applied 6 (native `Male_2_Thomas`). The wardrobe's own `SyncSelection`
then followed the re-applied row, so the log said "selection is X" while X was the stale one.

Second, independent defect on the client: `SetBaseModelIndex` is a plain method, not an RPC. The
client's `SetBaseModelIndex(1,16)` never reached the host (no matching line on the host), so a
client cannot change its own body model through the game path. Its map body stayed the native
`3P_ConcreteModel_Male_02`, whose skeleton has no `spine_04/spine_05`, and the rigged body then
skipped the build — the native body showed.

Third: the local body resolution. `CurrentPlayerProvider` is absent on the first map frames and
the old fallbacks accepted any setuper/model whose path contained "Player" (on the client that is
the HOST's body — `B`'s log shows `wardrobe selection IS 'TC2 Saxton Hale'` on client 1), and the
no-body path was silent.

## Log evidence

Lobby, host selection (this session):

```
[join:trace] SpawnPlayerModelOnServer(client=0) dictionaryIndex=24
[join:trace] SetBaseModelIndex(client=0, index=24)
[join:trace] SpawnPlayerModelOnServer(client=1) dictionaryIndex=6
```

Map entry, same session, 4 s later (`[scene] -> 'Lair Of Occultists Map Scene'`):

```
[join:trace] SpawnPlayerModelOnServer(client=0) dictionaryIndex=12 models=26
[join:trace] SetBaseModelIndex(client=0, index=12) models=26
[mapbody] SetupModel('Golubiro') idx=12 ours=yes setuper='GamePlayer_Separated | Owner: 0/...'
[join:trace] SpawnPlayerModelOnServer(client=1) dictionaryIndex=6 models=26
[join:trace] SetBaseModelIndex(client=1, index=6) models=26
[mapbody] SetupModel('Male_2_Thomas_PlayerModelData') idx=6 ours=no setuper='GamePlayer_Separated | Owner: 1/...'
[mapbody] t=... models=26 localPlayer=none setuper=none ... rig[on=1 bind='none'] ...
[reel:act] build deferred: no provider resolving the local player's interaction yet (player=False)
[sahurrig:nobody] rigged body wanted but no local ThirdViewPlayerModel resolved (owner='null'
                  scene='Lair Of Occultists Map Scene' on=True) - retrying in 1.0s
```

Before the fix, the client then bound the native skeleton and never built:

```
[sahurrig:skip] '3P_ConcreteModel_Male_02(Clone)' is missing 2 game bone(s) 'spine_04,spine_05'
```

## Fix

`tracks/merged/mod/AlterumModelSwap.Hot/` (hot tree only; `AlterumModelSwap/` and
`AlterumModelSwap.Contracts/` untouched):

| file | change |
|---|---|
| `SahurWardrobe.cs` | **wanted entry** (`_wantIndex`) persisted per instance to `BepInEx/config/alterum-wanted-model[.<instance>].cfg` by slug, set on every selection (wardrobe row, random roll, `apply`, `body=` request), cleared by a native row. In a **map** the body is driven from the wanted entry, never from the game's applied row; the mismatch is logged once per state (`[sahur:map]`). Host re-applies the wanted index through the game's own `SetBaseModelIndex` (map re-assert, 1.5 s). No wanted file: seeded from the random-on-join roll, else adopted from the row. |
| `SahurWardrobe.cs` | `LocalSetuper` resolves the owner with `ReelLogic.FindLocalPlayerEntity()` (provider, then Netcode `IsOwner`) and returns null without one — the "any setuper with Player in the path" fallback is gone. |
| `SahurRig.cs` | `FindLocalBody` uses the same owner resolution and never binds without an owner. Candidate bodies are filtered by the mapped bones. Missing chain bones are **aliased** (`spine_05→spine_04→spine_03`, `[sahurrig:alias]`), so a rigged model builds on any native skeleton. A missing-bone skip clears the binding instead of retrying the same dead skeleton. |
| `SahurModel.cs` | same owner resolution; no owner = no bind. |
| `SahurRig.cs`/`SahurModel.cs` | the silent no-op now logs `[sahurrig:nobody]` / `[sahur:nobody]` with the owner, scene and wanted state. |
| `MapBody.cs` | new `[mapbody]` trace: `SetupModel` postfix (data, index, ours, setuper path, caller) + a 1 Hz census (local resolution path, applied row, roster, every entry index, both body modules with their binding and renderer lease, wanted state). |
| `HotModule.cs`/`ModelSwapRuntime.cs` | install/dispose the trace; probe on every scene change; tick after the wardrobe. |
| `RandomModelOnJoin.cs` | exposes `RolledEntry` (used to seed the wanted state when the file is absent). |
| `CosmeticsRuntime.cs` | hat anchor shifted by the game-head → rig-head delta (`[cosmetic:anchor-shift]`). **Insufficient** — see open items. |

## Result in the map (both sides, after the fix)

```
host   [sahur:map] game applied 'Golubiro' idx=12 ours=True but wanted is 'TC2 Saxton Hale'
                  -> forcing the wanted body
host   [join:trace] SetupModel('TC2 Saxton Hale') idx=24   (map re-assert)
host   [sahurrig:build] 'TC2 Saxton Hale' root=...3P_ConcreteModel_Male_04_James(Clone) bones=24
host   [mapbody] ... applied='TC2 Saxton Hale' idx=24 ours=yes want='TC2 Saxton Hale'
                 rig[on=1 spec='tc2-saxton' bind='...James(Clone)' bodyGo=1 smr=1 hidden=8]
client [sahur:map] game applied 'Male_2_Thomas_PlayerModelData' idx=6 ... wanted is 'TC2 Soldier'
client [sahurrig:alias] game bone 'spine_04' is missing; using 'spine_03'
client [sahurrig:alias] game bone 'spine_05' is missing; using 'spine_03'
client [sahurrig:build] 'TC2 Soldier' root=...3P_ConcreteModel_Male_02(Clone) bones=24
client [mapbody] ... applied='TC2 Soldier' idx=16 ours=yes want='TC2 Soldier'
                 rig[on=1 spec='tc2-soldier' bind='...Male_02(Clone)' bodyGo=1 smr=1 hidden=10]
client [cosmetic:worn] slug='tc2-soldier' spec=Sahur_Tc2Soldier_Body anchor=True
       [cosmetic:bind] id=builtin.partyhat bone='head' ...
```

Captures (640x360, showcase camera through the game's own pipeline):

* `host-tc2-saxton-map.png` — host, map, `TC2 Saxton Hale` on the player.
* `client-tc2-soldier-map.png` — client, map, `TC2 Soldier` on the player.
* `client-golubiro-map.png` — client, map, rigid `Golubiro` (head on the game head bone).
* `host-headshot-map.png` — host head frame.

## Mid-emote model change (client, map)

```
[emote] START clip='The_Griddy' (Lethal Company port) ... lease=DoingSomething+DisableAnimancerAndAnimator
[sahur:wardrobe] 'TC2 Soldier' body forced on by request
[sahur:wardrobe] wardrobe selection is NOT 'Golubiro' (applied 'TC2 Soldier' index=16, why=map) -> body off
[emote] STOP reason=request-window clip='The_Griddy' t=0.04s released=True fadeOut=0.20s
[emote] STOP fadeout complete clip='The_Griddy' handed back to the game's abstraction
[cosmetic:worn] slug='tc2-soldier' ... anchor=True
[mapbody] rig[on=1 spec='tc2-soldier' bodyGo=1 smr=1 hidden=21 builds=14]
          rigid[on=0 spec='golubiro' bodyGo=0 smr=0 hidden=0 builds=6]
```

Clean stop, lease released, new body built, hat re-bound to the new worn spec, the previous rigid
body torn down. No stale body object.

## Not closed

* **Damage / debuff outcome from a client pull.** The force hook (`outcome=damage|debuff`) exists
  only on the host's own spin path (`LobbyProp.StartSpin` → `BeginSpin`); a client's
  `reel.request spin` becomes a plain `InteractRequest` and the host settles it naturally
  (`ReelNet` carries no outcome). A natural client pull needs a held handheld; the map spawns with
  empty writable inventories and `hold=<id>` only activates an *existing* slot
  (`[reel:debug] hold ... found no slot`). Not obtainable inside the box.
* **Vanilla-peer isolation re-run.** Needs a third install. `C:\AlterumPinned-pristine`,
  `C:\Interop`, `C:\AlterumTests` and the Steam install are off-limits for launching, and a new
  hardlinked instance shares `BepInEx/plugins` with `C:\AlterumB` (deleting the mod there would
  delete it everywhere). Not run.
* **Respawn survival** was not exercised (no death/respawn path driven in the map). The mechanism
  that covers it is the same wanted-entry assert that fixed map entry, but it is unproven.
* ~~**Hats on custom bodies render offset.**~~ **Fixed** in `verify/hat-seat/REPORT.md`: the seat
  is now measured from the worn model's own head geometry at bind/refit (same formula as the
  native path), the stored per-model table is only the fallback, and a socket-scale change is a
  refit reason. Measured seat in Lair: +0.017 m (host Saxton) / +0.008 m (client Soldier), from
  +0.293 / +0.112; remote hats use the same rule (+0.011 / +0.017).

## Hygiene

* `[Error]` = 1 on each log, and it is the game's own `QFSW.QC.QuantumConsole.OnDisable` on the map
  transition (accepted, not fixed).
* `grep -c SWAP` = 0 on both logs.
* Deck `cells=64 blank=22 item=21 damage=10 debuff=6 double=5`; two reels per map on both sides
  (`source=host epoch=1 rev=1 spawned=2 failed=0 live=2`); host-authoritative placements and pulls
  untouched; random-on-join state files left in place.
* The pin is running with the final hot dll (`dd27a587…`), in `Lair Of Occultists Map Scene`.
