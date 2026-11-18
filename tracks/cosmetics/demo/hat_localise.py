#!/usr/bin/env python3
"""Where did the change happen? Tile-based localisation of the hat-vs-none difference.

Uses the same face-box sub-pixel alignment as hat_diff.py, then reports, per threshold, the changed
pixel count over the whole frame plus the bbox, and a 32x32 tile map over the body zone so a solid
changed region (the hat) is distinguishable from scattered registration noise.
"""
import numpy as np
from PIL import Image

S = "/mnt/c/AlterumT3/BepInEx/screens/t3"
FACE = (935, 250, 1125, 345)
BODY = (760, 0, 1420, 1080)
T = 32

def load(p): return np.asarray(Image.open(p).convert("RGB"), dtype=np.float32)

def sample(img, xs, ys):
    H, W, _ = img.shape
    xs = np.clip(xs, 0, W - 2); ys = np.clip(ys, 0, H - 2)
    x0 = np.floor(xs).astype(int); y0 = np.floor(ys).astype(int)
    fx = (xs - x0)[None, :, None]; fy = (ys - y0)[:, None, None]
    a = img[y0][:, x0]; b = img[y0][:, x0 + 1]
    c = img[y0 + 1][:, x0]; d = img[y0 + 1][:, x0 + 1]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy

def roi(img, box, dx=0.0, dy=0.0):
    x0, y0, x1, y1 = box
    return sample(img, np.arange(x0, x1, dtype=np.float32) + dx, np.arange(y0, y1, dtype=np.float32) + dy)

def align(a, b, box, cy=110, cx=14):
    best = (1e18, 0.0, 0.0)
    for dy in range(-cy, cy + 1, 2):
        for dx in range(-cx, cx + 1, 2):
            m = float(np.abs(roi(b, box, dx, dy) - roi(a, box)).mean())
            if m < best[0]: best = (m, float(dx), float(dy))
    _, bx, by = best
    for dy in np.arange(by - 2, by + 2.01, 0.5):
        for dx in np.arange(bx - 2, bx + 2.01, 0.5):
            m = float(np.abs(roi(b, box, dx, dy) - roi(a, box)).mean())
            if m < best[0]: best = (m, float(dx), float(dy))
    return best

def report(name, pa, pb):
    a, b = load(pa), load(pb)
    m, dx, dy = align(a, b, FACE)
    bs = sample(b, np.arange(b.shape[1], dtype=np.float32) + dx, np.arange(b.shape[0], dtype=np.float32) + dy)
    print(f"\n{name}\n  alignment dx={dx:.2f} dy={dy:.2f} (face residual MAD {m:.2f})")
    d = np.abs(a - bs).max(axis=2)
    for thr in (8, 32, 64, 96, 128):
        mk = d > thr
        n = int(mk.sum())
        if n:
            ys, xs = np.nonzero(mk)
            print(f"  thr {thr:3d}: {n:7d} px ({100.0*n/mk.size:5.2f}% of frame)  bbox=({xs.min()},{ys.min()})-({xs.max()},{ys.max()})")
        else:
            print(f"  thr {thr:3d}: 0 px")
    # tile map over the body zone
    x0, y0, x1, y1 = BODY
    sub = (d[y0:y1, x0:x1] > 32)
    th, tw = sub.shape[0] // T, sub.shape[1] // T
    tiles = sub[:th*T, :tw*T].reshape(th, T, tw, T).mean(axis=(1, 3))
    hot = tiles > 0.5; warm = (tiles > 0.25) & ~hot
    print(f"  tiles with >50% pixels changed (32x32): {int(hot.sum())}")
    if hot.any():
        ys, xs = np.nonzero(hot)
        print(f"    hot tile bbox: x={x0+xs.min()*T}..{x0+(xs.max()+1)*T}  y={y0+ys.min()*T}..{y0+(ys.max()+1)*T}")
        print(f"    hot tiles changed-pixel share: {100.0*sub[:th*T,:tw*T].sum()/max(1,int(hot.sum())*T*T):.1f}% of their area")
    print(f"  tiles with 25-50% changed: {int(warm.sum())};  tiles with <10% changed: {int((tiles<0.10).sum())} of {th*tw}")

report("CONTROL  hat vs hat", f"{S}/pair_hat_A/frame_0001.png", f"{S}/pair_hat_B/frame_0001.png")
report("EXPERIMENT  hat vs no cosmetics", f"{S}/pair_hat_A/frame_0001.png", f"{S}/pair_none/frame_0001.png")
