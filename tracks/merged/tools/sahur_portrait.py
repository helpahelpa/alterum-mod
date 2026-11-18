#!/usr/bin/env python3
"""Render the baked Sahur body into wardrobe portraits (RGBA, transparent background).

The wardrobe/HUD portraits are `PlayerModelData.BustPortraitSprite` sprites. The other models
ship theirs as art assets; Sahur has none, so this renders the same mesh the runtime builds
(`sahur.mset`, already yawed and scaled exactly like `SahurModel.BuildMesh`) through the
software rasteriser in models/sahur/render.py, with an alpha channel instead of a background.

  python3 tracks/merged/tools/sahur_portrait.py
      -> AlterumModelSwap.Hot/sahur_portrait.png   256x320  full body, front
      -> AlterumModelSwap.Hot/sahur_bust.png       256x256  head + shoulders, front

No Blender, no Unity: it reads the same blob the game reads, so the icon cannot drift from
the model. The 180 deg yaw and the 0.01 scale are applied here exactly as at runtime.
"""
import os
import struct
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
DATA = os.path.expanduser("~/assets/monorepo/alterum-data")
# baked .mset/.rset/.png payload is gitignored and lives outside the repo, in the data tree
MOD = os.path.join(DATA, "tracks", "merged", "mod", "AlterumModelSwap.Hot")
sys.path.insert(0, os.path.join(DATA, "models", "sahur"))

import render as R  # noqa: E402  (look_at / sample only)


def load_mset(path):
    b = open(path, "rb").read()
    assert b[:7] == b"SAHURM1", "bad magic"
    o = 8
    version, = struct.unpack_from("<i", b, o); o += 4
    assert version == 1, version
    pieces, = struct.unpack_from("<i", b, o); o += 4
    lo = np.array(struct.unpack_from("<3f", b, o), np.float64); o += 12
    hi = np.array(struct.unpack_from("<3f", b, o), np.float64); o += 12
    nv, nt = struct.unpack_from("<2i", b, o); o += 8
    meta = []
    for _ in range(pieces):
        vs, reg, vc, ts, tc = struct.unpack_from("<iB3xiii", b, o); o += 20
        meta.append((vs, reg, vc, ts, tc))
    span = hi - lo
    q = np.frombuffer(b, np.int16, nv * 3, o).reshape(nv, 3).astype(np.float64)
    o += nv * 6
    V = lo + (q + 32768.0) / 65535.0 * span
    UV = np.frombuffer(b, np.uint16, nv * 2, o).reshape(nv, 2).astype(np.float64) / 65535.0
    o += nv * 4
    NR = np.frombuffer(b, np.int8, nv * 3, o).reshape(nv, 3).astype(np.float64) / 127.0
    o += nv * 3
    idx = np.frombuffer(b, np.uint16, nt * 3, o).reshape(nt, 3).astype(np.int64)
    o += nt * 6
    assert o == len(b), (o, len(b))

    # runtime transform: 0.01 scale, 180 deg yaw about Y (DecideFlip)
    V = V * np.array([-1.0, 1.0, -1.0]) * 0.01
    NR = NR * np.array([-1.0, 1.0, -1.0])

    # one triangle soup, like the runtime's 8 submeshes: the verts are already global indices
    tris = np.vstack([idx[ts:ts + tc] for (vs, reg, vc, ts, tc) in meta])
    return V, tris, UV, NR


