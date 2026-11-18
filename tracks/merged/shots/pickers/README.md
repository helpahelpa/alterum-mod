# Pickers — emote (U) and cosmetic (F12)

Replaces the F9 50-entry cycle and the F12 one-at-a-time cosmetic list with two grid overlays.
Merged build only (`tracks/merged/mod/`); `AlterumModelSwap.Contracts` and the three source tracks
are unchanged.

## UI approach

**IMGUI, via one 640x360 RGBA surface.** Both pickers draw into a private `PickerSurface`
(640x360 straight-alpha buffer, 5x7 embedded pixel font), which is:

* uploaded to a Texture2D and blitted by `OnGUI` (`GUI.DrawTexture`, 1:1 at 640x360), and
* alpha-composited over a game-camera RenderTexture when a capture is requested.

The live overlay and the screenshots are therefore the same bitmap. No uGUI Canvas and no game font
was borrowed: the game's menus are TMP scene canvases owned by their scenes, and taking one over
from a hot-reload generation would tie the picker to assets that can be unloaded or replaced. A
pixel surface has no asset dependency and is deterministic at 640x360.

Capture path: `ScreenShot.FindGameCamera` → its camera rendered into a 640x360 RenderTexture →
`PickerSurface.CompositeOver` → PNG. The desktop session is locked (a desktop grab is black), so
this is the same camera-read mechanism the rest of the project uses. The images below are that
composition; `composited=True` is logged for each.

## Key map (final)

| key | pickers closed | emote picker open | cosmetic picker open |
|---|---|---|---|
| `U` | open emote picker | close | switches to the emote picker |
| `F12` | open cosmetic picker | switches to cosmetics | close |
| `F6` | emote toggle (unchanged) | close (does not toggle) | unchanged |
| `F9` | cycle selected emote (unchanged) | next page | unchanged |
| `Shift+F9` | — | previous page | — |
| `PgUp` / `PgDn` | — | previous / next page | previous / next page |
| wheel | — | page (cursor over the panel) | page (cursor over the panel) |
| `1`..`9`, `0` | — | pick cell 1..10 on the page | pick cell 1..10 on the page |
| arrows | — | move highlight | move highlight |
| `Enter` / `Space` | — | pick highlight | pick highlight |
| click | — | pick the clicked cell | equip/unequip the clicked cell |
| `Esc` | — | close | close |

Unchanged and untouched: `F5` clothes, `F7` POV third person, `F8` native emote, `F10` screenshot,
`F11` clip recorder, `E` (reel prop / game interact). `U` is not in the game's InputActionAsset
binding block (`verify/keyboard-bindings.txt`); the other free letters were left alone.

Each picker also has a bottom-right action row, dimmed when there is nothing to act on:
`STOP EMOTE` (emote picker) calls the same `PosePlayer.Stop` the F6 toggle's active branch calls,
and `UNEQUIP COSMETICS` (cosmetic picker) clears the same equipped set `NONE` clears. Cursor
handling and marker: see the round-2 section of `../../verify/pickers.md`.

Startup logs the map (`[keys] merged`, `[keys] pickers`, `[keys] overlay legend`) and the mirror
overlay legend string was updated with it.

## Emote picker — 50 entries in 5 pages / 3 categories

| page | category | entries |
|---|---|---|
| 1 | LC EMOTES | 12 ported Lethal Company clips |
| 2 | BODY IDLES | 2 native full-body idle loops |
| 3 | ITEM ANIMATIONS | 36 native upper-body clips, part 1 |
| 4 | ITEM ANIMATIONS | part 2 |
| 5 | ITEM ANIMATIONS | part 3 |

The three tabs jump to a category's first page; `F9`/wheel/`PgUp`/`PgDn` walk the flat page list.
Each cell shows the emote's catalogue ordinal, its kind (`LC`, `BODY`, `ARM`), its display name
(underscores become spaces), duration and loop/once. The green bracket/`SELECTED` marks the clip
currently selected by `F9`; `PLAYING` and a green underline mark a clip that is actually running.
Picking calls `EmoteRuntime.StartAt` through `StartEmote` — the same `PosePlayer` lease, fade,
movement cancellation and jiggle-suspension path as `F6`/`F9`; there is no second playback path.
The picker closes on a successful pick. It opens while an emote is playing (the playing clip is
marked) and does not stop or restart it.

