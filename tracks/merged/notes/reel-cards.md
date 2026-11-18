# Reel of Fortune — the strip is the outcome table

Pass of 2026-11-13. Supersedes the "settle on a symbol, then roll an outcome" build, whose log
showed the machine settling on `code=MED name=health-injector` and then taking 29.9 HP.

## What changed

- **One source of truth.** `ReelDeck` (new, pure C#, no UnityEngine) composes a 64-card deck and
  shuffles its order per spin. The settle reads the card in the cell under the marker
  (`ReelArt.CardAt` → `ReelLogic.Resolve(cell, card)`) and applies *that* card. There is no second
  roll anywhere; `grep` the source for `RollOutcome`/`ReelOutcome`/`SymbolFor` — gone.
- **Four card types**, each with its own face colour, band, motif and 3-letter code:
  item (maroon + item icon + category band, e.g. `HFL`), damage (red, cracked heart, `DMG`),
  debuff (purple, spiral, `DBF`), double (gold, big ×2, `X2`).
- **Composition weights (our design, not game data)** — `ReelDeck.WeightItem/Damage/Debuff/Double`
  = 60/18/12/10, split by largest remainder over 64 cells → 38/12/8/6.
  Item cards are drawn over the 53 usable pool items with the per-category weights compiled into
  `ReelPool.g.cs` (consumables 100, LQWeapon 60, scrolls 35, HQWeapon 25, occult 20, exits 10,
  keys 8, debug 0/excluded).
- **Generated pool.** `tools/gen-reel-pool.py` reads `data/item-pool.json` and writes
  `mod/AlterumModelSwap.Hot/ReelPool.g.cs` (55 entries: id, name, 3-letter code, category, weight,
  sprite name, icon rect) plus `reel_pool_icons.png` (embedded, 768×672, 51 icons cropped from the
  game's own atlases with `--icons`). Regeneration is one command; the sheet is a build product.
- **Icons.** Runtime Sprite first (RenderTexture → ReadPixels → PngDecoder), embedded sheet as the
  fallback for the 37 sprites a map/Lobby does not have loaded. In the Lair map run: 50 runtime,
  0 sheet, 3 code-only.
- **Items with no icon sprite (4):** `ceremonial_candle`, `healthflask`, `ss_highjump`,
  `test_lagcomprensationgun` (the last is debug/excluded). Their cards draw the 3-letter code in the
  icon well instead of an icon.
- **Reshuffle cost.** Cards are cached per (type, item) at load; a spin re-blits 64 cards into the
  strip buffer and re-uploads it: measured `ms=render1/copy0/load2/apply4/total7`.
- **`reel.request`** still forces a path for verification, but it no longer bypasses anything: it
  picks a random cell that already carries the requested card type after the shuffle
  (`[reel] outcome=double injection: deck card=6 type=double code=X2`) and lets the normal settle
  resolve it. `outcome=hp` is accepted as the old spelling of `outcome=damage`.

## Captures (`shots/reel-cards/`)

| file | frame | marker card | resolution log |
|---|---|---|---|
| `settle-item-3.png` | `screens/settle-item-3/frame_0036.png` | 10 `WAL` (walkie-talkie, blue consumable band) | `settle card=10 type=item id='walkietalkie' -> granted='walkietalkie' n=1 consumed=1 inventory=0->1` |
| `settle-double.png` | `screens/settle-double-3/frame_0036.png` | 6 `X2` (gold) | `settle card=6 type=double id='rope' -> granted='rope' n=2 consumed=1 inventory=0->1 delta=1 ok=True dropped-or-clipped=1` |
| `settle-damage.png` | `screens/settle-damage/frame_0036.png` | 17 `DMG` (red, cracked heart) | `settle card=17 type=damage -> hp=50.0->15.1/100.0 damage=34.9 pct=0.35 dead=False clamped=False` |
| `settle-debuff.png` | `screens/settle-debuff/frame_0036.png` | 52 `DBF` (purple, spiral) | `settle card=52 type=debuff -> effect='Slow_Axe_ImposedEffectVariantData:x0.50' via=PlayersImposedEffects` |
| `settle-debuff-2.png` | `screens/settle-final/frame_0036.png` | 12 `DBF` | repeat of the debuff path in the final build |
| `strip-four-types.png` | loaded strip, cells 31–36 | DMG, DBF, DMG, DMG, HMU, X2 | all four types on one strip |
| `strip-final.png` | `BepInEx/screens/reel_strip.png` | full 8192×160 row | dump of the exact texture uploaded |

Lethal damage on the final build (no capture, log only):
`settled card=34 type=damage code=DMG …` → `settle card=34 type=damage -> hp=15.1->0.0/100.0 damage=37.7 pct=0.38 dead=True clamped=True`.

## Distribution (1000 decks, `tools/reel-dist`, link-compiles the production `ReelDeck.cs`)

```
type      cards    share    design   delta     landed   landed%
item       38000   59.38%   60.00%   -0.63      602   60.20%
damage     12000   18.75%   18.00%   +0.75      175   17.50%
debuff      8000   12.50%   12.00%   +0.50      122   12.20%
double      6000    9.38%   10.00%   -0.63      101   10.10%
```

`landed` is one random landing per deck (the natural spin chooses a uniform cell). Per-item counts
track the category table (e.g. `lantern` w=100 → 2.78 % vs 2.65 % expected; `dagger_*` w=60 →
~1.6 %; keys w=8 → ~0.2 %).

## Save backup

`work/reel/backup/save-backup-reel-cards-20261113-0510.reg` — `reg export` of
`HKCU\Software\Hot Winter\Alterum` (the game's PlayerPrefs save), taken before any grant test.
Restore with `reg import`.

## Open items / unverified

- **Double, clean `+2` in one inventory.** The grant call receives `count=2` and logs `n=2`, but this
  map run's writable inventory has a single free slot, so one instance goes to the game's own
  world-drop (`dropped-or-clipped=1`, `inventory=0->1`). A run with two free slots would show
  `0->2`; not reproducible on this character without an empty loadout.
- **Grants are map-only** by code (`ReelLogic.TryBeginPull` → `IsMapRun`), and every settle log line
  carries `consumed=1`; the Lobby refusal itself was not captured in this log window.
- Debuff cards are **real effects**, not folded into damage: `SlowImposedEffectVariantData` applies
  through the player's own `PlayersImposedEffects` component. The fold-to-damage branch exists only
  as an emergency fallback if no variant is loaded, and it logs loudly.
- Card faces at 128×160 are sized for the close-up camera (47 screen px per card). At spawn distance
  a card is ~13 screen px: type colours and the band read, the 3-letter codes do not.
- A second agent was hot-reloading the same merged assembly during this pass; the prop has to be
  re-placed (`reel.request place=here`) after every reload, and two captures were cut short by a
  reload and re-taken.
