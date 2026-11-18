# Wardrobe variants2 — two more entries, one withheld

Date: 2026-11-13. Adds the `models/variants2/baked/` models to the wardrobe as entries, through the
two renderer paths that already existed. Attribution for the already-shipped Sahur entries lives in
`notes/sahur-body.md` and `notes/sahur-roblox.md`; this is the same three places for the new ones:
this note, a comment next to the embedded resources in `AlterumModelSwap.Hot.csproj`, and the
`[sahur:wardrobe] ... credit:` lines the module logs when it starts.

## Entries, in wardrobe order

| idx | label | path | asset | licence |
|---|---|---|---|---|
| 10 | `Sahur` | `SahurRig` delta retarget (56..71-bone own skeleton) | `sahur_roblox.rset` | CC BY 4.0, alex.cgwarrior |
| 11 | `Sahur (Classic)` | `SahurModel` rigid, 6 regions | `sahur.mset` | CC0, @BakaFoxArt |
| 12 | `Golubiro` | `SahurModel` rigid, 10 pieces / 6 regions | `shpioniro-golubiro.mset` | **CC BY 4.0, alex.cgwarrior** |
| 13 | `Labubu` | `SahurModel` rigid, 12 pieces / 6 regions | `sigma-labubu-r15.mset` | **CC BY-NC + no-redistribution — NOT FOR DISTRIBUTION** |
| 14 | `Capuchino` | `SahurRig` delta retarget (56-bone Rigify rig, one root) | `balerina-capuchino.rset` | CC BY 4.0, alex.cgwarrior |
| 15 | `TC2 Scout` | `SahurRig` delta retarget (24-bone UE-named bake) | `tc2-scout.rset` | **no licence + Valve IP — NOT FOR DISTRIBUTION** |
| 16 | `TC2 Soldier` | same | `tc2-soldier.rset` | same |
| 17 | `TC2 Pyro` | same, **1 material / 1 submesh** | `tc2-pyro.rset` | same |
| 18 | `TC2 Demoman` | same | `tc2-demoman.rset` | same |
| 19 | `TC2 Heavy` | same | `tc2-heavy.rset` | same |
| 20 | `TC2 Engineer` | same | `tc2-engineer.rset` | same |
| 21 | `TC2 Medic` | same | `tc2-medic.rset` | same |
| 22 | `TC2 Sniper` | same | `tc2-sniper.rset` | same |
| 23 | `TC2 Spy` | same | `tc2-spy.rset` | same |
| 24 | `TC2 Saxton Hale` | same | `tc2-saxton.rset` | same |
| 25 | `Peter Griffin` | same | `peter-griffin.rset` | **no licence + Fox/Disney IP — NOT FOR DISTRIBUTION** |

The two new entries were appended behind the two Sahur entries on purpose: the game persists the
selected model as an index, so 10 and 11 must not move.

## 1. Credits (required)

* **Shpioniro Golubiro** by **`alex.cgwarrior`**, **CC BY 4.0** —
  <https://sketchfab.com/3d-models/shpioniro-golubiro-game-3d-model-free-download-f503749e40a44321bee4c46c00c759d9>.
  Modified: mesh mirrored in X, scaled to 180 cm, maps downscaled to one 1024² unlit albedo,
  normal/roughness/AO dropped and folded into constants.
* **Balerina Capuchino (rigged, game ready)** by **`alex.cgwarrior`**, **CC BY 4.0** —
  <https://sketchfab.com/3d-models/balerina-capuchino-rigged-game-ready-model-free-b825fe788ea34264b6fae18903162a05>.
  Modified: bones pruned 301→56, weights 8→4 influences, maps downscaled, AO/roughness dropped,
  mirrored in X, scaled to 1.80 m. **Not on screen yet** (entry withheld, §4) — the credit is in
  the code because the assets are embedded.
* **Sahur / Sahur (Classic)** — unchanged, see the two notes above.

## 2. Sigma Labubu — NOT FOR DISTRIBUTION

`Labubu` is integrated for the local demo only. The licence row on the download page is
**CC Attribution-NonCommercial 4.0**, and the author's own description adds a clause CC BY-NC does
not contain ("do not try to put it on the roblox marketplace or other stocks, I will DMCA you"), and
the character is third-party IP (Pop Mart / Kasing Lung Labubu, re-uploaded from a paid Roblox
bundle). Redistribution is exactly what shipping the mod would do.

