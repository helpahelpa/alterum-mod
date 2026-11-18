#!/usr/bin/env python3
"""Render the baked Roblox Sahur body into wardrobe portraits (RGBA, transparent background).

Renders the portrait sheet of the rigged variant: it reads `sahur_roblox.rset` (the
exact blob the runtime builds its SkinnedMeshRenderer from) and the downscaled albedo next to it,
in bind pose, through the software rasteriser in models/sahur/render.py.

  python3 tracks/merged/tools/sahur_roblox_portrait.py
      -> AlterumModelSwap.Hot/sahur_roblox_portrait.png   256x320  full body, front
      -> AlterumModelSwap.Hot/sahur_roblox_bust.png        256x256  head + shoulders, front

The bake already put the mesh in metres, mirrored to the game's handedness, facing +Z: the same
space the runtime's bind pose uses, so nothing is transformed here beyond the camera.
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

import render as R  # noqa: E402


def load_rset(path):
    b = open(path, "rb").read()
    assert b[:8] == b"SAHRRG1\x00", "bad magic"
    o = 8
    ver, flags = struct.unpack_from("<II", b, o); o += 8
    nv, nt, nb, ns, nm = struct.unpack_from("<IIIII", b, o); o += 20
    lo = np.array(struct.unpack_from("<3f", b, o), np.float64); o += 12
    hi = np.array(struct.unpack_from("<3f", b, o), np.float64); o += 12
    uvlo = np.array(struct.unpack_from("<2f", b, o), np.float64); o += 8
    uvhi = np.array(struct.unpack_from("<2f", b, o), np.float64); o += 8
    namelen, = struct.unpack_from("<I", b, o); o += 4 + namelen
    o += nb * 30
    subs = []
    for _ in range(ns):
        tn, mat, kind = struct.unpack_from("<IBB", b, o); o += 8
        subs.append((tn, mat, kind))
    pos = np.frombuffer(b, "<i2", nv * 3, o).reshape(nv, 3).astype(np.float64); o += nv * 6
    V = lo + (pos + 32768.0) / 65535.0 * (hi - lo)
    uvq = np.frombuffer(b, "<u2", nv * 2, o).reshape(nv, 2).astype(np.float64); o += nv * 4
    UV = uvlo + uvq / 65535.0 * (uvhi - uvlo)
    NR = np.frombuffer(b, "i1", nv * 3, o).reshape(nv, 3).astype(np.float64) / 127.0; o += nv * 3
    o += nv * 8
    idx = np.frombuffer(b, "<u2", nt * 3, o).reshape(nt, 3); o += nt * 6
    tris = np.vstack([idx[sum(s[0] for s in subs[:k]):sum(s[0] for s in subs[:k + 1])]
                      for k in range(len(subs))])
    return V, tris, UV, NR, subs, flags


def shade(V, tris, UV, NR, tex, eye, target, up, w, h, fov):
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
    V, tris, UV, NR, subs, flags = load_rset(os.path.join(MOD, "sahur_roblox.rset"))
    tex = np.asarray(Image.open(os.path.join(MOD, "sahur_roblox_albedo.png")).convert("RGB"),
                     np.float32) / 255.0
    lo, hi = V.min(axis=0), V.max(axis=0)
    print(f"mesh: {len(V)} verts {len(tris)} tris, bbox {np.round(lo, 3)}..{np.round(hi, 3)} "
          f"height={hi[1] - lo[1]:.3f} m mirror={bool(flags & 1)} submeshes={len(subs)}")

    up = np.array([0.0, 1.0, 0.0])
    front = np.array([0.0, 0.0, 1.0])
    jobs = [
        ("sahur_roblox_portrait.png", 256, 320, (lo[1] + hi[1]) / 2, 0.99, 6.0),
        ("sahur_roblox_bust.png", 256, 256, hi[1] - 0.30, 0.36, 4.0),
    ]
    for name, w, h, cy, half, dist in jobs:
        cx = (lo[0] + hi[0]) / 2
        cz = (lo[2] + hi[2]) / 2
        target = np.array([cx, cy, cz])
        fov = 2.0 * np.arctan(half / (dist / 2))
        eye = target + front * (dist / 2)
        col, alpha = shade(V, tris, UV, NR, tex, eye, target, up, w, h, fov)
        rgba = np.zeros((h, w, 4), np.uint8)
        rgba[..., :3] = (col * 255).astype(np.uint8)
        rgba[..., 3] = (alpha * 255).astype(np.uint8)
        out = os.path.join(MOD, name)
        Image.fromarray(rgba, "RGBA").save(out)
        ys, xs = np.nonzero(alpha)
        print(f"wrote {out} {w}x{h} coverage={alpha.mean() * 100:.1f}% "
              f"bbox x[{xs.min()},{xs.max()}] y[{ys.min()},{ys.max()}]")


if __name__ == "__main__":
    main()
