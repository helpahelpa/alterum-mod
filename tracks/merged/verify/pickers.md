# Pickers verification

Scope: `tracks/merged/mod/` only, hot-reloaded into install A
(`C:\Program Files (x86)\Steam\steamapps\common\Alterum`), headed 640x360, private Lobby.
`AlterumModelSwap.Contracts` and `tracks/bones|emotes|cosmetics` unchanged. T1/T2/T3 untouched.

## Files

| file | change |
|---|---|
| `AlterumModelSwap.Hot/PickerFont.cs` | new: embedded 5x7 pixel font (bits only, no asset) |
| `AlterumModelSwap.Hot/PickerSurface.cs` | new: 640x360 RGBA surface, fills/text/blit, Texture2D upload, camera composite |
| `AlterumModelSwap.Hot/PickerUI.cs` | new: both pickers, page/category logic, key/mouse/request input, captures |
| `AlterumModelSwap.Hot/EmoteRuntime.cs` | picker catalogue (`PickerEntry`/`PickerEntries`), `SelectedIndex`/`SelectedId`/`PlayingId`, `StartEmote` (calls the existing `StartAt`), picker key block, ready logs |
| `AlterumModelSwap.Hot/CosmeticsRuntime.cs` | `SortedCosmetics` internal, equipped get/toggle/clear, `EquippedSummary`, live 3D preview renderer + cache; old F12 button-list panel removed (it would double-handle F12); no layer/fit/material changes |
| `AlterumModelSwap.Hot/ModelSwapRuntime.cs` | owns/ticks draw-order for `PickerUI`, OnGUI blit, teardown, key legend/log |

Build: `dotnet build -c Release` in `AlterumModelSwap.Hot`. Deploy: atomic copy of
`bin/Release/AlterumModelSwap.Hot.dll` to `BepInEx/hot/` (hot reload only). md5 of the deployed
dll: `efca9151cf0ad895cca927760ab6f6d5`.

## Key collision census

`verify/keyboard-bindings.txt`: `U` is absent from the game's InputActionAsset binding block; F12
was already the mod's cosmetic key. F6/F9 page/close only while the emote picker is open, and
`EmoteRuntime.Keys` is gated by `SetPickerOpen` so F6/F8/F9 are not double-handled on the close
frame (EmoteRuntime updates before PickerUI).

## Lease-path check (emote)

The picker calls `EmoteRuntime.StartEmote(index, trigger)` -> `StartAt` -> `PosePlayer.Start` or
`StartNative`. There is no second entry point; the F6/F9 path and the picker differ only in the
trigger string. Evidence:

```
[picker:emote] pick id='The_Griddy' index=10 trigger=request:pick started=True page=1/5
[emote] requested 'The_Griddy' by picker:request:pick (LC port)
[emote] START clip='The_Griddy' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0
        dur=6.533s loop=True frames=393 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
[emote:move-check] delta=0.0000m over 0.5s (threshold 0.08) - no movement
[picker] closed (emote page=1/5) trigger=request:pick:picked
```

The movement-cancellation code is unchanged (`MovementCheck` still runs; the move-check lines above
are from the picked clip). The picker does not suppress movement and does not stop the clip itself.

A native clip through the picker takes the same path (`StartNative`): selecting `Axe Idle` starts
the arms-only clip with the hands mask (catalogue badge `ARM`, page 3).

## Cosmetic path check

Equip/unequip/clear only mutate the existing `_equipped` set; `Reconcile` still creates/destroys
instances in `_cosmetics.Update()` (the picker ticks before it, so the change lands the same frame):

```
[cosmetic:picker] clear trigger=picker:request:pick removed=1 set=0
[cosmetic:picker] equip id='builtin.tophat' trigger=picker:request:pick set=1 [builtin.tophat]
[cosmetic:load] id=builtin.tophat ... renderers=2 layer=6 (InteractableLookable)
[cosmetic:picker] unequip id='builtin.tophat' trigger=picker:request:pick set=0 []
[cosmetic:view] why=update pov=FirstPerson ... screen=False mirror=True localLayer=6
[cosmetic:layer] mirror mask 0x4CDB4F87 -> 0x4CDB4FC7 added localLayer=6 ... layer23=True
[cosmetic:material] ... template='MegaBook Page Back' ...
```

