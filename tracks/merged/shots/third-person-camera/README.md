# A real third-person camera, now with candidate poses — and no hat without a body

Build: `tracks/merged/mod` (hot dll **`d3feaec8a14cfe1469a6aec00911363e`**, 21 431 808 B), hot-reloaded
into install A (`C:\Program Files (x86)\Steam\steamapps\common\Alterum`), private lobby, 640x360,
headed, 15 fps cap. **No restart**: the process that was running when this work started is the one
that is running now. `AlterumModelSwap.Contracts` is untouched; `grep -c SWAP` over the sources is 0.

Log evidence: `tracks/merged/verify/third-person-camera.md`, `verify/third-person-camera-run.log`
(the driver transcript), `verify/spawn-probe.txt` (the spawn ray fan).
Driver: `tracks/merged/tools/tp-verify.sh`. Numbers: `stats.json`.

## What changed

| file | change |
|---|---|
| `AlterumModelSwap.Hot/ThirdPersonCamera.cs` | the single `behind` offset became an **ordered candidate set**; each candidate traces its own full ray from the eye, must leave usable room, and must have a **clear sight line to the body** before it can be chosen. `[tp-cam:pick]` logs which one won. `cam.request probe` dumps the ray fan the numbers come from, `cam.request teleport=to:x,z` is harness sugar for returning to the spawn. |
| `AlterumModelSwap.Hot/MirrorPlayerVisibility.cs` | one owner for **"is the local body on screen"** (`LocalBodyOnScreen`); the body bit and the cosmetic bit both follow it, and a sink tells the cosmetics runtime the moment it changes (including in `LateUpdate`, where the camera rig's veto is decided). |
| `AlterumModelSwap.Hot/CosmeticsRuntime.cs` | the local cosmetic bit is admitted only while `LocalBodyOnScreen`; `OnLocalBodyOnScreenChanged` writes bit 6 only, from a freshly read mask. Was `pov == ThirdPerson && !foreignHide` (a per-POV rule), which is what left the hat on screen when the body bit was withdrawn. |
| `AlterumModelSwap.Hot/ModelSwapRuntime.cs` | one line in `PlaceMirror`: wire the sink. |

## The candidate set, and why these shapes

`behind` is always tried first; the fallbacks only win where it has no usable room.

| # | candidate | back | up | yaw | ray | elevation | floor |
|---|---|---|---|---|---|---|---|
| 0 | `behind` | 2.30 | 0.42 | 0° | 2.338 m | 10.3° | 0.90 m |
| 1 | `behind-elevated` | 2.00 | 1.15 | 0° | 2.307 m | 29.9° | 0.90 m |
| 2 | `overshoulder-left` | 1.90 | 0.55 | +35° | 1.978 m | 16.1° | 0.90 m |
| 3 | `overshoulder-right` | 1.90 | 0.55 | −35° | 1.978 m | 16.1° | 0.90 m |
| 4 | `elevated-close` | 0.30 | 0.95 | 0° | 0.996 m | 72.5° | 0.55 m |
| 5 | `beside-left` | 1.45 | 0.30 | +72° | 1.481 m | 11.7° | 0.90 m |
| 6 | `beside-right` | 1.45 | 0.30 | −72° | 1.481 m | 11.7° | 0.90 m |

Acceptance, per candidate, in table order:

1. **full ray from the eye** (`Physics.RaycastAll`, the body's own 15 layer-14 hitboxes skipped):
   `room = firstHit − 0.20 m` (or infinite). The candidate needs `room ≥ its own floor` (+0.30 m
   hysteresis while the rig is refusing). The floor is per candidate because 0.90 m is the
   "outside the body, body readable" distance for a behind view, and a high-close view is not that.
2. **clear sight line to the body**: from the candidate pose to the body's bound centre and to the
   head. A lens that cleared the tent wall by passing *above its roof* has a clear offset ray and no
   view of the body at all — the sight test is what refuses it. This is measured, not assumed.
3. distance = `min(ray, room)`, a hard maximum; approach is immediate, pull-back is smoothed (12/s).
4. aim = body bound centre, pitch fitted to the camera's own vertical fov, head kept below the frame
   top by a 5° margin. The projection is never written (fov 70.000 / near 0.010 / far 500 in every dump).

If **nothing** passes, the game's own pose is kept and the body bit is withdrawn in the same frame
(and, with the coupling, so is the hat's).

### Which candidate wins where

| spot | candidate | distance | pitch down | head below frame top |
|---|---|---|---|---|
| **spawn** `(5.354,1.737,−7.568)` | **`elevated-close`** | 0.996 m (full ray) | 77.7° | 23.1° |
| 1.2 m forward of the spawn | `behind` | 1.433 m (shortened from 2.338) | 27.9° | 2.8° |
| 2.0 m forward of the spawn | `behind` | 2.338 m (full) | 23.8° | 10.8° |

At the spawn the four behind/over-shoulder candidates hit the tent's back wall
(`Palatka_Low/Colliders/Collider (3)`, 0.30 × 2.25 × 4.54 m, face 0.34 m behind the eye, top 0.59 m
above it) at 0.47-0.59 m; the sides have 1.4-1.6 m of room but the sight line is blocked by furniture
or the ray is shorter than the floor; the ceiling (`Collider (6)`) is at 1.06 m above the eye, so
nothing above 1.06 m fits either. `elevated-close` (0.30 m back, 0.95 m up) is the only shape that is
inside the room *and* has a line to the body.

## The coupling rule: no hat without a body

One owner: `MirrorPlayerVisibility.LocalBodyOnScreen` = **armed + third person + no native hide + the
camera rig's lens proven out of the body**. Everything follows it:

* `SyncScreenView` admits/withdraws the body bit (layer 14) from that answer, as before;
* `CosmeticsRuntime` admits/withdraws the local cosmetic bit (layer 6) from the same answer, and
  `MirrorPlayerVisibility` calls it back (`OnLocalBodyOnScreenChanged`) from the **mask write itself**,
  not from a prediction of it. That matters because the rig's veto is decided in `LateUpdate`, after
  the cosmetic policy has already run for the frame: with the callback both bits leave in the same
  frame instead of one frame apart.

It does not fight `CosmeticsRuntime`'s own mask ownership: each side still writes only its own bit,
from a freshly read mask, and `RestoreScreenCameraMasks` still restores the pristine mask minus the
body layer, so either dispose order is safe. The mirror is a separate mask and is untouched: it keeps
both bits in both POVs.

Effects: first person, the no-room/inside-body/not-proven fallbacks and a native body hide all take
the hat with the body; the mirror keeps showing both.

## Verification (live game camera, `ScreenShot`, not a proof camera)

| # | check | result |
|---|---|---|
| 1 | **spawn, third person** | candidate **`elevated-close`**, camera `(5.654,2.687,−7.557)`, 0.996 m from the eye, 1.03 m from the head, pitch 77.9° down, head 23.2° below the frame top. Mask `0x4DDBCFC7`. Body layer **15 971 px**, hat layer **19 233 px** in same-frame mask pairs. Camera inside the room (ceiling at 2.80). |
| 2 | **2 m into the room, third person** | candidate **`behind`**, full 2.338 m, pitch 23.8° down, head 10.8° below the top. Mask `0x4DDBCFC7`. Body **29 511 px**, hat **1 757 px**. Body + hat + Reel of Fortune in frame. |
| 3 | **first person** | mask **`0x4DDB8F87`** — byte-identical to the pre-change first-person mask; body layer **0 px**, hat layer **0 px** (on/off masks in the pair are both `0x4DDB8F87`); `[tp-cam:restore] posDelta=0.000m rotDelta=0.000deg`. |
| 4 | **mirror, both POVs** | mirror camera mask **`0x4CDB4FC7`** in third *and* first person (`bit=present layer=14` both times); the reflection shows body + hat in both. The screen camera has neither in first person. |
| 5 | **never the inside of the face** | `insideBodyFrames=0` in every generation, `insideBody=False` on every dump, no `[tp-cam:inside-body]` line this session; a refusal never writes a pose at all (`proven=False`, the game's own transform stays). Counters at the end of the final run: `noRoomFrames=0`, `insideBodyFrames=0` (the elevated lens is used at the spawn, so the refused path is exercised by the forced-fallback build in `verify/third-person-camera.md` section 8). |

60-frame stability, idle (`cam.request stab`):

| pose | max Δpos | max Δrot |
|---|---|---|
| spawn, `elevated-close` | **0.000 m** | **0.343°** |
| 2 m in, `behind` | **0.000 m** | **0.000°** |

The spawn's 0.34° is the aim pitch following the idle-animated body bounds (≈2 px); the position is
exact. The behind view is still bit-exact.

Untouched and re-checked after the last reload: `[keys] merged … 50-entry emote list`,
`[emote] pose player ready (12 LC clips + 38 game clips)`, an emote run (`START … lease=…` →
`STOP … released=True`) with `[jsolve:gate] suspended/resumed`; mirror body
`isolated=8 driven=8 selected=8/8` for this session's 8-renderer `Diana` rig (the earlier 9-of-9 note
was a 9-renderer rig; the invariant is `isolated == driven == renderers`).

## Captures (live game camera, 640x360)

| file | what |
|---|---|
| `spawn-thirdperson-final.png` | **spawn, third person**, `elevated-close`: the body from above with the hat on it |
| `final-spawn-tp-body_{on,off}.png`, `final-spawn-tp-hat_{on,off}.png` | same-frame mask pairs at that pose (15 971 body px, 19 233 hat px) |
| `room-thirdperson-behind.png` | **2 m into the room**, `behind` full 2.338 m: body + hat + Reel of Fortune |
| `midroom-tp-body_{on,off}.png`, `midroom-tp-hat_{on,off}.png` | same-frame pairs there (29 511 / 1 757 px) |
| `room-firstperson.png` | **first person**, same spot: no body, no hat; mask `0x4DDB8F87` |
| `fp-body_{on,off}.png`, `fp-hat_{on,off}.png` | first-person pairs — 0 px, both masks `0x4DDB8F87` |
| `mirror-thirdperson.png`, `mirror-firstperson.png` | the mirror glass from the room: body + hat in **both** POVs (captured through the game camera; rendering the mirror's disabled camera on demand comes back black) |
| `fallback-spawn-eye.png`, `fallback-spawn-hat_{on,off}.png` | the refused path at the spawn, from a **temporary forced-fallback build** (reverted immediately, md5 restored): game eye view, hat bit withdrawn, 0 hat px |
| `env-behind.png`, `env-overhead.png` | why there is no room behind the spawn: the wall 0.39 m behind, and the roof over the tent |
| `contact-sheet_third-person-camera.png` | all of the above, labelled |
| `stats.json` | the numbers above, machine-readable |

## What still looks wrong

1. **The spawn's third-person view is a top-down body view, and that is a property of the room.**
   Wall 0.34 m behind, ceiling 1.06 m above the eye, the open side blocked from the body by
   furniture. `elevated-close` (0.996 m, 72.5° up, 77.7° down) is the only admissible shape, so at
   the spawn you see the hat and the top of the head, not a body silhouette. It is a real third-person
   lens on a real body (15 971 body px, hat attached to the head), but it does not read as "over the
   shoulder". The Reel of Fortune is 5 m in front at eye height and is out of frame from a downward
   lens — that part of the spawn check cannot be met by any pose that fits here.
2. **The spawn's aim pitch is not bit-exact** (0.343°/60 frames) because it is fitted to the animated
   body bounds at ~1 m. A low-pass would remove it and add lag while walking; not done.
3. **The `beside-*` candidates are the least exercised.** At the spawn `beside-left` is refused by its
   sight line at 0.63 m (`Collider (5)`, furniture) and `beside-right` is refused by room, so they only
   matter where the high-close lens cannot fit. Their own sight test reported a hit at ~3.0 m from a
   1.4 m ray once (`Плоскость`), which is longer than the cast — the string is diagnostic only (the
   candidate is refused conservatively), but it means the blocker distances in the log are not always
   trustworthy.
4. **The mirror's body in first person depends on when it was bound.** Its binding captures the
   game's owner-hide key only if it was bound *while first person was active* (`keys=2`); a mirror bound
   in third person has `keys=0`, the game's first-person hide passes through, and after one POV cycle
   the body renderers stay `enabled=False` until the next rebind (then the mirror shows only the hat).
   Pre-existing (same class as the old README's hat-fragility note), unchanged by this work, and the
   reason the mirror captures here were taken after a reload with the binding made in first person.
   Fixing it means re-asserting the mirror's driven renderers after the game's hide, which needs a
   "was that hide foreign?" answer the binding currently cannot give (that is what the missing keys
   were for) — a mirror-track change, not this one.
5. **Collision shortening was exercised at 1.2 m** (2.338 → 1.433 m) but nowhere else this session,
   because the spots used for evidence are either walled in (spawn, no behind pose at all) or open
   (2 m in, full length). The 0.20 m margin, 0.90 m floor and 0.30 m hysteresis are unchanged.
6. **The harness still moves the player** (`cam.request teleport=…`, logged as "this is not the spawn
   pose"), and the game is left standing on the spawn in third person.
