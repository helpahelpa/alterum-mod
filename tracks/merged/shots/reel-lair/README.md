# Reel of Fortune — Lair of Occultists (instance A)

Instance A (`pid 29384`, main install, 640x360 client), driven with request files only while the
desktop was locked: no source edit, no build, no deploy, no hot reload. The deployed hot dll
(gen 107, `BepInEx/hot/AlterumModelSwap.Hot.dll`, 06:20) already carries every path used.

Necropolis `mapRun` was left behind on purpose: `necropolis-1` / `necropolis-2` in
`data/reel-placements.json` already hold the user's placements.

## Path (exit + start)

1. **Leave the map** — `spawn.request` = `lobby` → `SpawnProbe.GoLobby` →
   `SceneHandle.NetworkSceneLoader.Instance.LoadSceneOnServer("Lobby", <self>, false, null)`.

   ```
   [spawn:map] LoadSceneOnServer('Lobby') clients=1 iface=True
   [scene] -> 'Lobby' at t=3552.1
   [ui:scene] active='Lobby' ... mapRun=False
   ```

   `mapRun=False` confirmed by a fresh probe, not assumed.
2. **Select the map explicitly** (`mapBook` defaults to `selected=-1`) —
   `spawn.request` = `map=LairOfOccultists` → `[spawn:map] selected index=0
   'LairOfOccultists_GameMapData' (now 0)`; re-probe: `mapBook selected=0 effective=0`,
   `starter map='LairOfOccultists_GameMapData'`.
3. **Start through readiness, not a forced load** — `ui.request` = `req=1`:

   ```
   [ui:req] before required=4 ready=0 min=4 canStart=False
   [ui:req] RequiredPlayerNumberToStartGame -> 1
   [ui:req] after required=1 ready=1 min=4 canStart=True
   ```

   `UiDriver.RequiredPlayers` sets `RequiredPlayerNumberToStartGame.Value = 1`, then
   `MakePlayerUnreadyRpc(0)` / `MakePlayerReadyRpc(0)` so the controller recomputes
   `CanStartExpedition` itself. `StartTransitionToGameScene` was never called.
4. **The game's own countdown ran** (~128 s): `[scene] -> 'Lair Of Occultists Map Scene' at
   t=3680.1`, then `[ui:scene] active='Lair Of Occultists Map Scene' mapRun=True`.

## Reel pose

`reel.request` = `place=here` (no booth existed in the map run → `CreateAtPlayer`):

```
[prop] placed at player why=reel.request player='GamePlayer_Separated | Owner: 0'
       pose=ThirdViewPlayerModel pos=(-49.40,0.58,-33.57)
       at=(-49.37,0.567,-32.42) yaw=-178.5 colliders=0
[prop] Reel of Fortune placed pose=(-49.37, 0.57, -32.42) euler=(0,-178.51,0) floorY=0.567
       parts=76 tris=1113 renderers=76 materials=6 colliders=0
[prop:measure] worldBounds centre=(-49.37,2.04,-32.43) size=(1.009,2.950,0.392)
               baseY=0.567 topY=3.517
[prop:view] why=place camera='Game Global/Main Camera' eye=(-49.40,2.21,-33.57)
            fwd=(0.03,0.00,1.00) fov=70.0 px=640x360
```

1.15 m ahead of the player (player z -33.57, prop z -32.42, player fwd +z), floor-snapped to
`-3.130`-equivalent `0.567`, yaw -178.5 = turned back to the player, `colliders=0` (walk-through).

## Capture

`shot.request` = `reel-lair` through the live game camera (`ScreenShot.Capture` → RenderTexture,
works with the desktop locked):

```
[shot] ...\BepInEx\screens\shot_main_reel-lair_20261114-072321-023_05.png 640x360
       camera=Game Global/Main Camera
```

Repo copy: `shot_main_reel-lair_20261114-072321-023_05.png` (640x360). The stone corridor, a wall
torch and the machine's sign/tray band fill the frame.

The game's own third-person mode was tried for a wider frame (`pov.request` = `on`): the camera
rig collapses into the player model in this corridor (capture is the inside of the head), so the
attempt was reverted with `pov.request` = `off` (`[pov] third-person OFF (was ThirdPerson)`).

## State left

Alive (pid 29384, client 640x360), `Lair Of Occultists Map Scene`, `mapRun=True`, player at
`(-49.40, 0.58, -33.57)`, reel at `(-49.37, 0.567, -32.42)` yaw -178.5, first-person (game
default), no pickers or debug overlays, no request files left in `BepInEx/screens/` (two stale
ones from Nov 12/13 — `solver.request`, `^$.request` — were removed).