Layer 6 / layer 23 and the material clone are not touched by the preview: the preview objects are
on layer 6, 4 km below the lobby, destroyed synchronously after the readback, not registered in
`CosmeticMarkers`, and use the same mesh/material caches and clone path as an equipped instance (a preview can
populate a cache entry, but never installs a second pipeline or mutates a fitted instance). Log:
`[cosmetic:preview] id='builtin.tophat' ... nonBackgroundPixels=6638 coverage=25.9% ms=23`.

## Previews: why 3D

Parsed `cosmetics.apack` manifest: every cosmetic has an `icon` id (53..94, MoreCompany store icon
addresses) but the pack's 23 textures are material maps only (albedo/normal/mask); no icon image is
embedded. So the picker renders the highlighted cosmetic live instead of showing a thumbnail.

## Capture matrix

All captures are 640x360 from the game camera (`cameraPixels=640x360`) with the picker surface
composited; the same surface is what OnGUI blits. Saved in `shots/pickers/`, produced by
`picker.request`:

```
[src] open=emote / hover=Salute / capture=emote_lc
[src] tab=items / hover=Axe_Idle / capture=emote_items
[src] page=2 / hover=Common_Idle / capture=emote_body
[src] (third person via pov.request on) open=emote / hover=The_Griddy / capture=emote_lc_thirdperson
[src] open=cosmetic / page=1 / hover=none / pick=none / capture=cos_page1
[src] page=2 / hover=builtin.tophat / pick=builtin.tophat / capture=cos_equipped
[src] (third person) open=cosmetic / page=2 / hover=builtin.tophat / capture=cos_thirdperson
```

`[picker:shot] tag='…' 640x360 camera='Main Camera' cameraPixels=640x360 … composited=True` for each.

Emote playing after the pick: `emote_playing_the_griddy.png` (verify camera, 768x768) and the
equipped hat: `cosmetic_tophat_on_head.png` / `cosmetic_partyhat_on_head.png` (ClipRecorder
showcase, 640x360, third person).

## Non-breakage

* Third-person camera: `[tp-cam:stats]` continued across picker opens/closes; the POV on/off cycle
  was exercised for the third-person captures, `[tp-cam:order] applied pose survived` logged.
* Jiggle: `[jsolve:gate] suspended reason=emote` during the picked clip and resumed after; the
  picker itself writes no skeleton state.
* Mirror: `[mirror:rt-dump]` / `[mirror:body-check]` continued while pickers were open; first-person
  `screen=False mirror=True`, third-person `screen=True mirror=True`.
* Prop/reel: `LobbyProp.Tick` continues; the picker does not read E.
* No `[picker] tick threw` after the cold-start fix; no errors in the final run.

## Known limitations

* Raw IMGUI does not consume game input. A digit/click/wheel used in the picker also reaches the
  game. This matters in a match (ability slots 1..5, interaction clicks); in the private lobby it is
  inert. Fixing it needs a game-side input block, which would fight the movement/camera input the
  brief requires to keep working.
* `Esc` also reaches the game's own cancel/pause handler.
* Windows session input injection is blocked (locked desktop): `SendInput` returns 0,
  `SetForegroundWindow` cannot keep the target. The pickers were driven via `picker.request`, which
  calls the same handlers as the keys (`trigger` field differs).
* Previews are regenerated on first highlight only; a material change by the game later would need
  a reload to refresh the cached 160x160 image.

## Environment

The original install-A process (PID 6428) vanished at 02:22:57 with no BepInEx shutdown line and no
Application-log crash event. Verification was completed on a relaunch of the same install via
`win/run-game.ps1` (PID 80064, same config, private-lobby click sequence re-run). Hot reload only;
no other install was started or stopped.

---

# Round 2 — visible cursor, Stop Emote, Unequip Cosmetics (2026-11-13)

Same scope: `tracks/merged/mod/` only, hot-reloaded into install A (PID 80064, headed 640x360,
private lobby). `AlterumModelSwap.Contracts` and `tracks/bones|emotes|cosmetics` untouched. No
relaunch — every result below is a hot reload (generation 11 → 14). Deployed dll
`f45fc4cff2bc83169e8a58c2a796581c` (generation 14).

## What changed

| file | change |
|---|---|
| `PickerUI.cs` | cursor capture/restore with the requested log format; in-surface cursor marker; one screen→surface mapping used by hit-testing, the marker and the action buttons; the bottom-right action row; `stop=`/`unequip=`/`mouse=` request keys |
| `EmoteRuntime.cs` | `Playing` (button enabled state) and `StopEmote(trigger)` — the active branch of the F6 `Toggle`, i.e. `PosePlayer.Stop` |
| `CosmeticsRuntime.cs` | `ClearEquipped(trigger, unequipAll)` writes `[cosmetic:picker] unequip-all -> set=[NONE]`; same `_equipped` mutation as `NONE`, same `Reconcile` |
| `ModelSwapRuntime.cs` | `[keys] pickers` legend lists the two buttons |

