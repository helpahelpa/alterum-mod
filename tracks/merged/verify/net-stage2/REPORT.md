# Stage 2 — player A's custom model on player B

Code: `tracks/merged/mod/AlterumModelSwap.Hot/RemoteModels.cs` (new), plus one call each in
`SahurWardrobe.SyncSelection` (publish local slug) and `ModelSwapRuntime.Update / LateUpdate /
Dispose` (tick, pose, teardown). Nothing under `AlterumModelSwap/` or
`AlterumModelSwap.Contracts/` was touched; the bridge contract is unchanged.

Evidence: `A-net.log`, `c-net.log` (filtered game logs), `captures/` in this folder.

## Wire format

Inside a `NetKind.App` payload of the permanent bridge (18-byte `ASW1` envelope, 1024-byte ceiling).

| msg | bytes | layout |
|---|---|---|
| hello | 3 | `01 proto:u8 caps:u8` (proto=1, caps bit0 = mod present) |
| state | 11+len | `02 originClientId:u64 LE flags:u8 slugLen:u8 slug:ascii` |

Only a stable slug (`sahur-classic`, `sahur-roblox`, `golubiro`, …) or the empty string ("native")
ever crosses the wire. No mesh, texture, bone map or hash.

## Handshake

* server: hello to every connected peer at session start and on peer arrival (2 s retry, max 3),
  verdict after 3 s without an answer → `handshake client=N mod=NO … -> native body, no mod traffic to this client` (once).
* client: hello (and its state) to the server only — the bridge refuses any other target — and
  only sends state after the server answered.
* every state frame carries its origin client id because a client's frame reaches other clients
  only through the server, which relays it to mod-confirmed peers.

Log shapes:
```
[swap:net] session up epoch=… gen=… local=0 role=server proto=1 mod=yes localModel='sahur-roblox'
[swap:net] handshake client=4 mod=YES proto=1 caps=0x01 (answered) -> custom bodies enabled
[swap:net] handshake client=2 mod=NO after 3.0s (2 hello(s), no answer) -> native body, no mod traffic to this client
[swap:net] sent model='sahur-roblox' client=0 -> [4] (heartbeat)
[swap:net] recv client=4 slug='tc2-sniper' from=4 flags=0x00 bytes=…
[swap:net] relay client=4 slug='sahur-roblox' -> [1]
```

Sending is coalesced and rate-limited: a change is queued once, at most four sends per second,
plus a 5 s heartbeat; a burst of wardrobe clicks collapses to the latest slug. Re-published on
handshake, on generation change, on scene change (state kept, body dropped) and on the heartbeat.

## How a remote body is resolved and built

`Resolve(clientId)`: scans live `Player` components, matches `OwnerClientId == clientId`, then takes
that player's own `ThirdViewPlayerModel` (active first). Fallback only: the measured scene-name
marker `… | Owner: N` (digit-boundary checked). Never `Owner: 0`, `Camera.main` or the local
provider.

`Build(remote, spec)`:
* rigged → `SahurRig.AttachExternal` (own 71-bone skeleton, delta-retarget; its lease hides the
  native renderers itself), posed per frame in `LateUpdate` via `SahurRig.PoseExternal`;
* rigid → native renderers leased (`enabled=false`, snapshot restored on teardown) and
  `SahurModel.BuildPreviewBody` (bindpose from the native mesh, cloned mesh, 1.0 weights).

One `Remote` per client id: `Model/Root`, `Rig`, `Rigid`+mesh, hidden-renderer list, build/dispose
counters. Teardown on: slug change, native advert, peer leave, scene change, model destroyed
(1.5 s grace), session end, module dispose. Registration of the model objects uses the existing
`CosmeticMarkers` so the mirror/body sweeps treat them as ours.

## Assertions

