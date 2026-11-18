# Third-person camera + hat coupling — log extract

Install A, private lobby, 640x360, headed, 15 fps cap. Hot reload only; the process was never
restarted. Hot dll **`d3feaec8a14cfe1469a6aec00911363e`** (21 431 808 B) in `BepInEx/hot/`.
Nothing in `AlterumModelSwap.Contracts` or the host changed. Captures and pixel counts:
`tracks/merged/shots/third-person-camera/`. Driver: `tracks/merged/tools/tp-verify.sh`
(its transcript is `verify/third-person-camera-run.log`, the spawn ray fan is `verify/spawn-probe.txt`).

## The candidate set is data-driven, and the numbers come from the eye

```
[tp-cam:init] generation=1 candidates=7 behind=2.300m/0.420m minDist=0.900m margin=0.200m
              hook=CinemachineCore.CameraUpdatedEvent
    [tp-cam:candidate] 0 behind back=2.300 up=0.420 yaw=0.000 ray=2.338 elev=10.349 min=0.900 room=- hit='-'@- sight=-
    [tp-cam:candidate] 1 behind-elevated back=2.000 up=1.150 yaw=0.000 ray=2.307 elev=29.899 min=0.900 room=- hit='-'@- sight=-
    [tp-cam:candidate] 2 overshoulder-left back=1.900 up=0.550 yaw=35.000 ray=1.978 elev=16.144 min=0.900 room=- hit='-'@- sight=-
    [tp-cam:candidate] 3 overshoulder-right back=1.900 up=0.550 yaw=-35.000 ray=1.978 elev=16.144 min=0.900 room=- hit='-'@- sight=-
    [tp-cam:candidate] 4 elevated-close back=0.300 up=0.950 yaw=0.000 ray=0.996 elev=72.474 min=0.550 room=- hit='-'@- sight=-
    [tp-cam:candidate] 5 beside-left back=1.450 up=0.300 yaw=72.000 ray=1.481 elev=11.689 min=0.900 room=- hit='-'@- sight=-
    [tp-cam:candidate] 6 beside-right back=1.450 up=0.300 yaw=-72.000 ray=1.481 elev=11.689 min=0.900 room=- hit='-'@- sight=-
```

