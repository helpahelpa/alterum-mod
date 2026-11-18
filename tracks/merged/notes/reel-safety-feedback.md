# Reel of Fortune — safety, honest debuff cards, sound, interaction, result feedback

Pass of 2026-11-13 (merged tree, instance A). Rates are provisional (item 46 / damage 24 /
debuff 20 / double 10) — the reference-game odds analysis is still to land.

## 1–2. Never bet or win: runes and the X-lantern

Identified from `data/item-pool.json` by id/name, and enforced in two independent places:

| item | id | pool name | prefab / evidence | why |
|---|---|---|---|---|
| Curse Dispelling Rune | `rune` | Curse Dispelling Rune | `Curse Dispelling Rune Handheld`, sprite `item-icons_rune` | quest step consumes it |
| Lantern | `lantern` | Lantern | `Lantern Handheld`, `LanternProperties` (has a `Toggler`), sprite `item-icons_lantern` | the light in the game's own **Lantern Slot** (key **X**; `LANTERN_SLOT`, `FirstEmptyFAOrLanternSlotDefaultInventoryItemSlotDataProvider`, quest `LightTheLantern`) |

There is exactly **one** lantern item in the pool, so the X-selected lantern and "the ordinary
lantern" are the same id — nothing else was excluded for being a lantern.

Enforcement:

* **Deck**: `tools/gen-reel-pool.py` classifies by id/name (`lantern` / `*rune*` /
  "curse dispelling") and writes `weight 0` + an `Exclusion` reason on the row in
  `ReelPool.g.cs`. `ReelDeck.WeightedItem` only walks rows with `Weight > 0`, so they can never be
  composed. Log: `[reel] idle ... excluded=[lantern ... quest-lantern; rune ... quest-rune]`.
* **Input**: `ReelExclusions.ReasonFor(id, name)` runs in `ReelLogic.TryBeginPull` **before** any
  inventory take; refusal logs
  `[reel] pull refused reason=quest-rune held='rune' name='Curse Dispelling Rune' consumed=0`
  (and `reason=quest-lantern` for the lantern), shows the error line and plays the error buzz.
* 64-cell deck, live map: `item=30 damage=15 debuff=13 double=6` — no `LAN`, no `CDR` card in the
  strip order (logged in full by the `[reel] strip ... order(left->right)=[...]` line).

## 3. Debuff cards name their effect

Three real families, each a card of its own colour, short code and label line:

| card | game family (variant actually applied) | applied via | result line |
|---|---|---|---|
| `SLOW`  | `SlowImposedEffectVariantData` (`Slow_Axe_ImposedEffectVariantData:x0.50`) | `PlayersImposedEffects.ImposeTheEffect` | "Slowed 50 %" |
| `FATIG` | `FatigueImposedEffectVariantData` (`Fatigue_Permanent_50p_...:stamina+50%`) | `PlayersImposedEffects` | "Tired: stamina drains faster" |
| `BLIND` | `BlindnessImposedEffectVariantData` (`BlindnessAlteredAbility_ForExplorer_...`) | `ImposedEffectsUtilities.ImposeEffect` | "Blinded" |

The deck is composed with only the families that have a loaded variant (`EnsureDebuffScan`,
`[reel] debuff scan slow=N fatigue=N blind=N`); with one family it would compose only that one
card type. Cards carry the kind; resolve applies that kind or nothing (the old silent fold into
damage is gone). Each face: per-kind band/gradient/motif, big code in the plate, and an effect
label line under the motif (10 card px = ~3 screen px at 47 px/card; the code is the primary
label at that size).

## 4. Sound — synthesised, layered, FMOD

`ReelAudio.cs`. Eight cue buffers generated from partials + shaped noise with per-tone envelopes
and a per-cue transient/body/tail layering, normalised, converted to 16-bit mono WAV and created
through the game's own `RuntimeManager.CoreSystem.createSound(... OPENMEMORY)`. `AudioClip.Create`
is not usable here: `SetData(Il2CppStructArray<float>)` dies in
`Il2CppSystem.ReadOnlySpan.GetPinnableReference` and the PCMReaderCallback route fails with
"Method unstripping failed".

| cue | duration | used |
|---|---|---|
| Lever | 0.46 s | spin start (ratchet clicks + cabinet thump + spring ring) |
| Tick | 0.05 s | per card crossing the marker while scrolling; pitch 0.85–2.4 from strip speed |
| Settle | 0.42 s | card lands (knock + frame ring) |
| Win | 1.05 s | item card (bell arpeggio) |
| Double | 1.50 s | double card (two-note fanfare, fifth+octave stacks, shimmer) |
| Damage | 0.55 s | damage card (sub sweep + crack) |
| Debuff | 0.62 s | debuff card (detuned sagging buzz) |
| Error | 0.40 s | refusal / empty hand (two-tone refusal) |

Released in `Dispose` (`sound.release()`), buffers unpinned. Gated by `AudioListener.pause/volume`,
`Time.timeScale` and `scene.isLoaded`. The desktop was locked, so nothing could be heard: the log
lines `[reel:audio] cue=... dur=...s via=FMOD` are the only confirmation. Tick calls are not
logged individually (the `[reel:scroll]` line for the same cell change is).

## 5–6. Interaction prompt and empty hand

