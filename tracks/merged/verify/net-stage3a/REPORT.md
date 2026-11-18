# Stage 3A — cosmetics, enhancement toggles and emotes

Code (all in `tracks/merged/mod/AlterumModelSwap.Hot/`): `RemoteModels.cs` (wire + per-remote
reconcile), `CosmeticsRuntime.cs` (remote attach path), `CustomEnhancements.cs` (external bodies),
`RemoteEmotes.cs` (new), `SahurRig.cs` (lease restore), `ModelSwapRuntime.cs` (two hook lines),
`PosePlayer.cs` (`Seek`). `AlterumModelSwap/` and `AlterumModelSwap.Contracts/` were **not** touched:
the bridge, the 18-byte envelope and `NetProtocol.Minor` are unchanged.

## Wire format

All inside a `NetKind.App` payload (≤1024 B). `ProtoVersion` 1 → **2**; `hello` and `state` keep their
shapes, the new fields are appended / new message types added, and a proto mismatch is the existing
vanilla verdict (two states only — no negotiation, no capability gating).

| msg | layout |
|---|---|
| `hello` 1 | `[01][proto:u8][caps:u8]` (caps 0x07 = mod|cosmetics|enhancements; advertised, never gated on) |
| `state` 2 | `[02][origin:u64 LE][flags:u8][slugLen:u8][slug][count:u8]([idLen:u8][id])*` |
| `emote` 3 | `[03][origin:u64 LE][kind:u8][seq:u32 LE][t:double LE][idLen:u8][id]` |

* `flags` = bit0 chest, bit1 hips — the wearer's two booleans.
* The cosmetic list is the **equipped set** (bounded: 8 ids, 48 B each), never a single hat and never
  a transform. Ids are the pack's cosmetic slugs (`builtin.partyhat`, …).
* `emote` is an event: kind 0 = stop, 1 = start; `seq` orders per origin (a stale stop cannot undo a
  newer start, a duplicate start does not re-phase a running clip); `t` = start instant on the game's
  own `NetworkManager.ServerTime.Time` when the session exposes it (`clock=server`), else the
  process's monotonic unscaled time (`clock=local`).
* Relay: the server forwards the origin's **exact cached payload** (state for late joiners, event
  frames to every mod-confirmed peer, never back to the emitter). Vanilla peers are still never
  addressed — the handshake verdict is unchanged.

## How the presentation is built