def shade(V, tris, UV, NR, tex, eye, target, up, w, h, fov=0.30):
    """Same maths as render.render(), plus an alpha channel; orthographic would distort the
    silhouette against the game's own art, so this keeps the (gentle) perspective."""
    Rm = R.look_at(eye, target, up)
    cam = (Rm @ (V - eye).T).T
    depth = -cam[:, 2]
    f = (h / 2) / np.tan(fov / 2)
    sx = w / 2 + f * cam[:, 0] / depth
    sy = h / 2 - f * cam[:, 1] / depth
    col = np.zeros((h, w, 3), np.float32)
    alpha = np.zeros((h, w), np.float32)
    zbuf = np.full((h, w), np.inf, np.float32)
    light = np.array([0.35, 0.72, 0.60]); light /= np.linalg.norm(light)

    for tri in tris:
        d = depth[tri]
        if np.any(d <= 1e-6):
            continue
        x = sx[tri]; y = sy[tri]
        xmin, xmax = int(np.floor(x.min())), int(np.ceil(x.max()))
        ymin, ymax = int(np.floor(y.min())), int(np.ceil(y.max()))
        if xmax < 0 or ymax < 0 or xmin >= w or ymin >= h:
            continue
        xmin, ymin = max(xmin, 0), max(ymin, 0)
        xmax, ymax = min(xmax, w - 1), min(ymax, h - 1)
        if xmax <= xmin or ymax <= ymin:
            continue
        PX, PY = np.meshgrid(np.arange(xmin, xmax + 1) + 0.5, np.arange(ymin, ymax + 1) + 0.5)
        x0, y0 = x[0], y[0]
        x1, y1 = x[1], y[1]
        x2, y2 = x[2], y[2]
        den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
        if abs(den) < 1e-12:
            continue
        l0 = ((y1 - y2) * (PX - x2) + (x2 - x1) * (PY - y2)) / den
        l1 = ((y2 - y0) * (PX - x2) + (x0 - x2) * (PY - y2)) / den
        l2 = 1.0 - l0 - l1
        m = (l0 >= -1e-6) & (l1 >= -1e-6) & (l2 >= -1e-6)
        if not m.any():
            continue
        inv = 1.0 / d
        wsum = l0[m] * inv[0] + l1[m] * inv[1] + l2[m] * inv[2]
        z = 1.0 / wsum
        yy, xx = np.nonzero(m)
        ys, xs = yy + ymin, xx + xmin
        keep = z < zbuf[ys, xs]
        if not keep.any():
            continue
        ys, xs, z = ys[keep], xs[keep], z[keep]
        a0 = (l0[m][keep] * inv[0]) / (1.0 / z)
        a1 = (l1[m][keep] * inv[1]) / (1.0 / z)
        a2 = 1.0 - a0 - a1
        n = (NR[tri[0]] * a0[:, None] + NR[tri[1]] * a1[:, None] + NR[tri[2]] * a2[:, None])
        ln = np.linalg.norm(n, axis=1, keepdims=True); ln[ln == 0] = 1
        n = n / ln
        lam = np.clip(n @ light, 0, 1) * 0.70 + 0.36
        u = UV[tri[0], 0] * a0 + UV[tri[1], 0] * a1 + UV[tri[2], 0] * a2
        v = UV[tri[0], 1] * a0 + UV[tri[1], 1] * a1 + UV[tri[2], 1] * a2
        base = R.sample(tex, u, v)
        col[ys, xs] = np.clip(base * lam[:, None], 0, 1)
        alpha[ys, xs] = 1.0
        zbuf[ys, xs] = z

    return col, alpha


def main():
    mset = os.path.join(MOD, "sahur.mset")
    albedo = os.path.join(MOD, "sahur_albedo.png")
    tex = np.asarray(Image.open(albedo).convert("RGB"), np.float32) / 255.0
    V, tris, UV, NR = load_mset(mset)
    lo, hi = V.min(axis=0), V.max(axis=0)
    print(f"mesh: {len(V)} verts, {len(tris)} tris, "
          f"bbox {hi - lo} m (target 1.796 tall)")

    up = np.array([0.0, 1.0, 0.0])
    front = np.array([0.0, 0.0, 1.0])     # after the 180 deg yaw the mask faces +Z

    jobs = [
        # name,             width, height, centre y,   half-height (m), eye distance
        ("sahur_portrait.png", 256, 320, (lo[1] + hi[1]) / 2, 0.98, 6.0),
        ("sahur_bust.png", 256, 256, hi[1] - 0.34, 0.40, 4.0),
    ]
    for name, w, h, cy, half, dist in jobs:
        cx = (lo[0] + hi[0]) / 2
        cz = (lo[2] + hi[2]) / 2
        target = np.array([cx, cy, cz])
        fov = 2.0 * np.arctan(half / (dist / 2))     # half-extent at the target plane
        eye = target + front * (dist / 2)
        col, alpha = shade(V, tris, UV, NR, tex, eye, target, up, w, h, fov=fov)
        rgba = np.zeros((h, w, 4), np.uint8)
        rgba[..., :3] = (col * 255).astype(np.uint8)
        rgba[..., 3] = (alpha * 255).astype(np.uint8)
        out = os.path.join(MOD, name)
        Image.fromarray(rgba, "RGBA").save(out)
        cov = alpha.mean() * 100
        ys, xs = np.nonzero(alpha)
        print(f"wrote {out} {w}x{h} coverage={cov:.1f}% bbox x[{xs.min()},{xs.max()}] y[{ys.min()},{ys.max()}]")


if __name__ == "__main__":
    main()