Marked in four places:

1. the wardrobe row text is the plain label **`Labubu`** — the demo marking is deliberately *not*
   in the visible label (the wardrobe is the user-facing list, and a suffixed label was confusing
   the roster adoption on a hot reload); `AdoptNames` still carries the old `Labubu (demo only)`
   name so an entry a previous generation registered is adopted instead of duplicated;
2. `ModelRoster.cs`: `DemoOnly = true` plus the licence block above the spec, and a `LogWarning`
   at startup (`'Labubu' is DEMO ONLY - NOT FOR DISTRIBUTION ...`);
3. `AlterumModelSwap.Hot.csproj`: a `!! NOT FOR DISTRIBUTION !!` comment next to the four embedded
   `sigma-labubu-r15*` resources;
4. this file.

Packaging: there is no zip/dist script for the mod in this tree (`tools/deploy-hot.sh` and
`tools/build-deploy.sh` copy the hot DLL to the two live installs; `tools/reel-dist` builds the reel
tool). **Anything that ever packages `AlterumModelSwap.Hot` must exclude** `sigma-labubu-r15.mset`,
`sigma-labubu-r15_albedo.png`, `sigma-labubu-r15_portrait.png`, `sigma-labubu-r15_bust.png`.

### Nordbo — same exclusion, 11 models (added 2026-11-13)

The 11 Nordbo bakes in `models/nordbo/baked/` are **demo only, NOT FOR DISTRIBUTION**, for two
reasons at once:

1. **No licence.** Neither pack (`Nordbo-Team_Company_2-0.2.2`, `Nordbo-Peter_Griffin-0.1.0`)
   contains a licence file, licence text or repository — see
   `models/nordbo/team_company_2/LICENCE.md` and `models/nordbo/peter_griffin/LICENCE.md`.
2. **Third-party IP from game rips.** The characters are Valve's (the Team Fortress 2 cast and
   Saxton Hale) and Fox/Disney's (Peter Griffin).

| label | slug | payload |
|---|---|---|
| `TC2 Scout` | `tc2-scout` | 1,336,588 B |
| `TC2 Soldier` | `tc2-soldier` | 1,074,008 B |
| `TC2 Pyro` | `tc2-pyro` | 641,988 B |
| `TC2 Demoman` | `tc2-demoman` | 846,179 B |
| `TC2 Heavy` | `tc2-heavy` | 1,204,009 B |
| `TC2 Engineer` | `tc2-engineer` | 1,248,874 B |
| `TC2 Medic` | `tc2-medic` | 1,222,504 B |
| `TC2 Sniper` | `tc2-sniper` | 1,061,677 B |
| `TC2 Spy` | `tc2-spy` | 571,248 B |
| `TC2 Saxton Hale` | `tc2-saxton` | 1,372,082 B |
| `Peter Griffin` | `peter-griffin` | 662,437 B |

10.72 MiB in total. **Integrated 2026-11-13** as wardrobe entries 15–25 (see §2b below); the
paragraph that used to say "baked and staged only" is superseded. Each `models/nordbo/baked/<slug>/manifest.json`
carries `demo_only: true` with the reason. The visible row text is the plain label (no `(demo)`
marker — `AdoptNames` keeps the old suffixed name for reload adoption), `ModelSpec.DemoOnly`
is `true` with the same reason as `ModelSpec.DemoReason`, and **anything that packages
`AlterumModelSwap.Hot` must exclude** `models/nordbo/baked/*/*.rset`, `*.png` (11 blobs, 21 albedos,
11 portraits, 11 busts) and their embedded copies in `tracks/merged/mod/AlterumModelSwap.Hot/`.
Details: `models/nordbo/baked/MANIFEST.md`.

### 2b. Nordbo — as integrated (indices 15–25, all embedded)

