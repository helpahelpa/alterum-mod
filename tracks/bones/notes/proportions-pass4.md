# proportions pass 4 — the chest patch at 1.80 is skin, and 1.70 is the clean value

> **Superseded in part by pass 5** (`proportions-pass5.md`): the residual strap-junction seam this
> note could only measure is now closed by rewiring the tank top's chest weights to follow the skin
> under them (`ChestWeights.cs`), so the chest ships at **1.90** (leaf 1.55, cage 1.22581, hips
> 1.50) with 0 visible skin pixels at the strap junction. The 1.70 value and the diagnosis below
> stand as history: the vertex count could not see the seam, and the capture path lagged one
> request — both are what pass 5 was built on.

Scope: `tracks/bones/mod/AlterumModelSwap.Hot/Proportions.cs` (`ChestCageScaleDefault`).
Install `C:\AlterumT1`. Evidence in `shots/proportions-pass4/`.

## Result

| knob | pass 3 | **pass 4 (shipped)** |
|---|---|---|
| breast leaf (`chest_02_l/r`) | 1.55 | 1.55 (unchanged) |
| chest cage (parent of `chest_01_l/r`) | 1.161 | **1.097** |
| total chest | 1.80 | **1.70** |
| hips | 1.50 | 1.50 (unchanged) |

One constant changed; no other code path. The 1.80 panel is not clean and 1.70 is the largest value
that renders at the accepted 1.55 level.

## 1. What the patch is: the body skin, not the strap

In `diana-previous-candidate-shipped.png` (three-quarter row, shipped 1.80) there is a tan,
sharply-edged patch on the tank top right of centre, on the strap line. Rendered again at the same
value, one frozen pose, one camera, hiding one renderer per frame:

| hidden | patch |
|---|---|
| nothing | present |
| `Jane_Undershirt` (tank top) | patch region is skin, body silhouette continuous |
| `Diana_Pouch` (shoulder strap + pouch, the only strap object) | **unchanged** — 3371 px change elsewhere, patch pixel-identical |
| `Diana_Optimized_Body` (skin, 20836 verts) | **gone**, garment surface continuous |

So the patch belongs to `Diana_Optimized_Body`: skin through the tank top. `Diana_Pouch` is a single
mesh (4915 verts) whose strap is weighted to `spine_04` (1048 mass, the cage), `clavicle_l` (736) and
`spine_03` (315) — it does move with the cage, but it is not what shows. `shots/proportions-pass4/
attrib-patch-object.png`.

## 2. Why the sweep's count said "clean" and the picture did not

Two independent reasons, both reproduced:

**(a) The vertex test cannot see this event.** `pen=` counts GARMENT vertices that lie inside the
body surface. What is on screen is the opposite: the body (20836 verts) pushing out between garment
vertices (tank top, 2232 verts) — a whole skin facet fits inside one fabric triangle, so no garment
vertex has to be "inside" for a patch to render. 38 verts / 7.21 mm at 1.80 is real but is a
different, weakly-related number. Measured pixel areas at the same pose (see §3) go 248 px at 1.55
to 471 px at 1.80 while the vertex count moves 29 → 38.

**(b) A capture taken in the same request that changes the scale renders the PREVIOUS state.**
Proved by md5, not by eye
(`shots/proportions-pass4/capture-lag-proof/`): request `chest=1.00` twice, then `chest=2.80`, capture
— the 2.80 capture is byte-identical to the 1.00 one; capturing the same 2.80 values a second time
gives a different image. Same for 1.80→1.70 (all five 1.70 captures of a naive loop were
byte-identical to the 1.80 one). `sweep.sh` captures `full` immediately after each value change, so
each row's render verdict was taken on the previous, smaller value. Every capture in this pass sends
the same request twice and keeps the second (`tools/grab.sh`, noted in `ProportionShots.cs`).

With (b) fixed the ladder is monotone and matches the eye:

