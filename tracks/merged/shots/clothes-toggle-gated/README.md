# Gated clothes toggle — a garment is hidden only where skin lies under it

Install A (`C:\Program Files (x86)\Steam\steamapps\common\Alterum`), private lobby, headed at
640×360, 15 fps cap, merged build. **Hot reload only**: the process was never restarted or killed.
Deployed hot dll md5 `e1fae1d6b0b576dad41994271f7ec115`, `[hot] generation 19 active` at the end of
this run (the reload test at the end advanced 18 → 19). Host and `AlterumModelSwap.Contracts` are
unchanged; only `tracks/merged/mod/AlterumModelSwap.Hot/ClothesToggle.cs` changed. The mechanism
(`Renderer.forceRenderingOff`), model enumeration, mirror re-assert ordering and the per-renderer
table are the ones documented in `../clothes-toggle/README.md`; this directory covers what is new.

Driver: `tools/clothes-gated-verify.sh` (request files only, no input device needed). Every number
below is from that run.

## Modes and requests

| trigger | effect |
|---|---|
| **F5** | gated on/off — hide only garments with real skin under them |
| **Shift+F5** | hide everything on/off — the old unconditional behaviour |
| `clothes.request`: `gated` / `gate` | gated mode |
| `all` / `hide` / `off` / `0` | every garment, unconditional (`off` keeps its old meaning) |
| `on` / `show` / `1` | restore |
| `toggle` | flip gated ↔ on |
| `dump` | rule + full per-model renderer table + the coverage decisions |
| `coverage` | recompute and print the per-garment coverage decisions |
| `state` | counts |
| `tolerance=<m>`, `threshold=<0..1>`, `samples=<n>` | re-tune at runtime (tolerance/threshold re-decide from the cached distances; `samples=` resamples) |
| `mirror=<tag>` | unchanged (mirror RenderTexture dump) |

State is still not persisted across a hot reload: a new generation starts clothed.

## The rule

**Skin set.** Per model, the renderers classified `keep` whose GameObject name carries a body-part
token — the skin set is now its own token list, separate from the keep list:

```
skin = arm arms back belly body bust calf chest collarbone face feet finger fingers foot forearm
       hand hands head heel knee leg legs neck palm scalp shoulder shoulders skin thigh thighs
       thumb toe toes torso upperarm waist
```

Boots, hair, jewellery, glasses and beards are still `keep` but are **not** skin, so a shirt can
never be justified by the boots below it. The set is logged at startup and in every `dump`.

**Measurement** (bind pose, once per model per renderer-set change):

1. Every garment vertex is sampled with a fixed stride (≤600 samples, deterministic) and moved to
   **world bind pose** with the renderer's own `localToWorldMatrix` — the same frame the chest pass
   uses. Bind pose is deliberate: the result does not depend on the current animation, the wardrobe
   idle, the mannequin pose or the model's orientation, and it is stable across re-samples.
2. For each sample the distance to the nearest **skin vertex** is measured on a 50 mm grid. A
   3×3×3 cell neighbourhood is exactly the set of cells that can hold a vertex within 50 mm of the
   query point, so every distance ≤50 mm is exact; anything farther is reported as 51 mm, so
   "nothing near" can never be counted as a hit.
3. A sample is **covered** when that distance is ≤ **tolerance = 30 mm**;
   `coverage = covered / sampled`.
4. The garment is hidden when `coverage ≥ threshold = 0.80`; otherwise it stays on.

Every decision is logged per garment per model with the coverage number and the 10/20/30/40/50 mm
shares, so the tolerance/threshold can be re-chosen from one run's log without resampling
(`coverage.log`, and `dump`); the skin renderers that supplied the surface are logged per model.
Cost: 15–33 ms per model, one pass, then cached until the model's renderer set changes (wardrobe
switch / respawn / a new player model appearing).

## Why 30 mm and 0.80

