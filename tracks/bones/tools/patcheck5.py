#!/usr/bin/env python3
"""Visible skin-through-garment pixels — pass 5 version of tools/patcheck.py.

Same idea as patcheck.py (the vertex count `pen=` cannot see this: the body pushes out between
garment vertices), with two changes that make an A/B honest:

  * the counted area is the UNION of the garment's own silhouettes (each captured with every
    other renderer hidden) over every state and value in the sweep, so a state with a larger
    (re-weighted) garment cannot hide poke inside another state's smaller footprint, and each
    state is judged inside ITS OWN garment silhouette as well;
  * the strap mask is taken per state from that state's own full/hidden-pouch pair, not shared.

Inputs, all 720x1080 three-quarter renders of one frozen rig/camera:
    <stem>-three.png        full
    <stem>-shirt-three.png  the garment alone (every other renderer hidden)
    <stem>-nop-three.png    pouch/strap hidden
    <prefix>-ref-shirt-three.png   garment alone at the known-clean reference value

A pixel counts when it is skin-coloured, inside the union footprint AND inside that state's
garment silhouette, not part of the over-shirt strap, and its 4-connected blob does not reach the
body's background silhouette (arms/legs legitimately in front of the hem).

Usage: tools/patcheck5.py <dir> [<dir> ...]
"""
import glob
import os
import sys

import numpy as np
from PIL import Image

BG = np.array([26, 28, 33])
CROP = (330, 220, 470, 330)   # chest/strap area in 720x1080 captures


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


def analyse_dir(d):
    stems = sorted(f[:-len("-three.png")] for f in os.listdir(d)
                   if f.endswith("-three.png") and not f.endswith("-shirt-three.png")
                   and not f.endswith("-nop-three.png") and not f.endswith("-ref-shirt-three.png"))
    if not stems:
        print(f"{d}: no <stem>-three.png")
        return
    shirts = {}
    for s in stems:
        p = os.path.join(d, f"{s}-shirt-three.png")
        if not os.path.exists(p):
            print(f"{d}: {s} has no -shirt render, skipped")
            continue
        shirts[s] = silhouette(load(p))
    if not shirts:
        return
    area = None
    for s, m in shirts.items():
        area = m if area is None else (area | m)
    area = erode(area, 2)
    print(f"{d}\n  area (union of {len(shirts)} garment silhouettes) = {int(area.sum())} px")

    for s in sorted(shirts):
        full = load(os.path.join(d, f"{s}-three.png"))
        garment = shirts[s]
        nop_p = os.path.join(d, f"{s}-nop-three.png")
        nop = load(nop_p) if os.path.exists(nop_p) else full
        strap = dilate(np.abs(full - nop).sum(2) > 18, 3)
        r, g, b = full[:, :, 0], full[:, :, 1], full[:, :, 2]
        skin = (r - b > 40) & (r - g > 18) & (r > 80)
        bg = ~silhouette(full)

        m = area & garment & skin & ~strap
        lab_s, _ = components(skin)
        reach = dilate(bg, 2)
        touch = np.unique(lab_s[reach & (lab_s > 0)])
        if touch.size:
            m &= ~np.isin(lab_s, touch[touch > 0])

        lab, sizes = components(m)
        order = sorted(range(1, len(sizes)), key=lambda i: -sizes[i])
        blobs = [(i, sizes[i]) for i in order if sizes[i] >= 12]
        total = int(sum(x for _, x in blobs))
        names = [f"{x}px" for _, x in blobs[:4]]
        print(f"  {s:24s} skin px={total:5d} blobs={len(blobs):2d} {names}")

        ov = full.astype(np.uint8).copy()
        for i, _ in blobs:
            ov[lab == i] = (255, 0, 255)
        Image.fromarray(ov).save(os.path.join(d, f"{s}-pokepix.png"))


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    for d in sys.argv[1:]:
        analyse_dir(os.path.abspath(d))
