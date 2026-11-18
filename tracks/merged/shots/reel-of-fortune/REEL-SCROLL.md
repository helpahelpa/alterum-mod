# Reel of Fortune — one horizontal item row (case-opening read)

Source: `tracks/merged/mod/AlterumModelSwap.Hot/` (`ReelArt.cs`, `LobbyProp.cs`).
Main install `C:\Program Files (x86)\Steam\steamapps\common\Alterum`, 640×360, private lobby,
headed, hot reload only (`[hot] generation 36 → 39`; nothing was restarted). The prop keeps the
identical pose: `(0.35, 0.088, -8.57)`, yaw 66°, `baseY=0.088`, 0 colliders, `Cosmetic_PropLit`,
and the cosmetics template scan still clones `MegaBook Page Back`.

Previous mechanic: three narrow vertical drums (0.225 × 0.245 m each) with a 6-cell strip scrolled
in **v** (`_BaseMap_ST` y), staggered starts, ease-out, overshoot click. That reads as three
slot-machine reels, not a case opening.

## The row

| | old | new |
|---|---|---|
| windows | 3 × 0.225 × 0.245 m | **1 × 0.720 × 0.300 m** |
| cells | 6 per drum, square-ish | 16 cards, 0.240 × 0.300 m (0.8 aspect) |
| visible at once | 1 per drum | 3 cards (partials at both ends under the fade) |
| axis | vertical (v offset) | **horizontal (u offset)** |
| motion | 3 staggered spins | 1 row, 3–3.9 strip widths, decelerating to one card |
| marker | none | **fixed cream rule + two ticks + additive lamp, dead centre** |
| ends | hard clip | **alpha-ramp vignette, 26 % at each end** |

