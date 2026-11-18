# Reel of Fortune — placed in Necropolis (instance A)

Instance A (`pid 29384`, `BepInEx` main install, 640x360 windowed, private lobby), driven with
request files only while the desktop was locked: no source edit, no build, no hot reload.

## UI path / RPCs

1. `BepInEx/screens/ui.request` = `scene` + `lobby` + `maps` (read-only probe).
   Log: `active='Lobby'`, starter `map='LairOfOccultists_GameMapData'`, `mapBook selected=-1
   effective=0`, readiness `canStart=False ready=0 min=4`, maps `[0] Lair [1] Necropolis`.
2. `BepInEx/screens/spawn.request` = `map=Necropolis` → `[spawn:map] selected index=1
   'Necropolis_GameMapData' (now 1)`. Re-probe: starter `map='Necropolis_GameMapData'`,
   `mapBook selected=1 effective=1`, `min=4 max=5`.
3. `BepInEx/screens/ui.request` = `req=1` → `PlayersReadinessController`:
   - `RequiredPlayerNumberToStartGame.Value = 1`
   - `MakePlayerUnreadyRpc(0)` then `MakePlayerReadyRpc(0)` (the game then recomputes itself; this
     path never calls `StartTransitionToGameScene`)

   ```
   [ui:req] before required=4 ready=0 min=4 canStart=False
   [ui:req] RequiredPlayerNumberToStartGame -> 1
   [ui:req] after required=1 ready=1 min=4 canStart=True
   ```
4. The game started its own countdown/transition. Log confirms the scene that loaded:
   `[scene] -> 'Necropolis Map Scene' at t=1758.2` (`ui:scene`: `mapRun=True`).
5. `BepInEx/screens/reel.request` = `place=here` → `PlacePropAtPlayer` → `CreateAtPlayer`
   (no booth existed in the map run). One placement, no F4 press needed.

## Reel pose

```
[prop] placed at player why=reel.request player='GamePlayer_Separated | Owner: 0'
       pose=ThirdViewPlayerModel pos=(115.00,0.29,116.85)
       at=(115.98,0.278,116.25) yaw=-58.6 colliders=0
[prop] Reel of Fortune placed pose=(115.98, 0.28, 116.25) euler=(0,-58.56,0) floorY=0.278
       parts=76 tris=1113 renderers=76 materials=6 colliders=0
[prop:measure] worldBounds centre=(115.97,1.75,116.26) size=(0.828,2.950,1.045)
               baseY=0.278 topY=3.228
[prop:view] why=place camera='Game Global/Main Camera' eye=(115.00,1.93,116.85)
            fwd=(0.85,0.00,-0.52) dist=1.15 viewport=(0.50,0.39,1.14)
[prop:inside] Y!='Location_Graveyard_Stage#1/Buildings/Ruins/Building/Ruins_crypt_side_01'
              @(115.97,0.28,116.26)
```

`place=here` is the fixed resolver pose: 1.15 m ahead of the player, floor-snapped by the same
probe `CreateAtPlayer` uses, face turned back to the player (yaw -58.6 = opposite of the player's
fwd 0.85,-0.52). The `[prop:occl]`/`verdict=PARTLY_OCCLUDED` is the player's own
`... [Hitbox]` at 0.08 m, i.e. the first-person body ray, not world geometry.

At 1.15 m the 2.95 m sign overflows the 70° frame vertically (screen y -1.79..2.31): the capture
shows the face/tray band, the body below it and the Necropolis crypt around it.

## Capture

`shot.request` = `reel-necropolis`, through the live game camera (`Game Global/Main Camera`,
`ScreenShot.Capture` → RenderTexture, works with the desktop locked):

```
[shot] ...\BepInEx\screens\shot_main_reel-necropolis_20261114-065217-111_02.png 640x360
       camera=Game Global/Main Camera
```

Repo copy: `shot_main_reel-necropolis_20261114-065217-111_02.png` (640x360).

## State left

Alive (pid 29384), `Necropolis Map Scene`, map run active, player at `(115.00,0.29,116.85)`,
reel placed at `(115.98,0.28,116.25)` yaw -58.6, `colliders=0` (walk-through). No pickers open, no
debug overlay, cosmetics/clothes untouched (game re-applied its own base model on scene load), no
request files left behind. F4 still re-anchors the booth at the player for fine positioning.