(`room=-`/`sight=-` are the table at init, before the first pick; a `cam.request dump` refreshes
the same lines from the current eye pose, which is where section 1's numbers come from.)

`cam.request: probe` (a 9 x 11 ray fan from the eye, `verify/spawn-probe.txt`) is what the spawn's
numbers come from:

```
[tp-cam:probe] eye=(5.354,1.737,-7.568) viewYaw=267.964 forward=(-0.999,0.000,-0.036)
  elev=0   yaw=0   room=0.260 hit='Collider (3)'@0.460      <- behind: the tent back wall
  elev=15  yaw=0   room=0.276 hit='Collider (3)'@0.476
  elev=30  yaw=0   room=0.331 hit='Collider (3)'@0.531
  elev=45  yaw=0   room=0.451 hit='Collider (3)'@0.651
  elev=55  yaw=0   room=0.602 hit='Collider (5)'@0.802
  elev=60  yaw=0   room=inf   hit='none'@clear-to-3.0       <- escapes over the roof line
  elev=65  yaw=0   room=1.061 hit='Collider (6)'@1.261      <- the ceiling
  elev=70  yaw=0   room=1.016 hit='Collider (6)'@1.216
  elev=80  yaw=0   room=0.961 hit='Collider (6)'@1.161
  elev=0   yaw=+90 room=1.257 hit='Плоскость'@1.457         <- to the sides there is room...
  elev=0   yaw=-90 room=1.588 hit='Collider (2)'@1.788
```

## 1. At the spawn: no behind pose exists, so the elevated-close candidate is the one that fits

```
[tp-cam:dump] pose=(5.654,2.687,-7.557) euler=(77.733,270.985,0.000) mask=0x4DDBCFC7
[tp-cam:dump] engaged=True proven=True veto=False insideBody=False
              applied=(5.654,2.687,-7.557) euler=(77.733,270.985,0.000) aimElevation=-77.733
              dist=0.996 headDist=1.027 hit='none'@-1.000 full hitPath=none
[tp-cam:dump] frames=235 appliedFrames=217 shortened=0 noRoomFrames=0 engages=1 insideBodyFrames=0
[tp-cam:dump] picked=elevated-close picks=elevated-close:217
              lastPick=[candidate=elevated-close dist=0.996 full ray=0.996 pitch=77.733
                        elevCand=72.474 yaw=0.000 room=inf headBelowTop=23.096deg applied=0.996]
    [tp-cam:candidate] 0 behind back=2.300 up=0.420 yaw=0.000 ray=2.338 elev=10.349 min=0.900 room=0.268 hit='Collider (3)'@0.468 sight=not-tested
    [tp-cam:candidate] 1 behind-elevated back=2.000 up=1.150 yaw=0.000 ray=2.307 elev=29.899 min=0.900 room=0.331 hit='Collider (3)'@0.531 sight=not-tested
    [tp-cam:candidate] 2 overshoulder-left back=1.900 up=0.550 yaw=35.000 ray=1.978 elev=16.144 min=0.900 room=0.386 hit='Collider (3)'@0.586 sight=not-tested
    [tp-cam:candidate] 3 overshoulder-right back=1.900 up=0.550 yaw=-35.000 ray=1.978 elev=16.144 min=0.900 room=0.384 hit='Collider (3)'@0.584 sight=not-tested
    [tp-cam:candidate] 4 elevated-close back=0.300 up=0.950 yaw=0.000 ray=0.996 elev=72.474 min=0.550 room=inf hit='none'@- sight=clear
    [tp-cam:candidate] 5 beside-left back=1.450 up=0.300 yaw=72.000 ray=1.481 elev=11.689 min=0.900 room=- hit='-'@- sight=-
    [tp-cam:candidate] 6 beside-right back=1.450 up=0.300 yaw=-72.000 ray=1.481 elev=11.689 min=0.900 room=- hit='-'@- sight=-
```

The four behind/over-shoulder candidates all hit the tent's back wall within 0.47-0.59 m. The two
`beside-*` rows read `-` because the table is evaluated in order and the pick returns at
`elevated-close`; where they *are* evaluated (the forced-fallback dump in section 8) `beside-left`
has a clear offset ray but its sight line to the body is blocked by furniture, and `beside-right`
has 0.13-0.33 m of room depending on the exact pose. The 0.996 m, 72.5 deg elevated-close lens is the
only shape whose **full ray and its sight line to the body** are clear. It is inside the room
(y = 2.687 against a ceiling at 2.80), 1.03 m from the head.

## 2. Two metres into the room the natural behind view wins, unshortened

```
[tp-cam:dump] pose=(5.711,2.157,-7.540) euler=(23.770,268.146,-0.000)
[tp-cam:dump] applied=(5.711,2.157,-7.540) euler=(23.770,268.146,-0.000) aimElevation=-23.770
              dist=2.338 headDist=2.341 hit='none'@-1.000 full
[tp-cam:dump] picked=behind lastPick=[candidate=behind dist=2.338 full ray=2.338 pitch=23.770
              elevCand=10.349 yaw=0.000 hit='none'@none room=inf headBelowTop=10.768deg applied=2.338]
    [tp-cam:candidate] 0 behind ... room=inf hit='none'@- sight=clear
```

Collision shortening still applies when the wall is nearer (1.2 m forward of the spawn):

```
[tp-cam:pick] candidate=behind dist=1.433 shortened ray=2.338 pitch=27.860
              hit='Collider (3)'@1.633 room=1.433 headBelowTop=2.771deg applied=1.433
```

## 3. Stability (60-frame windows after `cam.request stab`)

```
# spawn, elevated-close
[tp-cam:stats] why=window ... lastDist=0.996 headDist=1.031 noRoom=0 insideBody=0
               picked=elevated-close windowMaxDeltaPos=0.000m/MaxDeltaRot=0.343deg
# 2.0 m into the room, behind
[tp-cam:stats] why=window ... lastDist=2.338 headDist=2.344 noRoom=2 insideBody=0
               picked=behind windowMaxDeltaPos=0.000m/MaxDeltaRot=0.000deg
```

The spawn view's position is exact; its aim pitch follows the idle-animated body bounds by at most
0.34 deg (about 3 px at 640x360). The behind view is bit-exact.

## 4. The coupling: one owner of "is the local body on screen", two bits follow

`MirrorPlayerVisibility.LocalBodyOnScreen` = armed + third person + no native hide + the rig's lens
proven out of the body. The body bit (`SyncScreenView`) and the local cosmetic bit
(`CosmeticsRuntime`, via the sink wired in `PlaceMirror`) both read that one answer, in the same
frame, in either direction:

```
# third person, body admitted -> hat admitted in the same frame
[bodyview:screen] pov=ThirdPerson want=True layer=14 ('Hitbox') bit=0x00004000 ... veto=no
                  masks='Lobby Global/Main Camera'=0x4DDBCFC7+o;
[cosmetic:layer] screen policy why=body-on-screen pov=ThirdPerson localLayer=6 bit=0x00000040
                 want=True screenCameras=1 masksChanged=1 bodyOnScreen=True
# the lens is refused -> both bits leave, and the printed mask is the post-coupling state
[bodyview:screen] pov=ThirdPerson want=False ... veto=no-room masks='Lobby Global/Main Camera'=0x4DDB8F87-.;
[cosmetic:layer] screen policy why=body-off-screen pov=ThirdPerson ... want=False bodyOnScreen=False
# first person -> both bits leave; the mask is the pristine one
[bodyview:screen] pov=FirstPerson want=False ... masks='Lobby Global/Main Camera'=0x4DDB8F87-.;
[cosmetic:layer] screen policy why=body-off-screen ... want=False bodyOnScreen=False
```

`CosmeticsRuntime.OnLocalBodyOnScreenChanged` is called from the mask write itself (not from a
prediction of it), which is why the two lines above agree in the frame they are logged. It writes
only bit 6, from a freshly read mask, and never the body bit; the body bit is written only by
`MirrorPlayerVisibility`.

## 5. First person is byte-identical, and the reload path returns to it

```
[tp-cam:dump] mask=0x4DDB8F87 fov=70.000 near=0.010 far=500.000 pixels=640x360
              pose=(3.408,1.737,-7.630) euler=(-0.000,267.964,0.000)
[tp-cam:restore] why=hook:not-third posDelta=0.000m rotDelta=0.000deg wroteBack=False
```

Same-frame mask pairs in first person (`screenproof.request`, `pair=live`, one frame, two renders):

```
pair tag='fp-body' masks on=0x4DDB8F87 off=0x4DDB8F87 -> 0 px
pair tag='fp-hat'  masks on=0x4DDB8F87 off=0x4DDB8F87 -> 0 px
```

Every hot reload restores the recorded pristine masks before the new generation re-tracks them:

```
[cosmetic:layer] restored 1/1 screen camera mask(s)
[cosmetic:layer] screen camera 'Lobby Global/Main Camera' pristineMask=0x4DDB8F87 tracked=1
[bodyview:screen-camera] 'Lobby Global/Main Camera' pristineMask=0x4DDB8F87 hadBit14=False tracked=1
```

## 6. The mirror keeps both bits in both POVs

```
[bodyview:proof] tag='mirror-cam-tp' source='AlterumMirror/AlterumMirrorCam' liveMask=0x4CDB4FC7
                 usedMask=0x4CDB4FC7 bit=present layer=14 ...
[bodyview:proof] tag='mirror-cam-fp' source='AlterumMirror/AlterumMirrorCam' liveMask=0x4CDB4FC7
                 usedMask=0x4CDB4FC7 bit=present layer=14 ...
```

The reflection itself (captured through the game camera at the glass, because rendering the mirror's
disabled camera on demand comes back black) shows body + hat in both POVs; see
`shots/.../mirror-thirdperson.png` and `mirror-firstperson.png`.

## 7. Never the inside of the face

```
[tp-cam:dump] ... insideBodyFrames=0
[tp-cam:no-room] ... no third-person pose with a clear sight line to the body exists here; camera
                       left at the game's own pose and the body bit withdrawn
```

No `[tp-cam:inside-body]` line was emitted in any generation of this session, and `insideBody=0`
throughout. The refused path never writes a pose at all (`appliedFrames` stops advancing and the
game's own transform stays), so a face-interior frame cannot be produced by a refusal.

## 8. Forced-fallback evidence (temporary build, reverted)

To photograph the refused path at the spawn (where `elevated-close` always fits in the shipped
build), a build with `SightClear` forced false was hot reloaded for the capture, the source was then
reverted and rebuilt byte-identically to the pre-test dll (`md5sum` verified) before anything else
ran:

```
[tp-cam:no-room] why=cinemachine no candidate of 7 fits (behind room=0.268m (hit 'Collider (3)'@0.468),
                 max=elevated-close room=infm sight=blocked:forced-for-proof; floors 0.550-0.900m,
                 +0.300m after a refusal): no third-person pose with a clear sight line to the body
                 exists here; camera left at the game's own pose and the body bit withdrawn
[tp-cam:dump] pose=(5.354,1.737,-7.568) mask=0x4DDB8F87 ... proven=False appliedFrames=0
[bodyview:proof] pair tag='fallback-spawn-hat' masks on=0x4DDB8F87 off=0x4DDB8F87 -> 0 px
```

## 9. Untouched paths (smoke test after the last reload)

```
[keys] merged: ... F9 cycle the 50-entry emote list ...
[emote] pose player ready (12 LC clips + 38 game clips); F6 toggles, F9 cycles the 50-entry list
[emote] START clip='Salute' (Lethal Company port) ... suspended=True lease=DoingSomething+DisableAnimancerAndAnimator
[jsolve:gate] suspended reason=emote: 2 leaf write(s) forgotten, chest writes stop here
[emote] STOP reason=end(Salute) ... released=True fadeOut=0.20s
[jsolve:gate] resumed reason=animation: rigs=1, particles re-seeded from the animated pose
```
