#!/usr/bin/env python3
"""Localised hat-versus-no-hat measurement on the two 1920x1080 showcase stills.

The showcase camera's pose is derived from the animated body bounds, so the two captures are a few
px apart; a whole-frame diff is dominated by that shift. Instead:
  1. align the two frames with a sub-pixel translation measured on the FACE box (present in both),
  2. measure the change inside the HAT box (where the hat is), and inside the FACE and TORSO boxes
     as controls.
Boxes are read off the 1920x1080 still around the head; the hat sits above the hairline.
"""
import numpy as np
from PIL import Image

S = "/mnt/c/AlterumT3/BepInEx/screens/t3"
HAT   = (925, 40, 1115, 245)      # where the party hat is (above the hair)
FACE  = (935, 250, 1125, 345)     # face + hair: identical content, used for alignment
TORSO = (860, 420, 1180, 780)     # vest/shirt/belt: control
HEADZONE = (860, 0, 1200, 400)    # whole head zone incl. hat, for bbox accounting

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

def mad(a, b, box, dx, dy):
    return float(np.abs(roi(b, box, dx, dy) - roi(a, box)).mean())

def align(a, b, box, cy=110, cx=14):
    """sub-pixel (dx,dy) with b[y+dy,x+dx] == a[y,x]"""
    best = (1e18, 0.0, 0.0)
    for dy in range(-cy, cy + 1, 2):
        for dx in range(-cx, cx + 1, 2):
            m = mad(a, b, box, dx, dy)
            if m < best[0]: best = (m, float(dx), float(dy))
    _, bx, by = best
    for dy in np.arange(by - 2, by + 2.01, 0.25):
        for dx in np.arange(bx - 2, bx + 2.01, 0.25):
            m = mad(a, b, box, dx, dy)
            if m < best[0]: best = (m, float(dx), float(dy))
    return best

def stats(a, b, box, thr):
    d = np.abs(roi(a, box) - roi(b, box)).max(axis=2)
    m = d > thr
    n = int(m.sum())
    if n:
        ys, xs = np.nonzero(m)
        bb = (box[0] + int(xs.min()), box[1] + int(ys.min()), box[0] + int(xs.max()), box[1] + int(ys.max()))
        return n, 100.0 * n / d.size, float(d.mean()), bb
    return 0, 0.0, float(d.mean()), None

def hat_coloured(img, box):
    """Saturated, bright pixels: the party hat's confetti/pom colours."""
    hsv = np.asarray(Image.open(PATHS[0]).convert("HSV"))[box[1]:box[3], box[0]:box[2]]
    sat = np.asarray(Image.fromarray(np.clip(img, 0, 255).astype(np.uint8)).convert("HSV"))[:, :, 1]
    sub = sat[box[1]:box[3], box[0]:box[2]]
    return int((sub > 150).sum()), int(sub.size)

def run(name, pa, pb):
    a, b = load(pa), load(pb)
    m, dx, dy = align(a, b, FACE)
    print(f"\n{name}")
    print(f"  A={pa}\n  B={pb}")
    print(f"  face-box alignment: dx={dx:.2f} dy={dy:.2f} px (residual MAD {m:.2f})")
    bs = sample(b, np.arange(b.shape[1], dtype=np.float32) + dx, np.arange(b.shape[0], dtype=np.float32) + dy)
    for label, box in (("HAT  ", HAT), ("FACE ", FACE), ("TORSO", TORSO)):
        for thr in (8, 32):
            n, pct, mean, bb = stats(a, bs, box, thr)
            print(f"  {label} >{thr:2d}: {n:6d} px of {box[2]-box[0]}x{box[3]-box[1]} ({pct:5.2f}%) mean={mean:6.2f}"
                  + (f"  changed bbox=({bb[0]},{bb[1]})-({bb[2]},{bb[3]})" if bb else ""))
        sa, ta = hat_coloured(a, box); sb, tb = hat_coloured(bs, box)
        print(f"  {label} saturated px (S>150): A={sa}  B={sb}  of {ta}")
    return a, bs

PATHS = [f"{S}/pair_hat_A/frame_0001.png", f"{S}/pair_none/frame_0001.png"]
if __name__ == "__main__":
    print("method noise floor first:")
    load_a, bs = run("CONTROL  hat A vs hat B (same equipment, pose jitter only)",
                     f"{S}/pair_hat_A/frame_0001.png", f"{S}/pair_hat_B/frame_0001.png")
    good_a, good_b = run("EXPERIMENT  hat (A) vs no cosmetics (B)",
                         f"{S}/pair_hat_A/frame_0001.png", f"{S}/pair_none/frame_0001.png")
    Image.fromarray(np.clip(good_b, 0, 255).astype(np.uint8)).save("out/pair_none_aligned.png")
    d = np.abs(good_a - good_b).max(axis=2)
    img = np.zeros((*d.shape, 3), dtype=np.uint8)
    img[..., 0] = np.clip(d * 4, 0, 255)
    g = (np.asarray(Image.open(PATHS[0]).convert("L"), dtype=np.float32) * 0.5).astype(np.uint8)
    img[..., 1] = g; img[..., 2] = g
    Image.fromarray(img[0:420, 820:1300]).save("out/pair_diff_head.png")
    print("\nwrote out/pair_none_aligned.png, out/pair_diff_head.png")
