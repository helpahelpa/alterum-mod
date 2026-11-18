# Smoke checklist — 26642252

Instance `matrix-26642252`, 640x360, private lobby. Line numbers refer to
`logs/runtime-LogOutput.log`.

| # | check | result | evidence (line) |
|---|---|---|---|
| 1 | `Chainloader startup complete` | PASS | 26 |
| 2 | `[host] ready` | PASS | 25 |
| 3 | `[hot] watching <expected>` | PASS | 18 — `...\BepInEx\hot\matrix-26642252\AlterumModelSwap.Hot.dll` |
| 4 | `[hot] generation 1 active` | PASS | 24 |
| 5 | atomic redeploy → generation 2 | PASS | 3742 (`[hot] generation 2 active`) |
| 6 | wardrobe 26 total rows / 16 custom | PASS | 2636 `tabgroup sync (attach): rows=26 tabs=26 ... added=26`; 4016 `roster: 16 entries (16 of 26 PlayerModelData rows are ours)` |
| 7 | no duplicates after reload | PASS | 4047+ — all 16 entries re-adopted at indices 10‑25; no `pruned`/tab-insert line after gen 2 |
| 8 | rigid swap `Sahur (Classic)` | PASS (second attempt) | first select 2983 was overridden by the game to index 7 at 3082 (before the wardrobe data pass); re-select 5762 → 5787 `wardrobe selection IS 'Sahur (Classic)' ... body on`; capture `shot_..._matrix-dev-classic2_...png` |
| 9 | rigged swap `Sahur` | PASS (second attempt) | 3073 reverted the same way; 5129 `wardrobe selection IS 'Sahur' (index 10) -> body on`; capture `shot_..._matrix-dev-rigged_...png` |
| 10 | cosmetics hat anchored on a custom model | PASS | 5743 `source=anchor('sahur-roblox')` + 5744 `[cosmetic:bind] id=builtin.partyhat bone='head'`; 6377/6378 `source=anchor('sahur-classic')` + bind; `tophat` loads at 5138/5797, `santahat` at 5757/5798 |
| 11 | reel spawns at a saved anchor in a map | PASS | map 2801 (entered by itself — this build's maps declare `min=1`) and 3924; 2980 & 4965 `[reel:spawn] ... saved=2 spawned=2 failed=0` |
| 12 | native `INTERACT` prompt shows | PASS | 6453 `forceui activeView shown=True owner=...DistantInteractionWorldSpaceUI ... button='e' name='interact'`; prompt built at 6447 (`build ok source=scene template ...`) |
| 13 | one successful reel interaction | **FAIL** | 6419 `InitializeInteraction threw: NullReferenceException`; 5048 `OnInteractionPerformed threw: NullReferenceException`; `state built=True accepted=0` (6454) |
| 14 | emote catalogue counts 50 | PASS | 1306/3639/4753 `pose player ready (12 LC clips + 38 game clips); ... F9 cycles the 50-entry list` |
| 15 | one ported clip plays | PASS | 4990 `requested 'Salute' (LC port)`; 5007 `STOP fadeout complete ... handed back to the game's abstraction` |
| 16 | one native clip plays | PASS | 5010 `requested 'native:Common_ThirdView_Movement_Idle' (game clip)`; 5043 STOP fadeout complete |
| 17 | ordinary animation resumes | PASS | same two STOP lines; no further `[emote:active]` |
| 18 | console opens with a non-empty table (109) | PASS (binding action, not a physical key) | 5022 `[qc] toggle request -> open IsActive=True ... commands=109`; Tilde keystroke BLOCKED for the same reason as the other build |
| 19 | lobby → map | PASS | 399 → 2801 (this build starts a solo map without the one-player write: `min=1`) |
| 20 | map → lobby | **BLOCKED** | no unattended path (pause menu exposes only *Exit to Menu*; the map exit is an in-world trigger) |
| 21 | ≥120 s settled observation, no recurring mod exceptions | PASS | window from line 6474 (125 s): 6 lines, 0 exceptions, 0 warnings |

## Error budget of the whole run

54 lines match `Exception|[Error`, of which 26 are `System.NullReferenceException` stack lines. All
of them come from two mod-side paths that are identical on the other build:

* the game's `HWG.Settings.Controls.RebindActionUI.UpdateBinding/UpdateBindingDisplay` throwing while
  the mod clones its Controls rows — 24 rows end up `row left disabled` (the documented
  `resolved=0/34` limitation);
* the reel interaction path (`InitializeInteraction`, `OnInteractionPerformed`).

No crash, no native fault, no missing-type/method exception at load: `ErrorLog.log` is 0 bytes and the
process ran to the end of the harness.
