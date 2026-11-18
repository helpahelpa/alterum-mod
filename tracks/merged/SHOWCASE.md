# Showcase capture — The_Griddy / Twerk on the merged build

Install: `C:\Program Files (x86)\Steam\steamapps\common\Alterum` (main, 640x360, 15 fps cap, private
lobby). Locked desktop: no keys, no window capture — every frame comes from `ClipRecorder`'s own
RenderTexture; the pov, the emote and the clip are driven by request files only.

Delivered: `clips/griddy.mp4`, `clips/twerk.mp4` (copies in `%USERPROFILE%\Videos\AlterumMods\`),
plus `griddy_showcase.png` / `twerk_showcase.png` in `~/assets/monorepo/alterum-data/filmstrips/`
(1504x203, 198 968 B / 177 877 B).

This is the **re-shoot**: the first pass used `yaw=auto` at `dist=3.3 fov=40 lift=0.14`. Same
motion, new framing (explicit `yaw=190`, `dist=3.8`, `lift=0.13`).

## Request files

`<ScreensDir>` = `C:\Program Files (x86)\Steam\steamapps\common\Alterum\BepInEx\screens`.

`pov.request` (sent before each capture; the mode was already third person, so the log shows
`[pov] request third-person (from pov.request)` and no transition):

```
thirdperson
```

`emote.request` — griddy:

```
clip=The_Griddy
repeats=2
```

`emote.request` — twerk:

```
clip=Twerk
repeats=44
```

`clip.request` — griddy:

```
tag=griddy
seconds=10.0
fps=10
view=showcase
yaw=190
dist=3.8
fov=40
lift=0.13
look=body
lookoffset=0,0.08,0
width=800
height=450
```

`clip.request` — twerk: identical except `tag=twerk` and `seconds=9.0`.

Request order per clip: drop `pov.request`, 0.5 s later `emote.request`, 0.5 s later `clip.request`.
The emote hold is 13.07 s (griddy) / 11.0 s (twerk) and the recorder runs 10.78 s / 9.61 s, so the
emote is still animating when the last frame is written.

## Yaw: measured, not derived

`body.transform.forward` is `(-1.00, 0.00, -0.04)`, i.e. the local model **faces -X**, and the
Reel of Fortune at `(0.35, 0.09, -8.57)` is on that side (the player stands at
`(5.5, ~1.0, -7.4)` looking at it). One still per candidate, 800x450, showcase view,
`dist=4.0 fov=40 lift=0.14`, `seconds=0.2 fps=5` (one frame each):

| yaw | camera (x,z) | what the still showed |
|---|---|---|
| 0 | (9.51, -7.41) | **inside the +X wall** — grey wall filling the frame with a mirror in it, and the mirror shows the body's **back**; `line of sight crosses geometry 'Collider (3)'@0.3m`. This is the "camera along the player's own forward axis" position: unusable (`shots/yaw000_mirror_back.png`). |
| 90 | (5.51, -3.41) | camera outside the building, wooden post and outdoor terrain, **no body in frame** (`crosses geometry 'Collider (2)'@1.66m`) |
| 180 | (1.51, -7.41) | **front**: face, tie, vest, belt buckle, hat on top; tables left and right, wall + mirror behind |
| 270 | (5.51, -11.41) | inside the -Z wall — flat grey |

Front hemisphere refined: `165` clear but the body reads smaller and right of centre; `175`, `185`,
`195`, `200` clear, all front-on; `150` unusable (the camera ends up *outside* the hut with its
outer wall and a window filling the frame, body not visible); `210` unusable (body visible but
pressed into the top-right corner, the +X wall corner taking the right half of the frame).

**Winner: `yaw=190`.** Reasoning, from the stills rather than from the numbers:

- 165 pushes the body to the frame edge; 150/210 are blocked or half-blocked; 200 pulls the left
  near-table into the foreground and shifts the body left.
- 180 is symmetric but dead flat and puts the mirror square behind the head; 190 keeps the whole
  face/chest toward the camera while the 10° offset gives the hat cone and the shoulder some
  separation from the mirror, and it leaves both tables at the sides instead of one across the
  lower body.
- 190 was also the only candidate where, over a full Griddy loop, no table edge crosses the torso.

This also corrects two assumptions in the brief. **The first pass was not a rear view:** with
`forward = -X`, `yaw=auto` put the camera on the same -X side that `yaw=180` does, and the old
`griddy.mp4`/`twerk.mp4` frames do show the face, tie and vest (the `yaw=0` still above is what a
rear view of this body looks like — and it is inside a wall). Nor was it small: measured on the
old MP4s with the same silhouette method, the subject filled 67-86 % of the frame height (median
72 %) on griddy and 79-80 % on twerk. The brief asks for 55-70 %, so this pass is deliberately a
little wider; what was actually wrong was the furniture converging into the body and the framing
drifting with the pose bounds.

## Framing

| | griddy | twerk |
|---|---|---|
| emote | `The_Griddy`, 6.533 s loop, 393 frames @60 | `Twerk`, 0.250 s loop, 16 frames @60 |
| repeats / hold | 2 / 13.067 s | 44 / 11.000 s |
| request | `seconds=10.0 fps=10` → 100 frames | `seconds=9.0 fps=10` → 90 frames |
| pose bounds at camera create | centre (5.28, 0.94, -7.60), size (1.95, 1.88, 1.18) | centre (4.98, 0.89, -7.69), size (1.10, 2.28, 1.13) |
| camera | yaw 190, dist 3.8, fov 40, lift 0.13, look=body, lookoffset 0,0.08,0 | same |
| eye / look | (1.54, 1.43, -8.26) / (5.28, 1.16, -7.60) | (1.24, 1.38, -8.35) / (4.98, 1.14, -7.69) |
| camera tilt | 4.11° down, looks along +X | 3.66° down |

`dist=4.0` is the hard limit on this side (the Reel of Fortune sits at x=0.35 and anything longer
puts the camera into it). Both 4.0 and 3.8 were shot; 4.0 measured 52.7-67.1 % (median 55.3 %),
which reads small in the bent half of the Griddy loop, so 3.8 shipped.

Measured on the written PNG sequences (temporal-difference silhouette, rows with ≥4 changed pixels
in x = 30-62 % of the frame):

| | top margin | bottom margin | subject height min / median / max |
|---|---|---|---|
| griddy (100 f) | 10.9 % | 11.8 % | 56.7 % / **64.1 %** / 77.3 % |
| twerk (90 f) | 16.0 % | 14.9 % | 67.6 % / **68.9 %** / 69.1 % |
| *first pass, griddy* | 1.8 % | 11.8 % | 67.3 % / 72.0 % / 86.2 % |
| *first pass, twerk* | 6.4 % | 13.1 % | 79.3 % / 79.8 % / 80.4 % |

Griddy's 77 % peak is the deepest lean, where a leg stretches out (frame 39); the median frame is
64 % and the upright phase 69-70 %. Nothing clips: hat and boots keep ≥10 % margin at every phase.

## Hat / higher camera

Tried `lift` 0.55, 0.28, 0.22, 0.20, 0.13, 0.14, -0.05 with the emote running:

- `0.55` puts the camera at y=3.39, **above the ceiling** — the frame is a grey ceiling.
- `0.28` / `0.22` / `0.20` look down on the leaned head and the hat covers *more* of the face, and
  the shot gains floor and loses body.
- `-0.05` (camera at y=0.69) still shows the hat cone pointing at the lens, plus a barrel in the
  foreground.
- `0.13` (kept) keeps the hat fully in frame (11 % of head-room at the tightest phase) and shows
  the face in the upright third of the Griddy loop.

The Griddy lean is toward the camera, so from any unoccluded yaw the cone points at the lens for
most of the loop; that is the cosmetic fit plus the clip, not the camera. A higher camera does not
fix it here.

## ffprobe (delivered files)

```
clips/griddy.mp4   h264 High 800x450 yuv420p  r_frame_rate=232/25 (9.28 fps)  nb_frames=100
                   duration=10.775862  size=504512  bit_rate=374549   md5 d89f8e84e760de28540354b1cd410441
clips/twerk.mp4    h264 High 800x450 yuv420p  r_frame_rate=937/100 (9.37 fps)  nb_frames=90
                   duration=9.605123   size=340034  bit_rate=283210   md5 3aa44a431c9e646a0cc798f2260c8a18
```

The recorder schedules on wall clock and drops missed slots; the encode rate is `frames / wall` from
the `[clip] finish` line, so the files play back at real time:

```
[clip] finish tag='griddy' frames=100 ... requested=10fps/10s wall=10.77s effective=9.28fps view=showcase
[clip] finish tag='twerk'  frames=90  ... requested=10fps/9s  wall=9.61s  effective=9.37fps view=showcase
```

The Windows copies are byte-identical (md5 above).

## Verification (on the encoded MP4s)

Body crop x=280..520, y=40..440 (240x400), consecutive-frame mean absolute difference:

| | frames | MAD mean | MAD min | MAD max | pairs > 0.5 | distinct frames |
|---|---|---|---|---|---|---|
| griddy | 100 | 4.988 | 2.733 | 7.496 | 99/99 | 100/100 (all sha1 unique) |
| twerk | 90 | 2.410 | 1.496 | 3.224 | 89/89 | 90/90 (all sha1 unique) |

`verify/clip_check.py` verdict on both PNG sequences: **REAL MOTION** (griddy diff min/mean/max
0.715/1.384/2.081, twerk 0.383/0.662/0.906, 67/89 pairs over 0.5).
The MAD is a little below the first pass (griddy 5.26 → 4.99, twerk 2.59 → 2.41 on the same crop)
because the subject is still slightly smaller in frame than the first pass's 72-80 %; the motion is
the same clip at the same rate.

## Log evidence (main `LogOutput.log`, this run)

Emote start/stop, both with `released=True`:

```
[emote] START clip='The_Griddy' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=6.533s loop=True frames=393 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
[emote:request] manual start clip='The_Griddy' repeats=2 dur=6.533s hold=13.067s active=True
[emote] STOP reason=request-window clip='The_Griddy' t=0.03s released=True fadeOut=0.20s
[emote] START clip='Twerk' (Lethal Company port) gen=1 mapped=51/51 targetMissing=0 dur=0.250s loop=True frames=16 suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
[emote:request] manual start clip='Twerk' repeats=44 dur=0.250s hold=11.000s active=True
[emote] STOP reason=request-window clip='Twerk' t=0.01s released=True fadeOut=0.20s
```

Body renderers enabled before recording (same instance/body; `[mirror:body-*]` blocks only print on
a mirror diagnostic, the `[clip]` line prints per capture):

```
[pov] third-person ON (was startup) controller='.../Player POV Controller'
[cosmetic:view] why=update pov=ThirdPerson bodyActive=True ... fit=True screen=True mirror=True instances=1 renderers=2
[mirror:body-check] owner='LobbyPlayer_Separated | Owner: 0' bound=True renderers=9 ... isolated=9 driven=9 ... suppressedKeys=0 passedKeys=0 ...
[mirror:body-renderer] 'Boots' ... selected=True active=True enabled=True forceOff=False ... layer=14
[mirror:body-renderer] 'Sheriff_Head' ... selected=True active=True enabled=True ... layer=14
[mirror:body-renderer] 'Shirt_Optimized_1' ... 'Vest' ... 'Pants' ... 'Hair' ... 'Hair_cap' ... enabled=True
[clip] mask showcase main=0x4DDB8F87 added=[14('Hitbox')] final=0x4DDBCF87 bodyLayers=[14,23]
[clip] showcase owner='LobbyPlayer_Separated | Owner: 0' ... renderers=11 center=(5.28, 0.94, -7.60) size=(1.95, 1.88, 1.18) dist=3.8 yaw=190
```

`renderers=11` = the 9 body renderers above plus the two enabled cosmetic renderers; both body
layers (14 `Hitbox`, 23 cosmetics) are in the camera mask and the frames contain the boots, shirt,
vest, face and the hat.

Nothing granted to the inventory: the only grant-shaped lines in the whole log are the lobby prop's
`[reel] settled ... grants=0` (before this capture window); no inventory/emote-grant line exists in
the capture window and `EmoteRuntime`/`PosePlayer` contain no inventory code.

## What still reads badly

- **The background is not open floor, and cannot be from this spot.** Front-on means looking from
  -X toward +X, so the wall, the mirror and the two bar tables are inevitably behind/beside the
  subject; the open floor the prop stands on is *behind the camera* (see the `yaw=0` still: that
  position, which is the "along the player's forward axis" one, is inside the +X wall). The tables
  no longer cross the torso at `dist=3.8` and `yaw=190`, but they are still in frame left and right.
- The room is dim and lit from behind the subject: the front of the body is the darkest thing in
  the frame, and the shirt/vest contrast with the wall is low. No light or exposure control in the
  recorder; the first pass had the same problem.
- The Griddy leans the head at the camera for most of its loop, so the party-hat cone hides the
  face except in the upright phase. Camera height does not rescue it inside this room: at lift 0.55
  the camera is above the ceiling and the frame is grey; 0.20-0.28 look down on the cone and hide
  more of the face; at -0.05 the cone still faces the lens.
- The Griddy spans 57-77 % over its loop: the deepest lean (one leg stretched out, frame 39) is
  over the 55-70 % band and leaves only 10.9 % of head-room. Pulling back far enough to keep that
  peak under 70 % (dist=4.0) drops the median to 55 % and reads small, which is the first pass's
  problem again; 3.8 is the compromise that shipped.
- Twerk is a subtle hip motion on a 0.25 s loop; at ~9.3 fps the recorder gets ~2.3 samples per
  loop, so it reads as fast shuffling rather than smooth motion (same as the first pass).
- The two cameras are not identical in derived pose — eye heights differ by 5 cm because the look
  point comes from each emote's pose bounds at clip start. Angle, distance and fov are equal.
