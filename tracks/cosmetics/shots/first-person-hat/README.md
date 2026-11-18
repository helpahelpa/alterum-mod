# First-person local-cosmetic visibility — pixel evidence

Install `C:\AlterumT3` (track `cosmetics`), generation 3 of the final build (hot reloaded into the
Lobby with `ALTERUM_COSMETIC_PROOF=1`). `builtin.partyhat` equipped, local player in front of the
mirror.

## Mechanism

The local player's cosmetics were moved off layer 23 (`TV_PlayerModelMesh`) onto a dedicated
**local-cosmetic layer, 6 = `InteractableLookable`**. The screen camera's culling mask carries bit
`0x40` only in third person; the mirror camera always carries it. The hat renderers stay
`enabled` in all four cases (`renderersEnabled=2/2`), so the only variable is the per-camera mask.
Layer 23 is never removed from the screen mask, so other players' hats stay visible in first
person.

## Layer probe

```
[cosmetic:layer] probe renderable=[0,2,4,6,7,9,10,11,14,15,20,23,24,27] screenMask=0x4DDB8F87
                 census=0=11865 1=2 3=45 7=2 23=9 24=1 31=1
[cosmetic:layer] selected=6 name='InteractableLookable' bit=0x00000040
                 reason=unused/outside-screen otherLiveRenderers=0 renderable=True inScreenMask=False
```

- Layer 6 is renderable in this URP pipeline (the shared 32-layer probe the mirror uses).
- The lobby census has **no layer 6 entry**: no live renderer occupies it.
- The lobby screen mask `0x4DDB8F87` does **not** contain bit 6 before the policy is applied.
- The camera masks observed per case:
  - screen, first person: `0x4DDB8F87` (bit 6 absent)
  - screen, third person: `0x4DDB8FC7` (bit 6 present)
  - mirror, both POVs: `0x4CDB4FC7` (bit 6 present)
  - layer 23 bit `0x00800000` is present in every screen mask.

## Four cases

Proof camera: same camera transform in all four cases (looks at the head from 1.1 m in front),
hat renderers enabled vs disabled in the same frame. `hatBbox` is the projected hat bounds
(bottom-left origin), `meanAbsChannel` is over the box, `maxAbsChannel` is the largest per-channel
absolute difference.

| case | mask | local bit | hatBbox | box px | **diff px (>2)** | mean | max |
|---|---|---|---:|---:|---:|---:|---:|
| first person / screen | `0x4DDB8F87` | absent | 219,365–424,592 | 46,968 | **0** | 0.0000 | 0 |
| first person / mirror | `0x4CDB4FC7` | present | 219,365–424,592 | 46,968 | **13,433** | 31.2860 | 198 |
| third person / screen | `0x4DDB8FC7` | present | 222,365–428,593 | 47,403 | **15,739** | 33.9299 | 142 |
| third person / mirror | `0x4CDB4FC7` | present | 222,365–428,593 | 47,403 | **14,020** | 31.1845 | 201 |

`firstperson_screen_hat.png` and `firstperson_screen_none.png` are byte-identical
(`sha256 b2b82ac1f59bc65d…`): zero hat pixels through the game camera mask while the hat renderers
are enabled.

## Actual mirror renders (live `AlterumMirrorCam`, real RT)

The mirror root was temporarily placed 2.6 m in front of the player for the capture; its own
glass/frame renderers were hidden exactly as `MirrorRuntime.Tick` does.

| case | RT | hatBbox | box px | **diff px (>2)** | mean | max |
|---|---|---:|---:|---:|---:|---:|
| first person / mirror RT | 1536² | 651,1080–873,1228 | 33,227 | **8,543** | 26.9021 | 152 |
| third person / mirror RT | 1536² | 654,1080–877,1229 | 33,600 | **8,899** | 26.6683 | 149 |

`firstperson_mirror_rt_hat.png` shows the full local body with the party hat; the `_none` pose is
the same body without it.

## Actual game camera (`Lobby Global/Main Camera`)

Rendered at its own transform/mask into an RT (same mechanism as the clip recorder; works with a
locked desktop). The hat is above the eye-level frustum in this lobby pose, so both POVs have
**0 differing pixels globally** (`firstperson_main_hat.png` == `_none`, `thirdperson_main_hat.png`
== `_none`). This is the "what the player sees" confirmation; the tight proof camera above is the
non-vacuous mask test.

## Independent re-measurement

The numbers were re-computed from the saved PNGs with NumPy (bottom-up bbox converted to PIL rows)
and match the in-game logs exactly. The `*_main_*` and `*_screen_*` hat/none pairs are byte
identical where reported.

## Files

| file | what |
|---|---|
| `firstperson_screen_hat.png` / `_none.png` | screen mask, first person (identical) |
| `firstperson_mirror_hat.png` / `_none.png` | mirror mask, first person |
| `thirdperson_screen_hat.png` / `_none.png` | screen mask, third person |
| `thirdperson_mirror_hat.png` / `_none.png` | mirror mask, third person |
| `firstperson_mirror_rt_hat.png` / `_none.png` | live mirror RT, first person |
| `thirdperson_mirror_rt_hat.png` / `_none.png` | live mirror RT, third person |
| `firstperson_main_hat.png` / `_none.png` | live game camera, first person |
| `thirdperson_main_hat.png` / `_none.png` | live game camera, third person |

Proof PNGs in the install: `C:\AlterumT3\BepInEx\screens\t3\first-person-hat\`.