All eleven are `ModelKind.Rigged` and load through the **existing** `SahurRig` path; nothing new was
written for rendering. The per-model data comes from each model's own `manifest.json`, staged by
`models/nordbo/baked/tools/gen_roster.py` (which also asserts the manifest bone order against the
blob's name table before it writes anything):

| idx | label | loader | BoneMap | materials | reload after adding it |
|---|---|---|---|---|---|
| 15 | `TC2 Scout` | `SahurRig` | 24 pairs, identity (from manifest) | 2 | 839 ms @ 29.7 MB |
| 16 | `TC2 Soldier` | `SahurRig` | same | 2 | 870 ms @ 30.9 MB |
| 17 | `TC2 Pyro` | `SahurRig` | same | **1 (1 submesh, all of it drawn)** | 754 ms @ 31.6 MB |
| 18 | `TC2 Demoman` | `SahurRig` | same | 2 | 716 ms @ 32.5 MB |
| 19 | `TC2 Heavy` | `SahurRig` | same | 2 | 566 ms @ 33.9 MB |
| 20 | `TC2 Engineer` | `SahurRig` | same | 2 | 624 ms @ 35.2 MB |
| 21 | `TC2 Medic` | `SahurRig` | same | 2 | 1001 ms @ 36.5 MB |
| 22 | `TC2 Sniper` | `SahurRig` | same | 2 | 782 ms @ 37.6 MB |
| 23 | `TC2 Spy` | `SahurRig` | same | 2 | 639 ms @ 38.3 MB |
| 24 | `TC2 Saxton Hale` | `SahurRig` | same | 2 | 722 ms @ 39.8 MB |
| 25 | `Peter Griffin` | `SahurRig` | same | 2 | 844 ms @ 40.5 MB |

**Route: embedded** (`EmbeddedResource` in `AlterumModelSwap.Hot.csproj`, generated block between the
`BEGIN/END nordbo` markers, `!! NOT FOR DISTRIBUTION !!` above it). The fallback to external files
did not trigger: the assembly went 28.3 MB → 40.5 MB and a hot reload stayed at 0.57–1.0 s during
integration (1.0–2.0 s for back-to-back reloads of the final 40.5 MB assembly), against a 10 s
cut-off. No external-loader path exists in the mod; if the payload ever grows past that budget, the
switch is `SahurRig.Resource()` (manifest name → file under `BepInEx/plugins/alterum-models/`).

Verified in the live lobby (instance A, 640×360, hot reload only): every entry as the player body
(one body rig + one preview rig live, 2+2 scene objects, previous rig disposed on every switch),
the wardrobe list rows and the preview body, the bottom-right bust portrait, and native → new →
native restore (0 objects of ours, 8 host renderers restored). No quirk was needed per model: the
facts that differ (up axis, scale, material count, bone names) are the bake's, carried in the
manifest and the spec, not in a branch.

Fixed while integrating: `SahurWardrobe.SyncSelection` walked the roster once, in order, so going
from a later entry to an earlier one of the same kind ran the later entry's "off" after the earlier
entry's "on" and destroyed the body the same tick it was built (Peter Griffin → TC2 Scout left no
body). It is now two passes — release first, acquire second. Two entries of one kind existed before
this (Sahur + Capuchino), so the bug was reachable without the Nordbo set.

## 3. How each one loads

Both new models are rigid region-split bakes (`.mset`, magic `SAHURM1`), so both drive the existing
`SahurModel` path: one mesh, one submesh per piece, every vertex 1.0 to a single game bone, the host
body renderers disabled and restored on switch. Nothing new was written for rendering; what changed
is that the *tables* became data:

| spec field | Golubiro | Labubu | (Sahur Classic) |
|---|---|---|---|
| `RegionBones` (region id → game bone) | torso→`spine_01`, head→`head`, arm_l→`upperarm_l`, arm_r→`upperarm_r`, leg_l→`thigh_l`, leg_r→`thigh_r` | same | same |
| pieces (submeshes) | 10 (character 6 + camera prop on `spine_01` + 3 sunglasses on `head`) | 12 (body 6 + 7 face pieces on `head`) | 8 |
| `Yaw180` | `false` (bake mirrored X to the game's handedness) | `false` | `true` (bake not mirrored) |
| `YOffset` | 0.0054 m | **0.8556 m** — the bake's origin is at the hips, so the mesh is lifted onto the floor | 0 |
| `Bat` | `false` | `false` | `true` |

`YOffset` is applied to the vertices, not to the body GameObject: a `SkinnedMeshRenderer` cancels
its own transform through its bindposes, so a transform offset does nothing (first attempt, seen in
`lab_preview.png` with the model half underground).

The rigged path (`SahurRig`) also became table-driven (`BoneMap` from the spec, one parsed asset per
spec, per-submesh material slot read from the blob), which is what Capuchino needed — see §4.

## 4. Capuchino — live again, the bake was rebuilt around one canonical space

Originally withheld: the bake multiplied already-normalised bone locals by its cm->m factor
(skeleton 1/105.9 of the mesh) and left the hierarchy with two roots, so the body rendered folded
inside out. `ModelSpec.BoneScale = 105.9055` was the load-time patch for the first defect and is
gone; nothing rescales bones at load any more.

`bake_v2_rig.py` was rewritten (2026-11-13) to convert exactly once, in one place:

* canonical space: `u=0.01`, `h_cm=190.63372720051228`, `a=1.8/(u*h_cm)=0.944219066811155`,
  `s=u*a=0.00944219066811155`, `H=diag(-1,1,1)`. Mesh points `s*H*(meshNodeWorld*[v,1])`, bind
  worlds from each cluster's `TransformLink` (polar rotation, exported 100x scale stripped,
  `[H*R*H, s*H*t; 0,0,0,1]`). No homogeneous matrix is divided by a scale.