| total chest | cage | visible skin patch (px) | largest blob |
|---|---|---|---|
| 1.55 (previous) | 1.000 | 248 | 99 (pre-existing seam notch) |
| 1.70 | 1.097 | **248** | 99 |
| 1.72 | 1.110 | 267 | 99 |
| 1.75 | 1.129 | 287 | 99 |
| 1.78 | 1.148 | 319 | 99 |
| 1.79 | 1.155 | 376 | 128 |
| **1.80 (was shipped)** | **1.161** | **471** | **223** |

The count is the `tools/patcheck.py` pixel test: skin-coloured pixels inside the tank top's own
footprint (fixed at the 1.55 value), the strap masked out by its own hidden render, components that
reach the background (arm/flank legitimately in front of the hem) dropped. It reproduces exactly in
two sessions with different poses (471 / 248).

`clavgrow=1` (grow the clavicles with the cage, the one structural lever in the file) does **not**
close it at 1.80 — rendered and compared, the patch is unchanged — so it was not used.

## 3. Before → after

`patch-before-after-4x.png` — 3.7×, one frozen pose and camera, 1.80 → 1.70: the kite is gone, the
small stalk that is also in the 1.55 panel remains. `value-ladder.png` — 6×, 1.55 / 1.70 / 1.75 /
1.78 / 1.80. `three-quarter-before-after.png` — the shipped panel, full frame.

The residual stalk exists at 1.55 as well (248 px, same blobs): the tank top's strap/neckline hem is
weighted to the clavicles (pinned at 1×) while the skin beneath rides `spine_05`/`chest_01`, so any
cage scale opens that seam a little. Closing it needs weight transfer on the garment, not a bone
scale (pass-3 §8).

## 4. Invariance and jiggle numbers at the new value

One frozen pose, same session, `propcap measure=1`:

| measure | cage 1.161 (1.80) | cage 1.097 (1.70) |
|---|---|---|
| shoulder joint width (`upperarm_l..r`) | 0.2925 m | **0.2925 m** |
| height (head → lowest bone) | 1.5972 m | **1.5972 m** |
| hip width (thigh roots) | 0.2990 m | **0.2990 m** |
| chest pivot → leaf (rest length) | 0.0572 m | **0.0540 m** (×1.097/1.161) |

Hips 1.50 untouched. The jiggle solver is untouched: pass 3 measured the impulse response identical
at cage 1.0 and 1.161 (21.646° / 35.87 mm, 0 clamps), and 1.097 lies inside that bracket; the lever
arm scales with the rest length, so the rendered excursion is that much smaller — the same
direction, no retune.

## 5. Regression

* `grep -c SWAP LogOutput.log` = **0**.
* reload clean: `[hot] generation 27 active`, no `[Error]` after it; `mirror:static-created` +
  `mirror:render-ok` + `mirror:rt-dump` all fire on the new build.
* `mirror:body-check ... isolated=9 driven=9` — last dump 12:48, i.e. from the pre-change build; the
  binding is diagnosed only when the wardrobe mirror binds the local body and no mirror file was
  touched (`Mirror.cs`, `MirrorPlayerVisibility.cs` unchanged). Re-run it by entering the wardrobe
  if a post-change witness is wanted.
* capture session released (`propcap release=1` → animators restored, capture camera destroyed);
  the local body `3P_ConcreteModel_Male_01(Clone)` is `active=True`; solver re-enabled
  (`solver.request enabled=1`).
* a stale one-shot `proportions.request` (`chestscale=1.16129`, left by the pass-3 session) was
  re-applied on every module start and overrode the new default; it has been deleted.

## Files

| file | change |
|---|---|
| `mod/AlterumModelSwap.Hot/Proportions.cs` | `ChestCageScaleDefault` 1.161f → **1.097f** + comment |
| `mod/AlterumModelSwap.Hot/ProportionShots.cs` | comment only: example keys 1.097, and the request-twice note for captures |
| `tools/grab.sh` | new: one propcap request → collected views + info |
| `tools/patcheck.py` | new: the visible-poke pixel test (what `pen=` cannot see) |
| `shots/proportions-pass4/` | montages, raw renders, `make_montage.py` |
| `shots/proportions-pass3/strap-attrib*/` | hide/show attribution renders, settled value sweep |
