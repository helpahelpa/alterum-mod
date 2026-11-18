# Stage 3B — host-authoritative Reel of Fortune

Code (all in `tracks/merged/mod/AlterumModelSwap.Hot/`): **`ReelNet.cs` (new)**, `ReelPlacements.cs`
(authority-aware spawn + publish), `LobbyProp.cs` (request-driven host spin, client playback),
`ReelLogic.cs` (actor-parameterised pull, outcome capture, read-only evidence hooks),
`RemoteModels.cs` (proto 3, reel frame dispatch, mod-peer fan-out, late-join snapshot).
`AlterumModelSwap/` and `AlterumModelSwap.Contracts/` were **not** touched; the wire additions live in
the hot `NetKind.App` payload, so `NetProtocol.Minor` and the 18-byte envelope are unchanged and no
bridge restart was needed.

Evidence: `A-stage3b.log`, `B-stage3b.log` (the same sessions, tag-filtered), `B-placements-orig.json`
(client's placements file before the override test).

## Wire (proto 2 → 3; two verdicts only, as before)

`NetKind.App` payload, type byte first. Types 1–3 are stage 2's `hello`/`state`/`emote`, unchanged.

| # | msg | layout |
|---|---|---|
| 4 | `spin` | `[04][spinId:u32][actor:u64][startAt:f64][dur:f32][fromCell:u16][targetCell:u16][composeSeed:u32][shuffleSeed:u32][mask:u8][counts:u8 ×5][idLen:u8][machineId]` |
| 5 | `settle` | `[05][spinId:u32][actor:u64][kind:u8][itemLen:u8][itemId][count:u8][hpLost:i16][textLen:u8][text]` |
| 6 | `interact` | `[06][requestId:u32][sceneEpoch:u32][idLen:u8][machineId][heldLen:u8][expectedHeldItemId]` |
| 7 | `verdict` | `[07][requestId:u32][status:u8][reason:u8][spinId:u32][textLen:u8][text]` |
| 8 | `place` | `[08][epoch:u32][rev:u32][mapLen:u8][mapKey][count:u8]([idLen:u8][id][x,y,z,yaw:f32])*` |
| 9 | `placeReq` | `[09]` (client asks for the placement snapshot) |

`status` = accepted/settled/rejected; `reason` = wrong-item, out-of-range, busy, stale-epoch,
forbidden-item, no-item, ineligible, unknown-machine, duplicate, malformed, internal.
A proto-2 peer is still classified vanilla/MISMATCH by the existing handshake (no negotiation).

Identities: `sceneEpoch` is **our counter for "this map entry"** (bumped once per host map generation),
never a Unity scene handle. The map key is the game's own `GameMapData|scene` identity that
`ReelPlacements` already stores under. A machine is named by its placement name (`lair-1`), which the
host broadcasts, so neither side invents an id and no index or handle crosses the wire.

## Authority model

* **Host** (Netcode server) owns placements and settlement. On map entry `ReelPlacements` spawns from
  its own file as before, then publishes `place` (epoch+1, rev 1) to every mod-confirmed peer. A late
  joiner gets the same frame in the handshake path (`ReelNet.OnPeerMod`); a reloaded client asks with
  `placeReq`. A host that moves a machine bumps only `rev`.
* **Client** renders the host's anchors and *nothing* from its local file — `TickClient` never calls
  `AllOn()`, and logs which source won (`authority=host … local-file-entries-not-used=N`). In solo play
  (no session) the file is still the source (`authority=local(solo)`), so solo is unchanged.
* **Settlement**: the client sends exactly one `interact` and never a result. The host validates, runs
  the existing spin/settle code against the *requesting* player, and broadcasts `spin` (deck seeds +
  landing cell + timing) then `settle` (the same result line the local path shows). The client applies
  nothing: `LobbyPropRuntime.RemoteSpin` recomposes the deck from the two seeds and animates to the same
  cell; `RemoteSettle` only displays the host's text, and only for the actor.
* **Host-local pulls** keep the local path and also broadcast (`spinId` from the same counter), so a
  host pull animates on the clients identically.
* **Rejection of forged frames**: `spin`/`settle`/`place` arriving at the server from a client are
  dropped and counted (`IGNORED forged settle … nothing applied`); at a client, anything not from
  `ServerClientId` is dropped.
* **Ledger** per `(client, requestId)`: `accepted → stake consumed → settled`, 60 s TTL. Duplicate →
  the stored status is re-sent (`no second stake`); stale epoch, busy, wrong held item, out of range,
  not facing, dead/absent actor and the quest-item exclusions reject before anything is consumed.

## Assertions

1. **Both players see the same two reels at the same anchors — PASS.** Host `publish … epoch=1 rev=1
   [lair-1@(-34.35,0.58,-2.48) lair-2@(-113.97,4.66,63.55)]`; client `placements from host … epoch=1
   rev=1 … source=host (map entry)` → `spawned=2 … authority=host epoch=1 rev=1 names=[lair-1,lair-2]`.
   **Local file does not override — PASS.** B's file was edited to three anchors at (10,0,10),
   (20,0,20), (30,0,30), the client's module reloaded mid-map: it asked for a snapshot and spawned
   exactly the host's two anchors — `waiting for the host's placements (local file has 3 anchor(s) …
   they are NOT used in a session)` then `spawned=2 … local-file-entries-not-used=3`. Host-side move
   (`placeat=0,lair-2`) came through as `rev=3` on the client (same position), i.e. revisions propagate.
2. **Client pull settles host-side once — PASS.** Client `request id=1 machine='lair-1' held='healthflask'
   epoch=1 bytes=28 -> server=0`; host `range ok dist=2.49 facing=0.603`, `ACCEPT id=1 client=5 …
   consumed=1`, `settled card=16 type=item id='axe_tactical'`, `settle card=16 … granted='axe_tactical'
   n=1 consumed=1 inventory=0->1 delta=1 ok=True`, `settle spin=1 actor=5 … -> 1 peer(s)`,
   `verdict id=1 -> client=5 status=settled`.
   Client-side evidence: before `inv` = `held='healthflask' count('healthflask')=1`; after =
   `held='axe_tactical' count('healthflask')=0 count('axe_tactical')=1`, `hp=100/100`. Client log:
   `request 1 ACCEPTED … spin=1`, `spin 1 animate machine='lair-1' actor=5 (you) … target=16 type=item
   code=ATA`, `spin 1 deck cells=64 … matches the host's`, `result text='You won 1 x Axe tactical'`,
   `request 1 SETTLED … (applied by the host; this client applied nothing)`. One spin per request:
   exactly one `spin start` and one `settled card` line for spin 1.
   **Duplicate — PASS.** `pull2@1` sent byte-identical frames twice: `duplicate request id=4 from
   client=5 … existing status=accepted … spin=2 (no second stake)`, one `ACCEPT`, one settle.
3. **Host pull — PASS.** `spin start trigger=request spins=1 machine='lair-1' spinId=1 actor=0 …`,
   `settled card=50 … id='throwingdagger_rusty-reaper'`, `granted … inventory=0->1 delta=1`, and the
   client animated it (`spin 1 animate … actor=0`, `settle … -> shown only to the actor`). The host's
   `[reel:inv]` shows the stake taken and the grant.
4. **Rejections — PASS.** wrong item: `reject id=2 … expected='torch' actual='axe_tactical'`;
   stale epoch: `reject id=3 … request=0 current=1`; busy: `reject id=7 … reason=busy` (second request
   inside the spin window); out of range: `reject id=1 … dist=10.42 limit=2.80 at=(-32.99,4.58,7.57)
   look=(-34.35,2.18,-2.48)`; forged result: `IGNORED forged settle from client=5 bytes=48: only the host
   settles (nothing applied)`. Every rejection reached the client as a verdict with a reason and a text,
   and consumed nothing.
5. **Late join mid-map — PASS (as a mid-map rejoin).** The reloaded client had no placements, asked
   (`asked the host for the placements snapshot ok=True`), the host answered
   (`placements -> client=5 (requested) … epoch=1 rev=1 machines=2 bytes=114`) and the client rebuilt at
   the host's anchors. Its model/hat state was re-established by the normal handshake (state replay).
6. **Stage-3A gaps — PARTIAL.**
   * *Lobby → map with two players — PASS.* The mod's own one-player switch (via its widget path so the
     pause-menu sync cannot revert it) gave `[lobby:one] required=1 ready=1 min=4 canStart=True`, then
     `startmap` moved the server and the client followed: both logged
     `scene 'Lobby' -> 'Lair Of Occultists Map Scene'`, both spawned two reels, and the session survived
     the transition (`session up … gen=N`, `handshake … mod=YES`). Models/hats did **not** survive as a
     *body*: on entering the map the local model reverted and was re-announced
     (`local model -> '<native>'`), so the client had no custom body to hang A's hat on; the hats/toggle
     state itself stayed correct (`state … cosmetics=[builtin.partyhat] chest=on hips=off`). This is the
     wardrobe's own lobby-only rule, not a stage-3B regression — it is the reason 6's "survive the
     transition" is only **PARTIAL**.
   * *Model change mid-emote — NOT RUN* (no request path selects a wardrobe model without the UI, and
     the time box ended; the stage-3A code path — body teardown → `RemoteEmotes.Drop` → hat/enhancement
     release — is unchanged).
   * *Vanilla peer — NOT RE-RUN* (would need a third, stripped instance; the classification code and the
     "never address a vanilla peer" rule are unchanged, and `SendToModPeers` only addresses
     `Handshake.Mod`).
