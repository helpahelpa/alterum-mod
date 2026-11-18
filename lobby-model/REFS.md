# REFS — YouTube `4-mo1lAqdjQ` → 3D modelling brief

Reconnaissance only: no mod source edited, no build run, no game process touched.
Video downloaded to `/tmp` only; everything committed to the repo is images + this file.

> **Supersedes an earlier 480p draft of this same file** written concurrently in this session.
> That draft's crab anatomy notes are kept (they match the 1080p frames); its resolution claims,
> screen aspect and neon-tube description are corrected below. See §9.

---

## 0. Answer: what the subject is

**The "Reel of Fortune"** — a tall, dark-plum planked **arcade cabinet / signboard** bolted to the
white lighthouse on **Island 1 (Lighthouse)**, the hub island of the game **How to Fish**
(Dazed Games, [Steam app 4001890](https://store.steampowered.com/app/4001890/How_to_Fish/)).

It is **not** a person and **not** a slot machine in the casino sense: no lever, no coin slot, no
legs, no reels of metal. It is a wooden kiosk whose whole front upper half is one **emissive 2D
screen** (a UI panel, pixel-art) inside a thick **cyan neon tube**, with a recessed blank tray above
it and a big **amber neon curved arrow** floating at its upper-left. You insert a rare "Drip
creature" (here: the blue **Rock Crab**) and it rolls a cosmetic weapon skin.

The video is a 54 s first-person guide: walk to the cabinet, insert the crab, watch it spin, receive
a **Knife** skin (checkerboard). Screen time is ~40 % machine, ~50 % carrying the crab around the
lighthouse, the rest scenery.

---

## 1. Metadata

| Field | Value |
|---|---|
| Title | *How to Use the Reel of Fortune to Get Skins How to Fish* |
| Video ID / URL | `4-mo1lAqdjQ` — https://www.youtube.com/watch?v=4-mo1lAqdjQ |
| Channel | **PazarGamingGuides** — https://www.youtube.com/@PazarGamingGuides |
| Published | 2026-08-26 (upload ts 1787739581 → 2026-08-26T10:19:41Z) |
| Duration | **53.6 s** (reported `54`) |
| Resolution | **1920×1080, 30 fps** available (also 854×480) — the 1080p stream is what the refs use |
| Views / likes at capture | 3 016 / 15 |
| Category / keywords | Gaming · `guides`, `video game tutorial`, `quick video game tutorial` |
| Captions | no manual subs; **auto-generated captions exist** (`yt-dlp -J` lists `automatic_captions`) — not downloaded (yt-dlp 429 on the sub fetch). Audio not analysed. |
| Thumbnail | `https://i.ytimg.com/vi/4-mo1lAqdjQ/maxresdefault.jpg` |

Description, verbatim:

> How To Use the Reel of Fortune to Get Skins How to Fish / Where To Use the Reel of Fortune to Get
> Skins How to Fish. Just a quick video showing how to use the Reel of Fortune to Get Skins How to
> Fish.

`oEmbed` cross-check agrees (`provider_name: YouTube`, author `PazarGamingGuides`).

### Names found on screen / in the catalogue

`Reel of Fortune` (cabinet) · `Insert drip creature to play` · `Skins for: Brass Knuckles, Knife` ·
`Kit is spinning…` · `Rock Crab` (Value 5, Weight 1.431 kg, Killscore 1.05x) · `Brown Crab $3`,
`Lobster $9`, `Shrimp $5`, `Rock Crab $7` (inventory) · `4 / 49 Creatures`, `1 / 49 Drip Creatures`
· `Change Weapon Skin [Z/C]` · HUD `Version 1.09`.

Game facts corroborated externally: Island 1's cabinet pays **Brass Knuckles or Knife skins only**,
one Drip creature = one spin, skins are cosmetic and cycled with `Z`/`C`.
([how-to-fish.wiki](https://how-to-fish.wiki/guides/skins/),
[howtofishgame.wiki](https://howtofishgame.wiki/guides/reel-of-fortune-weapon-skins))

---

## 2. What the video shows (shot list, 5 s grid)

| t | Frame | Content |
|---|---|---|
| 0 s | `frame_01.png` | Wide: pale sea, white cylindrical lighthouse right with a **dark planked arched door**, sandy-rock shore, wood dock left, the **blue crab** lying on the sand. HUD: `$` tile, hotbar 1–3, `[B]` |
| 5 s | `frame_02.png` | At the door; crab carried huge in front of camera; black boombox at the tower base; the cabinet enters at the right edge |
| 10 s | `frame_03.png` | Crab set down; catch card `Rock Crab`; black anvil-like stand |
| 15 s | `frame_04.png` | Inventory overlay `Hmm… What have I caught?` — grid of catches, `4 / 49 Creatures`, `1 / 49 Drip Creatures`, `Close [Tab]` |
| 20 s | `frame_05.png` | Looking seaward over the shore, crab held low, dock left |
| 25 s | `frame_06.png` | Turning back; crab held up **in front of the lit screen** |
| **30 s** | **`frame_07.png`** | **Best single reference.** Tower + door + the whole cabinet: tray, sign, body, arrow, anvil, at near-frontal angle |
| 35 s | `frame_08.png` | Insert + spin: `Kit is spinning…`, reel streaking; `Unknown Weapon Skin (1/3)` … `(3/3)` |
| 40 s | `frame_09.png` | Spin result; `Change Weapon Skin [Z/C]`; checkerboard knife in hotbar 2 |
| 45 s | `frame_10.png` | Sea view again, knife held |
| 50 s | `frame_11.png` | Cabinet fills frame; checkerboard-skinned knife held bottom-right |

There is **no lobby building, no crowd, no player character visible** — first person throughout.
"Island 1 hub" is the closest thing to the "lobby" this folder is named for.

---

## 3. Reference images obtained

`yt-dlp` (`~/.local/bin/yt-dlp`) + `ffmpeg` both work.
Downloaded `bv*[height=1080]+ba` → `/tmp/lobby1080.mp4` (8.3 MB, 1920×1080). Frames at exact 5 s
steps, PNG, full 1080p.

| File | Source | Shows |
|---|---|---|
| `frame_01.png` … `frame_11.png` | t = 0,5,…,50 s, 1080p | the whole video at 5 s steps; `frame_07` is the key wide shot |
| `detail_machine_front.png` | 50 s crop x960–1480, y60–835 | cabinet, near-frontal, full height (base cut by frame) |
| `detail_machine_34.png` | 37 s crop x880–1500, y0–900 | 3/4 close-up, sign filling frame, spinning state |
| `detail_sign_face.png` | 50 s crop → 2× | **sign artwork**: marquee, instruction band, reel pixel art, footer, bottom bar |
| `detail_header_arrow.png` | 31 s crop → 2× | **tray + amber arrow** with bevel and bloom |
| `detail_scene_wide.png` | 40 s full frame | tower, cabinet, anvil, ground line |
| `detail_door_anvil.png` | 50 s crop → 1.6× | door construction, boombox, anvil, tower plinth |
| `detail_crab_ground.png` | 10 s crop | Rock Crab held; NPC fisherman behind |
| `detail_knife_skin.png` | 40 s crop | checkerboard knife skin (texture job, not a mesh) |
| `detail_inventory.png` | 15 s crop | inventory UI |
| `palette.png` | generated | sampled palette swatches with hex labels |
| `thumb_*.jpg` | i.ytimg.com | maxres/hq1–3/sd/hq thumbnails (overlay text is the channel's graphic, **not** in-game) |
| `machine_full.png`, `machine_screen.png`, `crab_*.png`, `skin_sword.png` | earlier 480p pass | keep for the crab; mine are sharper for the cabinet |

---

## 4. Modelling brief (PRIMARY) — the Reel of Fortune cabinet

### 4.1 What it is, top to bottom

One tall slab standing on the sand, flush against the lighthouse wall:

1. **Tray** (top): a rectangular **recess** in the cabinet face, near-black interior
   (`#17161F`), surrounded by a raised **pale bevel** (`#B9A79F`, ~3 cm). Roughly 0.7 × 0.5 m,
   ~6 cm deep. Blank — no text, no object in it. Function not shown in the video (reads as a
   prize/output bay or a dead display); **model it as a dark recess, do not invent contents**.
2. **Sign / screen**: a **square-ish** panel (see 4.2) with a **thick cyan neon tube** frame,
   rounded corners, heavy bloom. Immediately inside the tube is a thin dark-plum bevel, then the
   emissive face. The face is a **flat 2D UI texture**, in four horizontal bands:
   - marquee, dark maroon (`#4E1A22`) with **"Reel of Fortune"** in chunky gold pixel type
     (`#FFD24A`, dark outline, slight red-orange halo);
   - a hairline gold rule, then a dark maroon strip: **"Insert drip creature to play"** in cream
     (`#F6EEE2`);
   - the reel window: pale-cream rails top and bottom carrying **lime `$` bills** (`#7FC94E`,
     dark green `$` glyph `#2F6B22`), and between them diagonal pixel bands of **pink
     `#F2A7D0`, salmon `#E9877F`, pale blue `#BFDCE8`** with more bills. Mid-spin the bills
     streak vertically; idle, they sit as a static pattern;
   - footer strip, dark maroon: **"Skins for: Brass Knuckles, Knife"** in small cream type, then
     a **bright amber/cream bar** (`#FFB43A` → `#F7EFC0`) as the bottom edge of the face.
3. **Body**: the same dark-plum wood continues down to the ground **at least 1.6 sign-heights**
   (its base is always occluded — see 4.2). Vertical plank seams, two or three proud vertical
   stiles making recessed panels, one horizontal batten. In sun the planks read as raised battens
   forming door-style frames.
   Face is coplanar-ish with the sign, i.e. the sign is inset, not bolted on top.
4. **Arrow**: an **amber neon curved arrow**, flat, floating ~3 cm proud of the face at the
   sign's **upper-left**, sweeping ~270° counter-clockwise and ending in a chunky triangular head
   pointing **right**, at the height of the tray/sign junction. Stroke ~6 cm wide, hot yellow core
   `#FFD34A`, body `#E0912F`, dark brown outline `#8A4A16`, glow `#FF9A3C`. It overlaps the tray's
   lower-left corner and the sign's top-left corner. **One-sided — the single asymmetry.**

No lever, no coin slot, no buttons, no cables, no visible base/plinth (the bottom ~20 cm is never
in frame; assume it runs straight into the sand).

### 4.2 Proportions and scale

Measured on `frame_07.png` (the least-distorted view; close-ups at 37–50 s are trapezoid-distorted
and make the sign look wide — **do not use them for aspect**).

| Feature | Pixels (frame_07) | Ratio to sign height |
|---|---|---|
| sign, tube outer | 363 w × 350 h (cyan-component bbox) | **1.04 : 1 — square-ish** (0.95–1.20 across views) |
| tray, bevel outer | 275 w × 205 h | w = 0.75 × sign w; h = 0.56 |
| gap tray → sign | ~4 px | flush, sign bezel touches tray |
| cabinet width at sign level | 390 px | 1.06 × sign width |
| body below sign (sign bottom → visible ground) | ≥ 350 px | **≥ 1.0, base occluded** by the sand rise; design 1.6 |
| cabinet total height | 915 px visible | ≥ 2.5 × sign height (design 3.2) |
| arrow | 115 w × 165 h | ≈ 0.45 × sign height |

Aspect note: an automated **connected-component bbox of the cyan tube** gives 1.04 on `frame_07`,
0.95 on `frame_08` and 1.15–1.20 on the oblique close-ups (`frame_09`, `frame_11`). So the panel
is effectively square; the 1.32 : 1 an earlier pass measured is the trapezoid of a near-camera
3/4 view. Build it square with ±10 % tolerance.

Design canon that preserves those ratios (metres — **proposal, ±25 %**; the video gives no metric
anchor except the ~1.8 m player and a door of ≈2.2 m):

```
cabinet      1.00 w × 0.34 d × 2.95 h   (stands on the ground, back against the tower)
body below   planked, 1.50 tall (1.6 × sign h), 1.00 wide, 0.34 deep, 3 recessed panels
sign         0.92 × 0.92, tube Ø 0.09, tube inset 0.04 → bottom edge at 1.50, top 2.42
tray         0.70 × 0.52, recessed 0.06, bevel 0.03 → 2.42 … 2.94 (top of cabinet)
arrow        0.42 tall, 0.03 off the face, centred ~2.30 up, overhangs the left edge
```

Cross-check: sign centre ≈ 1.96 m (just above a standing player's eyeline, which is why the
video always looks slightly *up* at it), total ≈ 1.6 × player height.

The cabinet is noticeably **taller than a person** — its top is above the lighthouse door's arch —
so it reads as a kiosk/signpost, not a tabletop arcade machine. That is a hard constraint on the
silhouette.

### 4.3 Palette (sampled sRGB, post-bloom → suggested base albedo)

| Element | Sampled | Use in-engine |
|---|---|---|
| neon tube core | `#6FE8FF` (hot `#B9F7FF`) | emissive, HDR ~4–6 |
| tube halo / bloom | `#2FA8D8` | bloom only |
| cabinet body wood | `#3B2A33` shadow `#241820` | base `#4A3440`, roughness high |
| plank seam | `#241820` | darker stick |
| tray bevel | `#B9A79F` | lit `#CFC6BD` |
| tray interior | `#17161F` | near-black, matte |
| marquee band | `#4E1A22` (bloomed `#C57067`) | dark maroon, lit by the text |
| marquee gold | `#FFD24A` (hilite `#FFDD6E`) | emissive text |
| instruction / footer band | `#3E1519` | dark maroon |
| band text | `#F6EEE2` | near-white |
| bill green / glyph | `#7FC94E` / `#2F6B22` | emissive-ish |
| reel pink / salmon / pale blue | `#F2A7D0` / `#E9877F` / `#BFDCE8` | texture only |
| reel rail cream | `#F7EFC0` | texture only |
| bottom bar | `#FFB43A` → `#F7EFC0` | emissive |
| arrow core / body / outline | `#FFD34A` / `#E0912F` / `#8A4A16` | emissive + bloom |
| lighthouse wall | `#E3DED6` (in-video `#CEC4C3`) | matte white |
| door planks | `#4A3527` | dark wood |
| sand | `#C9B9A0` (shadow `#836F5F`) | matte |

Scene grade: strong cool blue in shadow, warm rim on lit faces; the neon is the only saturated
source. `refs/palette.png` is a swatch sheet of these.

### 4.4 What makes it *this* cabinet (generic-blob test)

Must have:
1. **Square-ish** emissive face in a **thick cyan neon tube with visible bloom** — not a thin bezel.
2. **Dark-plum planked slab** at least 1.6 screen-heights tall under the screen: vertical planks,
   recessed panels. A plain stretched cube fails immediately.
3. **Recessed blank tray with a pale bevel directly above** the screen, similar width to the sign
   minus a bit — creates the "two stacked apertures" forehead.
4. The **oversized amber arrow** curling at the upper-left, arrowhead pointing right. This is the
   most recognisable single element in the thumbnail. Without it, it's "a vending machine".
5. **Gold pixel "Reel of Fortune"** plus the two thin maroon text strips; the **lime `$` bills**
   give the colour identity (maroon + lime + cyan + amber, four-way, no other hue).
6. Correct proportions: tall, narrow, one bright window high up — **not** a wide landscape screen.

Fails as: a generic arcade cabinet with a lever, a landscape TV screen, uniform cyan frame with no
bloom, missing arrow, or a body shorter than the screen.

### 4.5 Pose, symmetry, animation

Static prop. Structurally **left-right symmetric** (cabinet, tray, sign, tube) — build half and
mirror on X, then attach the **arrow** (asymmetric, upper-left only) with the mirror off.
Sign artwork is a texture, so it does not need to be symmetric. No animation needed; the neon is
constant in the video (optionally a 2–4 s emission pulse). Face normal horizontal. Only the arrow
and the trim protrude (~3 cm).

---

## 5. Secondary subjects (if the intent was something else)

### 5.1 Rock Crab (creature, 0–30 s and the thumbnail foreground)

Flat-faceted **periwinkle** crab, cooler and paler than expected: shell `#9FB4CE`–`#8FA3C4`,
lit facets `#AEBFD8`, cream/white underside rim `#DCD8D0`, leg tips and pincer tips dark
`#3B4A6B`–`#2A3350`. Flattened dome wider than long (~1.3 : 1 : 0.35), low peak, **scalloped
notched rim**, one short two-finger pincer each side (tips darker), 3–4 thin **blade/spike** legs
per side tucked under the rim, and **2–3 small dark peg spines** on the anterior rim (eyes/stalks
— present in several frames, not an artefact). ~0.25–0.35 m across in-game (catch weight 1.431 kg);
scale ×2–3 for a lobby prop. Bilaterally symmetric → build the +X half, mirror. **Flat shading is
mandatory**; a smooth sphere with six cylinders is the generic-blob failure mode. No mouthparts,
antennae or tail resolvable.

Not a "drip" variant in this video — the player inserts a *regular* Rock Crab at 30 s (the HUD says
1/49 Drip Creatures, so a drip is a rainbow variant; the crab in the video is plain blue).

### 5.2 Lighthouse + door (setting)

White cylinder, slightly flared plinth, small cross/ladder vent high up, conical/domed cap out of
frame. Door: dark planked wood, **ogee/pointed arch**, black hinge straps, brass lock plate, small
green tag and yellow handle, black boombox on the sand at its base, dark anvil on a stand nearby.
Cylinder + arch + boxes; trivial.

### 5.3 Checkerboard knife skin (40–50 s)

Cream/dark-brown checkerboard on a curved blade — a **texture** job, not geometry.

---

## 6. Procedural build notes (Unity, C#)

- **Cabinet**: ~12 boxes — body slab, 3 vertical stiles, 1 horizontal batten, tray recess (5 boxes
  or 1 inset box + 4 bevel strips), sign bezel, sign face quad, bottom bar. ~150 tris.
- **Neon tube**: a rounded-rect path swept as a 4–6-sided prism (or 4 boxes with corner spheres);
  Unlit/emissive HDR + Bloom; no shadows.
- **Sign face**: one quad with a generated `Texture2D` (~512×512). Draw the four bands as
  rectangles, the gold text with a bitmap-ish font or `GUI`/`TextMeshPro` on a world-space canvas.
  The pixel art is rectangles — no imported art needed. Emissive ~1.5–2.
- **Arrow**: either a flat quad with an alpha-cutout arrow sprite (cheapest, truest — the game's
  arrow is a glowing 2D decal in world space) or an extruded arc ribbon + 3-vertex head. Add an
  outer soft glow quad or rely on bloom.
- **Crab** (if built): ~30-tri flat-shaded half-carapace (dome with radially displaced rim verts),
  wedges for pincer fingers, 2-tri tapered blades for legs, 4-sided prisms for pegs. ~250–500 tris,
  mirrored.
- Low-poly look: hard normals everywhere (`RecalculateNormals` on split verts, smoothingAngle 0),
  flat colours, no normal maps.

---

## 7. Difficulty verdict

**Feasible procedurally, no sculpting required — this is a box-and-texture prop, not a character.**

- The machine is: one slab, a recess, a quad, a swept tube and a decal arrow. A good first pass is
  a few hundred lines of C#; the whole risk is *finish* (bloom, colour discipline, the arrow),
  not geometry.
- The only non-primitive content is the **screen artwork**, which is UI/pixel art — reproduce it
  as a generated texture or a world-space canvas. Text legibility is what sells it; expect to
  hand-place a few dozen rectangles.
- **Fidelity achievable: high** for silhouette, layout, palette and the neon read. Exact decal
  bloom and the game's font are approximations. No real person, no detailed character, no organic
  high-poly surface appears anywhere in the video.
- If the intended subject were instead the **crab**, that is also feasible (faceted dome + pincers
  + blades, mirrored), but the exact facet layout of the game shell cannot be recovered from this
  capture — "very close", not asset-identical. The NPC fisherman and the player's hands would
  *not* be worth attempting (stylised only).

---

## 8. Caveats / confidence

- Absolute metres are a **proposal**, derived from in-frame ratios + the ~1.8 m player and ~2.2 m
  door. Ratios in §4.2 are the hard constraint; scale to taste. ±25 %.
- The cabinet's **base** (bottom ~20 cm) and its **back/side** are never clearly shown; depth
  (0.34 m) is inferred from the shadowed side face seen at 31 s and 37 s.
- Close-up frames (37–50 s) are trapezoid-distorted; I used `frame_07` for aspect. An earlier
  draft measured 1.32 : 1 from a close-up — that is perspective, not the panel.
- Colours are post-bloom screen values from a graded scene; the "use in-engine" column is the
  de-bloomed intent. `palette.png` labels everything.
- Magenta does **not** run as a second tube inside the cyan frame: the rose you see at 34–40 s is
  bloom from the maroon sign bands. One cyan tube, one dark bevel.
- Audio not analysed; the guide is visual + on-screen labels.

## 9. Provenance

Two passes over this video wrote into this folder in the same session. The earlier pass used an
854×480 download and 1 fps-style sampling; this version re-downloaded at 1080p, re-cut frames at
exact 5 s steps, re-measured proportions from `frame_07`, and re-sampled the palette. The earlier
pass's **Rock Crab anatomy** (§5.1) and its identification of the game (How to Fish / Dazed Games)
are retained because they check out against the 1080p frames. Corrected here: native resolution is
1080p30 (not 480p), the screen is square-ish (not 1.32 : 1), the tray is a blank recess (not a
"prize bay with contents"), and there is no magenta inner tube.