Cards pierce the eye-level band as tiles, not as a slotted reel: each carries a rarity band
(blue/violet/gold/teal, CSGO's strongest single colour cue), a recessed icon well with the Alterum
item icon, a black code plate (`MED LMP KEY BOK ROP CRX`) and a gold card border. Six symbols are
laid out over 16 cards with no equal neighbours and no repeated symbol inside any three-card window
(cyclic, so the texture seam is covered too), plus a per-card tone (`CellSymbol`, `CellTone`); the
strip is a 4096×320 texture, one card per 256×320 px, i.e. the same 1067 px/m as the sign atlas.
Order left→right: `LMP KEY ROP BOK CRX KEY MED LMP BOK MED LMP KEY MED ROP CRX BOK`.

Why it reads as a case opening rather than a ticker:

* the **marker is fixed** and the row moves behind it — one card lands centred under it, by
  construction (`[reel] settled … centre=1.000`);
* the row **fades into the recess at both ends**, so cards enter and leave the window instead of
  being clipped by the frame;
* the cards and the rarity bands are **discrete tiles**, so speed is read as tiles/s, not as
  sliding paint;
* a **single row** (not three) is the CSGO case silhouette: one lane, one winner.

## Scroll

* One material (`AlterumPropItemStrip`, URP/Unlit, wrap Repeat), one full-window quad. The scroll is
  the material's `_BaseMap_ST`: `scale = (3/16, 1)`, `offset = (u, 0)`, `u` unwrapped and sent
  mod 1. The window samples `u … u + 0.1875` (three cards), the marker samples `u + 0.09375`.
* Direction: a rising `u` moves the cards to the viewer's left (uvMode 2 puts texture u 0 at the
  local +x edge, which is the viewer's left, so the art is not mirrored). Verified numerically: a
  still frozen at `u=0.29773` measured `u_left=0.2982` from the pixels.
* End fade: `AlterumPropFade`, alpha-blended URP/Unlit (queue 3000, `_ZWrite` off), drawing
  `AlterumPropFadeRamp` (256×8, black, alpha 1 at both edges → 0 inside a 26 % plateau). Opaque
  cards behind it, marker in front, so the marker never dims. A first pass used 48 tint-sliced
  quads; the banding was visible, hence the ramp overlay.
* Timing (`LobbyPropFactory`): `ScrollSpinSeconds = 3.00`, ease `d(u) = 1-(1-u)^1.74`, click
  0.18 s, overshoot 0.06 card (≈ 0.014 m ≈ 9 px in the close-up), travel = the first whole-strip
  multiple that reaches the random target card, so 3.00–3.88 strip widths (48–62 cards). p = 1.74
  is chosen so 70 % of the distance is covered in the first 50 % of the time.
* Marker emphasis: the lamp's additive tint pulses with card-centre proximity
  (`ScrollCentreProximity`, 0 when the gap is under the marker, 1 when a card is dead centre). At
  full speed that is a ~24 Hz pulse a 15 fps frame cannot resolve; during the deceleration each card
  visibly lights as it lands. No per-card geometry scale.
* Grant: nothing. `[reel] settled … grants=0`; no inventory/network call exists in the file.

## Triggers

`E` (unchanged path, `StartSpin("E", …)`) and `<ScreensDir>/reel.request` with `spin` plus optional
`capture=closeup|showcase|spawn|main` and `tag=…`. Two extra request verbs exist for verification:
`film` (seven frozen stills on the spin curve, each a one-frame pinned clip) and `seek=<u>`
(freeze at an exact offset, log the window mapping, one still). Both are diagnostics; neither is
reachable from E.

## Trace (one live spin, 48 samples at the game's 15 fps)

`t=0.067 s u=0.33624 travel=2.380 cards cell=6 sym=4 ROP v=35.698 cards/s phase=1` … full table in
`tracks/merged/clips/reel_scroll_trace.txt`, chart in `reel_scroll_trace.png`.

* monotone: no negative step in phase 1 (43 steps);
* 52.000 cards = 3.25 strip widths in 3.00 s, peak 52.060 at t=3.000 (overshoot 0.06 card), then the
  click back to 52.000;
* last 30 % of the distance starts at `t=1.533 s` → the final **52 %** of the 3.20 s spin;
  70/90/95/99 % at 1.533 / 2.200 / 2.467 / 2.800 s; 73.5 % of the distance at half time;
* speed 29.9 → 9.6 (t=2.40 s) → 0 cards/s;
* settled `cell 8 sym 3 BOK`, `centre=1.000`, `match=True`, `grants=0`.

The filmstrip uses the same curve on a fixed target (cell 7): 51.000 cards in 3.00 s, stills at
t = 0.06 / 0.24 / 0.50 / 0.85 / 1.35 / 2.10 / 3.17 s, marker card `MED KEY ROP LMP KEY KEY LMP`,
settled `cell 7 sym 1 LMP`, `centre=1.000`.

## Captures

| file | what |
|---|---|
| `filmstrips/reel_scroll.png` | 7-row filmstrip of the row, one pinned close-up (1.30 m, fov 40, 960×540), each row labelled with `t`, `u`, card/symbol/code, travel and the card-centre factor (466 KB) |
| `filmstrips/reel_scroll_trace.png` | the `[reel:scroll]` trace as offset + speed against time |
| `clips/reel_scroll_showcase.mp4` | whole machine, pinned 4.20 m / fov 42 / 960×540, 39 frames @ 8.03 fps, full spin |
| `clips/reel_scroll_gamecam.mp4` | `view=main`, the live game camera, 47 frames @ 12.07 fps |
| `clips/reel_scroll_spawn.mp4` | 640×360 / fov 70 at the game camera's recorded spawn pose (4.6 m in front, 9.7° off axis), 47 frames @ 12.01 fps |
| `clips/reel_scroll_closeup.mp4` | window close-up (1.30 m, fov 40), 36 frames @ 10.16 fps |
| `clips/reel_scroll_trace.txt` | the 48-sample `[reel:scroll]` table, the derived numbers and the raw log lines |
| `shots/reel-of-fortune/reel_scroll_spawn.png` | full 640×360 spawn-distance frame, settled |
| `shots/reel-of-fortune/reel_scroll_spawn_mid.png` | same camera mid-spin |
| `shots/reel-of-fortune/reel_scroll_showcase.png` | whole machine, settled |
| `shots/reel-of-fortune/reel_scroll_closeup_settled.png` | window close-up, settled |
| `shots/reel-of-fortune/reel_scroll_live_gamecam.png` | what the live game camera actually shows right now |

### Caveat: the live game camera is not on the machine

From this session's `[prop:player]` line the local player stands at `(0.81, 1.02, -11.09)` with the
camera at `(0.75, 1.80, -11.30)` facing `(-0.84, -0.14, -0.52)` — **behind** the cabinet, so
`view=main` shows a doorway and `[prop:view] verdict=BEHIND_CAMERA`. The spawn-pose clip is a pinned
reproduction of the camera pose the same log recorded when the prop was placed
(`eye=(5.35, 1.80, -7.57)`, `fwd=(-1.00, 0.00, -0.04)`, `fov=70`, `px=640×360`), so the frame is the
lens and the distance the sign is actually seen from, just not the live player's current aim. No
input was injected and nothing was restarted.

## What still does not look like a CSGO case

* **15 fps.** The travel advances ~2–3.5 cards per rendered frame; CSGO shows a motion blur, this
  shows a strobe. The filmstrip is the only place the scroll can be read as motion.
* **No motion blur, no depth.** A real case has a blurred strip and parallax between the cards and
  the window; here it is one flat quad with a texel offset, so nothing skews or smears.
* **No wear/float/rarity text.** Only a coloured band, an icon and a three-letter code; the codes
  are the identifier because 0.24 m at 4.6 m is ~10 screen px and no word fits a 1-px stroke.
* **The window is small.** At spawn the row is ~30 × 13 px of a 640×360 frame, so the cards read as
  a lit strip with a centre mark, not as items; the machine itself is 2.95 m tall on a 4.6 m
  viewing distance, which is what caps this, not the art.
* **No card scale on the winner.** The emphasis is a lamp pulse, not a zoom; a true per-cell scale
  would need a second draw of the winning card and would stutter at 15 fps.
* **Nothing is granted**, by design: the case never opens anything, so the "reveal" ends at the
  marker instead of an inventory popup.
