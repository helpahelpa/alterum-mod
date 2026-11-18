#!/usr/bin/env python3
"""Equipped-vs-unequipped pair from a pinned camera pose: no image registration at all.

Both captures were made through ClipRecorder with an explicit `eye=`/`lookat=` pose, so the camera
does not derive from body bounds (the old bounds-derived shot moved 83 px when the cosmetic was
removed, because body bounds include the cosmetic). Any remaining difference is the accessory plus
the body's idle animation, so a same-equipment control pair (A vs B, a few seconds apart) measures
the animation noise floor in the same boxes.

Run from tracks/cosmetics:  python3 demo/pinned_pair_diff.py
"""
import numpy as np
from PIL import Image

S = "/mnt/c/AlterumT3/BepInEx/screens/t3"
OUT = "demo/out"

A = f"{S}/pair3_hat_A/frame_0001.png"    # party hat equipped
B = f"{S}/pair3_hat_B/frame_0001.png"    # party hat equipped, later (control)
N = f"{S}/pair3_none/frame_0001.png"     # nothing equipped

HAT = (800, 100, 1130, 470)      # party hat + the gap above the hair
FACE = (860, 450, 1090, 830)     # face/hair: unchanged content, the control zone
TORSO = (780, 850, 1200, 1070)   # vest/tie/shirt: unchanged content, the control zone


def load(p):
    return np.asarray(Image.open(p).convert("RGB"), dtype=np.float32)


def zone(d, box, thr):
    x0, y0, x1, y1 = box
    sub = d[y0:y1, x0:x1]
    m = sub > thr
    n = int(m.sum())
    total = sub.size
    if n:
        ys, xs = np.nonzero(m)
        bb = (x0 + int(xs.min()), y0 + int(ys.min()), x0 + int(xs.max()), y0 + int(ys.max()))
    else:
        bb = None
    return n, 100.0 * n / total, float(sub.mean()), bb


def compare(name, pa, pb):
    a, b = load(pa), load(pb)
    d = np.abs(a - b).max(axis=2)
    print(f"\n{name}\n  A={pa}\n  B={pb}")
    print(f"  whole frame: mean={d.mean():.2f} p99={np.percentile(d, 99):.1f} max={d.max():.0f}")
    for label, box in (("HAT  ", HAT), ("FACE ", FACE), ("TORSO", TORSO)):
        for thr in (8, 32, 64):
            n, pct, mean, bb = zone(d, box, thr)
            extra = f"  changed bbox={bb}" if bb else ""
            print(f"  {label} >{thr:2d}: {n:7d} px ({pct:5.2f}% of box) mean={mean:6.2f}{extra}")
    return d


hat_a = load(A)
hat_b = load(B)
none = load(N)
print("pinned camera pose: eye=(4.01,2.01,-7.61) lookat=(5.31,1.88,-7.56) fov=40 1920x1080")
print(f"images: A={A} B={B} N={N}")

compare("CONTROL  hat A vs hat B (same equipment, idle-animation noise floor)", A, B)
d = compare("EXPERIMENT  hat vs no cosmetics (same pinned camera pose)", A, N)

# Diff visualisation: red = removed/added pixels above the control noise, grey = image A.
ctrl = np.abs(hat_a - hat_b).max(axis=2)
exp = d
img = np.zeros((*exp.shape, 3), dtype=np.uint8)
g = (np.asarray(Image.open(A).convert("L"), dtype=np.float32) * 0.45).astype(np.uint8)
img[..., 1] = g
img[..., 2] = g
img[..., 0] = np.clip(g.astype(np.float32) * 0.45 + exp * 2.0, 0, 255).astype(np.uint8)
Image.fromarray(img[0:900, 640:1280]).save(f"{OUT}/pair_pinned_diff_head.png")

# Experiment over control, per zone: >1 means the change exceeds the idle-animation noise floor.
print("\n  zone        control mean   experiment mean   ratio")
for label, box in (("HAT  ", HAT), ("FACE ", FACE), ("TORSO", TORSO)):
    cm = float(zone(ctrl, box, 256)[2])
    em = float(zone(exp, box, 256)[2])
    print(f"  {label}      {cm:8.2f}      {em:8.2f}      {em / max(cm, 1e-6):5.2f}x")

print(f"\nwrote {OUT}/pair_pinned_diff_head.png")
