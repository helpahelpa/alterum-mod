#!/usr/bin/env python3
"""Visible poke-through: the picture test the vertex-based `pen=` sweep cannot see.

`pen=` counts GARMENT vertices that lie inside the body surface. The artefact this checks for is
the opposite shape of the same event: the body surface pushing through between garment vertices,
which puts skin pixels on screen inside the garment silhouette while no garment vertex is "inside"
the body. The tank top has 2232 vertices, the body 20836, so a whole skin facet fits between two
fabric vertices - the count stays low and the render shows a patch.

For every captured value (three-quarter view, one frozen rig) four renders exist:
    <stem>-three.png      full
    <stem>-nos-three.png  tank top hidden  (body silhouette)
    <stem>-nob-three.png  body hidden      (garment only)
    <stem>-nop-three.png  pouch/strap hidden

A pixel is a visible poke-through when it is
  * skin-coloured (warm: R-B > 40, R-G > 18),
  * inside the garment-only silhouette (something that belongs to the garment layer covers it),
  * not the pouch strap (worn over the shirt by design; masked from the -nop difference), and
  * not part of the body's own shadow silhouette: components that touch the body outline are the
    arm/side legitimately in front of the hem, so they are dropped.

Prints count, largest blob and the blob list; writes <stem>-pokepix.png (magenta overlay) and
<pstem>-pokepix-crop.png next to the inputs.
"""
import os
import sys

import numpy as np
from PIL import Image

BG = np.array([26, 28, 33])
CROP = (330, 220, 470, 330)   # the chest/strap area, 720x1080 captures


def load(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(np.int16)


def silhouette(im):
    return np.abs(im - BG).sum(2) > 24


def dilate(m, n=3):
    for _ in range(n):
        m = m | np.roll(m, 1, 0) | np.roll(m, -1, 0) | np.roll(m, 1, 1) | np.roll(m, -1, 1)
    return m


def erode(m, n=2):
    for _ in range(n):
        m = m & np.roll(m, 1, 0) & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1)
    return m


def components(mask):
    """Label 4-connected components; returns (labels, sizes) sorted by size desc."""
    h, w = mask.shape
    lab = np.zeros((h, w), np.int32)
    sizes = [0]
    cur = 0
    ys, xs = np.where(mask)
    for y0, x0 in zip(ys, xs):
        if lab[y0, x0]:
            continue
        cur += 1
        stack = [(y0, x0)]
        lab[y0, x0] = cur
        n = 0
        while stack:
            y, x = stack.pop()
            n += 1
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ny, nx = y + dy, x + dx
                if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not lab[ny, nx]:
                    lab[ny, nx] = cur
                    stack.append((ny, nx))
        sizes.append(n)
    return lab, sizes


def analyse(base, outdir, refnob):
    full = load(os.path.join(outdir, f"{base}-full-three.png"))
    # masks may come from another value's capture of the same frozen pose (the strap and the tank
    # top's footprint move by well under a pixel across the values under test)
    masks = os.environ.get("MASK_BASE", base)
    nob = load(os.path.join(outdir, f"{masks}-nob-three.png"))
    nop = load(os.path.join(outdir, f"{masks}-nop-three.png"))
    ref = load(refnob)

    # The area to judge is the tank top's own footprint at the previous, known-clean value (the
    # belt/hem/hands are outside it by construction, so nothing else can be counted).
    area = erode(silhouette(ref), 2)
    garment = silhouette(nob)
    strap = dilate(np.abs(full - nop).sum(2) > 18, 3)
    r, g, b = full[:, :, 0], full[:, :, 1], full[:, :, 2]
    skin = (r - b > 40) & (r - g > 18) & (r > 80)
    bg = ~silhouette(full)

    m = area & garment & skin & ~strap
    lab_s, _ = components(skin)
    # drop skin that reaches the background: that is the arm/side in front of the hem, not a hole
    reach = dilate(bg, 2)
    touch = np.unique(lab_s[reach & (lab_s > 0)])
    if touch.size:
        m &= ~np.isin(lab_s, touch[touch > 0])

    lab, sizes = components(m)
    order = sorted(range(1, len(sizes)), key=lambda i: -sizes[i])
    blobs = [(i, sizes[i]) for i in order if sizes[i] >= 12]
    total = int(sum(s for _, s in blobs))

    ov = full.astype(np.uint8).copy()
    for i, _ in blobs:
        ov[lab == i] = (255, 0, 255)
    outbase = os.path.basename(outdir)
    Image.fromarray(ov).save(os.path.join(outdir, f"{base}-pokepix.png"))
    x0, y0, x1, y1 = CROP
    c = Image.fromarray(ov).crop(CROP)
    c.resize((c.width * 4, c.height * 4), Image.NEAREST).save(
        os.path.join(outdir, f"{base}-pokepix-crop.png"))
    return total, blobs


def main(paths):
    for p in paths:
        p = os.path.abspath(p)
        outdir = p if os.path.isdir(p) else (os.path.dirname(p) or ".")
        base = None if os.path.isdir(p) else os.path.basename(p)
        if base is None:
            bases = sorted({f[:-len("-full-three.png")] for f in os.listdir(outdir)
                            if f.endswith("-full-three.png")})
        else:
            bases = [base]
        refnob = os.path.join(outdir, "ref-shirt-three.png")
        if not os.path.exists(refnob):
            print(f"!! missing reference {refnob} (capture chest 1.55, shirt renderer alone)")
            return
        for b in bases:
            total, blobs = analyse(b, outdir, refnob)
            names = [f"{s}px" for _, s in blobs[:4]]
            print(f"{b:22s} visible skin-patch px={total:5d} blobs={len(blobs):2d} {names}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1:])
