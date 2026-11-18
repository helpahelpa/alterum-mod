# Hat seat on custom bodies — measured, not nudged (2026-11-15)

Hot tree only (`AlterumModelSwap.Hot/CosmeticsRuntime.cs`); `AlterumModelSwap/`,
`AlterumModelSwap.Contracts/`, the pristine copy, `C:\Interop`, `C:\AlterumTests` and the Steam
install untouched. Final hot dll **`f076564a1aa111f9a20f209983882820`** on the pin and on
`C:\AlterumB` (both live, generation reloaded without a restart until the session reset).

## What was actually wrong

The per-model `HatAnchor` table was measured against **one** skeleton context and applied as a
fixed head-bone-local offset. Two things move under it:

1. **The head socket's lossy scale.** Measured live: the same body's `head` bone reports
   `lossy=1.0` on some bodies and **`1.5376`** on others, and the *same* body changes during/after
   spawn. `Attach` converts the stored local offset to world by dividing by the socket's current
   lossy, so an offset fitted at 1.0 is multiplied by 1.54 once the socket settles — exactly the
   "0.2–0.4 m above the head" in the report's screenshots. Measured drift: host +0.293 m, client
   +0.112 m.
2. **The stored box itself is wrong for the map skeleton.** Re-fitting the table at the settled map
   scale does not rescue it: the anchor is then sunk **-0.079 m** (Saxton) / **-0.072 m**
   (Soldier). Lobby, same build: **-0.083 / -0.068**. The table is a lobby-donor measurement and
   the map draws a different skeleton/scale, so no per-context constant can be right for both.

The old "rig-head − game-head" shift never applied on these bodies: for a rigged body the rig head
*is* snapped to the game head bone, so the delta is ~0; for a rigid body there is no rig head at
all. That is why it was "too small to correct it".

## Rule adopted: (c) — compute the seat from the worn model's own head at bind/refit time

`ComputeFit` now measures the **geometry that is drawn**: every vertex weighted ≥ 0.5 to the worn
model's own head bone (`SB_head` / the rigid `head` region bone), skinned to where it is *now*,
expressed in the same hat axes `MeasureHeadMesh` uses for a native head, and scaled by the head's
own lossy scale (the native path already did that multiply). It then runs the **same** placement
formula (`ApplyMeasuredBox`) — width/top/cx/cz → scale/shift — so there is one formula, two
sources:

| path | when |
|---|---|
| `measured-worn` | a custom body is worn and its head-weighted mesh is readable (**default**) |
| `stored-anchor` | the worn body's head mesh is unreadable: the per-model table, unchanged (incl. the rig-head shift) |
| `measured-native` | no custom body: the game head mesh, as before |
| `neck-ratio` | last resort (unreadable mesh, no anchor) |

Two supporting changes, both required for the seat to stay put:

* **Refit on socket scale change.** `PollBind` treats a `head.lossyScale` change (>1 %) as a refit
  reason, next to a new body/head/worn spec (`[cosmetic:refit] head 'head' lossy … -> …:
  re-measuring the seat`). Without it the correct fit made at 1.0 drifts as soon as the socket
  reaches 1.54 — the measured **+0.017 → +0.293 m** drift observed in the map before this change.
* **One rule for the remote path.** `AttachRemote` no longer builds its own anchor target: it calls
  the same `BuildTarget(...)`, which measures the *remote* body's own head (the body the viewer has
  on screen) and only falls back to the table. The two paths cannot disagree by construction now.

Harness (documented, not shipped behaviour): `hatrule.request` = `anchor` forces the stored table so
before/after numbers come from the same measurement code; the file is deleted on pickup, default is
`measured`.

## Measured seat numbers (world metres, hat bottom vs worn-head top; + = floats)

Lobby, this session, same build, `partyhat` equipped, wardrobe `index=` switching:

| model | rule | head lossy | head top y | hat bottom y | **gap** |
|---|---|---|---|---|---|
| TC2 Saxton Hale | stored anchor (forced) | 1.5376 | 1.9365 | 1.8536 | **-0.083** |
| TC2 Saxton Hale | measured-worn | 1.5376 | 1.9359 | 1.9518 | **+0.016** |
| TC2 Soldier | stored anchor (forced) | 1.5376 | 1.9319 | 1.8641 | **-0.068** |
| TC2 Soldier | measured-worn | 1.5376 | 1.9316 | 1.9413 | **+0.010** |
| Golubiro (rigid) | stored anchor (forced) | 1.0 | 1.8990 | 1.9485 | **+0.049** |
| Golubiro (rigid) | measured-worn | 1.0 | 1.8985 | 1.9293 | **+0.031** |

Lair map (live two-player map session, both sides, head lossy 1.5376):