1. **A selects rigid (`Sahur (Classic)`) → B shows it** — PASS.
   `recv client=0 slug='sahur-classic'`, `resolve client=0 player='LobbyPlayer_Separated | Owner: 0' … layer=2`,
   `lease client=0 hid 8 native renderer(s)`, `BUILD client=0 slug='sahur-classic' kind=Rigid`,
   whole-body capture `captures/c-sees-A-rigged-close.png` (see note below on A's position).
2. **Reverse roles** — PASS. A: `recv client=3 slug='golubiro'`, `resolve … Owner: 3`,
   `hid 8 native renderer(s)`, `BUILD … kind=Rigid`; capture `captures/A-sees-c-rigged.png`.
3. **Rigged model observed remotely** — PASS. A switched to `sahur-roblox`; c:
   `[sahurrig:preview] attached label='remote:0:sahur-roblox' bones=71 hiddenHost=8`,
   `BUILD client=0 slug='sahur-roblox' kind=Rigged`, `poses=3318` on A's side for client 3.
4. **Both on different custom models** — PASS (04:29:07–04:31): A=`sahur-roblox` rigged,
   c=`sahur-classic` rigid at the same time; each side built the other's kind (rigid on A's view of
   c, rigged on c's view of A). Later both happened to converge on the same entry.
5. **Peer leaves → native restored, no orphans** — PASS with one caveat. Rigid lease:
   `restored 8 native renderer(s)`; both peer leaves:
   `disposed client=N … why=peer-left builds=1 disposes=1 hiddenLeft=0`, then
   `census ours=2 [SahurRigged_Body=1 SahurRigged_Skeleton=1] remotes=[]` (only the local body).
   Caveat: for the *rigged* lease the detach logged
   `[sahurrig:body] restored 0 body renderer(s)` on `peer-left`; the same lease restored 8 on
   `module-dispose`, and the rigid lease always restored 8. The despawned player took its
   renderers with it, so nothing was left hidden in the scene, but the count is unexplained.
6. **Scene change keeps presentations correct** — PARTIAL. Menu→Lobby was exercised repeatedly
   (`scene 'Menu' -> 'Lobby': dropping every remote body`, then one rebuild per client). A
   lobby→map run is impossible in this session: both shipped maps demand 4+ players
   (`[spawn:map] min=4` / `min=5`) and the lobby had 2.
7. **Hot reload re-establishes without duplicates** — PASS on both sides.
   `disposed client=3 … why=module-dispose builds=3 disposes=3 hiddenLeft=0` → `[hot] generation 2
   active` → exactly one `BUILD … builds=1` (and one `attached … builds=1`). Same on c.
8. **Vanilla peer** — PASS. A vanilla `C:\AlterumB` (`BepInEx/plugins` + `hot` moved aside, joined
   with `+connect_lobby`) produced
   `handshake client=2 mod=NO after 3.0s (2 hello(s), no answer) -> native body, no mod traffic to this client`;
   no frame was ever addressed to client 2 (`grep target=2` = 0) and A built no body for it.

## Captures

The desktop was locked for the whole session (`LogonUI` running), so `ScreenCapture` returned a
stale frame (`captures/c-camera-stale-screenCapture.png`). The evidence path therefore renders the
game's own camera object to a RenderTexture, with the same camera, fov, near/far and culling mask;
the log records the pose. Two obstacles limited framing: A's avatar stood inside the tent, and shots
from B's side were blocked by tent walls.
* `A-sees-c-rigged.png` — A's camera, c's rigged Sahur with bat, clean.
* `c-sees-A-rigged-close.png` — c's camera at 1.2 m, A's rigged body through the tent window.
* `c-sees-A-tent-obstructed.png` — same shot at 2.6 m, body behind the tent wall (kept as evidence
  that the framing, not the body, was the problem).

## Constraints / regressions

Final pin state: pid 40440, `C:\AlterumPinned`, hot dll md5 `0d844c4a…`, in the **Lobby**, no peers,
`remotes=0`, `[Error]`=0, `grep -c SWAP`=0, `players=0`, reel pool `51/51`.
The reel deck/`cells=64` and two-reels-per-map checks need a map scene, which this 2-player session
could not start; the reel code path was not modified.

## Unproven

* lobby→map with two modded players (map minimum 4 players).
* Whether the rigged lease's `restored 0` on `peer-left` hides a leak (the player object despawns
  with it).
* A/B model changes after the burst test under a long session; no soak was run.