The catalogue order on page 1 is the pack's order (`Clap, D_Clap, D_Middle_Finger, D_Sign,
Idle1Fix, Middle_Finger, Prisyadka, Salute, Shy, Sign, The_Griddy, Twerk`); all 12 required clips are
present, just not in the order the brief listed them.

## Cosmetic picker — 21 cosmetics + NONE

Grid of 12 cells per page (2 pages): `NONE` (clear the set) followed by the 21 ids in id order.
Each cell shows the short name, slot (`HAT`/`CHEST`/`HIP`/`WRIST`), an `X`/green state when
equipped, and `CLICK TO EQUIP`/`EQUIPPED`. The header reports `EQUIPPED n`; the footer shows the
whole set. Equip/unequip goes through the existing equipped set (`CosmeticsRuntime.ToggleEquipped` /
`ClearEquipped`), which is the same set `cosmetic.request` writes and the same path `Reconcile`
turns into instances. Multi-equip is preserved (the existing data model is a set), and `NONE`
clears it. The first-person layer-6 rule, layer 23 for other players, and the
`MegaBook Page Back` material clone are untouched:
`[cosmetic:view] ... screen=False mirror=True` in first person,
`[cosmetic:mirror-mask] ... layer23=True`, and
`[cosmetic:material] ... template='MegaBook Page Back'`.

### Preview icons: none in the pack → live 3D preview

`CosmeticDef.Icon` is a MoreCompany store icon address (53..94), but the pack carries only material
textures; parsing the embedded `cosmetics.apack` manifest confirms there is no icon image for any
cosmetic (the 23 textures are albedo/normal/mask maps). The picker therefore renders the
highlighted cosmetic from its real meshes and cached materials with a private camera at 160x160:
its own layer (6), 4 km below the lobby, destroyed after the pixel readback, never registered in
`CosmeticMarkers`, and it uses the same mesh/material caches and clone path as an equipped
instance (it can populate a cache entry, but it never installs a second pipeline or mutates a
fitted instance). Log example:
`[cosmetic:preview] id='builtin.tophat' ... renderers=2 ... nonBackgroundPixels=6638 coverage=25.9% ms=23`.
The panel shows `LIVE 3D PREVIEW`, the id and `HAT | EQUIPPED`/`NOT EQUIPPED`.

## Captures (picker overlays at 640x360; proof frames noted)

| file | state |
|---|---|
| `emote_lc.png` | emote picker, LC page, `Salute` selected + highlighted |
| `emote_items_page3.png` | page change: item animations, page 3/5, `Axe Idle` highlighted |
| `emote_body.png` | page change: body idles, page 2/5 |
| `emote_lc_thirdperson.png` | emote picker in third person (page 1) |
| `cosmetic_page1_none.png` | cosmetic page 1/2, `NONE` highlighted, set empty |
| `cosmetic_page2_equipped.png` | cosmetic page 2/2, `TOPHAT` equipped + live preview |
| `cosmetic_thirdperson.png` | cosmetic picker in third person, tophat equipped |
| `emote_playing_the_griddy.png` | after picking `The_Griddy` in the picker (verify camera, 768x768) |
| `cosmetic_tophat_on_head.png` | tophat equipped and worn (third person) |
| `cosmetic_partyhat_on_head.png` | partyhat equipped and worn (third person) |

![Emote picker, LC page](emote_lc.png)
![Emote picker, item animations page 3](emote_items_page3.png)
![Emote picker, body idles](emote_body.png)
![Emote picker in third person](emote_lc_thirdperson.png)
![Cosmetic picker, none](cosmetic_page1_none.png)
![Cosmetic picker, tophat equipped](cosmetic_page2_equipped.png)
![Cosmetic picker in third person](cosmetic_thirdperson.png)
![The Griddy playing after a picker pick](emote_playing_the_griddy.png)
![Tophat worn](cosmetic_tophat_on_head.png)
![Partyhat worn](cosmetic_partyhat_on_head.png)

Captures are written by `picker.request`’s `capture=<tag>` and land in
`…/BepInEx/screens/picker_<tag>.png`; the copies here are the 640x360 originals.

## Verification excerpts

```
[keys] merged: F5 clothes toggle | F6 emote toggle | U emote picker | F7 POV third-person |
       F8 emote native toggle (selected game clip) | F9 cycle the 50-entry emote list |
       F10 screenshot | F11 clip recorder | F12 cosmetic picker
[keys] pickers: U emote picker (F9/Shift+F9/PgUp/PgDn/wheel page, 1-9 0 pick, arrows+Enter,
       Esc/U/F6 close) | F12 cosmetic picker (click toggles, 1-9 0 pick, PgUp/PgDn/wheel page, Esc/F12 close)
[picker] ready: ... overlay 640x360 IMGUI blit, camera-composited captures
[picker:emote] open trigger=request:open page=1/5 clips=50 selected='Salute' highlight='Salute'
[picker:emote] page 2/5 category=BODY sub=1/1 trigger=request:page
[picker:emote] page 3/5 category=ITEMS sub=1/3 trigger=request:page
[picker:emote] pick id='The_Griddy' index=10 trigger=request:pick started=True page=1/5
[emote] requested 'The_Griddy' by picker:request:pick (LC port)
[emote] START clip='The_Griddy' ... dur=6.533s loop=True ... lease=DoingSomething+DisableAnimancerAndAnimator
[picker] closed (emote page=1/5) trigger=request:pick:picked
[picker:cosmetic] open trigger=request:open page=2/2 cosmetics=21 equipped=1 highlight='builtin.tophat'
[cosmetic:picker] clear trigger=picker:request:pick removed=1 set=0
[picker:cosmetic] pick NONE trigger=request:pick was=1 equipped=0
[cosmetic:picker] equip id='builtin.tophat' trigger=picker:request:pick set=1 [builtin.tophat]
[picker:cosmetic] pick id='builtin.tophat' trigger=request:pick equipped=True count=1
[cosmetic:picker] unequip id='builtin.tophat' trigger=picker:request:pick set=0 []
[picker:cosmetic] pick id='builtin.tophat' trigger=request:pick equipped=False count=0
[cosmetic:load] id=builtin.tophat ... renderers=2 layer=6 (InteractableLookable)
[cosmetic:preview] id='builtin.tophat' size=160x160 renderers=2 ... coverage=25.9% ms=23
[picker:shot] tag='emote_lc' 640x360 camera='Main Camera' cameraPixels=640x360 open=Emote
             page=1/5 category=LC highlight='Salute' composited=True