| model / side | before (shipped rule, fitted early → drift) | after (measured-worn) |
|---|---|---|
| host TC2 Saxton Hale | **+0.293** | **+0.017** |
| client TC2 Soldier | **+0.112** | **+0.008** |
| host's view of client's hat (remote path) | — | **+0.011** |
| client's view of host's hat (remote path) | — | **+0.017** |

Full log line per case: `[hatseat] when=fit|probe|remote … gameHead pos/rot/lossy … modelHead
pos … wornHeadTop(world.y) … hatRoot … hatBottom(y) … gap=±…m` — the game head bone world
position/rotation, the model's own head bone world position and the resulting hat root position are
all in it, as asked. `hatanchor.request` re-measures live and prints the same block (`when=probe`).

The remaining +0.01–0.03 m is the party hat's own geometry: its brim (the ball ring) is the lowest
*visible* part but not the lowest geometry in the prefab, so the renderer AABB bottom sits a
centimetre or two under the visible brim. The same prefab on the native path shows the same amount.

## Captures

`verify/hat-seat/`:

* `lobby-host-tc2-saxton-head.png` — host, pin, lobby, `TC2 Saxton Hale` + party hat, **seated on the
  hat brim** (measured rule). This is the money shot: before the fix the same body/hat pair showed
  the hat 0.2–0.4 m up.
* `map-host-tc2-saxton-body.png` — host in Lair, map session, back-lit body + hat (640×360).
* `map-client-tc2-soldier-side.png` — client in Lair, `TC2 Soldier` + hat, side view (poor light).
* `map-client-tc2-soldier-head.png` — client in Lair, head close-up. **Caveat:** taken mid-run
  before the refit-on-scale-change fix; it still shows a visible gap of ~0.05–0.07 m while the same
  frame's measurement says +0.009 m, i.e. the prefab-brim offset above, not a placement error. I did
  not get a post-fix map capture (map re-entry failed, see below), so this one is illustrative only.
* `lobby-host-native-control.png` — **not a usable control**: the owner's native body is not drawn
  by the game in the local view (only the mod body and the first-person arm are), so a native hat
  control needs a *remote* native body. Not obtained.

## Not closed (and why)

A native-row switch (`sahur.request on`) **inside the map** put the host into a per-frame body
rebuild loop (`TC2 Saxton` ↔ `Sahur (Classic)`) whose bone transforms diverged (game `hand_l`
x → −1.4e12, head `lossy` → 5.6e17). Both instances were restarted from scratch. Re-entering Lair
then failed: `spawn.request map=LairOfOccultists` selects the map and `ui.request req=1` reports
`canStart=True`, but the controller immediately recomputes `required=4 ready=1 canStart=False` and
the countdown never starts (`ui:req` ×3, `req=2` also tried). So the session ended in the **Lobby**,
not Lair — the pin is live on the final dll, wardrobe `TC2 Saxton Hale`, hat equipped, but the
"leave it in Lair" instruction is **not** met.

Consequently Task 3 items were not run in this pass:

1. **Client pull damage/debuff** — the grant path is now identified: the QC console has **no
   commands** in the pin (`pinned/QC-CONSOLE.md`), so `handhelds.to-inventory` is not available; the
   reel's own test hook is `net.request giveremote=<clientId>,<itemId>` → `DebugGivePlayer` →
   the game's `PlayerInventoryUtilities.AddItemOrDropIfNoSpace` on the *server* actor. That is the
   correct stake path, not `hold=`. Untested.
2. **Respawn survival** — not exercised. (No console kill command; the game's own death path or a
   lethal damage card is needed.)
3. **Vanilla-peer isolation** — not run. `C:\AlterumB\BepInEx\plugins` (1.7 MB) and
   `hot/b` (40 MB) are intact; the plan is still: stop B, move those two dirs aside, launch B
   vanilla with the same gbe identity, join, check native body / no hats / no emotes / no
   `[net:recv]` mod traffic on the peer, then restore.

Also new and worth its own fix: the native-row switch in a map must not rebuild forever. The
wardrobe's selection check ("selection is NOT 'Sahur (Classic)' -> body off") and the forced body
fight each other there.

## Hygiene

`[Error]` = 0 and `grep -c SWAP` = 0 on both current logs (lobby, after the reset). The map session's
hygiene figures (cells=64, one accepted `QuantumConsole.OnDisable` on transition, two reels per map)
are the ones in `verify/map-run/REPORT.md`; they were not re-measured after the reset. No request
files left behind; `hatrule`/`hatanchor` probes consumed.

## Files

* `mod/AlterumModelSwap.Hot/CosmeticsRuntime.cs` — `MeasureWornHead`, `TryWornHeadBone`,
  `ApplyMeasuredBox`, `BuildTarget` (shared local/remote rule), `LogSeat`/`LogRemoteSeat`
  (`[hatseat]`), refit-on-scale-change in `PollBind`, `hatrule.request` harness override.
* `ModelRoster.MeasuredHatAnchors` unchanged — it is the fallback, not the rule.
