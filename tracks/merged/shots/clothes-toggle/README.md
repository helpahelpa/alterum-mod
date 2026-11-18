# Clothes toggle — garments hidden on every lobby player model

Install A (`C:\Program Files (x86)\Steam\steamapps\common\Alterum`), private lobby, headed at
640×360, 15 fps cap, merged build. Hot reload only: the process was never restarted or stopped.
Deployed hot dll md5 `cff7d0df34ba700f9a0c738ddfba5954`, `[hot] generation 9 active`
(the reload test at the end of the sequence advanced it to generation 10). The host
and `AlterumModelSwap.Contracts` are unchanged; only `tracks/merged/mod/AlterumModelSwap.Hot/
ClothesToggle.cs` was added and hooked into `ModelSwapRuntime` (`Update` / `Dispose` / key
legend).

Trigger: **F5**, or `<ScreensDir>/clothes.request`, line-oriented and deleted on pickup:

```
off | on | toggle      hide / restore / flip
dump                   print the rule and the full per-model renderer table
state                  print the current counts
mirror=<tag>           save the mirror's own RenderTexture to <ScreensDir>/clothes/<tag>_mirror.png
```

`F4` is free but unused. Driver: `tools/clothes-verify.sh` (request files only; no input device
needed).

## Mechanism

`Renderer.forceRenderingOff = true` on every renderer classified as a garment; on `on` each one
is written back to the value it had **before** the first hide.

`enabled = false` was rejected because `enabled` is already owned by three other systems in this
build:

* the game's own `RenderersRenderingController` hide keys (death / cutscene / equipment),
* `MirrorPlayerVisibility`'s `Drive`/`Selected` state — it writes `r.enabled` on every `Rebind`
  and restores the snapshot on teardown, and `[mirror:body-check]` reports that as `driven=N`,
* the first-person screen policy (`bodyview:screen` admits/withdraws the isolated body layer).

`forceRenderingOff` is a separate per-renderer flag. It removes the renderer from every camera
that would draw it — the screen camera and the mirror camera both render the same `Renderer`, so
one flag covers both — and it leaves `enabled`, layer, `bounds`, `shadowCastingMode`, LOD state,
bone weights and animator state untouched. The third-person rig's body bounds
(`TryGetBodyBounds`), the layer census, the mirror's `isolated/driven` counts and the chest-weight
clones (the undershirt stays bound and animated; only its pixels are dropped) are therefore
unaffected.

Ordering: `ClothesToggle.Tick` runs after `_mirrorRt.Visibility.Update()`. That sweep can write
`forceRenderingOff = false` on the renderers it drives when a wardrobe change makes it `Rebind`
(`MirrorPlayerVisibility`), so the re-assert has to happen after it in the same frame. Evidence
(see `evidence.log`):

```
[bounds:setup] SetupModel#1 -> sweep on next update (respawn / wardrobe change covered)
[mirror:body-bound] ... body='3P_ConcreteModel_Female_01(Clone)' selected=9/9 ... layer=14 (Hitbox)
[clothes] re-asserted forceRenderingOff on 4 renderer(s) that had been reset (mirror Rebind / native enable)
```