* binds: both shells' 222 cluster `TransformLink`s agree exactly and reproduce their mesh node
  (`TransformLink*Transform` vs node world: worst 2.3e-5 in source cm). Determinants
  0.999995..1.000004 - the rig itself is not mirrored, the only reflection is the explicit `H`.
* one root: `DEF-spine` (mapped to `pelvis`). `root` and `MCH-torso.parent`, the thighs and pelvis
  helpers hang off it, shoulders/breasts off `DEF-spine.004`, arms off their shoulders. Bind
  worlds were frozen first and the locals re-derived, so the re-parenting moves no bind pose.
* clip: evaluated on the original FBX graph including the non-bone `rig` node (100x, Z-up ->
  Y-up), then frame-zero-relative world deltas, reflected, emitted in the new bone order.
* `verify()` now reads the finished blob back and fails the bake on: height, skeleton span ratio
  (0.7671), byte weights summing to 255, quantisation, winding versus the source, decoded joint
  binds against the frozen worlds, unit quaternions, single root, and CPU-skinned fixtures
  (identity deltas, +0.10 m pelvis translation, +-30 degree articulation pulses).
* `compare_render.py` renders source and decoded bake through identical cameras: silhouette IoU
  0.9999-1.0000, depth RMS 0.012-0.416 mm, unlit albedo MAE <=0.023/255.

The residual live bind deltas logged at play time (head ~50 deg, hands ~63 deg, feet ~21 deg) are
the game's own idle-vs-bind rotation, not asset error: the same numbers appear for the shipped
Sahur rig, and at game-rest the retarget is the identity by construction (the offline fixtures).

## 5. Preview and restore

* `SahurPreview` no longer gates on "the Sahur entries": it reads the selected entry's `Kind` from
  the roster and attaches either `SahurRig.AttachExternal` (rigged) or `SahurModel.BuildPreviewBody`
  (rigid) to the preview instance.
* The displayer's `_models` map is built once from `PlayerModelDatabase.Models` as it was at
  `Awake`, so rows appended afterwards were not keys and a first click on them threw
  `KeyNotFoundException` from `DisplayModel` (measured live: 12 keys — 10 native + `Sahur` +
  `Sahur (Classic)`; `key=NO` for entries 2..15, the whole `Golubiro`-and-below list).
  `SahurPreview.EnsureInstances` now extends the map through the displayer's own
  `InstantiateModels`, called once with only the missing rows temporarily swapped into
  `PlayerModelDatabase._models` (calling it with the full list re-adds existing keys and throws;
  it does not clear the map). The displayer therefore shows the clicked row's own instance, and a
  logging-only `DisplayModel` prefix (`wardrobe=preview=map` dumps the map) proves `key=yes` per
  row. A sweep destroys preview instances no database row maps to (a failed `InstantiateModels`
  leaves one active clone behind); across three close/open cycles the census stayed 26 keys /
  26 instances.
* Bodies are destroyed on switch-off, and `SahurModel.SweepOrphans` / the sweep in `SahurRig.Build`
  remove objects left by an earlier hot generation. Measured with `wardrobe=dump`:
  rigid entries 2 objects (body + preview body), rigged 4 (body + skeleton, ×2 for the preview),
  native selection 0.
