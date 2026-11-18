# The game's 109 console commands: signatures and the exact InvokeCommand string

Source of truth, in two halves:

* **Declaring type / method / parameter types and names** — read statically out of the interop
  assembly `BepInEx/interop/Assembly-CSharp.dll` (ECMA-335 metadata decode, `work/qc/sigall.py`;
  raw dump `work/qc/sigall.txt`). The `[Command]` attributes are stripped by Il2CppInterop, so the
  *names* cannot come from there.
* **Command name -> method** — the game's own table, read from
  `QuantumConsoleProcessor._commandCache` (an array of `CommandData`, each carrying
  `CommandName`, `ParameterSignature` and `MethodData`). Retrieved through the mod console's `dump`
  command; full copy in `qc-command-table.txt`. This link is not guessable: e.g. `gamecycle.start`
  is `StartLocationsTransitioning()`, `quest.complete-all` is `CompleteAllTask(byte)`, and
  `exit` is `GotoGameCommandsWrapper.GotoExit(int, int)`.

`InvokeCommand` takes one line: the command name, then one whitespace-separated token per
parameter in declaration order (strings unquoted; enums by member name; `bool` as `true`/`false`).
Overloads are resolved by the argument count and parse.

| command | declaring type | method | parameters (type name) | InvokeCommand form |
|---|---|---|---|---|
| `abilities.reset-all-cooldowns` | `_Scripts.ConsoleCommands.Network.AbilitiesCommandsWrapper` | `ResetCooldown` | () | `abilities.reset-all-cooldowns` |
| `blessing-totem` | `ConsoleCommands.GotoGameCommandsWrapper` | `GotoBlessingTotem` | totemIndex | `blessing-totem <totemIndex>` |
| `blessing-totem` | `ConsoleCommands.GotoGameCommandsWrapper` | `GotoBlessingTotem` | totemIndex locationIndex | `blessing-totem <totemIndex> <locationIndex>` |
| `camera.cinematic.disable-current` | `Cinemachine.CinematicCamerasCommandsWrapper` | `Disable` | () | `camera.cinematic.disable-current` |
| `camera.cinematic.enable` | `Cinemachine.CinematicCamerasCommandsWrapper` | `Enable` | cameraId | `camera.cinematic.enable <cameraId>` |
| `connect` | `ConsoleCommands.Network.Connection.UnityTransportConnectionCommandsWrapper` | `ConnectTo` | ip | `connect <ip>` |
| `connect` | `ConsoleCommands.Network.Connection.UnityTransportConnectionCommandsWrapper` | `ConnectToLocal` | () | `connect` |
| `connect-from-menu` | `ConsoleCommands.Network.Connection.MenuConnectionCommandsWrapper` | `ConnectToLocal` | () | `connect-from-menu` |
| `csp.get-reconciliation-enabled` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `GetReconciliationEnabled` | () | `csp.get-reconciliation-enabled` |
| `csp.set-guest-simulation-delay` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `SetGuestSimulationDelaySec` | () | `csp.set-guest-simulation-delay` |
| `csp.set-guest-simulation-delay` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `SetGuestSimulationDelaySec` | delaySec | `csp.set-guest-simulation-delay <delaySec>` |
| `csp.set-predict-weapon-hit-effects` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `SetPredictWeaponHitEffects` | isEnabled | `csp.set-predict-weapon-hit-effects <isEnabled>` |
| `csp.set-reconciliation-debuglog-enabled` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `SetReconciliationDebugEnabled` | isEnabled | `csp.set-reconciliation-debuglog-enabled <isEnabled>` |
| `csp.set-reconciliation-enabled` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `SetReconciliationEnabled` | isEnabled | `csp.set-reconciliation-enabled <isEnabled>` |
| `csp.set-target-slack-ticks` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `SetTargetSlackTicks` | () | `csp.set-target-slack-ticks` |
| `csp.set-target-slack-ticks` | `ConsoleCommands.Network.ClientsidePredictionCommandsWrapper` | `SetTargetSlackTicks` | targetSlackTicks | `csp.set-target-slack-ticks <targetSlackTicks>` |
| `debug.debug-connection-indicator` | `ConsoleCommands.DebugCommandsWrapper` | `DebugConnectionIndicator` | () | `debug.debug-connection-indicator` |
| `debug.debug-cursor` | `ConsoleCommands.DebugCommandsWrapper` | `DebugCursor` | () | `debug.debug-cursor` |
| `debug.foot-replay` | `ConsoleCommands.DebugCommandsWrapper` | `FootReplay` | () | `debug.foot-replay` |
| `debug.log` | `ConsoleCommands.DebugCommandsWrapper` | `Log` | log | `debug.log <log>` |
| `debug.log-foot-sound-state` | `ConsoleCommands.DebugCommandsWrapper` | `LogFootSoundState` | () | `debug.log-foot-sound-state` |
| `debug.print-steam-connection-log` | `ConsoleCommands.DebugCommandsWrapper` | `PrintSteamConnectionLog` | () | `debug.print-steam-connection-log` |
| `debug.print-voice-chat-log` | `ConsoleCommands.DebugCommandsWrapper` | `PrintVoiceChatSystemLog` | () | `debug.print-voice-chat-log` |
| `debug.set-cursor-lock-state` | `ConsoleCommands.DebugCommandsWrapper` | `SetCursorLockedState` | mode | `debug.set-cursor-lock-state <mode>` |
| `debug.set-cursor-visibility` | `ConsoleCommands.DebugCommandsWrapper` | `SetCursorVisibility` | visible | `debug.set-cursor-visibility <visible>` |
| `disconnect` | `ConsoleCommands.Network.Connection.UnityTransportConnectionCommandsWrapper` | `Disconnect` | () | `disconnect` |
| `effects.describe` | `ConsoleCommands.Network.ImposedEffectCommandsWrapper` | `Description` | effectId | `effects.describe <effectId>` |
| `effects.describe` | `ConsoleCommands.Network.ImposedEffectCommandsWrapper` | `Description` | effectId effectVariantId | `effects.describe <effectId> <effectVariantId>` |
| `effects.impose` | `ConsoleCommands.Network.ImposedEffectCommandsWrapper` | `Impose` | casterClientId effectId effectVariantId | `effects.impose <casterClientId> <effectId> <effectVariantId>` |
| `effects.print-all` | `ConsoleCommands.Network.ImposedEffectCommandsWrapper` | `PrintAll` | () | `effects.print-all` |
| `effects.remove` | `ConsoleCommands.Network.ImposedEffectCommandsWrapper` | `Remove` | effectHolderClientId targetClientId effectId effectVariantId | `effects.remove <effectHolderClientId> <targetClientId> <effectId> <effectVariantId>` |
| `effects.remove-all` | `ConsoleCommands.Network.ImposedEffectCommandsWrapper` | `RemoveAll` | effectHolderClientId | `effects.remove-all <effectHolderClientId>` |
| `exit` | `ConsoleCommands.GotoGameCommandsWrapper` | `GotoExit` | exitIndex | `exit <exitIndex>` |
| `exit` | `ConsoleCommands.GotoGameCommandsWrapper` | `GotoExit` | exitIndex locationIndex | `exit <exitIndex> <locationIndex>` |
| `gamecycle.bless-all-totems` | `ConsoleCommands.Network.LocationsCommandsWrapper` | `BlessAllTotems` | locationIndex | `gamecycle.bless-all-totems <locationIndex>` |
| `gamecycle.open-all-exits` | `ConsoleCommands.Network.LocationsCommandsWrapper` | `OpenAllExits` | locationIndex | `gamecycle.open-all-exits <locationIndex>` |
| `gamecycle.start` | `ConsoleCommands.Network.LocationsCommandsWrapper` | `StartLocationsTransitioning` | () | `gamecycle.start` |
| `gamecycle.start-night` | `ConsoleCommands.Network.LocationsCommandsWrapper` | `StartTransitionToNight` | locationIndex | `gamecycle.start-night <locationIndex>` |
| `gamecycle.start-resulting` | `ConsoleCommands.Network.LocationsCommandsWrapper` | `StartResulting` | explorersWin | `gamecycle.start-resulting <explorersWin>` |
| `gamecycle.stop` | `ConsoleCommands.Network.LocationsCommandsWrapper` | `StopLocationsTransitioning` | () | `gamecycle.stop` |
| `goto.place` | `ConsoleCommands.Network.GotoCommandsWrapper` | `GotoPlace` | placeIndex | `goto.place <placeIndex>` |
| `goto.player` | `ConsoleCommands.Network.GotoCommandsWrapper` | `GotoPlayer` | nickname | `goto.player <nickname>` |
| `goto.position` | `ConsoleCommands.Network.GotoCommandsWrapper` | `GotoPosition` | position | `goto.position <position>` |
| `handhelds.get` | `ConsoleCommands.Network.HandheldPoolFactoryCommandsWrapper` | `GetItemConsoleCommand` | itemId | `handhelds.get <itemId>` |
| `handhelds.print-items-indexes` | `ConsoleCommands.Network.HandheldPoolFactoryCommandsWrapper` | `PrintAllItemsIndexes` | () | `handhelds.print-items-indexes` |
| `handhelds.release` | `ConsoleCommands.Network.HandheldPoolFactoryCommandsWrapper` | `ReleaseCurrentHandheldItem` | clientId | `handhelds.release <clientId>` |
| `handhelds.to-inventory` | `ConsoleCommands.Network.HandheldPoolFactoryCommandsWrapper` | `GetItemToPlayerInventoryConsoleCommand` | itemId | `handhelds.to-inventory <itemId>` |
| `handhelds.to-inventory` | `ConsoleCommands.Network.HandheldPoolFactoryCommandsWrapper` | `GetItemToPlayerInventoryConsoleCommand` | itemId number | `handhelds.to-inventory <itemId> <number>` |
| `handhelds.to-inventory` | `ConsoleCommands.Network.HandheldPoolFactoryCommandsWrapper` | `GetItemToPlayerInventoryConsoleCommand` | itemId number clientId | `handhelds.to-inventory <itemId> <number> <clientId>` |
| `health.damage` | `ConsoleCommands.Network.HealthCommandsWrapper` | `Damage` | hp | `health.damage <hp>` |
| `health.damage` | `ConsoleCommands.Network.HealthCommandsWrapper` | `Damage` | hp targetClientId | `health.damage <hp> <targetClientId>` |
| `health.heal` | `ConsoleCommands.Network.HealthCommandsWrapper` | `Heal` | hp | `health.heal <hp>` |
| `health.heal` | `ConsoleCommands.Network.HealthCommandsWrapper` | `Heal` | hp targetClientId | `health.heal <hp> <targetClientId>` |
| `health.print` | `ConsoleCommands.Network.HealthCommandsWrapper` | `PrintHealth` | () | `health.print` |
| `health.print` | `ConsoleCommands.Network.HealthCommandsWrapper` | `PrintHealth` | clientId | `health.print <clientId>` |
| `health.set` | `ConsoleCommands.Network.HealthCommandsWrapper` | `SetHealth` | hp | `health.set <hp>` |
| `health.set` | `ConsoleCommands.Network.HealthCommandsWrapper` | `SetHealth` | hp targetClientId | `health.set <hp> <targetClientId>` |
| `host` | `ConsoleCommands.Network.Connection.UnityTransportConnectionCommandsWrapper` | `StartHost` | ip | `host <ip>` |
| `host` | `ConsoleCommands.Network.Connection.UnityTransportConnectionCommandsWrapper` | `StartLocalHost` | () | `host` |
| `host-from-menu` | `ConsoleCommands.Network.Connection.MenuConnectionCommandsWrapper` | `StartLocalHost` | () | `host-from-menu` |
| `interactable.interact-ignore-constraints` | `ConsoleCommands.Network.InteractableCommandsWrapper` | `InteractIgnoreConstraints` | () | `interactable.interact-ignore-constraints` |
| `kcc.get-position` | `ConsoleCommands.Network.CharacterControllersCommandsWrapper` | `GetPosition` | () | `kcc.get-position` |
| `kcc.get-position` | `ConsoleCommands.Network.CharacterControllersCommandsWrapper` | `GetPosition` | clientId | `kcc.get-position <clientId>` |
| `kcc.noclip` | `ConsoleCommands.Network.CharacterControllersCommandsWrapper` | `ToggleNoclip` | () | `kcc.noclip` |
| `map.generate-maze` | `ConsoleCommands.MapCommandsWrapper` | `GenerateMaze` | () | `map.generate-maze` |
| `occultist.add-energy` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `AddEnergy` | () | `occultist.add-energy` |
| `occultist.add-energy` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `AddEnergy` | energyToAdd | `occultist.add-energy <energyToAdd>` |
| `occultist.add-energy` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `AddEnergy` | energyToAdd occultistClientId | `occultist.add-energy <energyToAdd> <occultistClientId>` |
| `occultist.enchant-dagger` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `ChangeStateOfBladeOfDeath` | activate | `occultist.enchant-dagger <activate>` |
| `occultist.enchant-dagger` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `ChangeStateOfBladeOfDeath` | activate occultistClientId | `occultist.enchant-dagger <activate> <occultistClientId>` |
| `occultist.take-energy` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `TakeEnergy` | () | `occultist.take-energy` |
| `occultist.take-energy` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `TakeEnergy` | energyToTake | `occultist.take-energy <energyToTake>` |
| `occultist.take-energy` | `ConsoleCommands.Network.OccultistCommandsWrapper` | `TakeEnergy` | energyToTake occultistClientId | `occultist.take-energy <energyToTake> <occultistClientId>` |
| `po.assign-role` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `AssignRole` | roleIndex | `po.assign-role <roleIndex>` |
| `po.assign-role` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `AssignRole` | roleIndex modelIndex | `po.assign-role <roleIndex> <modelIndex>` |
| `po.despawn` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `DespawnPlayer` | () | `po.despawn` |
| `po.print-players` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `PrintPlayers` | () | `po.print-players` |
| `po.print-roles` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `PrintRoles` | () | `po.print-roles` |
| `po.return-nickname` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `ReturnNickname` | () | `po.return-nickname` |
| `po.return-nickname` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `ReturnNickname` | clientId | `po.return-nickname <clientId>` |
| `po.set-nickname` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `SetNickname` | newNickname | `po.set-nickname <newNickname>` |
| `po.set-nickname` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `SetNickname` | newNickname clientId | `po.set-nickname <newNickname> <clientId>` |
| `po.spawn-lobbier` | `ConsoleCommands.LobbyPlayerObjectCommandsWrapper` | `SpawnPlayerAndAssignLobbier` | () | `po.spawn-lobbier` |
| `po.spawn-lobbier` | `ConsoleCommands.LobbyPlayerObjectCommandsWrapper` | `SpawnPlayerAndAssignLobbier` | modelIndex | `po.spawn-lobbier <modelIndex>` |
| `po.spawn-player` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `SpawnPlayer` | () | `po.spawn-player` |
| `po.spawn-player` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `SpawnPlayer` | spawnDelay | `po.spawn-player <spawnDelay>` |
| `po.switch-model` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `SwitchPlayerModel` | modelIndex | `po.switch-model <modelIndex>` |
| `po.switch-pov` | `ConsoleCommands.Network.PlayerObjectCommandsWrapper` | `SwitchPOV` | () | `po.switch-pov` |
| `quest.complete-all` | `ConsoleCommands.Network.QuestsCommandsWrapper` | `CompleteAllTask` | locationIndex | `quest.complete-all <locationIndex>` |
| `quest.complete-subtask` | `ConsoleCommands.Network.QuestsCommandsWrapper` | `CompleteSubTask` | locationIndex subTaskId | `quest.complete-subtask <locationIndex> <subTaskId>` |
| `quest.complete-task` | `ConsoleCommands.Network.QuestsCommandsWrapper` | `CompleteCurrentTask` | locationIndex | `quest.complete-task <locationIndex>` |
| `quest.complete-task-wait` | `ConsoleCommands.Network.QuestsCommandsWrapper` | `CompleteCurrentTask` | locationIndex secondsToComplete | `quest.complete-task-wait <locationIndex> <secondsToComplete>` |
| `quest.uncomplete-subtask` | `ConsoleCommands.Network.QuestsCommandsWrapper` | `UncompleteSubTask` | locationIndex subTaskId | `quest.uncomplete-subtask <locationIndex> <subTaskId>` |
| `roles-distribution.add-rounds-since` | `Gameplay.Cycle.RolesDistributionCommandsWrapper` | `AddRoundsSince` | clientId rounds | `roles-distribution.add-rounds-since <clientId> <rounds>` |
| `roles-distribution.print-data` | `Gameplay.Cycle.RolesDistributionCommandsWrapper` | `PrintAllData` | () | `roles-distribution.print-data` |
| `roles-distribution.reset-rounds-since` | `Gameplay.Cycle.RolesDistributionCommandsWrapper` | `ResetRoundsSince` | clientId | `roles-distribution.reset-rounds-since <clientId>` |
| `sabotages.start` | `ConsoleCommands.Network.SabotagesCommandsWrapper` | `StartSabotage` | locationIndex sabotageIndex state | `sabotages.start <locationIndex> <sabotageIndex> <state>` |
| `settings.game.get-sensitivity` | `ConsoleCommands.GameSettingsCommandsWrapper` | `GetSensitivity` | () | `settings.game.get-sensitivity` |
| `settings.game.set-sensitivity` | `ConsoleCommands.GameSettingsCommandsWrapper` | `SetSensitivity` | sensitivity | `settings.game.set-sensitivity <sensitivity>` |
| `stats.all` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintAll` | () | `stats.all` |
| `stats.all` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintAll` | entriesLimit | `stats.all <entriesLimit>` |
| `stats.player` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintPlayer` | playerId | `stats.player <playerId>` |
| `stats.player` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintPlayer` | playerId entriesLimit | `stats.player <playerId> <entriesLimit>` |
| `stats.players` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintPlayers` | () | `stats.players` |
| `stats.trigger-time` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintTriggerTime` | playerId | `stats.trigger-time <playerId>` |
| `stats.trigger-time` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintTriggerTime` | playerId windowMinutes | `stats.trigger-time <playerId> <windowMinutes>` |
| `stats.trigger-time-all` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintTriggerTimeAll` | () | `stats.trigger-time-all` |
| `stats.trigger-time-all` | `ConsoleCommands.StatisticsCommandsWrapper` | `PrintTriggerTimeAll` | windowMinutes | `stats.trigger-time-all <windowMinutes>` |
| `walkietalkie.start-talking-all-walkies` | `Gameplay.Radio.WalkieTalkieTestingCommandsWrapper` | `StartTalkingAllWalkies` | () | `walkietalkie.start-talking-all-walkies` |
