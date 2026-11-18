# Reel of Fortune — visibility from the player's own camera

Source: `tracks/merged/mod/AlterumModelSwap.Hot/` (`LobbyProp.cs`, `PropView.cs`).
Install A, 640x360 windowed, private lobby, headed (`Quiet=false`), 15 fps cap.

Art and spin evidence stays in `tracks/merged/mod/shots/reel-of-fortune/README.md`. This page is
about one question only: **can a player standing in the lobby see it**, answered with `ScreenShot`
(the game's own main camera), not with a purpose-built showcase camera.

## What the log said before the move (anchor 1.75, -9.75, yaw -12)

```
[prop] Reel of Fortune placed pose=(1.75, 0.09, -9.75) euler=(0,-12,0) floorY=0.088 parts=69
       tris=1095 renderers=69 materials=7 colliders=0 spec=1.00x0.34x2.95 ...
[prop:view] why=place camera='Lobby Global/Main Camera' eye=(5.35,1.80,-7.57) fwd=(-1.00,0.00,-0.04)
            fov=70.0 aspect=1.778 px=640x360
[prop:view] why=place dist=4.21 angle=29.1deg viewport=(0.28,0.45,3.68) inside=9/9
            screen x=[0.20,0.33] y=[0.11,0.78] depth=[3.14,4.22] side=inside
[prop:occl]  eye->prop blocked=16/16 nearest='Camp/Headquarters/Table 2/PF_PaperPile01/Collider'
             d=2.25m propDist=4.21m blockers=['World/Camp/Headquarters/Palatka_Low/Плоскость'x14
             'Camp/Headquarters/Table 2/PF_PaperPile01/Collider'x1 'World/Camp/Headquarters/Table 2/Wooden Table'x1]
[prop:inside] Y!='World/Terrain'@(1.75,0.06,-9.73); Y!='Camp/Headquarters/Palatka_Low/Colliders/Floor Collider'@(1.75,0.09,-9.73);
[prop:view] verdict=OCCLUDED('Camp/Headquarters/Table 2/PF_PaperPile01/Collider'@2.25)
```

So the prop was in the frame but standing on the far side of the tent's inner fabric wall
(`World/Camp/Headquarters/Palatka_Low/Плоскость`, 1.05 m in front of it) — 14 of 16 silhouette
samples were behind that plane, leaving only the neon strip that shows in
`before-occluded-by-fabric.png` / `-zoom.png`.

## Where it is now (anchor 0.35, -8.57, yaw 66)

Chosen by `PropView.Scan`, which raycasts floor, headroom over the prop's footprint, horizontal
clearance at four heights, and sign-face visibility for every 0.5 m cell within 7 m of the game
camera (235 cells; 146 fail headroom because the tent ridge is the only place a 2.95 m sign fits,
60 fail clearance, 25 fail sign-face visibility, 4 survive). It is the closest survivor:

```
[prop] Reel of Fortune placed pose=(0.35, 0.09, -8.57) euler=(0,66,0) floorY=0.088 parts=69
       tris=1095 renderers=69 materials=7 colliders=0 spec=1.00x0.34x2.95 ...
[prop:measure] worldBounds centre=(0.37, 1.56, -8.57) size=(0.751,2.950,1.060) height=2.950
               floorY=0.088 baseY=0.088 topY=3.038 centreAboveFloor=1.475 ...
[prop:view] why=place dist=5.09 angle=9.7deg viewport=(0.43,0.47,5.02) inside=9/9
            screen x=[0.38,0.48] y=[0.24,0.69] depth=[4.63,5.41] side=inside
[prop:occl]  eye->prop blocked=0/16 nearest='none' d=0.00m propDist=5.09m blockers=[]
[prop:player] body centre=(5.51,1.01,-7.42) size=(1.15,1.95,1.08) feetY=0.04
              eye=(5.35,1.80,-7.57) eyeToProp=5.09
[prop:inside] Y!='World/Terrain'; Y!='Camp/Headquarters/Palatka_Low/Colliders/Floor Collider';
[prop:view] verdict=VISIBLE
```

`PropView.Poll` also accepts a `prop.request` file in `BepInEx/screens/` (`probe`, `scan`,
`move x=.. z=.. yaw=..`, `shot tag=..`), which is how the candidates were compared without a
restart. Nothing was restarted for this change: `BepInEx/hot/AlterumModelSwap.Hot.dll` was rebuilt
and copied, and the host hot-reloaded it (`[hot] generation 19 active`; the deployed dll is
`63b5fe89ffd847829459d37fc39bc17e`).

## Files

| file | what |
|---|---|
| `after-gamecam-1/2.png` | `ScreenShot` from `Lobby Global/Main Camera` at the spawn pose, two idle frames (self-lit face, current art) |
| `after-gamecam-spin.png` | same camera ~1 s into a `reel.request` spin (strips scrolling, settled `match=True grants=0`) |
| `after-face-closeup.png` | 960x540 pinned showcase camera 1.5 m in front of the face, for direct inspection |
| `after-gamecam-3.png` | same camera, idle, before the self-lit pass (old face; kept as the before) |
| `after-vicinity-wide.png` | whole tent from `(4.20,1.70,-6.60)`: the prop on the open floor, clear of the sleeping bags and the fabric walls, top under the ridge |
| `after-vicinity-side.png` | closer side render: arrow, neon frame, procedural sign, tray, planks |
| `after-vicinity-overhead.png` | from above the roof, proving the 2.95 m prop does not pierce the tent fabric (the small ridge sliver is scene geometry: it is unchanged with the prop moved 1 m away) |
| `before-occluded-by-fabric.png` | the same game camera before the move: only a strip of neon shows past the fabric wall |
| `before-occluded-by-fabric-zoom.png` | 2x crop of the above |
| `front.png`, `side.png`, `three-quarter.png`, `iter1-3-*` | the earlier art iteration renders (showcase camera, anchor 1.75/-12 as built) |

One caveat in the game-camera frames: with the `cosmetics` POV in third person the local player's
party hat (`Cone`/`Sphere`, layer 23) can draw in front of the screen camera and overlap the sign.
It is the player's own cosmetic, it comes and goes with the POV state, and it is unrelated to the
prop — `after-vicinity-wide.png` and the idle frames are clean.

Reproduce the game-camera frame:

```
printf 'reel_spawn\n' > "/mnt/c/Program Files (x86)/Steam/steamapps/common/Alterum/BepInEx/screens/shot.request"
```

The 2.95 m prop now stands 4.6 m ahead of the spawn camera, 9.7 deg off its axis, whole in the
frame (viewport x 0.38-0.48, y 0.24-0.69), top 3.04 m under the tent ridge, 0 colliders, floor
snapped to y=0.088.

## Self-lit face (readability at the spawn distance)

The face was already `Universal Render Pipeline/Unlit` with the generated atlas; the dark render
was the art, not the material. From spawn the 0.76 m art area is ~42x40 px, so the old single-line
title (15 glyphs, scale 11) put 0.45-px strokes on screen and the mip filter averaged them into the
plate. Changes:

* **Title split over the two top bands** — `REEL OF` / `FORTUNE` in the 3x5 display font at scale
  32: 6.5 screen-px caps, 1.3 screen-px strokes, 1.6-px gap. A single line cannot exceed 7 glyphs at
  that stroke weight in 1024 atlas px; two lines fit exactly.
* **Self-lit gain 1.20** applied to the per-renderer tint of the four art quads (title /
  banner / housing / footer), 1.15 to the reel windows. A renderer's `MaterialPropertyBlock`
  `_BaseColor` overrides the material colour, so the multiplier lives there; the shader still
  samples the atlas and ignores room light, and the title gold lands above 1.0.
* **Contrast**: plate raised from #4E1A22 to a #722832→#45141D gradient, title #FFE08A hot gold,
  trim unchanged; reel codes cream on #1A070C.
* **Neon dimmed**: tube #6FE8FF→#63D6EE, additive skirts alpha 0.26/0.72→0.16/0.46, so the face is
  the focal point. The core filament is unchanged.
* **Reel labels are 3-letter codes** (`MED LMP KEY BOK ROP CRX`): the reel window is 12 screen px
  wide, and a full word cannot hold a 1-px stroke there. Same 3x5 font at scale 20 = ~1 screen px
  strokes. The icon above each code carries the shape.

The material name `Cosmetic_PropLit` is unchanged and the sign material stays Unlit, so the
cosmetics template scan (URP/Lit only, skips `Cosmetic_*`) still clones `MegaBook Page Back`
(`[cosmetic:shader-template]` after the reload).

**Judgement at 640x360 from spawn** (`after-gamecam-1.png`): `REEL OF` / `FORTUNE` are legible —
the sign reads as a named machine, not a glowing rectangle. The three reel codes are legible with
the eye on them (`KEY` is the clearest). The footer pair (`INSERT DRIP CREATURE TO PLAY`,
`SKINS FOR: KNIFE, KNUCKLES`) is not legible at this distance: at 6/1024 of the art width it is a
sub-pixel gold texture; it stays for the close-up. The reel item icons are recognisable shapes but
not identifiable symbols at 640x360; the code below each is the identifier.
