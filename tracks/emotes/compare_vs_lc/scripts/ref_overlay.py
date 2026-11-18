#!/usr/bin/env python3
"""RETIRED reference overlay: projects ref_eval_RETIRED's canonical joints onto a real LC frame.
Kept as the record of the evidence that ref_eval's arms are wrong; ref_eval_RETIRED is HALF SCALE
(missing ScavengerModel's 2.0) - see ../EVIDENCE.txt. Do not use for new comparisons.

The LC capture's camera pose in the character's frame is fixed but its world yaw/centre is not
recorded. We fit yaw, focal length and the 2-D image offset of the pelvis to the orange suit mask,
using only torso/leg joints (so the arms cannot bend the fit), then draw every joint.
"""
import csv, json, math, os, sys
import numpy as np
from PIL import Image, ImageDraw
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from overlay import Cam, BONES, JOINTS, P, LEFT, RIGHT

HERE = os.path.dirname(os.path.abspath(__file__))
TRACK = os.path.normpath(os.path.join(HERE, "..", ".."))
DATA = os.path.expanduser("~/assets/monorepo/alterum-data")
LCR = os.path.normpath(os.path.join(DATA, "lc-reference"))
BODY = ["pelvis", "spine_01", "spine_02", "spine_03", "neck_01",
        "thigh_l", "calf_l", "foot_l", "thigh_r", "calf_r", "foot_r"]
SPAN = ["upperarm_l", "upperarm_r", "foot_l", "foot_r", "ball_l", "ball_r"]

def mask_of(path):
    a = np.asarray(Image.open(path).convert("RGB")).astype(int)
    m = ((a[:, :, 0] - a[:, :, 2]) > 18) & ((a[:, :, 0] - a[:, :, 1]) > 8) & (a[:, :, 0] > 45)
    # dilate a little so joints just inside the silhouette score 0
    out = m.copy()
    for dx in (-2, -1, 0, 1, 2):
        for dy in (-2, -1, 0, 1, 2):
            out |= np.roll(np.roll(m, dy, axis=0), dx, axis=1)
    return out

def dist(mask):
    # cheap distance-to-mask in pixels: iterative 4-neighbour dilation buckets
    d = np.full(mask.shape, 50.0)
    cur = mask.copy()
    d[cur] = 0
    for r in range(1, 50):
        nxt = cur.copy()
        nxt[1:, :] |= cur[:-1, :]; nxt[:-1, :] |= cur[1:, :]
        nxt[:, 1:] |= cur[:, :-1]; nxt[:, :-1] |= cur[:, 1:]
        d[nxt & ~cur] = r
        cur = nxt
    return d

def project(rows, yaw, f, aim, w, h):
    dirv = (math.sin(math.radians(yaw)), 0.0, math.cos(math.radians(yaw)))
    eye = (0.0, aim, 0.0)
    eye = (eye[0] + dirv[0] * 4.2, eye[1] + 0.2, eye[2] + dirv[2] * 4.2)
    cam = Cam(eye, (0.0, aim, 0.0), 42, w, h)
    cam.f = f
    return {j: cam.project(P(rows, j)) for j in JOINTS}

def fit(rows, dt, mask, w, h):
    """yaw + focal + image offset, with the projected body height pinned to the suit mask height
    (otherwise the objective collapses the figure onto the mask) and the centroid initialising the
    offset."""
    ys, xs = np.where(mask)
    mh = ys.max() - ys.min()
    mcx, mcy = xs.mean(), ys.mean()
    best = None
    for f in range(620, 2200, 25):
        for yaw in range(0, 360, 6):
            pr = project(rows, yaw, f, 1.0, w, h)
            if any(pr[j] is None for j in BODY): continue
            base = np.array([pr[j] for j in BODY], dtype=float)
            sp = np.array([pr[j] for j in SPAN], dtype=float)
            ph = sp[:, 1].max() - sp[:, 1].min()
            if abs(ph - mh) > 30: continue
            dx0, dy0 = mcx - base[:, 0].mean(), mcy - base[:, 1].mean()
            for ddx in (-6, -3, 0, 3, 6):
                for ddy in (-6, -3, 0, 3, 6):
                    dx, dy = dx0 + ddx, dy0 + ddy
                    px = np.clip((base[:, 0] + dx).astype(int), 0, w - 1)
                    py = np.clip((base[:, 1] + dy).astype(int), 0, h - 1)
                    s = dt[py, px].mean() + 0.6 * abs(ph - mh)
                    if best is None or s < best[0]:
                        best = (s, yaw, f, dx, dy)
    return best

if __name__ == "__main__":
    clip, frame = sys.argv[1], int(sys.argv[2])
    out = sys.argv[3]
    lcf = os.path.join(LCR, "frames", clip, f"seq_{clip}_{frame:03d}.png")
    ref = list(csv.DictReader(open(os.path.join(TRACK, "compare", "ref", clip + ".csv"))))
    # ref row nearest the phase the LC frame sat on
    csvrows = list(csv.DictReader(open(os.path.join(LCR, "frames", clip, f"seq_{clip}.csv"))))
    ph = float(csvrows[frame]["norm1"]) % 1.0
    dur = float(ref[-1]["t"]) + 1 / 60
    row = min(ref, key=lambda r: abs(((float(r["t"]) / dur) % 1.0) - ph))
    img = Image.open(lcf).convert("RGB")
    w, h = img.size
    dt = dist(mask_of(lcf))
    mask = mask_of(lcf)
    s, yaw, f, dx, dy = fit(row, dt, mask, w, h)
    print(f"{clip}#{frame:03d} phase={ph:.3f} fit score={s:.2f}px yaw={yaw:.0f} f={f} shift=({dx:.0f},{dy:.0f})")
    pr = project(row, yaw, f, 1.0, w, h)
    d = ImageDraw.Draw(img)
    for a, b in BONES:
        if pr.get(a) and pr.get(b):
            col = (0, 255, 90) if (a in LEFT or b in LEFT) else ((90, 200, 255) if (a in RIGHT or b in RIGHT) else (255, 240, 0))
            d.line([(pr[a][0] + dx, pr[a][1] + dy), (pr[b][0] + dx, pr[b][1] + dy)], fill=col, width=2)
    img.save(out)
