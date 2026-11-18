# Game-built-in (native) emotes — census, classification, verification

Rendered from `C:\AlterumT2\BepInEx\LogOutput.log` by `tracks/emotes/tools/native_census_table.py`; every number below is a log line from the run, not a hand transcription.

Entry points: `F9`/`F7` cycle the list (12 LC + native), `F8` toggles the selected native emote, `emote.request` accepts `clip=native:<Name>`; all native clips go through `PosePlayer.StartNative` → the same `DoingSomethingHandler` + `DisableAnimancerAndAnimator` lease, the same `PostAnimationUpdate` write phase, the same 0.2 s fade and the same `RestorePoseNow` hand-back as the ported LC clips.

## Added: 38 native emotes (`native:<ClipName>`)

Body clips: `native:CommonAdventure_ThirdView_Movement_Idle`, `native:Common_ThirdView_Movement_Idle`

Upper-body clips, full id `native:FemaleAdventure_Hands_TP_<name>` (arms-only, played through the game's `Hands Mask`): `AdrenalineMushroom_Idle`, `AdrenalineMushroom_Using`, `Axe_Attack_1`, `Axe_Attack_2`, `Axe_Attack_Throw`, `Axe_Idle`, `Axe_Idle_ThrowPrepare`, `CeremonialCandle_Idle`, `Crucifix_Idle`, `Crucifix_Purification`, `DoingSomething`, `DoingSomething_CeremonialCandle`, `DoingSomething_Rune`, `DoorKey_Idle`, `Flashlight_Idle`, `HealthFlask_Idle`, `HealthFlask_Using_Quick`, `HealthMushroom_Idle`, `HealthMushroom_Using`, `HealthSyringe_Idle`, `HealthSyringe_Using_Quick`, `Lantern_Idle`, `RitualDagger_Attack_1`, `RitualDagger_Attack_2`, `RitualDagger_Idle`, `Rope_Idle`, `Rune_Idle`, `SpellScrollPart_Idle`, `SpellScroll_Burning`, `SpellScroll_Idle`, `Spellbook_Idle`, `ThrowingDagger_Attack_Throw`, `ThrowingDagger_Idle`, `ThrowingDagger_Idle_ThrowPrepare`, `WalkieTalkie_Idle`, `WalkieTalkie_IdleLowered`

## Source A — the third-person model's own Animancer graph

Enumerated by walking every `PersonAnimancerComponent` layer's states (mixers recursed) plus `GatherAnimationClips`/`GetAnimationClips` (41 unique clips), then sampling each clip on a scratch copy of the rig to count the bones it actually drives.

| layer | enum name | additive | mask | mask body parts | states |
|---|---|---|---|---|---|
| 0 | BaseLayer | False | none | no-mask(full body) | 2 |
| 1 | LeftHand | False | Hands Mask | LeftArm,RightArm,LeftFingers,RightFingers,LeftHandIK,RightHandIK | 0 |
| 2 | RightHand | False | Left Hand Mask | LeftArm,LeftFingers,LeftHandIK | 0 |
| 3 | BothHands | False | Right Hand Mask | RightArm,RightFingers,RightHandIK | 0 |
| 4 | Legs | False | Legs Mask | LeftLeg,RightLeg,LeftFootIK,RightFootIK | 0 |

| # | clip | len (s) | loop | layer membership | bones driven (rot/pos) | class | reason |
|---|---|---|---|---|---|---|---|
| 0 | `CommonAdventure_ThirdView_Movement_Idle` | 6.133 | yes | 0(BaseLayer) | 21/176 rot, 1 pos | emote | self-contained standing idle loop (6.13 s) for the adventure body set |
| 1 | `CommonAdventure_ThirdView_Movement_Idle_Turn_L` | 0.933 | yes | 0(BaseLayer) | 52/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 2 | `CommonAdventure_ThirdView_Movement_Idle_Turn_R` | 0.933 | yes | 0(BaseLayer) | 52/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 3 | `CommonAdventure_ThirdView_Movement_Jog_Back` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 4 | `CommonAdventure_ThirdView_Movement_Jog_Back_Left` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 5 | `CommonAdventure_ThirdView_Movement_Jog_Back_Right` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 6 | `CommonAdventure_ThirdView_Movement_Jog_Forward` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 7 | `CommonAdventure_ThirdView_Movement_Jog_Forward_Left` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 8 | `CommonAdventure_ThirdView_Movement_Jog_Forward_Right` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 9 | `CommonAdventure_ThirdView_Movement_Jog_Left` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 10 | `CommonAdventure_ThirdView_Movement_Jog_Right` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 11 | `CommonAdventure_ThirdView_Movement_Run_Forward` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 12 | `CommonAdventure_ThirdView_Movement_Run_Forward_Left` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 13 | `CommonAdventure_ThirdView_Movement_Run_Forward_Right` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 14 | `CommonAdventure_ThirdView_Movement_Run_Left` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 15 | `CommonAdventure_ThirdView_Movement_Run_Right` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 16 | `CommonAdventure_ThirdView_Movement_Walk_Back` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 17 | `CommonAdventure_ThirdView_Movement_Walk_Forward` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 18 | `CommonAdventure_ThirdView_Movement_Walk_Left` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 19 | `CommonAdventure_ThirdView_Movement_Walk_Right` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 20 | `Common_ThirdView_Movement_Idle` | 6.133 | yes | 0(BaseLayer) | 21/176 rot, 1 pos | emote | self-contained standing idle loop (6.13 s); deliberate resting pose, no locomotion input |
| 21 | `Common_ThirdView_Movement_Idle_Turn_L` | 0.933 | yes | 0(BaseLayer) | 52/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 22 | `Common_ThirdView_Movement_Idle_Turn_R` | 0.933 | yes | 0(BaseLayer) | 52/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 23 | `Hands_FPS_General_StaticIdle` | 0.083 | no | 0(BaseLayer) | 20/176 rot, 1 pos | unusable | first-person hands clip; the third-person player model has no matching rig for it |
| 24 | `Jog Back Left Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 25 | `Jog Back Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 26 | `Jog Back Right Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 27 | `Jog Forward Left Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 28 | `Jog Forward Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 29 | `Jog Forward Right Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 30 | `Jog Left Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 31 | `Jog Right Loop` | 0.800 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 32 | `Run Forward Left Loop` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 33 | `Run Forward Loop` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 34 | `Run Forward Right Loop` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 35 | `Run Left Loop` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 36 | `Run Right Loop` | 0.567 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 37 | `Walk Back Loop` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 38 | `Walk Forward Loop` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 39 | `Walk Left Loop` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |
| 40 | `Walk Right Loop` | 1.200 | yes | 0(BaseLayer) | 22/176 rot, 1 pos | unusable | locomotion blend-tree state authored for the movement mixer; not a self-contained pose |

## Source B — the player-character animation content loaded in this scene

The hands/legs layers carry **no states** in the lobby, so their clips are loaded but not instantiated: a state walk alone cannot see them. They are enumerated from the loaded `AnimationClip` set by the player-character name family, then sampled on this model's rig before any of them is offered. The `FemaleAdventure_*` family is the female adventure body set; its clips are Humanoid and retarget onto this rig (proved by the per-bone drive counts below and by the rendered captures). The `Female_*`/`Male*` families are the same animations authored for the other body variants and are logged as duplicates so the cycle does not repeat every emote four times.

`rot-driven bones` is measured by `NativeSampler` on the player rig (bones whose local rotation the clip changes over 16 sample phases, of 176 transforms). `-` means the clip was not sampled because it was rejected by role before any sampling; its class comes from the clip's authored role, and for the border-line cases the rendered pose in `shots/native-emotes/contact_sheet.png` was used.

| # | clip | len (s) | loop | rot-driven bones | class | reason |
|---|---|---|---|---|---|---|
| 0 | `FemaleAdventure_Body_TP_DaggerInstantKill_EndKill_Victim` | 2.400 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 1 | `FemaleAdventure_Body_TP_DaggerInstantKill_Start_Victim` | 0.933 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 2 | `FemaleAdventure_Body_TP_DraggerInstantKill_EndKill_Att` | 2.400 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 3 | `FemaleAdventure_Body_TP_DraggerInstantKill_Start_Att` | 0.933 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 4 | `FemaleAdventure_Exile_TP_ExileDeath` | 2.050 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 5 | `FemaleAdventure_Exile_TP_Idle` | 0.083 | yes | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 6 | `FemaleAdventure_Exile_TP_Idle_Tied` | 0.083 | yes | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 7 | `FemaleAdventure_Hands_TP_AdrenalineMushroom_Idle` | 0.333 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 8 | `FemaleAdventure_Hands_TP_AdrenalineMushroom_TakingOut` | 0.500 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 9 | `FemaleAdventure_Hands_TP_AdrenalineMushroom_Using` | 0.933 | no | 49/176 (playablegraph) | emote | one-shot item-use action; arms-only clip, layered over the body pose |
| 10 | `FemaleAdventure_Hands_TP_Axe_Attack_1` | 0.500 | no | 50/176 (playablegraph) | emote | one-shot weapon action; arms-only clip, layered over the body pose |
| 11 | `FemaleAdventure_Hands_TP_Axe_Attack_2` | 0.500 | no | 50/176 (playablegraph) | emote | one-shot weapon action; arms-only clip, layered over the body pose |
| 12 | `FemaleAdventure_Hands_TP_Axe_Attack_Throw` | 0.400 | no | 50/176 (playablegraph) | emote | one-shot weapon action; arms-only clip, layered over the body pose |
| 13 | `FemaleAdventure_Hands_TP_Axe_Idle` | 3.000 | yes | 50/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 14 | `FemaleAdventure_Hands_TP_Axe_Idle_ThrowPrepare` | 3.000 | yes | 50/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 15 | `FemaleAdventure_Hands_TP_Axe_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 16 | `FemaleAdventure_Hands_TP_CeremonialCandle_Idle` | 1.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 17 | `FemaleAdventure_Hands_TP_CeremonialCandle_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 18 | `FemaleAdventure_Hands_TP_Crucifix_Idle` | 1.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 19 | `FemaleAdventure_Hands_TP_Crucifix_Purification` | 3.333 | no | 49/176 (playablegraph) | emote | one-shot item-use action; arms-only clip, layered over the body pose |
| 20 | `FemaleAdventure_Hands_TP_Crucifix_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 21 | `FemaleAdventure_Hands_TP_DoingSomething` | 2.500 | yes | 50/176 (playablegraph) | emote | deliberate scripted action for the upper body; arms-only clip, layered over the body pose |
| 22 | `FemaleAdventure_Hands_TP_DoingSomething_CeremonialCandle` | 2.500 | yes | 50/176 (playablegraph) | emote | deliberate scripted action for the upper body; arms-only clip, layered over the body pose |
| 23 | `FemaleAdventure_Hands_TP_DoingSomething_Rune` | 2.500 | yes | 50/176 (playablegraph) | emote | deliberate scripted action for the upper body; arms-only clip, layered over the body pose |
| 24 | `FemaleAdventure_Hands_TP_DoorKey_Idle` | 1.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 25 | `FemaleAdventure_Hands_TP_DoorKey_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 26 | `FemaleAdventure_Hands_TP_Flashlight_Idle` | 1.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 27 | `FemaleAdventure_Hands_TP_Flashlight_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 28 | `FemaleAdventure_Hands_TP_HealthFlask_Idle` | 3.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 29 | `FemaleAdventure_Hands_TP_HealthFlask_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 30 | `FemaleAdventure_Hands_TP_HealthFlask_Using_Quick` | 5.167 | no | 50/176 (playablegraph) | emote | one-shot item-use action; arms-only clip, layered over the body pose |
| 31 | `FemaleAdventure_Hands_TP_HealthMushroom_Idle` | 0.333 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 32 | `FemaleAdventure_Hands_TP_HealthMushroom_TakingOut` | 0.500 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 33 | `FemaleAdventure_Hands_TP_HealthMushroom_Using` | 0.933 | no | 49/176 (playablegraph) | emote | one-shot item-use action; arms-only clip, layered over the body pose |
| 34 | `FemaleAdventure_Hands_TP_HealthSyringe_Idle` | 1.000 | yes | 48/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 35 | `FemaleAdventure_Hands_TP_HealthSyringe_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 36 | `FemaleAdventure_Hands_TP_HealthSyringe_Using_Quick` | 1.833 | no | 50/176 (playablegraph) | emote | one-shot item-use action; arms-only clip, layered over the body pose |
| 37 | `FemaleAdventure_Hands_TP_Lantern_Idle` | 0.167 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 38 | `FemaleAdventure_Hands_TP_Lantern_TakingOut` | 0.583 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 39 | `FemaleAdventure_Hands_TP_RitualDagger_Attack_1` | 0.500 | no | 49/176 (playablegraph) | emote | one-shot weapon action; arms-only clip, layered over the body pose |
| 40 | `FemaleAdventure_Hands_TP_RitualDagger_Attack_2` | 0.500 | no | 49/176 (playablegraph) | emote | one-shot weapon action; arms-only clip, layered over the body pose |
| 41 | `FemaleAdventure_Hands_TP_RitualDagger_Idle` | 4.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 42 | `FemaleAdventure_Hands_TP_RitualDagger_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 43 | `FemaleAdventure_Hands_TP_Rope_Idle` | 1.000 | yes | 48/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 44 | `FemaleAdventure_Hands_TP_Rope_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 45 | `FemaleAdventure_Hands_TP_Rune_Idle` | 1.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 46 | `FemaleAdventure_Hands_TP_Rune_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 47 | `FemaleAdventure_Hands_TP_SpellScrollPart_Idle` | 1.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 48 | `FemaleAdventure_Hands_TP_SpellScrollPart_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 49 | `FemaleAdventure_Hands_TP_SpellScroll_Burning` | 2.000 | no | 49/176 (playablegraph) | emote | one-shot item-use action; arms-only clip, layered over the body pose |
| 50 | `FemaleAdventure_Hands_TP_SpellScroll_Idle` | 1.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 51 | `FemaleAdventure_Hands_TP_SpellScroll_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 52 | `FemaleAdventure_Hands_TP_Spellbook_Idle` | 3.000 | yes | 50/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 53 | `FemaleAdventure_Hands_TP_Spellbook_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 54 | `FemaleAdventure_Hands_TP_ThrowingDagger_Attack_Throw` | 0.300 | no | 50/176 (playablegraph) | emote | one-shot weapon action; arms-only clip, layered over the body pose |
| 55 | `FemaleAdventure_Hands_TP_ThrowingDagger_Idle` | 3.000 | yes | 50/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 56 | `FemaleAdventure_Hands_TP_ThrowingDagger_Idle_ThrowPrepare` | 3.000 | yes | 50/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 57 | `FemaleAdventure_Hands_TP_ThrowingDagger_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 58 | `FemaleAdventure_Hands_TP_WalkieTalkie_Idle` | 3.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 59 | `FemaleAdventure_Hands_TP_WalkieTalkie_IdleLowered` | 3.000 | yes | 49/176 (playablegraph) | emote | held-item pose loop; arms-only clip, layered over the body pose |
| 60 | `FemaleAdventure_Hands_TP_WalkieTalkie_TakingOut` | 1.000 | no | - | unusable | transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn |
| 61 | `FemaleAdventure_Movement_TP_Crawling_Backward` | 1.200 | yes | - | unusable | locomotion/crawl state for the movement mixer; not a self-contained pose |
| 62 | `FemaleAdventure_Movement_TP_Crawling_Forward` | 1.200 | yes | - | unusable | locomotion/crawl state for the movement mixer; not a self-contained pose |
| 63 | `FemaleAdventure_Movement_TP_Crawling_Idle_1` | 3.000 | yes | - | unusable | locomotion/crawl state for the movement mixer; not a self-contained pose |
| 64 | `FemaleAdventure_Movement_TP_Crawling_Idle_2` | 6.000 | yes | - | unusable | locomotion/crawl state for the movement mixer; not a self-contained pose |
| 65 | `FemaleAdventure_TP_RitualDagger_Ability_Hit` | 1.000 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 66 | `FemaleAdventure_TP_RitualDagger_Ability_Idle` | 3.000 | yes | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 67 | `FemaleAdventure_TP_RitualDagger_Ability_Start` | 0.583 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |
| 68 | `FemaleAdventure_TP_RitualDagger_Sacrifice_Quick` | 0.833 | no | - | unusable | scripted sequence (kill/victim/exile); it assumes another character's position and state |

<details><summary>Duplicate variants (not offered): 198 clips</summary>

- `Female_Body_TP_DraggerInstantKill_EndKill_Att`
- `Female_Body_TP_DraggerInstantKill_Start_Att`
- `Female_Dragging_TP_Players_Idle`
- `Female_Exile_TP_ExileDeath`
- `Female_Exile_TP_Idle`
- `Female_Exile_TP_Idle_Tied`
- `Female_Hands_TP_Axe_Attack_1`
- `Female_Hands_TP_Axe_Attack_2`
- `Female_Hands_TP_Axe_Attack_Throw`
- `Female_Hands_TP_Axe_Idle`
- `Female_Hands_TP_Axe_Idle_ThrowPrepare`
- `Female_Hands_TP_Axe_TakingOut`
- `Female_Hands_TP_CeremonialCandle_Idle`
- `Female_Hands_TP_CeremonialCandle_TakingOut`
- `Female_Hands_TP_Crucifix_Idle`
- `Female_Hands_TP_Crucifix_Purification`
- `Female_Hands_TP_Crucifix_TakingOut`
- `Female_Hands_TP_DoingSomething`
- `Female_Hands_TP_DoingSomething_CeremonialCandle`
- `Female_Hands_TP_DoingSomething_Rune`
- `Female_Hands_TP_DoorKey_Idle`
- `Female_Hands_TP_DoorKey_TakingOut`
- `Female_Hands_TP_DragPlayers_Idle`
- `Female_Hands_TP_Flashlight_Idle`
- `Female_Hands_TP_Flashlight_TakingOut`
- `Female_Hands_TP_HealthFlask_Idle`
- `Female_Hands_TP_HealthFlask_TakingOut`
- `Female_Hands_TP_HealthFlask_Using_Quick`
- `Female_Hands_TP_HealthMushroom_Idle`
- `Female_Hands_TP_HealthMushroom_TakingOut`
- `Female_Hands_TP_HealthMushroom_Using`
- `Female_Hands_TP_HealthSyringe_Idle`
- `Female_Hands_TP_HealthSyringe_TakingOut`
- `Female_Hands_TP_HealthSyringe_Using_Quick`
- `Female_Hands_TP_Lantern_Idle`
- `Female_Hands_TP_Lantern_TakingOut`
- `Female_Hands_TP_RitualDagger_Attack_1`
- `Female_Hands_TP_RitualDagger_Attack_2`
- `Female_Hands_TP_RitualDagger_Idle`
- `Female_Hands_TP_RitualDagger_TakingOut`
- `Female_Hands_TP_Rope_Idle`
- `Female_Hands_TP_Rope_TakingOut`
- `Female_Hands_TP_Rune_Idle`
- `Female_Hands_TP_Rune_TakingOut`
- `Female_Hands_TP_SpellScrollPart_Idle`
- `Female_Hands_TP_SpellScrollPart_TakingOut`
- `Female_Hands_TP_SpellScroll_Burning`
- `Female_Hands_TP_SpellScroll_Idle`
- `Female_Hands_TP_SpellScroll_TakingOut`
- `Female_Hands_TP_Spellbook_Idle`
- `Female_Hands_TP_Spellbook_TakingOut`
- `Female_Hands_TP_ThrowingDagger_Attack_Throw`
- `Female_Hands_TP_ThrowingDagger_Idle`
- `Female_Hands_TP_ThrowingDagger_Idle_ThrowPrepare`
- `Female_Hands_TP_ThrowingDagger_TakingOut`
- `Female_Hands_TP_WalkieTalkie_Idle`
- `Female_Hands_TP_WalkieTalkie_IdleLowered`
- `Female_Hands_TP_WalkieTalkie_TakingOut`
- `Female_Movement_TP_Crawling_Backward`
- `Female_Movement_TP_Crawling_Forward`
- `Female_Movement_TP_Crawling_Idle_1`
- `Female_Movement_TP_Crawling_Idle_2`
- `Female_TP_RitualDagger_Ability_Hit`
- `Female_TP_RitualDagger_Ability_Idle`
- `Female_TP_RitualDagger_Ability_Start`
- `Female_TP_RitualDagger_Sacrifice_Quick`
- `MaleAdventure_Exile_TP_ExileDeath`
- `MaleAdventure_Exile_TP_Idle`
- `MaleAdventure_Exile_TP_Idle_Tied`
- `MaleAdventure_Hands_TP_AdrenalineMushroom_Idle`
- `MaleAdventure_Hands_TP_AdrenalineMushroom_TakingOut`
- `MaleAdventure_Hands_TP_AdrenalineMushroom_Using`
- `MaleAdventure_Hands_TP_Axe_Attack_1`
- `MaleAdventure_Hands_TP_Axe_Attack_2`
- `MaleAdventure_Hands_TP_Axe_Attack_Throw`
- `MaleAdventure_Hands_TP_Axe_Idle`
- `MaleAdventure_Hands_TP_Axe_Idle_ThrowPrepare`
- `MaleAdventure_Hands_TP_Axe_TakingOut`
- `MaleAdventure_Hands_TP_CeremonialCandle_Idle`
- `MaleAdventure_Hands_TP_CeremonialCandle_TakingOut`
- `MaleAdventure_Hands_TP_Crucifix_Idle`
- `MaleAdventure_Hands_TP_Crucifix_Purification`
- `MaleAdventure_Hands_TP_Crucifix_TakingOut`
- `MaleAdventure_Hands_TP_DoingSomething`
- `MaleAdventure_Hands_TP_DoingSomething_CeremonialCandle`
- `MaleAdventure_Hands_TP_DoingSomething_Rune`
- `MaleAdventure_Hands_TP_DoorKey_Idle`
- `MaleAdventure_Hands_TP_DoorKey_TakingOut`
- `MaleAdventure_Hands_TP_Flashlight_Idle`
- `MaleAdventure_Hands_TP_Flashlight_TakingOut`
- `MaleAdventure_Hands_TP_HealthFlask_Idle`
- `MaleAdventure_Hands_TP_HealthFlask_TakingOut`
- `MaleAdventure_Hands_TP_HealthFlask_Using_Quick`
- `MaleAdventure_Hands_TP_HealthMushroom_Idle`
- `MaleAdventure_Hands_TP_HealthMushroom_TakingOut`
- `MaleAdventure_Hands_TP_HealthMushroom_Using`
- `MaleAdventure_Hands_TP_HealthSyringe_Idle`
- `MaleAdventure_Hands_TP_HealthSyringe_TakingOut`
- `MaleAdventure_Hands_TP_HealthSyringe_Using_Quick`
- `MaleAdventure_Hands_TP_Lantern_Idle`
- `MaleAdventure_Hands_TP_Lantern_TakingOut`
- `MaleAdventure_Hands_TP_RitualDagger_Attack_1`
- `MaleAdventure_Hands_TP_RitualDagger_Attack_2`
- `MaleAdventure_Hands_TP_RitualDagger_Idle`
- `MaleAdventure_Hands_TP_RitualDagger_TakingOut`
- `MaleAdventure_Hands_TP_Rope_Idle`
- `MaleAdventure_Hands_TP_Rope_TakingOut`
- `MaleAdventure_Hands_TP_Rune_Idle`
- `MaleAdventure_Hands_TP_Rune_TakingOut`
- `MaleAdventure_Hands_TP_SpellScrollPart_Idle`
- `MaleAdventure_Hands_TP_SpellScrollPart_TakingOut`
- `MaleAdventure_Hands_TP_SpellScroll_Burning`
- `MaleAdventure_Hands_TP_SpellScroll_Idle`
- `MaleAdventure_Hands_TP_SpellScroll_TakingOut`
- `MaleAdventure_Hands_TP_Spellbook_Idle`
- `MaleAdventure_Hands_TP_Spellbook_TakingOut`
- `MaleAdventure_Hands_TP_ThrowingDagger_Attack_Throw`
- `MaleAdventure_Hands_TP_ThrowingDagger_Idle`
- `MaleAdventure_Hands_TP_ThrowingDagger_Idle_ThrowPrepare`
- `MaleAdventure_Hands_TP_ThrowingDagger_TakingOut`
- `MaleAdventure_Hands_TP_WalkieTalkie_Idle`
- `MaleAdventure_Hands_TP_WalkieTalkie_IdleLowered`
- `MaleAdventure_Hands_TP_WalkieTalkie_TakingOut`
- `MaleAdventure_Movement_TP_Crawling_Backward`
- `MaleAdventure_Movement_TP_Crawling_Forward`
- `MaleAdventure_Movement_TP_Crawling_Idle_1`
- `MaleAdventure_Movement_TP_Crawling_Idle_2`
- `MaleAdventure_TP_RitualDagger_Ability_Hit`
- `MaleAdventure_TP_RitualDagger_Ability_Idle`
- `MaleAdventure_TP_RitualDagger_Ability_Start`
- `MaleAdventure_TP_RitualDagger_Sacrifice_Quick`
- `Male_Body_TP_DaggerInstantKill_EndKill_Victim`
- `Male_Body_TP_DaggerInstantKill_Start_Victim`
- `Male_Body_TP_DraggerInstantKill_EndKill_Att`
- `Male_Body_TP_DraggerInstantKill_Start_Att`
- `Male_Dragging_TP_Players_Idle`
- `Male_Exile_TP_ExileDeath`
- `Male_Exile_TP_Idle`
- `Male_Exile_TP_Idle_Tied`
- `Male_Hands_TP_Axe_Attack_1`
- `Male_Hands_TP_Axe_Attack_2`
- `Male_Hands_TP_Axe_Attack_Throw`
- `Male_Hands_TP_Axe_Idle`
- `Male_Hands_TP_Axe_Idle_ThrowPrepare`
- `Male_Hands_TP_Axe_TakingOut`
- `Male_Hands_TP_CeremonialCandle_Idle`
- `Male_Hands_TP_CeremonialCandle_TakingOut`
- `Male_Hands_TP_Crucifix_Idle`
- `Male_Hands_TP_Crucifix_Purification`
- `Male_Hands_TP_Crucifix_TakingOut`
- `Male_Hands_TP_DoingSomething`
- `Male_Hands_TP_DoingSomething_CeremonialCandle`
- `Male_Hands_TP_DoingSomething_Rune`
- `Male_Hands_TP_DoorKey_Idle`
- `Male_Hands_TP_DoorKey_TakingOut`
- `Male_Hands_TP_DragPlayers_Idle`
- `Male_Hands_TP_Flashlight_Idle`
- `Male_Hands_TP_Flashlight_TakingOut`
- `Male_Hands_TP_HealthFlask_Idle`
- `Male_Hands_TP_HealthFlask_TakingOut`
- `Male_Hands_TP_HealthFlask_Using_Quick`
- `Male_Hands_TP_HealthMushroom_Idle`
- `Male_Hands_TP_HealthMushroom_TakingOut`
- `Male_Hands_TP_HealthMushroom_Using`
- `Male_Hands_TP_HealthSyringe_Idle`
- `Male_Hands_TP_HealthSyringe_TakingOut`
- `Male_Hands_TP_HealthSyringe_Using_Quick`
- `Male_Hands_TP_Lantern_Idle`
- `Male_Hands_TP_Lantern_TakingOut`
- `Male_Hands_TP_RitualDagger_Attack_1`
- `Male_Hands_TP_RitualDagger_Attack_2`
- `Male_Hands_TP_RitualDagger_Idle`
- `Male_Hands_TP_RitualDagger_TakingOut`
- `Male_Hands_TP_Rope_Idle`
- `Male_Hands_TP_Rope_TakingOut`
- `Male_Hands_TP_Rune_Idle`
- `Male_Hands_TP_Rune_TakingOut`
- `Male_Hands_TP_SpellScrollPart_Idle`
- `Male_Hands_TP_SpellScrollPart_TakingOut`
- `Male_Hands_TP_SpellScroll_Burning`
- `Male_Hands_TP_SpellScroll_Idle`
- `Male_Hands_TP_SpellScroll_TakingOut`
- `Male_Hands_TP_Spellbook_Idle`
- `Male_Hands_TP_Spellbook_TakingOut`
- `Male_Hands_TP_ThrowingDagger_Attack_Throw`
- `Male_Hands_TP_ThrowingDagger_Idle`
- `Male_Hands_TP_ThrowingDagger_Idle_ThrowPrepare`
- `Male_Hands_TP_ThrowingDagger_TakingOut`
- `Male_Hands_TP_WalkieTalkie_Idle`
- `Male_Hands_TP_WalkieTalkie_IdleLowered`
- `Male_Hands_TP_WalkieTalkie_TakingOut`
- `Male_Movement_TP_Crawling_Backward`
- `Male_Movement_TP_Crawling_Forward`
- `Male_Movement_TP_Crawling_Idle_1`
- `Male_Movement_TP_Crawling_Idle_2`
- `Male_TP_RitualDagger_Ability_Idle`
- `Male_TP_RitualDagger_Ability_Start`
- `Male_TP_RitualDagger_Sacrifice_Quick`

</details>

198 further loaded clips are the same animations for the other body variants (`Female_Hands_TP_*`, `Male_Hands_TP_*`, `MaleAdventure_Hands_TP_*`): class **duplicate**, offered once via the FemaleAdventure variant. They are all listed in the `LOADEDCLIP` section of the log (548 entries, de-duplicated by name).

### The rest of the loaded clip set (548 names, de-duplicated)

These are counted so the census is complete; each name and length is in the `LOADEDCLIP` section of the log. They are not player-character animation (NPC/enemy/object/world sets) and are outside both sources above.

| name family | clips |
|---|---|
| `Hands*` | 165 |
| `FemaleAdventure_Hands*` | 54 |
| `MaleAdventure_Hands*` | 54 |
| `Female_Hands*` | 52 |
| `Male_Hands*` | 52 |
| `MOB1*` | 42 |
| `CommonAdventure*` | 31 |
| `FP*` | 4 |
| `FemaleAdventure_Body*` | 4 |
| `FemaleAdventure_Movement*` | 4 |
| `FemaleAdventure_TP*` | 4 |
| `Female_Movement*` | 4 |
| `Female_TP*` | 4 |
| `MaleAdventure_Movement*` | 4 |
| `MaleAdventure_TP*` | 4 |
| `Male_Body*` | 4 |
| `Male_Movement*` | 4 |
| `Object*` | 4 |
| (other) | 54 |

## Rejected — every reason, with the clips it covers

| reason | clips |
|---|---|
| player-character clip outside the curated manifest (locomotion, first-person hands, or scripted) | 268: `CommonAdventure_ThirdView_Movement_Crouch_Idle`, `CommonAdventure_ThirdView_Movement_Crouch_Turn_In_Place_Left`, `CommonAdventure_ThirdView_Movement_Crouch_Turn_In_Place_Right`, `CommonAdventure_ThirdView_Movement_Crouch_Walk_Back` (+264 more) |
| locomotion blend-tree state authored for the movement mixer; not a self-contained pose | 38: `CommonAdventure_ThirdView_Movement_Idle_Turn_L`, `CommonAdventure_ThirdView_Movement_Idle_Turn_R`, `CommonAdventure_ThirdView_Movement_Jog_Back`, `CommonAdventure_ThirdView_Movement_Jog_Back_Left` (+34 more) |
| transitional enter-item animation: starts from the empty-hands pose and ends waiting for a prop this mod does not spawn | 18: `FemaleAdventure_Hands_TP_AdrenalineMushroom_TakingOut`, `FemaleAdventure_Hands_TP_Axe_TakingOut`, `FemaleAdventure_Hands_TP_CeremonialCandle_TakingOut`, `FemaleAdventure_Hands_TP_Crucifix_TakingOut` (+14 more) |
| scripted sequence (kill/victim/exile); it assumes another character's position and state | 11: `FemaleAdventure_Body_TP_DaggerInstantKill_EndKill_Victim`, `FemaleAdventure_Body_TP_DaggerInstantKill_Start_Victim`, `FemaleAdventure_Body_TP_DraggerInstantKill_EndKill_Att`, `FemaleAdventure_Body_TP_DraggerInstantKill_Start_Att` (+7 more) |
| locomotion/crawl state for the movement mixer; not a self-contained pose | 4: `FemaleAdventure_Movement_TP_Crawling_Backward`, `FemaleAdventure_Movement_TP_Crawling_Forward`, `FemaleAdventure_Movement_TP_Crawling_Idle_1`, `FemaleAdventure_Movement_TP_Crawling_Idle_2` |
| first-person hands clip; the third-person player model has no matching rig for it | 1: `Hands_FPS_General_StaticIdle` |

## Per-clip playback verification (all offered native emotes)

Armed per run with `nativesweep=1`. Each row: start, 1.6 s hold, stop, then the lease is released and the game animation is given 1.0 s to resume; `restoreMotion` is the maximum bone change over the following 0.4 s (must be >0: the game is animating again), `poseVsEntry` is the largest probe-bone angle between the pre-start pose and the settled pose (a stuck limb would be tens of degrees), `pelvisDelta` is the pelvis local-Y change after release.

| # | clip (native:id) | start | hold | stop | released | animEnabled | restoreMotion (deg) | moved bones | poseVsEntry (deg, worst) | pelvisDelta (m) |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `native:CommonAdventure_ThirdView_Movement_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 18/20 | 4.17 (calf_l) | 0.0130 |
| 2 | `native:Common_ThirdView_Movement_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 18/20 | 0.37 (upperarm_l) | 0.0008 |
| 3 | `native:FemaleAdventure_Hands_TP_AdrenalineMushroom_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 18/20 | 4.17 (calf_l) | 0.0130 |
| 4 | `native:FemaleAdventure_Hands_TP_AdrenalineMushroom_Using` | ok | 1.60 s | ok | True | True | 2.09 | 20/20 | 1.31 (upperarm_l) | 0.0020 |
| 5 | `native:FemaleAdventure_Hands_TP_Axe_Attack_1` | ok | 1.60 s | ok | True | True | 2.38 | 17/20 | 2.18 (upperarm_r) | 0.0023 |
| 6 | `native:FemaleAdventure_Hands_TP_Axe_Attack_2` | ok | 1.60 s | ok | True | True | 1.71 | 18/20 | 2.29 (clavicle_r) | 0.0019 |
| 7 | `native:FemaleAdventure_Hands_TP_Axe_Attack_Throw` | ok | 1.60 s | ok | True | True | 2.04 | 17/20 | 2.52 (upperarm_l) | 0.0027 |
| 8 | `native:FemaleAdventure_Hands_TP_Axe_Idle` | ok | 1.60 s | ok | True | True | 1.72 | 18/20 | 5.00 (calf_l) | 0.0121 |
| 9 | `native:FemaleAdventure_Hands_TP_Axe_Idle_ThrowPrepare` | ok | 1.60 s | ok | True | True | 1.69 | 19/20 | 4.14 (calf_l) | 0.0122 |
| 10 | `native:FemaleAdventure_Hands_TP_CeremonialCandle_Idle` | ok | 1.60 s | ok | True | True | 1.72 | 17/20 | 4.24 (calf_l) | 0.0132 |
| 11 | `native:FemaleAdventure_Hands_TP_Crucifix_Idle` | ok | 1.60 s | ok | True | True | 1.80 | 19/20 | 4.17 (calf_l) | 0.0130 |
| 12 | `native:FemaleAdventure_Hands_TP_Crucifix_Purification` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 4.22 (calf_l) | 0.0124 |
| 13 | `native:FemaleAdventure_Hands_TP_DoingSomething` | ok | 1.60 s | ok | True | True | 1.72 | 17/20 | 4.56 (calf_l) | 0.0133 |
| 14 | `native:FemaleAdventure_Hands_TP_DoingSomething_CeremonialCandle` | ok | 1.60 s | ok | True | True | 1.69 | 19/20 | 4.14 (calf_l) | 0.0122 |
| 15 | `native:FemaleAdventure_Hands_TP_DoingSomething_Rune` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 4.22 (calf_l) | 0.0124 |
| 16 | `native:FemaleAdventure_Hands_TP_DoorKey_Idle` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 4.53 (calf_l) | 0.0125 |
| 17 | `native:FemaleAdventure_Hands_TP_Flashlight_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 18/20 | 4.56 (calf_l) | 0.0133 |
| 18 | `native:FemaleAdventure_Hands_TP_HealthFlask_Idle` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 4.14 (calf_l) | 0.0122 |
| 19 | `native:FemaleAdventure_Hands_TP_HealthFlask_Using_Quick` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 4.53 (calf_l) | 0.0125 |
| 20 | `native:FemaleAdventure_Hands_TP_HealthMushroom_Idle` | ok | 1.60 s | ok | True | True | 1.80 | 19/20 | 4.56 (calf_l) | 0.0133 |
| 21 | `native:FemaleAdventure_Hands_TP_HealthMushroom_Using` | ok | 1.60 s | ok | True | True | 1.70 | 19/20 | 0.90 (upperarm_l) | 0.0012 |
| 22 | `native:FemaleAdventure_Hands_TP_HealthSyringe_Idle` | ok | 1.60 s | ok | True | True | 1.87 | 19/20 | 5.76 (calf_l) | 0.0142 |
| 23 | `native:FemaleAdventure_Hands_TP_HealthSyringe_Using_Quick` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 4.53 (calf_l) | 0.0125 |
| 24 | `native:FemaleAdventure_Hands_TP_Lantern_Idle` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 4.53 (calf_l) | 0.0125 |
| 25 | `native:FemaleAdventure_Hands_TP_RitualDagger_Attack_1` | ok | 1.60 s | ok | True | True | 2.49 | 18/20 | 0.73 (upperarm_r) | 0.0007 |
| 26 | `native:FemaleAdventure_Hands_TP_RitualDagger_Attack_2` | ok | 1.60 s | ok | True | True | 2.52 | 18/20 | 2.55 (clavicle_r) | 0.0015 |
| 27 | `native:FemaleAdventure_Hands_TP_RitualDagger_Idle` | ok | 1.60 s | ok | True | True | 1.87 | 20/20 | 5.30 (calf_l) | 0.0117 |
| 28 | `native:FemaleAdventure_Hands_TP_Rope_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 17/20 | 4.56 (calf_l) | 0.0133 |
| 29 | `native:FemaleAdventure_Hands_TP_Rune_Idle` | ok | 1.60 s | ok | True | True | 1.87 | 19/20 | 4.14 (calf_l) | 0.0122 |
| 30 | `native:FemaleAdventure_Hands_TP_SpellScrollPart_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 18/20 | 4.56 (calf_l) | 0.0133 |
| 31 | `native:FemaleAdventure_Hands_TP_SpellScroll_Burning` | ok | 1.60 s | ok | True | True | 1.72 | 18/20 | 4.17 (calf_l) | 0.0130 |
| 32 | `native:FemaleAdventure_Hands_TP_SpellScroll_Idle` | ok | 1.60 s | ok | True | True | 1.69 | 19/20 | 4.14 (calf_l) | 0.0122 |
| 33 | `native:FemaleAdventure_Hands_TP_Spellbook_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 18/20 | 4.24 (calf_l) | 0.0132 |
| 34 | `native:FemaleAdventure_Hands_TP_ThrowingDagger_Attack_Throw` | ok | 1.60 s | ok | True | True | 1.84 | 17/20 | 2.05 (lowerarm_r) | 0.0025 |
| 35 | `native:FemaleAdventure_Hands_TP_ThrowingDagger_Idle` | ok | 1.60 s | ok | True | True | 1.73 | 17/20 | 4.79 (upperarm_l) | 0.0111 |
| 36 | `native:FemaleAdventure_Hands_TP_ThrowingDagger_Idle_ThrowPrepare` | ok | 1.60 s | ok | True | True | 1.73 | 17/20 | 4.17 (calf_l) | 0.0130 |
| 37 | `native:FemaleAdventure_Hands_TP_WalkieTalkie_Idle` | ok | 1.60 s | ok | True | True | 1.72 | 17/20 | 4.17 (calf_l) | 0.0130 |
| 38 | `native:FemaleAdventure_Hands_TP_WalkieTalkie_IdleLowered` | ok | 1.60 s | ok | True | True | 1.69 | 19/20 | 4.14 (calf_l) | 0.0122 |

Offered: **38** (2 full-body + 36 upper-body). Failed and removed from the list: **0**.

## 12 ported LC clips — armed sweep after the change

- [emote] requested 'Salute' by selftest#1 (LC port)
- [emote] START clip='Salute' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=1.783s loop=False frames=108 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='Salute' t=1.64s released=True fadeOut=0.20s
- [emote] requested 'Middle_Finger' by selftest#2 (LC port)
- [emote] START clip='Middle_Finger' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=0.867s loop=False frames=53 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=end(Middle_Finger) clip='Middle_Finger' t=0.92s released=True fadeOut=0.20s
- [emote] requested 'Sign' by selftest#3 (LC port)
- [emote] START clip='Sign' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=2.083s loop=True frames=126 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='Sign' t=1.65s released=True fadeOut=0.20s
- [emote] requested 'D_Sign' by selftest#4 (LC port)
- [emote] START clip='D_Sign' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=3.150s loop=False frames=190 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='D_Sign' t=1.62s released=True fadeOut=0.20s
- [emote] requested 'The_Griddy' by selftest#5 (LC port)
- [emote] START clip='The_Griddy' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=6.533s loop=True frames=393 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='The_Griddy' t=1.62s released=True fadeOut=0.20s
- [emote] requested 'Clap' by selftest#6 (LC port)
- [emote] START clip='Clap' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=1.000s loop=True frames=61 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='Clap' t=0.64s released=True fadeOut=0.20s
- [emote] requested 'Twerk' by selftest#7 (LC port)
- [emote] START clip='Twerk' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=0.250s loop=True frames=16 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='Twerk' t=0.01s released=True fadeOut=0.20s
- [emote] requested 'Prisyadka' by selftest#8 (LC port)
- [emote] START clip='Prisyadka' arms-only arm source: 8 bones, 406 frames
- [emote] START clip='Prisyadka' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=6.750s loop=True frames=406 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='Prisyadka' t=2.06s released=True fadeOut=0.20s
- [emote] requested 'Idle1Fix' by selftest#9 (LC port)
- [emote] START clip='Idle1Fix' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=4.033s loop=True frames=243 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=movement(0.600m) clip='Idle1Fix' t=1.02s released=True fadeOut=0.20s
- [emote] requested 'Shy' by selftest#10 (LC port)
- [emote] START clip='Shy' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=2.500s loop=True frames=151 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='Shy' t=1.66s released=True fadeOut=0.20s
- [emote] requested 'D_Clap' by selftest#11 (LC port)
- [emote] START clip='D_Clap' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=1.000s loop=True frames=61 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=selftest-window clip='D_Clap' t=0.04s released=True fadeOut=0.20s
- [emote] requested 'D_Middle_Finger' by selftest#12 (LC port)
- [emote] START clip='D_Middle_Finger' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=0.867s loop=False frames=53 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
- [emote] STOP reason=end(D_Middle_Finger) clip='D_Middle_Finger' t=0.92s released=True fadeOut=0.20s

Restore check after the 12th clip: max bone change over 0.4 s = **2.32 deg**.

## Reproduce

```bash
./track.sh emotes build && ./track.sh emotes deploy     # C:\AlterumT2, hot reload
printf 'census=1\n' > .../BepInEx/screens/t2/emote.request        # census + bone counts
printf 'nativesweep=1\n' > .../emote.request                      # verification table
printf 'selftest=1\n' > .../emote.request                         # 12-clip LC sweep
printf 'nativefilm=1\nfilmnames=<a,b,...>\n' > .../emote.request  # filmstrips
python3 tools/native_census_table.py    # this document from the log
python3 tools/native_filmstrips.py      # shots/native-emotes/*
```

Deployed binary: `/mnt/c/AlterumT2/BepInEx/hot/t2/AlterumModelSwap.Hot.dll` md5 `4c14c9b254483299a2b2bcc76de5b36e`, mtime 2026-11-12 11:46:13. Hot reload only; the game was relaunched once at 1020x552 because the previous instance had crashed.

## Entry points exercised (no keyboard: desktop locked)

- `clip=native:<Name>` — request-driven start; used for the single-clip probes and all sweeps.
- `native=1` / `native=0` — start/stop the selected native emote.
- `cycle=N` — calls the exact `CycleNext` path the `F9`/`F7` keys call; log lines `[emote] request-cycle: clip index k/50 -> '<id>'` prove the combined 12 + 38 cycle. The F9 key itself only adds the `wasPressedThisFrame` read in `Keys()`.
- `nativesweep=1` — the verification table above; `nativefilm=1 filmnames=<a,b,...>` — the captures.

## Captures

`shots/native-emotes/`: 8 ClipRecorder showcase filmstrips (640x360, 2 fps, 6 frames / 3 s, cropped) for `native:Common_ThirdView_Movement_Idle`, `native:CommonAdventure_ThirdView_Movement_Idle`, `native:FemaleAdventure_Hands_TP_Axe_Idle`, `native:FemaleAdventure_Hands_TP_WalkieTalkie_Idle`, `native:FemaleAdventure_Hands_TP_Spellbook_Idle`, `native:FemaleAdventure_Hands_TP_HealthFlask_Using_Quick`, `native:FemaleAdventure_Hands_TP_Axe_Attack_1`, `native:FemaleAdventure_Hands_TP_Crucifix_Purification`; plus `contact_sheet.png` (all 38 verified clips, release-time RenderTexture still) and one `_still.png` per filmstripped clip. All captures are RenderTexture reads; the desktop was never used.