## 1. Cursor visible while a picker is open

Open captures the current pair, sets visible/None, and logs it; close and `Dispose` put the captured
pair back and log what the property reads afterwards:

```
[picker:emote] open trigger=request:open page=1/5 clips=50 selected='Salute' highlight='Salute'
[picker:cursor] visible=true locked=None (was false/Locked) trigger=request:open
[picker:cursor] map screen=640x360 -> surface=640x360 scaleX=1.000 scaleY=1.000 mouse=(319,181) from screen (319,179) src=mouse
[picker:state] open=Emote ... cursor=visible:true/locked:None saved=false/Locked ...
[picker:cursor] restored visible=false locked=Locked readback=false/Locked trigger=request:close
[picker:state] open=None ... cursor=visible:false/locked:Locked saved=none ...
```

Hot reload with the emote picker open and an emote playing (generation 13 → 14; the deployed
dll changed only the hover draw between the two generations). The retiring
generation restores through `Dispose`, the new one starts closed with nothing to restore, and the
cursor is back to the game's pair rather than left visible:

```
[picker:state] open=Emote ... cursor=visible:true/locked:None saved=false/Locked ...
[hot] change detected, reloading
[picker:cursor] restored visible=false locked=Locked readback=false/Locked trigger=dispose
[picker] closed (emote page=1/5) trigger=dispose
[hot] generation 14 active
```

The game did not re-hide or re-lock the cursor during any open (the 0.5 s check logs
`visible=true locked=None (saved false/Locked)` each time), so the picker still does not re-assert
it every frame.

### Marker and mapping

The marker is drawn into the 640x360 surface (not by `OnGUI`), so it is in the live blit and in the
camera-composited capture: an amber crosshair with a dark outline and a 1 px core. Position comes
from `Mouse.position` scaled by `Screen.width/height` and Y-flipped, because the blit is
`StretchToFill`. Proven with the window resized to 800x450 (outside `SetResolution`, via
`win/window-rect.ps1`), then restored to 640x360:

```
[picker:cursor] map screen=800x450 -> surface=640x360 scaleX=0.800 scaleY=0.800
[picker:cursor] mouse screen=(400,225) of 800x450 -> surface=(320,180) [0..639x0..359] src=request
[picker:shot] tag='emote_marker_800x450' 640x360 camera='Main Camera' cameraPixels=800x450 ... composited=True
```

The old code computed the same scale inline for hit-testing only; there was no marker to place
and no action row to hit-test. One function now feeds all three, so the drawn marker, the cell
hover and the button click can not disagree about where the pointer is. `mouse=x,y` (screen
client pixels) is the automation stand-in when the desktop is locked; it goes through that
mapping, not around it.

## 2. Stop Emote

`STOP EMOTE` sits on the bottom-right action row, with the hint to its left:
`STOP '<clip>' | SAME STOP AS F6 (LEASE RELEASED, JIGGLE RESUMES)` while playing,
`NOTHING PLAYING | PICK A CLIP FIRST` and a dimmed frame otherwise. The click path calls
`PressAction -> EmoteRuntime.StopEmote -> PosePlayer.Stop`, which is exactly the branch
`EmoteRuntime.Toggle("F6")` takes when an emote is active — no second stop mechanism.

Pressed 3.04 s into a `The_Griddy` loop:

```
[emote] START clip='The_Griddy' (Lethal Company port) ... lease=DoingSomething+DisableAnimancerAndAnimator
[picker:emote] action='stop emote' trigger=request:stop was='The_Griddy' accepted=True playing='-' lease=free
[emote] STOP reason=picker:request:stop clip='The_Griddy' t=3.04s released=True fadeOut=0.20s
[emote] STOP fadeout complete clip='The_Griddy' handed back to the game's abstraction
[jsolve:gate] resumed reason=animation: rigs=0, particles re-seeded from the animated pose
```

`released=True` is `PosePlayer.Stop`'s DoingSomethingHandler/Animancer release; `resumed` is
`ModelSwapRuntime.LateUpdate` driving `JiggleSolver.SetSuppressed(false)` once `SkeletonOwned`
drops. `rigs=0` in this lobby (no third-view rig was attached in this session) — the line is the
same one the F6 path produces, not a new one.