[picker:shot] tag='cos_equipped' 640x360 ... open=Cosmetic page=2/2 highlight='builtin.tophat'
             equipped=[builtin.tophat] composited=True
[cosmetic:view] why=update pov=FirstPerson ... screen=False mirror=True localLayer=6
[cosmetic:layer] mirror mask 0x4CDB4F87 -> 0x4CDB4FC7 added localLayer=6 ... layer23=True
[cosmetic:material] ... template='MegaBook Page Back' ...
[emote:move-check] delta=0.0000m over 0.5s (threshold 0.08) - no movement   (throughout playback)
```

No `[picker] tick threw` after the first cold-start generation; the initial NRE (the picker ticked
before `CosmeticsRuntime` loaded its pack) was fixed by making `SortedCosmetics()` return an empty
list and marking the surface dirty when either catalogue appears.

## 640x360 notes

* 5x7 font at scale 1 for cells/hints, scale 2 for the title. Lowercase is folded to uppercase.
* Long native names wrap at spaces to 2 lines and ellipsize; the full id is in the highlight line.
  Examples: `ADRENALINEMUSHROOM IDLE` (2 lines), `DOINGSOMETHING CEREMONIALCANDLE` (2 lines).
* 12 cells per page is the largest that keeps the 147x64 emote cells readable; 36 item animations
  therefore need 3 pages. A single 50-cell grid would be ~5x smaller per cell and unreadable.
* The cosmetic grid is 4x3 at 90x64 with a 216x200 preview panel to its right; full ids are in the
  footer rather than the cells.
* The picker has no scrolling and no pagination beyond the page keys; everything fits at 640x360
  with no element clipped by the panel (checked on every capture above).
* What does not fit: 36 item names on one page, and full `FemaleAdventure_Hands_TP_*` ids in cells
  (ellipsized / footer only).

## Input limitations

* While a picker is **closed** it reads only `U` and `F12`, draws nothing, hit-tests nothing and
  never writes `Cursor` state; no `Event.current` is used or consumed.
* While open it still cannot consume the game's input (there is no EventSystem for a raw IMGUI
  blit). A click or a digit therefore also reaches the game; the same is true of the wheel. In the
  private lobby this is harmless (no weapon/ability in hand). In a match, the picker would need a
  game-side input block, which was deliberately not added because it would fight the movement and
  camera input the brief says must keep working.
* `Esc` is handled as a close key, but the game also sees it; if the game's own menu opens on the
  same press, that is the game reacting, not the picker.
* Session input injection is unavailable (the Windows session is locked: `SendInput` returns 0 and
  `SetForegroundWindow` cannot hold a foreground window). The pickers were therefore opened and
  driven through `picker.request`, which calls the same `Open`/`StepPage`/`PickCell` handlers the
  keys call; the log trigger field distinguishes `request:*` from `key:*`.

## Automation path

`<ScreensDir>/picker.request`, line-oriented, applied in order, then deleted:

```
open=emote|cosmetic|none
page=next|prev|<n>          # 1-based
tab=lc|body|items           # emote picker only
hover=<id|emote-index>      # cosmetic: <id|none>
pick=<id|emote-index|none|highlight>
capture=<tag>
state=1
close=1
stop=1                      # emote picker only: same PressAction as the Stop Emote button
unequip=1                   # cosmetic picker only: same PressAction as the Unequip Cosmetics button
mouse=<screenX>,<screenY>   # pointer override in screen client px (locked desktop); mouse=clear releases it
```

`pick` uses the picker path (same as a click/number key). `stop` and `unequip` use the same
`PressAction` the two action-row buttons call (the click only differs in the trigger string).
`mouse` feeds the same screen→surface mapping the real `Mouse.position` does and is logged with
both the screen and the surface coordinates. `capture` writes `screens/picker_<tag>.png`. Example
used for the captures above:

```
open=emote
hover=Salute
capture=emote_lc
tab=items
hover=Axe_Idle
capture=emote_items
```

The file is read and deleted in `PickerUI.PollRequestFile`; a read collision (a writer still holding
the file) is retried instead of deleting the request.

## Environment note

The instance used for verification (install A, original PID 6428) disappeared at 02:22:57 with no
BepInEx shutdown line and no Windows Application-log event. It was relaunched with the existing
`win/run-game.ps1` launcher (same install, same config; the config's `ClickSequence` recreates the
private lobby) as PID 80064. The hot dll was reloaded in place; no other install (B/T1/T2/T3) was
started or stopped.