The model set is re-scanned every 0.5 s and on every scene change. Models are enumerated exactly
like the body binding does: every `ThirdViewPlayerModel` and `FirstViewPlayerModel` whose
GameObject has a scene (prefab assets and the wardrobe's shared templates are skipped). That
covers the local player's third-person body, the wardrobe mannequins, and any other player model
in the scene, including the layer-23 ones. A `SetupModel` rebuild that replaces the children of
the same model object is detected and its dead renderer entries are pruned.

## Classification rule

Renderer GameObject names are tokenised on non-alphanumerics, camelCase boundaries and
letter/digit boundaries (`Hair_cap` → `hair, cap`; `Ryan_Pants1` → `ryan, pants, 1`;
`Shirt_Optimized_2` → `shirt, optimized, 2`). **Keep tokens win over garment tokens** (so
`Hair_cap` is hair, not a cap); a name that matches neither is kept.

```
keep    body head hair hairstyle scalp face skin eyebrow eyelash eyelid eye eyes teeth tooth
        tongue hand hands arm arms forearm finger fingers thumb nail nails beard moustache
        boot boots shoe shoes foot feet leg legs glass glasses
        watch chain medallion token pouch bag scrunchie necklace earring ring bracelet bandage tattoo
garment shirt undershirt tshirt tank blouse tee top sweater hoodie jacket coat raincoat poncho
        cloak robe apron vest suspenders tie scarf
        pants jeans trousers skirt shorts kilt overalls jumpsuit dress uniform
        belt
        hat helmet cap sleeve sleeves glove gloves mitten mittens sock socks stocking stockings
default keep (no rule)
```

The set above is grouped for readability; the exact logged token list is sorted
(`[clothes] rule keep=[…] garment=[…] no-rule -> keep`, on startup and in every `dump`).

Ambiguous names were resolved from the render, not the name alone:

| name | bounds / render | decision |
|---|---|---|
| `Female_Legs_3` (Diana) | c.y 0.20 m, 0.37×0.26×0.42 m — inside the boots at ankle height | keep (leg/ankle skin under the footwear) |
| `Kiona_Arm_R2` | 8 cm blob at hip height, hidden inside the body/bag | keep (the name says arm; harmless either way) |
| `Diana_Pouch`, `Sarah_Chain`, `Medallion`, `Ryan_Token1`, `Ryan_Watch` | visible in the renders as jewellery / a strap | keep — they are accessories, not garments |
| `Hat` (Female_02) | 0.33×0.24×0.32 m at head height, a straw hat in the clothed capture | **garment** (worn headwear); gone in the unclothed capture |
| `Hair_cap` / `HairCap` | scalp shell under the hair | keep (hair token wins) |
| `Cone`, `Sphere` | two transient primitives on the freshly rebuilt local model | keep (no rule); pruned on the next scan |

Accessories that are kept stay where they are when the garment under them disappears (Diana's
pouch strap across the bare chest, Female_02's medallion) — visible and intentional in the
captures.

## Per-model renderer counts (generation 9)

```
[clothes] table: 12 model(s), 38 garment, 64 keep renderer(s)
```

| # | where | model | renderers | garments | kept |
|---|---|---|---|---|---|
| 1 | local player | `3P_ConcreteModel_Female_01(Clone)` | 9 | **4** | 5 |
| 2 | wardrobe | `3P_ConcreteModel_Male_05_Ryan(Clone)` | 14 | **3** | 11 |
| 3 | wardrobe | `3P_ConcreteModel_Male_04_James(Clone)` | 8 | **2** | 6 |
| 4 | wardrobe | `3P_ConcreteModel_Male_03(Clone)` | 8 | **3** | 5 |
| 5 | wardrobe | `3P_ConcreteModel_Male_02(Clone)` | 10 | **5** | 5 |
| 6 | wardrobe | `3P_ConcreteModel_Male_01(Clone)` | 9 | **4** | 5 |
| 7 | wardrobe | `3P_ConcreteModel_Female_05_Sarah(Clone)` | 8 | **3** | 5 |
| 8 | wardrobe | `3P_ConcreteModel_Female_04_Kiona(Clone)` | 9 | **3** | 6 |
| 9 | wardrobe | `3P_ConcreteModel_Female_03_Diana(Clone)` | 8 | **3** | 5 |
| 10 | wardrobe | `3P_ConcreteModel_Female_02(Clone)` | 9 | **4** | 5 |
| 11 | wardrobe | `3P_ConcreteModel_Female_01(Clone)` | 9 | **4** | 5 |
| 12 | local player (first person) | `1P_ConcreteModel_Female(Clone)` | 1 | **0** | 1 |

The first-person body is the game's `1P_ConcreteModel_Female`; it carries exactly one renderer,
`Hands_08_Mesh` (hands), and no garment at all. So there is nothing to hide in first person in
this lobby — the rule still covers it, and the table shows the zero. The local third-person body
in first person is hidden by the game's own POV policy, not by this toggle; the mirror still
draws it, and the mirror capture shows the garments gone.

## Full renderer table

Each model is listed with every renderer and its class. (Same data as `classification-log.txt`,
the live `dump` output.)

| # | where | model | renderers | garments | kept |
|---|---|---|---|---|---|
| 1 | local player | `3P_ConcreteModel_Female_01(Clone)` | 9 | **4** | 5 |
| 2 | wardrobe | `3P_ConcreteModel_Male_05_Ryan(Clone)` | 14 | **3** | 11 |
| 3 | wardrobe | `3P_ConcreteModel_Male_04_James(Clone)` | 8 | **2** | 6 |
| 4 | wardrobe | `3P_ConcreteModel_Male_03(Clone)` | 8 | **3** | 5 |
| 5 | wardrobe | `3P_ConcreteModel_Male_02(Clone)` | 10 | **5** | 5 |
| 6 | wardrobe | `3P_ConcreteModel_Male_01(Clone)` | 9 | **4** | 5 |
| 7 | wardrobe | `3P_ConcreteModel_Female_05_Sarah(Clone)` | 8 | **3** | 5 |
| 8 | wardrobe | `3P_ConcreteModel_Female_04_Kiona(Clone)` | 9 | **3** | 6 |
| 9 | wardrobe | `3P_ConcreteModel_Female_03_Diana(Clone)` | 8 | **3** | 5 |
| 10 | wardrobe | `3P_ConcreteModel_Female_02(Clone)` | 9 | **4** | 5 |
| 11 | wardrobe | `3P_ConcreteModel_Female_01(Clone)` | 9 | **4** | 5 |
| 12 | local player | `1P_ConcreteModel_Female(Clone)` | 1 | **0** | 1 |

#### 1. local player — `3P_ConcreteModel_Female_01(Clone)` (9 renderers, 4 garments)

| renderer | class | rule |
|---|---|---|
| `Body_Optimized` | keep | keep token 'body' |
| `Boots` | keep | keep token 'boots' |
| `Medallion` | keep | keep token 'medallion' |
| `Pants` | **garment** | garment token 'pants' |
| `Scarf` | **garment** | garment token 'scarf' |
| `Shirt` | **garment** | garment token 'shirt' |
| `Suspenders` | **garment** | garment token 'suspenders' |
| `Head` | keep | keep token 'head' |
| `Hair_full` | keep | keep token 'hair' |

#### 2. wardrobe — `3P_ConcreteModel_Male_05_Ryan(Clone)` (14 renderers, 3 garments)

| renderer | class | rule |
|---|---|---|
| `Male_Arm_1` | keep | keep token 'arm' |
| `Male_Arm_2` | keep | keep token 'arm' |
| `Male_Arm_3` | keep | keep token 'arm' |
| `Ryan_Head` | keep | keep token 'head' |
| `Ryan_Belt` | **garment** | garment token 'belt' |
| `Ryan_Boots` | keep | keep token 'boots' |
| `Ryan_Glasses` | keep | keep token 'glasses' |
| `Ryan_Pants1` | **garment** | garment token 'pants' |
| `Ryan_Token1` | keep | keep token 'token' |
| `Ryan_Vest` | **garment** | garment token 'vest' |
| `Ryan_Watch` | keep | keep token 'watch' |
| `Ryan_Beard` | keep | keep token 'beard' |
| `Ryan_Hair` | keep | keep token 'hair' |
| `Ryan_HairCap` | keep | keep token 'hair' |

#### 3. wardrobe — `3P_ConcreteModel_Male_04_James(Clone)` (8 renderers, 2 garments)

| renderer | class | rule |
|---|---|---|
| `James_Head1` | keep | keep token 'head' |
| `Male_Arm_3` | keep | keep token 'arm' |
| `James_Boots` | keep | keep token 'boots' |
| `James_Chain` | keep | keep token 'chain' |
| `James_Jeans` | **garment** | garment token 'jeans' |
| `James_Shirt_Pants` | **garment** | garment token 'shirt' |
| `James_Beard` | keep | keep token 'beard' |
| `James_Hairstyle` | keep | keep token 'hairstyle' |

#### 4. wardrobe — `3P_ConcreteModel_Male_03(Clone)` (8 renderers, 3 garments)

| renderer | class | rule |
|---|---|---|
| `Boots` | keep | keep token 'boots' |
| `Hands_Optimized` | keep | keep token 'hands' |
| `Jacob_head` | keep | keep token 'head' |
| `Pants` | **garment** | garment token 'pants' |
| `Pants_belt` | **garment** | garment token 'pants' |
| `Shirt` | **garment** | garment token 'shirt' |
| `Hair_cap` | keep | keep token 'hair' |
| `Hair_full` | keep | keep token 'hair' |

#### 5. wardrobe — `3P_ConcreteModel_Male_02(Clone)` (10 renderers, 5 garments)

| renderer | class | rule |
|---|---|---|
| `Boot` | keep | keep token 'boot' |
| `Hands_Optimized` | keep | keep token 'hands' |
| `Jacket` | **garment** | garment token 'jacket' |
| `Jeans` | **garment** | garment token 'jeans' |
| `Michael_head` | keep | keep token 'head' |
| `Raincoat` | **garment** | garment token 'raincoat' |
| `Scarf` | **garment** | garment token 'scarf' |
| `Shirt_Optimized_2` | **garment** | garment token 'shirt' |
| `Hair_cap` | keep | keep token 'hair' |
| `Hair_full` | keep | keep token 'hair' |

#### 6. wardrobe — `3P_ConcreteModel_Male_01(Clone)` (9 renderers, 4 garments)

| renderer | class | rule |
|---|---|---|
| `Boots` | keep | keep token 'boots' |
| `Hands_Optimized` | keep | keep token 'hands' |
| `Pants` | **garment** | garment token 'pants' |
| `Sheriff_Head` | keep | keep token 'head' |
| `Shirt_Optimized_1` | **garment** | garment token 'shirt' |
| `Tie_Optimized` | **garment** | garment token 'tie' |
| `Vest` | **garment** | garment token 'vest' |
| `Hair` | keep | keep token 'hair' |
| `Hair_cap` | keep | keep token 'hair' |

#### 7. wardrobe — `3P_ConcreteModel_Female_05_Sarah(Clone)` (8 renderers, 3 garments)

| renderer | class | rule |
|---|---|---|
| `Diana_Boots` | keep | keep token 'boots' |
| `Sarah_Belt` | **garment** | garment token 'belt' |
| `Sarah_Chain` | keep | keep token 'chain' |
| `Sarah_Pants` | **garment** | garment token 'pants' |
| `Sarah_Shirt` | **garment** | garment token 'shirt' |
| `Sarah_Hands` | keep | keep token 'hands' |
| `Sarah_Head` | keep | keep token 'head' |
| `Sarah_Hairstyle` | keep | keep token 'hairstyle' |

#### 8. wardrobe — `3P_ConcreteModel_Female_04_Kiona(Clone)` (9 renderers, 3 garments)

| renderer | class | rule |
|---|---|---|
| `Kiona_Optimized_Body` | keep | keep token 'body' |
| `Kiona_Arm_R2` | keep | keep token 'arm' |
| `Kiona_Bag` | keep | keep token 'bag' |
| `Kiona_Belt` | **garment** | garment token 'belt' |
| `Kiona_Boots` | keep | keep token 'boots' |
| `Kiona_Pants_cut` | **garment** | garment token 'pants' |
| `Kiona_Undershirt` | **garment** | garment token 'undershirt' |
| `Kiona_Hair` | keep | keep token 'hair' |
| `Kiona_Hair_Scrunchie` | keep | keep token 'hair' |

#### 9. wardrobe — `3P_ConcreteModel_Female_03_Diana(Clone)` (8 renderers, 3 garments)

| renderer | class | rule |
|---|---|---|
| `Diana_Optimized_Body` | keep | keep token 'body' |
| `Female_Legs_3` | keep | keep token 'legs' |
| `Diana_Belt` | **garment** | garment token 'belt' |
| `Diana_Boots_2` | keep | keep token 'boots' |
| `Diana_Pants_full` | **garment** | garment token 'pants' |
| `Diana_Pouch` | keep | keep token 'pouch' |
| `Jane_Undershirt` | **garment** | garment token 'undershirt' |
| `Diana_Hair` | keep | keep token 'hair' |

#### 10. wardrobe — `3P_ConcreteModel_Female_02(Clone)` (9 renderers, 4 garments)

| renderer | class | rule |
|---|---|---|
| `Belt_2` | **garment** | garment token 'belt' |
| `body_Optimized` | keep | keep token 'body' |
| `Boot` | keep | keep token 'boot' |
| `Shirt` | **garment** | garment token 'shirt' |
| `Skirt` | **garment** | garment token 'skirt' |
| `Head` | keep | keep token 'head' |
| `Hair_1_Cutted` | keep | keep token 'hair' |
| `HairCap` | keep | keep token 'hair' |
| `Hat` | **garment** | garment token 'hat' |

#### 11. wardrobe — `3P_ConcreteModel_Female_01(Clone)` (9 renderers, 4 garments)

| renderer | class | rule |
|---|---|---|
| `Body_Optimized` | keep | keep token 'body' |
| `Boots` | keep | keep token 'boots' |
| `Medallion` | keep | keep token 'medallion' |
| `Pants` | **garment** | garment token 'pants' |
| `Scarf` | **garment** | garment token 'scarf' |
| `Shirt` | **garment** | garment token 'shirt' |
| `Suspenders` | **garment** | garment token 'suspenders' |
| `Head` | keep | keep token 'head' |
| `Hair_full` | keep | keep token 'hair' |

#### 12. local player — `1P_ConcreteModel_Female(Clone)` (1 renderers, 0 garments)

| renderer | class | rule |
|---|---|---|
| `Hands_08_Mesh` | keep | keep token 'hands' |

## What the models actually look like with the clothes gone

The garments are not cut-outs over a complete body: the body meshes are authored only where the
garments leave them visible. Removing the garments exposes that missing geometry — reported here
as it appears, not cropped.

| model | unclothed result |
|---|---|
| local player `Female_01` | head, a small neck/upper-chest stub, two hands, cowboy boots. **No torso, no arms between shoulder and hand, no hips or legs** — the shirt and pants were the only geometry there. (The party hat is a mod cosmetic and is correctly untouched.) |
| `Female_03_Diana` | head, neck, torso/bust, full arms and hands, sneakers. **No hips and no legs**: the body stops at the waist and the shoes float. The pouch strap stays across the bare chest (kept accessory). |
| `Female_04_Kiona` | head, tattooed arms, upper chest. **Holes across the mid-torso** where the undershirt covered the skin, and the hip/leg region is a few disconnected patches (thigh/knee fragments); boots float. |
| `Female_02` | head + neck stub, forearms + hands, lower legs + boots. **Torso, upper arms and thighs missing**; straw hat, shirt, skirt and belt are gone. |
| `Male_02` (Michael) | head, neck/shoulder/upper-chest skin, hands, boots. **No torso, arms or legs** — the coat, raincoat, shirt and jeans were the only geometry. |
| `Female_05_Sarah` | not captured: she has no body renderer at all (the chest-weight census already recorded that her only chest surface is `Sarah_Shirt`). Unclothed she has no torso. |

So "clothes off" is mechanically correct and reversible, but the result is an incomplete body on
every body type in this lobby. That is the game's asset authoring, not a fault of the hide: the
kept renderers (`Body_Optimized`, `Diana_Optimized_Body`, `Kiona_Optimized_Body`, `Male_Arm_*`,
hands, heads, hair, boots) are still there and still enabled.

## Interaction with the existing systems (evidence in `evidence.log`)

* **Mirror body binding** — unchanged. `[mirror:body-check] … bound=True renderers=9 … isolated=9
  driven=9 …` reads the same in every generation before and after the toggles (it is a census of
  layers/controllers/keys, none of which this toggle writes); the one place the binding writes
  `forceRenderingOff` (Rebind) is countered by the same-frame re-assert above.
* **First-person layer-6 rule / the game's layer-24 first-view proxy** — untouched: no layer,
  mask, camera or `enabled` write. The 1P model is in the scan, keeps its hands renderer.
* **Other players (layer 23)** — the wardrobe mannequins are on layer 23 (the prop capture camera
  runs `mask=0x00800000`) and are covered by the same enumeration, so "any remote player model
  that appears" is the same code path.
* **Chest-weight clones** — the undershirt (`Jane_Undershirt`, `Kiona_Undershirt`) is hidden but
  not disabled: `enabled` stays true, the clone's skinning keeps running, the reweight bookkeeping
  is not touched.
* **Emote / jiggle lease** — no animator, bone, collider or transform writes; the lease and the
  solver see the same rig as before.
* **Cosmetics** — mod cosmetics carry `CosmeticMarkers` and are skipped (never classified, never
  hidden); the party hat in the mirror captures stays in both states. The cosmetics foreign-hide
  measurement (`TryMeasureNativeHide`) counts controller keys and `IsRenderingEnabled`, not
  per-renderer `forceRenderingOff`, so clothes-off cannot be mistaken for a native hide.

## Reversibility, reload, state

* `off` → `on`: every hidden renderer is restored to its recorded base value, and the log counts
  agree: `[clothes] off: garments=38/38 models=11 renderers=102 state=off` →
  `[clothes] on: restored=38 models=11 reason=request on`.
* State is **not** persisted across a hot reload, deliberately: a new generation starts clothed.
  The test is stronger than that: the toggle was left **off**, the hot dll was replaced
  (`[hot] generation 10 active`), and the retiring generation logged
  `[clothes] dispose: restored 38 renderer(s) our generation had hidden` while the new one logged
  `set: … hidden=0 state=on`. Any `forceRenderingOff` that still survived (a generation that
  could not restore, or the mirror's teardown snapshot) is cleared when that model is first seen
  and logged as `cleared stale forceRenderingOff on N…`; the scrub was not needed in this run
  because the retiring generation restored all 38 itself. The `local-reload-clothed*` captures
  are the mirror and screen after that reload.
* Within a generation the state survives scene reloads, wardrobe switches and joins, because the
  scan re-applies it to the new model set.

## Captures

All in this directory. Same camera and pose within each pair; `clothed` and `unclothed` differ
only by the toggle.

| file | what it is |
|---|---|
| `local-clothed.png` / `local-unclothed.png` | local player, game screen camera, 640×360, at the mirror |
| `local-clothed_mirror.png` / `local-unclothed_mirror.png` | the mirror's own RenderTexture (1536×1536, what the glass shows) |
| `local-restored.png` / `local-restored_mirror.png` | after `on`, same camera |
| `local-reload-clothed.png` / `local-reload-clothed_mirror.png` | after an unclothed hot reload: starts clothed |
| `diana-clothed-{front,three}.png` / `diana-unclothed-{front,three}.png` | wardrobe `Female_03_Diana`, frozen prop-capture rig |
| `kiona-clothed-{front,three}.png` / `kiona-unclothed-{front,three}.png` | wardrobe `Female_04_Kiona`, same rig |
| `f02-clothed-{front,three}.png` / `f02-unclothed-{front,three}.png` | wardrobe `Female_02`, same rig (also resolves the `Hat`) |
| `classification-log.txt` | raw `[clothes]` dump lines from the live log |
| `classification-tables.md` | the same, formatted |
| `evidence.log` | the filtered generation-9/10 log against the claims above |

Mannequin pairs use `propcap.request` with `freeze=1` (the carousel keeps animating otherwise)
and `solo=1` (all mannequins share layer 23, so without it the models overlap). The camera is
orthographic, derived from the frozen skeleton, 720×1080; the pose and framing are identical
between the two states. `release=1` afterwards restores every animator, rigidbody and deactivated
model.

The mirror is a fixed lobby fixture at (−0.33, 1.15, −12.27); the wardrobe carousel is about
24 m away in another room, so the mirror cannot frame a mannequin without moving it. The mirror
evidence covers the local player; the mannequin evidence is screen-camera (prop-capture) output.
`source=mirror` rendering of the mirror camera itself comes back black in this URP build, which
is why the mirror images here are the mirror's own RenderTexture.

## Gates

* `grep -c '[Error' LogOutput.log` → **0**
* `grep -c 'SWAP' LogOutput.log` → **0**
* `[clothes] tick failed` → **0**
* `[mirror:body-check]` during the run: `isolated=9 driven=9`, `bound=True`, keys/foreign state
  unchanged.
