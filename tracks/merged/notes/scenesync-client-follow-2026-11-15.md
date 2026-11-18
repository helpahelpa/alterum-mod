# Lobby -> map: the client follows the host (no defect found, 2026-11-15)

The bench report was "host reaches Lair, client stays in the Lobby". Two clean two-peer runs
disagree, and the `[scenesync]` trace (added to the hot module for this) says why: the client is
in the load from the first frame of the transition.

What the trace shows on a host-initiated map load (both runs, `ui.request: startmap`):

```
host   client finished preload -> FinishedPreLoadServerRpc: server=True  clientsLoading=[0,1]
host   ShouldBlockBarrier: block=False clientsLoading=[0,1]
host   [scene] -> 'Lair Of Occultists Map Scene'
client [scenesync] client finished preload -> FinishedPreLoadServerRpc: server=False clientsLoading=[]
client [scene] -> 'Lair Of Occultists Map Scene'
```

`clientsLoading` is `NetworkSceneLoader.ClientsLoadingNetworkScene`, so `[0,1]` *is* the client set
the transition asked to carry, and the client's own `FinishedPreLoadServerRpc` line is its side of
the barrier. Both peers then spawn both reels (`[reel:spawn] spawned=2 live=2` on each).

## The two hypotheses, settled

* **`StartWithOnePlayer=true` stranding the client** - no. The bench start runs with
  `canStart=False readyCount=0` (`required` forced to 1, nobody ready) and the client is still in
  `clientsLoading=[0,1]`. Readiness does not build the client list, so the one-player path cannot
  drop a peer from the load.
* **`startmap` skipping normal client scene-sync** - no. `ReelLogic.StartMap` only forces readiness
  and then calls the game's own `GameSceneStarter.StartTransitionToGameScene()`, which is the same
  call the lobby countdown makes. No loader call is bypassed.

## If it is seen again

The window between the host landing and the client landing is ~35-45 s (preload, fade, then the
client's five `LocalScenePostLoadable`s). A client looked at inside that window is still in the
Lobby and looks stranded. Two things separate a slow arrival from a real failure, both now in the
log:

* `[scenesync] AbortGuardedLoad: reason='...'` on the client - a genuine aborted load;
* `[scenesync] connection:` losing 1 - the client was dropped, which is a known standing issue
  ("the joining client gets dropped after roughly 1-2 minutes", workspace README) and is the one
  cause that legitimately leaves the client behind.
