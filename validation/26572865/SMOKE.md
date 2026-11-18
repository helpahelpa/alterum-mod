# Smoke checklist — 26572865

Instance `matrix-26572865`, 640x360, private lobby via the cfg click sequence. Line numbers refer to
`logs/runtime-LogOutput.log` (the final run; earlier runs' logs are `logs/run2-LogOutput.log` and
`logs/run2-instance.log`, kept for the record).

| # | check | result | evidence (line) |
|---|---|---|---|
| 1 | `Chainloader startup complete` | PASS | 26 |
| 2 | `[host] ready` | PASS | 25 |
| 3 | `[hot] watching <expected>` | PASS | 18 — `...\BepInEx\hot\matrix-26572865\AlterumModelSwap.Hot.dll` |
| 4 | `[hot] generation 1 active` | PASS | 24 |
| 5 | atomic redeploy → generation 2 | PASS | 4191 (`[hot] generation 2 active`); atomic temp-name + rename in `BepInEx/hot/matrix-26572865` |
| 6 | wardrobe 26 total rows / 16 custom | PASS | 2684 `tabgroup sync (attach): rows=26 tabs=26 ... added=26`; 4756 `roster: 16 entries (16 of 26 PlayerModelData rows are ours)` |
| 7 | no duplicates after reload | PASS | 4787+ — all 16 re-adopted at their previous indices (10‑25), no `pruned`/`inserted tab` line after gen 2 |
| 8 | rigid swap `Sahur (Classic)` | PASS | 2827/2847 `select 'Sahur (Classic)' (index 11)` → `wardrobe selection IS 'Sahur (Classic)' ... body on`; capture `shot_..._m3-classic_...png` |
| 9 | rigged swap `Sahur` | PASS | 3495/3508 `select 'Sahur' (index 10)` → `wardrobe selection IS 'Sahur' ... body on`; capture `shot_..._m3-rigged_...png` |
| 10 | cosmetics hat anchored on a custom model | PASS | 3425 `source=anchor('sahur-classic')` + 3426 `[cosmetic:bind] id=builtin.partyhat bone='head'`; 4127/4128 `source=anchor('sahur-roblox')` + bind; `santahat` loads at 3489/6229 |
| 11 | reel spawns at a saved anchor in a map | PASS | 7114 `[scene] -> 'Lair Of Occultists Map Scene'`; 7293 `[reel:spawn] ... saved=2 spawned=2 failed=0` (`lair-1`, `lair-2`) |
| 12 | native `INTERACT` prompt shows | PASS | 7458 prompt component `DistantInteractionStaticPrompt`, resolved action `interact`; 8256 `forceui activeView shown=True ... button='e' name='interact'` (the game's own world-space prompt object) |
| 13 | one successful reel interaction | **FAIL** | 7445 `InitializeInteraction threw: NullReferenceException`; 8270 `OnInteractionPerformed threw: NullReferenceException`; `state built=True accepted=0` (8259, 8281) |
| 14 | emote catalogue counts 50 | PASS | 1350/6899 `pose player ready (12 LC clips + 38 game clips); ... F9 cycles the 50-entry list` |
| 15 | one ported clip plays | PASS | 6933 `requested 'Salute' (LC port)`; 6961 `STOP fadeout complete ... handed back to the game's abstraction` |
| 16 | one native clip plays | PASS | 6993 `requested 'native:Common_ThirdView_Movement_Idle' (game clip)`; 7057 STOP fadeout complete |
| 17 | ordinary animation resumes | PASS | the two `STOP fadeout complete ... handed back to the game's abstraction` lines (6961, 7057); no `[emote:active]` after them |
| 18 | console opens with a non-empty table (109) | PASS (opened through the mod binding action) | 7068 `[qc] toggle request -> open IsActive=True ... commands=109`; 7071 close. The physical Tilde keystroke is BLOCKED — see below |
| 19 | lobby → map | PASS | 396 → 7114 |
| 20 | map → lobby | **BLOCKED** | no unattended path: the pause menu exposes only `Exit to Menu` (dump line 8479); the map exit is an in-world stage-transition trigger |
| 21 | ≥120 s settled observation, no recurring mod exceptions | PASS | window from line 8506 (125 s): 0 error/exception lines, 0 warnings; an earlier 125 s window in the map (4969 lines) also had 0 exceptions but 620 recurring `'3P_ConcreteModel_Corpse_Skeleton(Clone)' is missing 22 game bone(s)` warnings |

## Physical Tilde keystroke (item 18)

Three attempts from an interactive-session task (the only place a window can exist):

1. `SetForegroundWindow` refused (returned the previous foreground hwnd).
2. `AttachThreadInput` + `SetForegroundWindow` → `setfg=False`; `keybd_event(VK_OEM_3)` delivered to
   the wrong window, no `[qc]` line.
3. `SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT,0)` + synthetic Alt tap → focus obtained
   (`now=<our hwnd>`), `keybd_event(VK_OEM_3)` sent → still no toggle (the mod reads the new Input
   System, which did not see the injected event). Follow-up `SendInput` attempts could not re-acquire
   the foreground.

The console was therefore opened by `qc.request toggle`, which is the exact action the
`mod.console.toggle` (Tilde) binding dispatches (`GameConsole.Toggle` → `QuantumConsole.Activate()`)
— the game's own console, 109 commands, table generated (`ensure(confirm) commands=109
TableGenerated=True`, line 2697 area). What is *not* proven is the OS keystroke itself.