7. **Hygiene.** `grep -c SWAP` = 0 on both logs. Deck `cells=64 blank=22 item=21 damage=10 debuff=6
   double=5` on both, and the client's recomposed deck logged `matches the host's` on every spin. Two
   reels per map: `saved=2 spawned=2 failed=0 expected=2 live=2` on the host, `spawned=2 live=2` on the
   client. Random-on-join files were left in place. Native E prompt: both sides build the game's own
   lookable/interactable (`[reel:act] built lookable fields … `, `build prompt … resolved='interact'`);
   the client's E is routed to `InteractRequest` (the file-driven `pull@1` exercises exactly that call).
   **`[Error]` = 1 on each log**, and it is not ours: `QFSW.QC.QuantumConsole.OnDisable` →
   "Could not deregister object 'Quantum Console (SRP) Canvas'" fires once on the map transition in the
   game's own dev console. Zero errors from the reel/net/model paths.

## Unproven / open

* Model change while an emote plays (6b), and the vanilla-peer isolation re-run (6c).
* Client E key pressed by hand — the request path under it is proven (`pull@1`, identical encoder).
* Damage/debuff outcomes for a *remote* actor: only item outcomes were forced (damage needs a Health
  write on the client's player, debuff needs the effect family; both use the same resolved-player path
  the item grant proved, but they were not observed end to end).
* `settle` text for a non-actor peer is deliberately not shown; that is a choice, not a test result.
* Final state: the pin (pid 10672) is running **in the Lobby** with the final hot dll
  (`BepInEx/hot/AlterumModelSwap.Hot.dll` md5 `e3c43df6…`, identical to the built file; `generation 1`).
  `C:\AlterumB` (pid 91480) ended in the Menu — its join click sequence did not fire after the restart
  and the `join=` hook did not pick up, so it is not in the lobby.
* B's local placements file was restored from `B-placements-orig.json` after the override test.