**Hats.** `RemoteModels.RuleTick` compares the wearer's set and the remote head-bone pointer with a
`CosmeticsRuntime.RemoteHats` lease. On change it disposes the lease and re-attaches the whole set, so
there is exactly one attach per (set, head) — no duplicates by construction. Placement is
`head bone × ModelRoster.AnchorFor(observer's roster entry for the advertised slug)` — the anchor comes
from our own measured table, computed locally from our own model data; nothing positional crosses the
wire. Remote cosmetics are built on layer 23 (the game's own third-person mesh layer) and registered
in `CosmeticMarkers`, so the mirror/body sweeps skip them and disposal unregisters them. The local
POV/mirror layer (6) is untouched. The observer's local `_instances` list is never involved, so
`ApplyScreenState`, the POV mask and the native-hide path cannot touch a remote hat. No measured anchor
for a slug (e.g. a roster entry added without one) logs `no-anchor` and attaches nothing rather than
guessing.

**Enhancements.** `CustomEnhancements.ApplyExternal` runs the same `ApplyOne` writer (same
`ModelEnhancementProfiles` table, same immutable-original re-derivation) with the search root switched
to the remote model. Records are keyed by the body renderer's pointer and flagged `External`, so:
apply/release cannot collide with the local body, a rebuilt body is a new record (pointer identity is
checked, not just the key), and `ReleaseExternal` runs before the body is destroyed. Rigged remote
bodies now get their **own clone** of the bake mesh (`Remote.BodyMesh`, destroyed with the lease),
because the rigged bake mesh is shared through the asset cache — without the clone a remote chest
deform would also reshape our own body on the same slug. Rigid remote bodies already use
`SahurModel.BuildPreviewBody`'s clone. Only vertices and bone `localScale` change; no collider,
hitbox, stat or gameplay dimension is touched.

**Emotes.** `RemoteEmotes` owns one `PosePlayer` per remote client, bound through
`player.GetRefs<PlayerModelReferences>()` → `ThirdPlayerModelPersonAnimancerController` →
`CurrentPersonAnimancerComponent` — i.e. the remote actor's own animancer, the same lease the local
emote takes (`DoingSomethingHandler.StartDoingSomething(key)` +
`DisableAnimancerAndAnimator(key)`) and the same write phase (`RemoteModels.LateTick`, after the
ordinary skeleton writers, before the mirror). A start computes `age = now - t` on the shared clock and
calls `PosePlayer.Seek(age)`: the clip is sampled at that position with no fade, so a late/relayed
start is in phase instead of restarting at frame 0 (loop clips wrap, one-shots clamp just short of the
end). A start that arrives before the body exists is remembered and started in phase when the body
appears. Stop conditions: the wire stop, movement > 0.12 m, the game re-enabling the animancer while we
hold the lease, dead/replaced skeleton, body teardown (model change, native advert, peer leave, scene
change), and module dispose; each hands the pose back (`PosePlayer.Stop` → fade-out → lease release →
`Animator` re-enabled) and the remote rigged/rigid body follows because both are driven from the game
skeleton the lease writes.

**DoingSomethingHandler audit (observers never initiate gameplay).** Decompiled
`Gameplay.PlayerSystem.DoingSomethingHandler` (`refs/Assembly-CSharp.dll`, ilspycmd) has exactly:
`StateChanged` (`Action<bool>`), `_isDoingSomethingSettable` (`SettableSafeVariableWrapper<bool>`),
`get_IsDoingSomething`, `StartDoingSomething(Object)`, `StopDoingSomething(Object)`, `OnSafeValueChanged`,
`Awake`, `OnDestroy`. `SafeVariables.dll` has no network/RPC/IsOwner member at all — it is a local
modificator-key boolean wrapper. `ModelPersonAnimancerController.DisableAnimancerAndAnimator` /
`EnabledAnimancerAndAnimator` are local component switches. The remote path calls nothing else: no input
injection, no RPC, no NetworkVariable, no damage/hitbox/interaction. The binding logs this audit line
once per actor.

## Assertions (two instances: pin = server, `C:\AlterumB` = client)

Evidence: `A-stage3a.log`, `B-stage3a.log` (tag-filtered), `captures/` in this folder.

1. **Two hats on A's remote head, anchored on A's model — PASS.**
   A: `[cosmetic:request] equipped=2 changed=1`; B: `recv client=0 cosmetics=[builtin.partyhat,builtin.tophat] … bytes=56`,
   `[cosmetic:remote] attach client=0 id=2 attached=2 renderers=4 set=[builtin.partyhat,builtin.tophat] layer=23
   anchor('sahur-roblox') offset=(0.29,0.00,0.00) scale=0.5300 head='head'`.
2. **Unequip one reconciles, no duplicates — PASS.** B: `[cosmetic:remote] detach client=0 instances=2`
   then `attach client=0 id=1 attached=1 renderers=2 attaches=3 detaches=2`; the marker count returns
   to 3 (`markersLeft=3`). No attach happened without its detach in any step (attaches−detaches = live
   instances in every line).
3. **Four chest/hips combinations reach B, B's own body unaffected — PASS (view), PASS (isolation).**
   B received and re-derived all four, exact values from the immutable originals:
   `chest=off hips=on maxDelta=0.0482` → `chest=on hips=off maxDelta=0.0738` → `chest=off hips=off
   maxDelta=0.0000` → `chest=on hips=off 0.0738`. B's local body is native (`localModel='<native>'`)
   and has no `[enh:custom]` record without `(remote)`; the remote record deforms only
   `BodyMesh`/the preview clone.
4. **A plays an emote, B plays the same clip in phase, stop restores — PASS (core).**
   A: `sent emote start id='Salute' seq=1 t=4060.349 clock=server … -> [5]`;
   B: `[swap:emote] START client=0 id='Salute' seq=1 age=0.134s seek=0.134s dur=1.783s native=0` and
   `[emote] seek applied clip='Salute' t=0.134s … (late arrival: no frame-zero restart)`;
   then `sent emote stop` → `STOP client=0 id='Salute' seq=2 why=peer-stop lease=False`.
5. **Model change while wearing hats and mid-emote — NOT RUN.** No request path selects a wardrobe
   model without the UI (the `select`/`index` keys live behind a driver that was not part of this
   run), and the time box ended first. Code path: any body teardown calls `DisposeBody` →
   `RemoteEmotes.Drop` → `ReleaseEnhancements` → `ReconcileHats("no-body")` → rig detach, then `Build`
   re-attaches hats/enhancements in the build frame; `HatHeadPtr`/`BodyMesh` are invalidated with the
   body, so a rebuild cannot reuse a stale head or mesh.
6. **Scene change (menu→lobby) — PASS, incidentally.** Both instances transitioned Menu→Lobby after
   the last deploy: `[swap:net] scene 'Menu' -> 'Lobby': dropping every remote body, states are kept`,
   one rebuild plus one `[cosmetic:remote] attach … why=body-built` per client (no duplicate attach).
7. **Lobby→map with 4-player minimum — NOT RUN.** The one-player start path exists
   (`[lobby:cfg] onePlayer`, `[lobby:one] before/after[required=4 ready=…]`), but the pin is in a lobby
   with one peer and the time box ended before the readiness RPC sequence could be driven. Hats,
   enhancements and emotes are per-state, not per-scene: a scene change drops bodies and rebuilds from
   the cached state, and the map transition is the same path as 6. The two map reels were not exercised
   in this session; `[reel:pool] … pool=51/51` is the only reel line in the run.
8. **Vanilla peer — NOT RE-RUN.** Unchanged from stage 2 (proto mismatch → `mod=NO`, never addressed,
   native body); this run had two modded peers, so there was nothing to observe. The new message types
   are only sent to `Handshake.Mod` peers, and a proto-1 peer is classified vanilla by the same
   handshake code.

Captures: `captures/B-sees-A-two-hats.png`, `captures/B-sees-A-hats-wide.png` — **both obstructed**
(A's avatar is inside the lobby tent; the same framing problem stage 2 recorded). They are kept as
evidence that the shot path ran (`shot client=0 … hook=game-camera-to-rt … mask=0x4DDB8F87`), not as
hat evidence. Hat evidence is the attach/anchor log lines above.

## `DetachExternal` — "restored 0 body renderer(s)"

Two causes, both now handled and both logged separately:

1. **The lease only owned what it changed.** `HideBody` set `Hidden=true` only on a renderer it flipped
   enabled→disabled. A lease that snapshotted an already-disabled renderer recorded `Hidden=false`, so
   `Restore` skipped it and printed 0 while the renderers it did own came back. This is the "lease that
   reports restoring nothing on one path" — it left the renderers disabled with no owner. Fixed:
   `HideBody` now marks every disabled entry (and logs `hid N, tracked M for restore (incl. ones
   already disabled)`), and `Restore` is unconditional per entry, restoring the recorded `HadEnabled`
   (a no-op when it was already false).
2. **Dead wrappers.** On `peer-left` the game destroys the remote player object before our 1 s stale
   timeout, so the `Renderer` wrappers of that body are fake-null and cannot be restored. `Restore` now
   counts them instead of silently reporting a smaller number:
   `restored R already=A lost=L untracked=U` (+ an explicit "owner object was destroyed" note).
   This is the asymmetry versus `module-dispose` (the player still exists there), not a leak: the
   objects no longer exist in the scene.

Additionally the local rig could snapshot a **remote** player's renderers: `SahurRig.FindLocalBody`
accepted any active `ThirdViewPlayerModel` when `CurrentPlayerProvider.CurrentPlayer` was null (a
client can hit this), so the local body hide hid another player's body — that produced "hid 8" for a
body it did not own, and a later restore that owned nothing. Fixed: the local rig requires a resolved
owner and `MirrorPlayerVisibility.Under(model, owner)`, and `HideBodyBeforeBuild` skips renderers a
remote lease owns (`RemoteModels.IsLeased`). `BodyAlive` no longer trusts the pose-snapshot struct
(which made a healthy remote body look dead and rebuilt it every heartbeat); the built body object is
tracked on the `Remote` instead.

## Regressions / state

`[Error]` = 0 on both logs, `grep -c SWAP` = 0 on both. Pin left running (pid 40440) in the **Lobby**
with one peer, hot dll `273aab93…` deployed (`[hot] generation 6 active`); B (pid 31416) still up. The
reel deck/`cells=64` and two-reels-per-map checks were not exercisable without a map scene (assertion 7).

## Unproven

* assertions 5, 7, 8 (reasons above); the map reel path was not re-measured.
* `emote` framing when a start arrives before the remote body exists (code path + log line exist,
  not exercised live in this run).
* native (`native:<Name>`) remote emote ids: resolved from the observer's own graph/loaded clips with
  the hands mask; only the LC (`Salute`) path was run live.
* `clock=local` fallback: without a shared server clock a late start cannot be aged, so it plays from
  the start (guarded and logged); this run's emote used `clock=server`.
