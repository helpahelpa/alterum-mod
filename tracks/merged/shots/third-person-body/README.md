# Third-person body through the game camera — pixel evidence

Build: `tracks/merged/mod` (hot dll `d516818fabc897d8`, generation 22), hot-reloaded into install A
(`C:\Program Files (x86)\Steam\steamapps\common\Alterum`), private lobby, 640x360, headed. No restart.

## What changed

`MirrorPlayerVisibility` now admits the isolated body layer (**14, `Hitbox`**) to every screen
camera's culling mask while the game reports `POV.ThirdPerson`, and withdraws it otherwise. The bit
is written per camera, tracked, withdrawn before the mirror's own conflict check, and withdrawn on
every teardown (`Disarm`/`Dispose`/hot reload). See `BodyLayerCensus.cs` (census + guard),
`BodyScreenProof.cs` (the proof camera used below) and `MirrorPlayerVisibility.SyncScreenView`.

## Layer 14 census (the risk the task asked for)

```
[bodylayer:census] why=body-bound layer=14 ('Hitbox') liveBody=9 liveForeign=0 inactiveBody=0
  inactiveForeign=0 assetRenderers=0 collidersOnLayer=162 collidersUnderLocalPlayer=15
  bodyRenderers=['Hair_cap','Hair','Vest','Tie_Optimized','Shirt_Optimized_1','Sheriff_Head',
                 'Pants','Hands_Optimized','Boots']
[bodylayer:census] no foreign renderer on layer 14: a wider culling mask adds only the local
  body's own 9 live renderer(s)
[bodylayer:census] colliders on layer 14: total=162 underLocalPlayer=15 (a culling mask never
  touches these) foreignRoots=['Wardrobe'#0x21B3FE=147;]
```

* **Renderers**: 9 live, all nine are the local `3P_ConcreteModel_Male_01(Clone)` body renderers
  (`[mirror:body-bound] ... selected=9/9 layer=14`). Zero foreign live renderers, zero foreign
  *inactive* renderers, zero prefab/asset renderers on the layer. Nothing else on layer 14 can draw.