* The booth is now a lookable: camera→sign-face centre dot ≥ 0.90 and ≤ 2.80 m shows the prompt;
  looking away hides it. E only acts while looked at (`[reel:ui] looking dot=0.976 dist=1.16m`).
* Adoption attempt, logged: `ActionPromptsController` is reachable
  (`.../HUD Canvas/Key Prompts(Clone)/Actions Prompts`), but `ShowOrChangePrompt` NREs inside
  `KeyActionPrompt.GetTableKey` for any action outside its static `_tableKeys` map, and that map
  holds only the nine non-interact actions (Use/Attack/Aim/Lower/Drop/Toggle/Show/Microphone/
  SpiritualBond) — no interact entry exists to borrow. The game's E prompt for a looked-at object
  is the world-space `DistantInteractionPrompt` owned by a networked `LookableDistantInteractable`
  and its own prefab, which a hot-loaded mod cannot join in this session.
* Fallback, as the brief allows: camera-parented textured quads (`ReelHudWorld`) drawn in the same
  visual language (dark plate, gold/red rule, pale 5x7 text), 0.06 m in front of the camera so held
  items do not cover them. They appear in `ScreenShot` captures, which is what made the 640×360
  proof possible on a locked desktop. The object name is in the prompt:
  `REEL OF FORTUNE - BET AN ITEM`.
* Empty hand: `[reel] pull rejected reason=no-held-item ... message='You need an item to bet'` +
  error buzz + the drawn line.
* Results: `GameNotifications` is found (`Master Canvas/Notifications`) but both static and
  instance `ShowNotification` throw NRE from this mod, so the message is drawn by the same path:
  `You won 1 x <item>`, `Double! 2 x <item>`, `You lost <N> HP` / `... - you went down`,
  `Slowed 50 %`, `Tired: stamina drains faster`, `Blinded`.

## 7. Captures (640×360, `shots/reel-safety/`)

| file | content |
|---|---|
| `01_prompt_and_empty_hand_error.png` | prompt + "YOU NEED AN ITEM TO BET" |
| `02_refuse_rune.png` | "NOT THE CURSE DISPELLING RUNE: QUEST ITEM" |
| `03_result_item.png` | "YOU WON 1 X SPELL SCROLL: CURSING TOTEM" + CTO card |
| `04_result_double.png` | "DOUBLE! 2 X ROPE" + X2 card (held rope partly over the line) |
| `05_result_damage_down.png` | "YOU LOST 10 HP - YOU WENT DOWN" |
| `06_result_slow.png` | "SLOWED 50 %" + SLOW card |
| `07_result_blind.png` | "BLINDED" + BLIND card, screen visibly blinded |
| `deck_strip_8192x160.png` | the exact strip uploaded (card faces at native 128×160) |

The lantern refusal was not captured in-game: the inventory filled up during the run, so
`give=lantern`/`hold=lantern` failed (`hold ... found no slot`). The rule itself is exercised by the
same call and is checked headlessly in `refusal_rules.txt`
(`refuse('lantern') = 'quest-lantern'`, `refuse('rune') = 'quest-rune'`,
`refuse('rune_stone') = 'quest-rune'`).

## 8. Rates — structure now

* One table: `ReelTable` in `ReelDeck.cs` (`WeightItem/Damage/Debuff/Double`, the pity knobs, the
  debuff table). Item-category weights stay generated in `ReelPool.g.cs`.
* One algorithm function: `ReelDraw.Next(state, cards, seed)` with `ReelDrawMode`
  `FixedMultisetReshuffled` (current) | `BagWithoutReplacement` | `PityCounter`. Switching is
  `ReelTable.Mode` / `_draw.Reset(mode)` (request: `algorithm=fixed|bag|pity`), no caller changes.
* Provisional 64-cell composition: 30/15/13/6 (46.875/23.4/20.3/9.375 % per card vs 46/24/20/10
  requested — within one card of each weight, largest-remainder split).
* 1000 synthetic pulls through the production algorithm: item 47.0 / damage 24.9 / debuff 19.6 /
  double 8.5 (fixed), 46.3/23.5/21.1/9.1 (bag), 46.7/22.3/21.3/9.7 (pity). Debuff split
  SLOW/FATIG/BLIND ≈ 5/4/4 of the deck.

For the rate pass: swap the four numbers (and, if the analysis changes the scheme, `ReelTable.Mode`
and the one `ReelDraw` branch); the deck size, strip, art and resolve path stay as they are.

## Shared files touched (other agents may own lines there)

* `ModelSwapRuntime.cs` — one added line in `OnGUI`: `ReelHud.OnGUI()` (last-resort drawn UI).
* `LobbyProp.cs` — reel-only sections: interaction/look test, prompt, E gating, audio cues,
  deck recompose on debuff-mask change, `ReelDraw.Next` for the target cell, request keys
  (`algorithm=`, `outcome=slow|fatigue|blind`), dispose of audio/prompt.
* `ReelLogic.cs` — refusal gate before consume, per-kind debuff scan/apply, result texts.
* `ReelArt.cs` — per-debuff palettes/motifs, label line, card key/tone include the kind.
* `ReelDeck.cs` — table, algorithm, exclusions, census (rewritten).
* `Alterum.References.props` — added `UnityEngine.AudioModule`, `UnityEngine.TextRenderingModule`,
  `Patterns`, `FMODUnity` (all `Private=false`).
* New: `ReelAudio.cs`, `ReelHud.cs`, `ReelHudWorld.cs`.
