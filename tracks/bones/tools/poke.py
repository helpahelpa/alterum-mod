#!/usr/bin/env python3
"""Poke-through measurement for the proportions-pass3 sweep.

For each captured value the probe produced three front renders of the SAME frozen rig, camera and
lighting:
  <tag>-front.png        full body
  <tag>-nos-front.png    garment hidden  (shirt renderer disabled)
  <tag>-nob-front.png    body skin hidden (skin renderer disabled)

If the fabric covers the skin, hiding it changes every pixel inside its own silhouette. A pixel
that is unchanged between the full render and the "garment hidden" render INSIDE the garment's
silhouette is therefore a pixel where something else (the skin) is in front of the fabric: that is
poke-through. The check is restricted to the chest band (between the chest pivot and just below the
bust) so the belt/waist (worn OVER the shirt by design) and the hands cannot count.

Also prints the cartoon silhouette width/depth at the chest rows, so the "same pose" promise is
verifiable from the numbers.
"""
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(os.path.expanduser("~/assets/monorepo/alterum-data"), "tracks", "bones",
                   "shots", "proportions-pass3", "raw")
BG = np.array([26, 28, 33])
ROW0 = 120  # chest band, image rows (720x1080 captures); refined per tag from the landmark file


def load(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(np.int16)


def silhouette(img):
    return np.abs(img - BG).sum(2) > 24


def band_rows(tag):
    """Chest band from the propcap landmark file (bone pixel rows, top-left origin)."""
    p = os.path.join(RAW, f"{tag}-info.txt")
    rows = {}
    try:
        for line in open(p, encoding="utf-8", errors="ignore"):
            if not line.startswith("bone="):
                continue
            parts = line.split()
            name = parts[0][5:]
            rows[name] = float(parts[1].split(",")[1])
            if name == "head":
                break
    except FileNotFoundError:
        pass
    if "chest_01_l" in rows and "chest_02_l" in rows:
        lo = int(rows["chest_01_l"] - 55)
        hi = int(rows["chest_02_l"] + 110)
        return max(0, lo), hi
    return 250, 430


def analyse(tag):
    full = load(os.path.join(RAW, f"{tag}-front.png"))
    nos = load(os.path.join(RAW, f"{tag}-nos-front.png"))
    nob = load(os.path.join(RAW, f"{tag}-nob-front.png"))
    rows = band_rows(tag)
    band = np.zeros(full.shape[:2], bool)
    band[rows[0]:rows[1], :] = True

    shirt_only = silhouette(nob)            # garment present, skin absent
    changed = np.abs(full - nos).sum(2) > 22  # hiding the garment changed this pixel
    poke = band & shirt_only & ~changed
    # ignore 2 px of the silhouette edge (anti-aliasing) and isolated single pixels
    return full, nos, nob, poke, rows


def erode(mask, n=2):
    m = mask.copy()
    for _ in range(n):
        m = m & np.roll(m, 1, 0) & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1)
    return m


def dilate(mask, n=2):
    m = mask.copy()
    for _ in range(n):
        m = m | np.roll(m, 1, 0) | np.roll(m, -1, 0) | np.roll(m, 1, 1) | np.roll(m, -1, 1)
    return m


def main(tags):
    """Poke-through: the fabric is no longer the front-most surface inside the area it covers.

    Per value, same frozen rig/camera/light:
      -front  full body            (A)
      -nos    shirt+pouch hidden   (B)
      -nop    pouch hidden         (P: the shoulder-strap mask)
      -nob    body skin hidden     (garment layer)

    Inside the area the tank top covers at the reference value (its own 1.55 silhouette), hiding the
    garment must change the pixel. Unchanged = the body mesh (skin/bra) is drawn in front of - or
    through - the fabric: poke-through, including the case where the fabric folds back behind the
    skin (its own surface then faces away, so the shirt-only render shows background there and a
    silhouette-based mask cannot see it; this reference-area mask can).
    """
    ref = os.path.join(RAW, "d-1.55-nob-front.png")
    base = load(ref)
    area = erode(silhouette(base), 2)
    print(f"{'tag':10s} {'band rows':12s} {'poke px':>8s} {'largest blob':>12s}  notes")
    for tag in tags:
        try:
            full = load(os.path.join(RAW, f"{tag}-front.png"))
            nos = load(os.path.join(RAW, f"{tag}-nos-front.png"))
            nop = load(os.path.join(RAW, f"{tag}-nop-front.png"))
        except FileNotFoundError as e:
            print(f"{tag:10s} missing: {e}")
            continue
        rows = band_rows(tag)
        band = np.zeros(full.shape[:2], bool)
        band[rows[0]:rows[1], :] = True
        strap = dilate(np.abs(full - nop).sum(2) > 18, 3)
        bg = silhouette(full)                       # ignore the background
        unchanged = np.abs(full - nos).sum(2) < 12
        p = band & area & bg & unchanged & ~strap
        nb = sum(np.roll(np.roll(p, dy, 0), dx, 1) for dy in (-1, 0, 1) for dx in (-1, 0, 1))
        p = p & (nb >= 3)
        blobs = components(p)
        n = int(p.sum())
        note = "clean" if n == 0 else f"blobs={blobs[:3]}"
        print(f"{tag:10s} {rows[0]:4d}-{rows[1]:4d} {n:8d} {(blobs[0] if blobs else 0):12d}  {note}")
        ov = full.copy().astype(np.uint8)
        ov[p] = (255, 0, 0)
        Image.fromarray(ov).save(os.path.join(RAW, f"{tag}-poke.png"))


def components(p):
    seen = np.zeros(p.shape, bool)
    blobs = []
    ys, xs = np.where(p)
    for y, x in zip(ys, xs):
        if seen[y, x]:
            continue
        stack = [(y, x)]
        seen[y, x] = True
        cnt = 0
        while stack:
            cy, cx = stack.pop()
            cnt += 1
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    ny, nx = cy + dy, cx + dx
                    if 0 <= ny < p.shape[0] and 0 <= nx < p.shape[1] and p[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
        blobs.append(cnt)
    blobs.sort(reverse=True)
    return blobs


if __name__ == "__main__":
    main(sys.argv[1:])