The distribution has a wide gap: every garment that really lies on a body lands at **0.895–0.938**,
every garment over missing body lands at **0.000–0.558** (next-closest: 0.778, Kiona's belt — a narrow
ring over the one strip of waist her body does have). Any threshold inside **0.78…0.89** therefore
makes exactly the same decisions; 0.80 sits in the middle. Only a threshold at or below 0.778 adds
Kiona's belt, which is harmless (the pants under it stay on).

The lower edge is not arbitrary. Diana's undershirt measures **0.527**, and hiding it opens a hole:
her body mesh stops at the underbust, her pants start at the belt, so the tank top is the only
geometry across her waist. Probe captures with the threshold deliberately lowered (same frozen
camera and pose, only the threshold differs):

| Diana, gated | undershirt | enclosed background inside the silhouette |
|---|---|---|
| threshold 0.80 (shipped) | kept | 806 px total, largest island 23 px (edge noise) |
| threshold 0.52 (probe) | hidden | 5675 px total, one **4066 px = 107 cm²** hole between the underbust and the pants waistband |

See `diana-threshold-0.80-probe.png` / `diana-threshold-0.52-probe.png`. Hiding a garment whose
coverage is only ~50 % reveals exactly the kind of hole this task is about.

The tolerance needs to be ≥20 mm: at 10 mm Kiona's undershirt drops to 0.51 and the hat to 0.29, so
nothing would hide at all. Between 20 and 50 mm both decisions hold (Kiona 0.88 → 0.98, hat
0.88 → 0.98); 30 mm is the middle of that stable band, and also the fabric offset these meshes are
authored with (Kiona's undershirt over her body: median nearest-skin distance 10 mm, p90 24 mm).

## Per-garment decisions (12 models, 38 garments, live run)

Coverage is the share of that garment's sampled vertices with a skin vertex within 30 mm. `d≤N` is
the same share at N mm, for re-tuning. Produced by `clothes.request coverage`.

#### 1. local player (`LobbyPlayer_Separated/…/3P_ConcreteModel_Female_01(Clone)`)

`3P_ConcreteModel_Female_01(Clone)` — skin renderers: `'Body_Optimized':4570v,'Head':11898v` (16468 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Pants` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Scarf` | **0.323** | 0.15 | 0.29 | 0.32 | 0.35 | 0.37 | keep |
| `Shirt` | **0.158** | 0.10 | 0.14 | 0.16 | 0.18 | 0.22 | keep |
| `Suspenders` | **0.065** | 0.02 | 0.06 | 0.06 | 0.07 | 0.08 | keep |

#### 2. wardrobe `Male_05_Ryan`

`3P_ConcreteModel_Male_05_Ryan(Clone)` — skin renderers: `'Male_Arm_1':704v,'Male_Arm_2':324v,'Male_Arm_3':4504v,'Ryan_Head':13951v` (19483 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Ryan_Belt` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Ryan_Pants1` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Ryan_Vest` | **0.510** | 0.30 | 0.47 | 0.51 | 0.57 | 0.60 | keep |

#### 3. wardrobe `Male_04_James`

`3P_ConcreteModel_Male_04_James(Clone)` — skin renderers: `'James_Head1':13949v,'Male_Arm_3':4504v` (18453 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `James_Jeans` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `James_Shirt_Pants` | **0.363** | 0.15 | 0.28 | 0.36 | 0.43 | 0.51 | keep |

#### 4. wardrobe `Male_03`

`3P_ConcreteModel_Male_03(Clone)` — skin renderers: `'Hands_Optimized':4948v,'Jacob_head':12432v` (17380 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Pants` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Pants_belt` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Shirt` | **0.463** | 0.18 | 0.42 | 0.46 | 0.55 | 0.58 | keep |

#### 5. wardrobe `Male_02`

`3P_ConcreteModel_Male_02(Clone)` — skin renderers: `'Hands_Optimized':4654v,'Michael_head':12431v` (17085 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Jacket` | **0.152** | 0.00 | 0.08 | 0.15 | 0.21 | 0.23 | keep |
| `Jeans` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Raincoat` | **0.047** | 0.00 | 0.00 | 0.05 | 0.08 | 0.10 | keep |
| `Scarf` | **0.558** | 0.04 | 0.20 | 0.56 | 0.66 | 0.78 | keep |
| `Shirt_Optimized_2` | **0.398** | 0.15 | 0.30 | 0.40 | 0.47 | 0.49 | keep |

#### 6. wardrobe `Male_01`

`3P_ConcreteModel_Male_01(Clone)` — skin renderers: `'Hands_Optimized':4672v,'Sheriff_Head':14102v` (18774 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Pants` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Shirt_Optimized_1` | **0.558** | 0.22 | 0.44 | 0.56 | 0.60 | 0.61 | keep |
| `Tie_Optimized` | **0.440** | 0.00 | 0.09 | 0.44 | 0.85 | 1.00 | keep |
| `Vest` | **0.057** | 0.01 | 0.03 | 0.06 | 0.08 | 0.09 | keep |

#### 7. wardrobe `Female_05_Sarah`

`3P_ConcreteModel_Female_05_Sarah(Clone)` — skin renderers: `'Sarah_Hands':4636v,'Sarah_Head':13797v` (18433 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Sarah_Belt` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Sarah_Pants` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Sarah_Shirt` | **0.398** | 0.16 | 0.33 | 0.40 | 0.49 | 0.56 | keep |

#### 8. wardrobe `Female_04_Kiona`

`3P_ConcreteModel_Female_04_Kiona(Clone)` — skin renderers: `'Kiona_Optimized_Body':21338v,'Kiona_Arm_R2':1526v` (22864 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Kiona_Belt` | **0.778** | 0.01 | 0.39 | 0.78 | 0.91 | 0.95 | keep |
| `Kiona_Pants_cut` | **0.385** | 0.09 | 0.30 | 0.38 | 0.47 | 0.52 | keep |
| `Kiona_Undershirt` | **0.938** | 0.51 | 0.88 | 0.94 | 0.97 | 0.98 | **hide** |

#### 9. wardrobe `Female_03_Diana`

`3P_ConcreteModel_Female_03_Diana(Clone)` — skin renderers: `'Diana_Optimized_Body':20836v,'Female_Legs_3':3778v` (24614 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Diana_Belt` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Diana_Pants_full` | **0.080** | 0.02 | 0.06 | 0.08 | 0.09 | 0.09 | keep |
| `Jane_Undershirt` | **0.527** | 0.32 | 0.50 | 0.53 | 0.56 | 0.58 | keep |

#### 10. wardrobe `Female_02`

`3P_ConcreteModel_Female_02(Clone)` — skin renderers: `'body_Optimized':5656v,'Head':12001v` (17657 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Belt_2` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Shirt` | **0.467** | 0.25 | 0.43 | 0.47 | 0.48 | 0.50 | keep |
| `Skirt` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Hat` | **0.895** | 0.29 | 0.88 | 0.89 | 0.93 | 0.98 | **hide** |

#### 11. wardrobe `Female_01` (same prefab as the local player)

`3P_ConcreteModel_Female_01(Clone)` — skin renderers: `'Body_Optimized':4570v,'Head':11898v` (16468 verts)

| garment | coverage @30 mm | d≤10 | d≤20 | d≤30 | d≤40 | d≤50 | decision |
|---|---|---|---|---|---|---|---|
| `Pants` | **0.000** | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | keep |
| `Scarf` | **0.323** | 0.15 | 0.29 | 0.32 | 0.35 | 0.37 | keep |
| `Shirt` | **0.158** | 0.10 | 0.14 | 0.16 | 0.18 | 0.22 | keep |
| `Suspenders` | **0.065** | 0.02 | 0.06 | 0.06 | 0.07 | 0.08 | keep |

#### 12. first person `1P_ConcreteModel_Female`

`1P_ConcreteModel_Female(Clone)` — skin renderers: `none` (0 verts)

| garment | coverage | decision |
|---|---|---|
| (none: this model has no garments) | – | – |

### The two garments that hide

* `Kiona_Undershirt` — cov 0.938. Her body is present under it, with the small pre-existing holes she
  ships with (below).
* `Hat` (Female_02) — cov 0.895. The head is under it; hiding the straw hat reveals hair and face and
  nothing else.

`Kiona_Belt` is the closest call at 0.778 (2 points under the threshold). Its decision is visually
irrelevant — the pants that cover the same waist strip are kept either way.

## Models that hide nothing, and why

All of these stay **fully dressed** in gated mode. The reason is always the same: the renderer the
game calls a "body" on that prefab is a fragment, so there is no skin under the garment.

| model | skin renderers | best garment coverage | why nothing hides |
|---|---|---|---|
| local `Female_01` + wardrobe `Female_01` | `Body_Optimized` 4 570 v, `Head` 11 898 v | `Scarf` 0.323 | `Body_Optimized` holds the neck stub, hands and forearm stumps only — no torso, hips or legs at all. `Shirt` 0.158, `Suspenders` 0.065, `Pants` 0.000. |
| wardrobe `Female_03_Diana` | `Diana_Optimized_Body` 20 836 v, `Female_Legs_3` 3 778 v | `Jane_Undershirt` 0.527 | The body stops at the underbust; under the tank top's lower half there is nothing. `Diana_Pants_full` 0.080, `Diana_Belt` 0.000 (no hips/legs). |
| wardrobe `Male_05_Ryan` | `Male_Arm_1/2/3`, `Ryan_Head` | `Ryan_Vest` 0.510 | Arms and head only — no torso. `Ryan_Belt` 0.000, `Ryan_Pants1` 0.000. |
| wardrobe `Male_04_James` | `James_Head1`, `Male_Arm_3` | `James_Shirt_Pants` 0.363 | One arm mesh and a head; no torso, no legs. `James_Jeans` 0.000. |
| wardrobe `Male_03` | `Hands_Optimized`, `Jacob_head` | `Shirt` 0.463 | Hands and head only — no arms, no torso, no legs. `Pants` 0.000, `Pants_belt` 0.000. |
| wardrobe `Male_02` (Michael) | `Hands_Optimized`, `Michael_head` | `Scarf` 0.558 | Head, neck/shoulder skin and hands. `Shirt_Optimized_2` 0.398, `Jacket` 0.152, `Raincoat` 0.047, `Jeans` 0.000. |
| wardrobe `Male_01` (Sheriff) | `Hands_Optimized`, `Sheriff_Head` | `Shirt_Optimized_1` 0.558 | As above. `Tie_Optimized` 0.440, `Vest` 0.057, `Pants` 0.000. |
| wardrobe `Female_05_Sarah` | `Sarah_Hands`, `Sarah_Head` | `Sarah_Shirt` 0.398 | She has **no body renderer at all** (the chest-weight census found the same thing), so no garment can be justified. `Sarah_Belt` 0.000, `Sarah_Pants` 0.000. |
| first person `1P_ConcreteModel_Female` | none readable | – | Its single renderer is `Hands_08_Mesh`; it has no garments. |

This is the intended outcome: a toggle that visibly does nothing on these models is correct, not a
failure, and none of them is forced.

## Captures and the "no disembodied parts" test

10 third-person models × {clothed, gated, all} × {front, three-quarter}, 720×1080 orthographic, model
soloed, **frozen in the bind pose** (`propcap` `freeze=1 rebind=1`). The bind pose is the only pose
that reproduces across requests: the wardrobe idle, hair physics and the garments' own cloth
simulation keep moving otherwise (a same-state re-capture already differs by ~1 k px in the hair).

The measurement is in `analysis.txt` and does not trust the eye:

* **connected components of the model mask** — a floating head, detached hands or bootless legs are
  extra components. Gated = **1 component for all 10 models**. `all` = **3–5 components** on every
  model: that is the old behaviour this task replaces.
* **enclosed background inside the silhouette** — a hole where a garment was removed. Gated vs
  clothed stays within ±16 px on every model **except Kiona (+1917 px = 54.6 cm² at 1.687 mm/px)**,
  which is her body's own missing geometry, not a garment-shaped hole (see below).
* **pixels differing from `clothed`** — 0 for Ryan, Male_01 and Male_03 (the gate hid nothing on
  them, so the captures are identical); 171–7802 px on Diana, James, Sarah, Female_01 and Male_02,
  all confined to hair or to the garments' own cloth simulation (Male_02's 7802 px are raincoat
  folds, the spawned Female_01's 5080 px are the pants' waistband settling); and exactly the two
  models where a garment is hidden: Female_02 = 37 653 px (hat + its shadow), Kiona = 25 987 px
  (undershirt). The `changed bbox` column shows where: Female_02 from the hat down (y37–962), Kiona
  from collar to belt (y151–559), never over a model that hid nothing.

```
per-model verdict (front view, 720x1080, frozen bind pose)
model    clothed(components)        gated(components)          all(components)            px vs clothed changed bbox y/x     holes clothed->gated
diana    1 [208903]                 1 [208903]                 3 [91919, 7781, 6705]      171       y258-393 x267-489    806 -> 806 (+0) OK
f01      1 [178353]                 1 [177136]                 5 [22393, 11177, 11029]    5080      y165-520 x228-491    120 -> 130 (+10) OK
f02      1 [347423]                 1 [333900]                 5 [22498, 21642, 21543]    37653     y37-962 x101-670     571 -> 560 (-11) OK
james    1 [227806]                 1 [227795]                 5 [25189, 7320, 6905]      218       y70-242 x307-491     231 -> 229 (-2) OK
kiona    1 [187628]                 1 [185645]                 5 [91780, 9537, 9282]      25987     y151-559 x249-490    457 -> 2374 (+1917) OK
m01      1 [234331]                 1 [234331]                 5 [40611, 18973, 18604]    0         none                 1020 -> 1020 (+0) OK
m02      1 [249068]                 1 [249045]                 5 [29951, 13267, 13257]    7802      y64-1024 x207-490    102 -> 102 (+0) OK
m03      1 [220409]                 1 [220409]                 5 [37273, 11638, 6623]     0         none                 172 -> 172 (+0) OK
ryan     1 [205067]                 1 [205067]                 5 [25095, 16590, 15962]    0         none                 325 -> 325 (+0) OK
sarah    1 [202246]                 1 [202300]                 5 [25382, 11499, 10818]    651       y43-343 x282-426     270 -> 254 (-16) OK

verdict: gated keeps every model one piece: YES
(component lists are the largest areas in px; a floating head/hand/boot is an extra entry.
 holes = background enclosed by the silhouette; +0 means nothing new was opened.)

local player (screen camera + the mirror RT):
  local-clothed.png            present
  local-clothed_mirror.png     present
  local-gated.png              present
  local-gated_mirror.png       present
  local-all.png                present
  local-all_mirror.png         present
  screen  clothed components=1 gated components=1  gated-clothed px=4900
  (the local player is idle-animating between the two shots, so a pixel diff is expected; the
   garment evidence for this prefab is the frozen `f01-*` pair above.)
```

Notes on the individual cases:

* **Kiona** is the only model where hiding a garment exposes anything: her undershirt is 0.938
  covered, but the mid-torso skin she ships is not continuous. The +1917 px (54.6 cm²) are her body's
  own holes — the same ones the `all` capture shows from every side, which the undershirt was
  closing — not a garment-shaped region. Her `Kiona_Belt` and `Kiona_Pants_cut` stay on, so hips and
  legs stay covered. The task's expected outcome ("her undershirt hides, her pants stay") is exactly
  this; the alternative threshold (≥0.94) would keep her undershirt too and make the gated mode a
  no-op on every model.
* **Female_02** loses only the `Hat`; head, hair and every other garment stay.
* **Female_01** (the local player's own prefab, captured through the probe's `spawn` path because the
  local player's copy is the first match for that name) keeps all four garments; its 5080 px
  difference is cloth settling at the waistband, not a removed garment (the log counts 2/38 hidden
  garments in total, and neither is on this model).
* Every `all` capture is the contrast, not a shipped mode: it is what Shift+F5 / `clothes.request
  all` still produce on request.

## Mirror and screen camera agree

`forceRenderingOff` is one flag per renderer, so both cameras see the same thing — checked on the
local player, the only model the mirror can frame:

* gated: `local-gated.png` (screen camera) and `local-gated_mirror.png` (the mirror's own
  RenderTexture, 1536×1536) both show her fully dressed — the expected result for this prefab.
* `all`: `local-all.png` and `local-all_mirror.png` both show the same head + two hands + boots with
  the garments gone. Nothing is left on in one camera and hidden in the other.

## Reversibility and reload

* `all` → `gated`: `kept 36 garment(s) visible in gated mode (no skin under them)`,
  `gated: garments=2/38 models=2`.
* `gated` → `on`: `on: restored=2 models=2 reason=request on`.
* `on` → `gated` → `on` → `all` → `on` repeats exactly, counts stay 2/38 and 38/38.
* Left in `all`, hot dll replaced: the retiring generation logged
  `dispose: restored 38 renderer(s) our generation had hidden`; the new one (`generation 19`) logged
  `set: … hidden=0 state=on` and `state: garments=0/38 … state=on`.
  `local-reload-clothed.png` / `local-reload-clothed_mirror.png` are the screen and mirror after that
  reload: clothed.

## Gates

* `[Error]` lines: **0**
* `grep -c SWAP`: **0**
* `[mirror:body-check] … bound=True renderers=9 controllers=2 keys=0 isolated=9 driven=9` — unchanged
  before, during and after the gated/all toggles.
* the re-assert-after-Rebind path is intact in this build: with `all` active, a local-player
  `SetupModel` (the probe's `setmodel=` test path, which rebuilt the same prefab) produced
  `[mirror:body-bound]` and then, in the same frame,
  `[clothes] re-asserted forceRenderingOff on 4 renderer(s) that had been reset (mirror Rebind /
  native enable)` — the 4 local garment renderers the mirror had just cleared.
* chest-weight clones still bound while a hidden renderer is off, from `renderer-table.log`:
  ```
  GARMENT 'Kiona_Undershirt' … cov=0.938 hide=True forceOff=True applied=True enabled=True
          mesh='Kiona_Undershirt(Clone)' bones=18 bounds=c=(25.66,1.47,-11.74) …
  GARMENT 'Jane_Undershirt'  … cov=0.527 hide=False forceOff=False applied=False enabled=True
          mesh='Jane_Undershirt(Clone)' bones=18 bounds=c=(25.59,1.45,-11.73) …
  ```
  `forceRenderingOff` drops the pixels only: the renderer stays `enabled`, the reweighted clone is
  still the bound mesh with all 18 bones, and its world bounds follow the animated skeleton.
* `[clothes] tick failed` / `hide failed` / `restore failed`: none in the run.

## Files

| file | what it is |
|---|---|
| `<model>-{clothed,gated,all}-{front,three}.png` | the 10 models, three states, one frozen camera and pose |
| `f01-*` | the `Female_01` prefab (wardrobe / local player), spawned by the probe so it is unambiguous |
| `local-{clothed,gated,all}.png` / `..._mirror.png` | local player, screen camera and mirror RenderTexture |
| `local-reload-clothed{,_mirror}.png` | after an unclothed hot reload: starts clothed |
| `diana-threshold-{0.80,0.52}-probe.png` | the threshold experiment (0.52 is not the shipped default) |
| `analysis.txt` | the component / hole / pixel-diff table above |
| `coverage.log` | the full per-garment gate audit of the run |
| `renderer-table.log` | `dump`: every renderer with class, skin flag, coverage, decision, `enabled`, mesh, bounds |
| `state.log` | the `[clothes]` state transitions of the run |