## 3. Unequip Cosmetics

`UNEQUIP COSMETICS` is the same action row in the cosmetic picker, dimmed when the set is empty.
`PressAction -> CosmeticsRuntime.ClearEquipped(..., unequipAll: true)` empties the same `_equipped`
set `NONE` empties and leaves `Reconcile` as the only destroyer of instances:

```
[cosmetic:load] id=builtin.tophat ... renderers=2 layer=6 (InteractableLookable)
[picker:cosmetic] action='unequip cosmetics' trigger=request:unequip removed=1 equipped=0
[cosmetic:picker] unequip-all -> set=[NONE] removed=1 trigger=picker:request:unequip (reconcile runs in the same frame)
[cosmetic:unequip] id=builtin.tophat instances=0 markers=0
[cosmetic:state] wrote 0 id(s) to '...\BepInEx\config\cosmetics.txt':
```

Layer 6 / layer 23, the mirror and the material clone are unchanged by the clear (they are runtime
rules, not per-instance ones, so the masks stay applied with zero instances):

```
[cosmetic:mirror-mask] camera='AlterumMirror/AlterumMirrorCam' mask=0x4CDB4F87 layer23=True name='TV_PlayerModelMesh'
[cosmetic:layer] mirror mask 0x4CDB4F87 -> 0x4CDB4FC7 added localLayer=6 ('InteractableLookable') layer23=True
[cosmetic:view] why=update pov=FirstPerson ... fit=True screen=False mirror=True ... localLayer=6 instances=0 renderers=0
[cosmetic:material] ... shader='Universal Render Pipeline/Lit' template='MegaBook Page Back' ...
[mirror:body-check] owner='LobbyPlayer_Separated | Owner: 0' bound=True renderers=14 controllers=2 keys=2 isolated=14 driven=14 ...
```

`NONE` is untouched: its cell, its `UNEQUIP ALL`/`CLEARS THE SET` copy and its
`[cosmetic:picker] clear trigger=... set=0` log are unchanged.

## Captures

`shots/pickers-v2/` (640x360, game camera + composited surface, `composited=True`):

| file | state |
|---|---|
| `emote_idle.png` | emote picker, nothing playing, marker at centre, Stop Emote dimmed |
| `emote_playing.png` | `The_Griddy` playing, cell marked `PLAYING`, Stop Emote enabled |
| `emote_button_hover.png` | pointer on the button (marker over it): hover frame, enabled |
| `emote_stopped.png` | after the press: footer `STOPPED`, `NO EMOTE`, button dimmed again |
| `cos_equipped.png` | `builtin.tophat` equipped, `EQUIPPED 1`, button enabled |
| `cos_unequipped.png` | after the press: `EQUIPPED 0`, `SET: EMPTY`, status `UNEQUIPPED ALL`, button dimmed |
| `emote_marker_800x450.png` | window resized to 800x450, marker still at the mapped centre |

## Counts (generation 14 window, 1128 lines, generation start at log line 16666)

`[Error]` = 0, `grep -c SWAP` = 0 (whole file), `[picker] tick threw` = 0,
`[mirror:body-check] ... isolated=14 driven=14` (×2).

The brief asked for `isolated=9 driven=9`; this install reports `14` for both, in every dump of the
whole log (28 of 28, `grep -c 'isolated=9'` = 0). The local body model here
(`3P_ConcreteModel_Male_05_Ryan`) has 14 renderers. The invariant the line exists to prove
(`isolated == driven`) holds; the absolute number is install/scene-dependent.

## What is not proven here

* The desktop session is locked, so the **hardware cursor cannot be photographed**: a
  RenderTexture/Present capture never contains it and there is no desktop capture. The cursor
  claim rests on (a) the `Cursor.visible`/`Cursor.lockState` transitions logged at open, close and
  dispose, and (b) the in-surface marker, which is the pointer substitute in every image above.
  The `restored ... readback=` values are Unity's own cached state read back after the write; they
  are not an OS-level "is the arrow drawn" query (session 0 cannot query session 1's desktop).
* No real click was used for either button: the desktop is locked, so `SendInput` is inert and the
  request keys call the same `PressAction` the click does. The `trigger` field distinguishes them.
* The emote picker closed twice between requests on a stray `trigger=key:U` close (a key event
  arrived while no request was pending); the runs above were therefore issued as one atomic request
  per action set. Not a code path of this change.
