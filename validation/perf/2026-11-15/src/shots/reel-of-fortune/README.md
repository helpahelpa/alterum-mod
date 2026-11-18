# Reel of Fortune — procedural art + spin evidence

Source: `tracks/merged/mod/AlterumModelSwap.Hot/` (`ReelArt.cs`, `LobbyProp.cs`).
Captured on install A at 640x360, private lobby, desktop locked (RenderTexture captures only).

**The face below is pre-distance-cut.** These stills and the spin video show the single-line
`REEL OF FORTUNE` title, full-word reel labels and the bright neon frame. The current face (two-line
title in the 3x5 display font, 3-letter reel codes, 1.2 self-lit tint, dimmed neon) is documented
and captured on the placement page: `tracks/merged/shots/reel-of-fortune/README.md` and
`after-face-closeup.png` there. The atlas layout also changed: title rows 0-186, banner 186-372,
reels 372-802, footer 804-946, with the two top bands now reading as one two-line title plaque.

## Reproduce

Unattended spin + synchronized capture (no input device needed):

```
printf 'spin\ncapture=clip\ntag=reelspinN\n' > \
  "/mnt/c/Program Files (x86)/Steam/steamapps/common/Alterum/BepInEx/screens/reel.request"
```

The prop starts a `ClipRecorder` clip on the spin's first tick, writing
`screens/reelspinN/frame_0001..0027.png` (960x540, 8 fps request). Encode at the
achieved rate printed by `[clip] finish ... effective=8.11fps`:

```
../../../make_video.sh shots/reel-of-fortune/frames shots/reel-of-fortune/reel-spin.mp4 8.11
```

Idle stills were recorded with a pinned `clip.request` (0.4 s, 15 fps, 960x540):

```
tag=sign_idle
view=showcase
seconds=0.4
fps=15
width=960
height=540
fov=45
eye=1.428,1.980,-8.234
lookat=1.750,1.950,-9.750
```

The whole-cabinet 640x360 still is a `shot.request` capture from the game camera.

## Files

| file | what |
|---|---|
| `reel-spin.mp4` | 27 frames, 960x540, 8.11 fps (nominal = achieved), 3.329 s |
| `reel-spin-filmstrip.png` | 6 frames from the clip, with the matching `[reel:uv]` offsets/symbols |
| `reel-spin-trace.txt` | `[reel] spin start`, every `[reel:uv]` tick, `[reel] settled` |
| `reel-sign-idle.png` | 960x540 sign face, procedural atlas |
| `reel-sign-ingame-640x360.png` | whole cabinet through the game camera |
| `frames/` | the 27 source frames |