* **Colliders**: 162. 15 are the local player's own bone hitboxes (`thigh_r [Hitbox]`, `head
  [Hitbox]`, …), 147 belong to the `Wardrobe` root (the mannequins' bone hitboxes). A camera culling
  mask is a render filter only — it never affects colliders, raycasts or hit detection — so none of
  these can become visible. Their renderers live on other layers (the wardrobe is drawn through the
  normal screen mask already).
* The same probe is re-run before every admission and a *cheap* live-renderer guard runs on every
  2 s rescan while the bit is on: if a non-body renderer ever appears on layer 14, the bit is
  withdrawn and `[bodyview:blocked]` names it. So the mask can carry the local body and nothing else.

## Camera masks

| camera | POV | mask | note |
|---|---|---|---|
| `Lobby Global/Main Camera` (screen) | third person | `0x4DDBCFC7` | was `0x4DDB8FC7`: **+ bit14 `0x4000`** |
| `Lobby Global/Main Camera` (screen) | first person | `0x4DDB8F87` | **identical to the pre-change mask** (no bit 14, no bit 6) |
| `AlterumMirror/AlterumMirrorCam` | both | `0x4CDB4FC7` | unchanged; bit 6 + bit 14 already present |

`[bodyview:screen] pov=ThirdPerson want=True layer=14 ('Hitbox') bit=0x00004000 screenCameras=1
masksChanged=1 ... eyeInsideBody=True eyeIn=['Sheriff_Head','Shirt_Optimized_1','Vest','Hair']`

## Verification, with numbers

Diffs are `max(abs(rgb)) > 2` over the whole 640x360 frame, `on` vs `off` rendered **in the same
frame, same pose** (two `Render()` calls back to back), so the only variable is the body bit.

| # | case | masks | diff px | mean | max | bbox (x0,y0,x1,y1) |
|---|---|---|---:|---:|---:|---|
| 1 | third person / screen | `0x4DDBCFC7` vs `0x4DDB8FC7` | **15,062** | 3.92 | 252 | 292,80–445,359 |
| 2 | first person / screen | `0x4DDB8F87` vs `0x4DDB8F87` (bit forced in, to test the policy) | live frame carries **0** body px; forcing the bit adds 15,097 | 3.87 | 216 | 293,90–445,359 |
| 3 | mirror / third person | `0x4CDB4FC7` vs `0x4CDB0FC7` | **51,513** | 16.61 | 252 | 212,65–505,359 |
| 4 | mirror / first person | `0x4CDB4FC7` vs `0x4CDB0FC7` | **51,328** | 16.45 | 252 | 215,65–505,359 |

1. **Third person / screen**: body **and** hat present (`thirdperson-screen-body-on.png`), against
   the same frame without the bit (`thirdperson-screen-body-off.png` = exactly the pre-change mask,
   the floating hat). The diff is the body silhouette plus a faint ambient-occlusion halo on the wall
   behind it — no other geometry appears (`thirdperson-screen-diff.png`). The hat is on layer 6 and
   was already in the third-person mask, so it is identical in both frames.
2. **First person / screen**: the live mask keeps **no** bit 14 and no bit 6, so the frame carries no
   body and no hat (`firstperson-screen-live.png`). Forcing the bit into the same mask adds 15,097 px
   of body (`firstperson-screen-bodybit-forced.png`) — so the body is rendered, available, and it is
   the mask that keeps it out. The FP screen mask is byte-for-byte the pre-change value
   (`0x4DDB8F87`), so the existing hat 0-diff result cannot regress.
3. **Mirror, both POVs**: mirror mask unchanged (`0x4CDB4FC7` in both), body + hat present in both
   (`mirror-mask-thirdperson.png`, `mirror-mask-firstperson.png`).
   `[mirror:body-check] bound=True renderers=9 controllers=2 keys=2 isolated=9 driven=9` after the
   change — the same numbers as before it.
4. **Other players**: not on layer 14. Their third-person bodies are on layer 23
   (`TV_PlayerModelMesh`), which is in the screen mask in *every* POV (bit `0x00800000` present in
   both `0x4DDB8F87` and `0x4DDBCFC7`) and is never written by this change; only the local body's own
   nine renderers are ever relayered to 14 (`[mirror:body-bound] selected=9/9`, census
   `liveForeign=0`). Confirmed by the census above (no foreign renderer exists on 14 at all, active
   or inactive) and by the guard, which withdraws the bit if one ever appears.

## What still looks wrong

**The game camera sits inside the player's head in third person.** `PovToggle` switches the game's own
view mode; it does not move the camera. Measured: camera `(1.11, 1.87, -8.10)`, head centre
`(1.13, 1.80, -8.06)`, and the mask log says the eye is inside four of the body's own renderer bounds
(`eyeInsideBody=True eyeIn=['Sheriff_Head','Shirt_Optimized_1','Vest','Hair']`). With the body bit
admitted, the live third-person frame is therefore the **inside of the face and the hat brim**
(`thirdperson-gamecam.png`) — the machine is gone. This is not a mask leak; it is what "draw the
owner's body from an eye camera" means in this build. The proof camera at 1.8 m
(`thirdperson-screen-body-on.png`) is the view where the body is actually a silhouette, and it is
exactly the view the old contact sheet used.

Options for the live camera, none of them done here (all outside "add the mask bit"):
* withdraw the bit whenever the eye is inside a body renderer's bounds (keeps the old eye view,
  body appears only from cameras that are outside the body — one line in `SyncScreenView`);
* hide head+hair from the screen camera only (needs a second isolated layer; the hat would then float
  over a headless body in the proof view);
* a real third-person camera offset (the game has none for the local owner in the Lobby).

Other notes:
* `[bodyview:screen]` prints the mask *before* `CosmeticsRuntime` writes its layer-6 bit in the same
  frame, so that one line can still show the hat bit while in first person; the
  `[cosmetic:layer] screen policy` line that follows is the authoritative FP value (`0x4DDB8F87`).
* Pre-existing, unrelated to this change and reproducible: after a live POV switch on a generation
  whose body binding found **no** hide keys (`[mirror:body-key] ... no disable keys`,
  `keys=0`), the game's own first-person hide is *passed through*
  (`[mirror:body-hide-passed] ... noOwnerHideBound (allowed)`) and the body stops being drawn for
  both the screen camera and the mirror until the next `Rebind` (a hot reload re-enables it,
  `[mirror:body-check] keys=2 isolated=9 driven=9`). Same-file evidence: the 13:39 FP capture had no
  body even with the bit forced; after a reload in FP the same capture has 15,097 px.
* `CosmeticsRuntime.MirrorBodyLayer` is still the hard-coded `14`; `MirrorFactory.SelectBodyLayer`
  still lands on 14, but it now ignores the bit this module owns while accumulating the screen mask,
  so a live admission cannot make it pick a different layer.

## Files

| file | what |
|---|---|
| `thirdperson-screen-body-on.png` | TP, screen mask with layer 14: body + hat |
| `thirdperson-screen-body-off.png` | same frame, mask without layer 14 (pre-change): floating hat |
| `thirdperson-screen-diff.png` | the 15,062 px difference (body silhouette) |
| `thirdperson-gamecam.png` | the real game camera in TP: eye inside the head |
| `firstperson-screen-live.png` | FP, live screen mask: no body, no hat |
| `firstperson-screen-bodybit-forced.png` | FP, same mask with the body bit forced: 15,097 px of body |
| `firstperson-screen-diff.png` | that difference |
| `mirror-mask-thirdperson.png`, `mirror-mask-firstperson.png` | mirror mask, body + hat in both POVs |
| `mirror-rt-live.png` | the live mirror RT (`mirror_static_g144059_180.png`): fixed lobby pose, player out of its frustum |
| `firstperson-gamecam.png` | the real game camera in FP (unchanged view) |
| `contact-sheet_third-person-body.png` | all of the above, labelled |
| `stats.json` | the diff numbers above, machine-readable |

Reproduce any capture from the live instance (no rebuild):

```
BepInEx/screens/screenproof.request   tag=..., pair=live|bit, source=game|mirror, eye=x,y,z, lookat=x,y,z
BepInEx/screens/bodylayer.request     any text -> full layer census; "check" also dumps the binding
BepInEx/screens/shot.request          the real game camera, as the player sees it
BepInEx/screens/pov.request           on|off|toggle
```
