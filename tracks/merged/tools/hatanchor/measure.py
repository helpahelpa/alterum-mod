#!/usr/bin/env python3
"""Per-model head geometry -> HatAnchor. Reads the baked blobs directly.

For each roster spec: parse its .mset/.rset, take the vertices whose dominant bone is the HEAD
(rigid: the `head` region piece; rigged: weight >= 0.5 on the spec's head bone), and report

  * head bounds in the model's own root space at bind (metres, Y up),
  * the same box in the hat axes (rSrc^-1) -- the frame the cosmetic fit consumes,
  * the same box in the game head bone's local frame (H_ref^-1),
  * the resulting HatAnchor { Offset, Scale, Rotation } in the game head bone's local frame.

Reference frames (measured from the live native body, James male, and from the cosmetic pack):
  H_ref  = game `head` bone bind transform in model-root space
  rSrc   = cosmetic pack source head rotation relative to its model
  A_src  = cosmetic pack source head box anchor point (centerX, top, centerZ)
"""
import json
import math
import os
import struct
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", ".."))
DATA = os.environ.get("ALTERUM_DATA_DIR", os.path.expanduser("~/assets/monorepo/alterum-data"))
HOT = os.path.join(DATA, "tracks/merged/mod/AlterumModelSwap.Hot")

H_REF_P = (0.0, 1.67700, 0.01629)
H_REF_Q = (-0.50000, 0.50000, -0.50000, 0.50000)   # xyzw
SRC_Q = (0.01131, 0.03390, -0.00038, 0.99936)     # RotRelModel, xyzw
SRC_W, SRC_TOP, SRC_CX, SRC_CZ = 0.83511, 0.76573, 0.00009, 0.02436

# ---------------------------------------------------------------- quaternion helpers (xyzw)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])


def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def q2m(q):
    x, y, z, w = q
    return [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ]


def m2q(m):
    tr = m[0][0] + m[1][1] + m[2][2]
    if tr > 0:
        s = math.sqrt(tr + 1.0) * 2
        w = 0.25 * s
        x = (m[2][1] - m[1][2]) / s
        y = (m[0][2] - m[2][0]) / s
        z = (m[1][0] - m[0][1]) / s
    elif m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]) * 2
        w = (m[2][1] - m[1][2]) / s
        x = 0.25 * s
        y = (m[0][1] + m[1][0]) / s
        z = (m[0][2] + m[2][0]) / s
    elif m[1][1] > m[2][2]:
        s = math.sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]) * 2
        w = (m[0][2] - m[2][0]) / s
        x = (m[0][1] + m[1][0]) / s
        y = 0.25 * s
        z = (m[1][2] + m[2][1]) / s
    else:
        s = math.sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]) * 2
        w = (m[1][0] - m[0][1]) / s
        x = (m[0][2] + m[2][0]) / s
        y = (m[1][2] + m[2][1]) / s
        z = 0.25 * s
    return (x, y, z, w)


def qnormalize(q):
    l = math.sqrt(sum(c * c for c in q))
    return tuple(c / l for c in q)


def matmul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def trs(t, q, s=(1.0, 1.0, 1.0)):
    m = [[0.0] * 4 for _ in range(4)]
    r = q2m(q)
    for i in range(3):
        for j in range(3):
            m[i][j] = r[i][j] * s[j]
        m[i][3] = t[i]
    m[3][3] = 1.0
    return m


def xform(m, v):
    return tuple(m[i][0] * v[0] + m[i][1] * v[1] + m[i][2] * v[2] + m[i][3] for i in range(3))


def box(points):
    xs = [p[0] for p in points]
    ys = [p[1] for p in points]
    zs = [p[2] for p in points]
    lo = (min(xs), min(ys), min(zs))
    hi = (max(xs), max(ys), max(zs))
    size = tuple(hi[i] - lo[i] for i in range(3))
    centre = tuple((hi[i] + lo[i]) * 0.5 for i in range(3))
    return lo, hi, centre, size


# ---------------------------------------------------------------- blob parsers


def parse_mset(path, spec):
    b = open(path, "rb").read()
    assert b[:7] == b"SAHURM1", path
    o = 8
    version, pieces = struct.unpack_from("<ii", b, o); o += 8
    lo = struct.unpack_from("<3f", b, o); o += 12
    hi = struct.unpack_from("<3f", b, o); o += 12
    nvert, ntri = struct.unpack_from("<ii", b, o); o += 8
    meta = []
    for _ in range(pieces):
        vs, region = struct.unpack_from("<iB", b, o); o += 4 + 4
        vc, ts, tc = struct.unpack_from("<iii", b, o); o += 12
        meta.append((vs, region, vc, ts, tc))
    span = tuple(hi[i] - lo[i] for i in range(3))
    pos = []
    for i in range(nvert):
        q = struct.unpack_from("<3h", b, o + i * 6)
        p = [lo[j] + (q[j] + 32768.0) / 65535.0 * span[j] for j in range(3)]
        if spec.get("yaw180"):
            p[0], p[2] = -p[0], -p[2]
        p = [p[j] * 0.01 for j in range(3)]
        p[1] += spec.get("yoffset", 0.0)
        pos.append(tuple(p))
    region = [0] * nvert
    for vs, r, vc, _, _ in meta:
        for v in range(vc):
            region[vs + v] = r
    head_region = spec["region_bones"].index("head")
    head = [pos[i] for i in range(nvert) if region[i] == head_region]
    return pos, head, {"pieces": pieces, "verts": nvert, "tris": ntri}


def parse_rset(path, spec):
    b = open(path, "rb").read()
    assert b[:7] == b"SAHRRG1", path
    o = 8
    version, flags, nv, nt, nb, ns, nm = struct.unpack_from("<7i", b, o); o += 28
    lo = struct.unpack_from("<3f", b, o); o += 12
    hi = struct.unpack_from("<3f", b, o); o += 12
    uvlo = struct.unpack_from("<2f", b, o); o += 8
    uvhi = struct.unpack_from("<2f", b, o); o += 8
    name_len = struct.unpack_from("<i", b, o)[0]; o += 4
    names = b[o:o + name_len].decode("utf-8").split("\n"); o += name_len
    parent = []
    bpos = []
    brot = []
    for i in range(nb):
        parent.append(struct.unpack_from("<b", b, o)[0]); o += 2
        bpos.append(struct.unpack_from("<3f", b, o)); o += 12
        brot.append(qnormalize(struct.unpack_from("<4f", b, o))); o += 16
    subs = []
    for _ in range(ns):
        tn, mat, kind = struct.unpack_from("<iBB", b, o); o += 8
        subs.append((tn, mat, kind))
    span = tuple(hi[i] - lo[i] for i in range(3))
    pos = []
    for i in range(nv):
        q = struct.unpack_from("<3h", b, o + i * 6)
        pos.append(tuple(lo[j] + (q[j] + 32768.0) / 65535.0 * span[j] for j in range(3)))
    o += nv * 6
    o += nv * 4
    o += nv * 3
    bidx = b[o:o + nv * 4]; o += nv * 4
    wts = b[o:o + nv * 4]; o += nv * 4
    # bone chain in model/root space (yaw 0 for every baked asset here, checked by the runtime log)
    chain = []
    for i in range(nb):
        local = trs(bpos[i], brot[i])
        chain.append(matmul(chain[parent[i]], local) if parent[i] >= 0 else local)
    head_name = spec["head_bone"].lower()
    head_index = None
    for i, n in enumerate(names):
        if n.strip().lower() == head_name:
            head_index = i
            break
    assert head_index is not None, (path, head_name, names[:5])
    head = []
    head_pts = []
    for v in range(nv):
        w = 0.0
        for k in range(4):
            if bidx[v * 4 + k] == head_index:
                w += wts[v * 4 + k] / 255.0
        if w >= 0.5:
            head.append(pos[v])
            head_pts.append(v)
    return pos, head, {"bones": nb, "verts": nv, "tris": nt, "subs": ns, "head_bone": names[head_index].strip()}


# ---------------------------------------------------------------- specs (mirrors ModelRoster)

SPECS = [
    dict(slug="sahur-roblox", label="Sahur", kind="rigged", blob="sahur_roblox.rset",
         head_bone="Head"),
    dict(slug="golubiro", label="Golubiro", kind="rigid", blob="shpioniro-golubiro.mset",
         yaw180=False, yoffset=0.0054,
         region_bones=["spine_01", "head", "upperarm_l", "upperarm_r", "thigh_l", "thigh_r"]),
    dict(slug="sigma-labubu", label="Labubu (demo only)", kind="rigid", blob="sigma-labubu-r15.mset",
         yaw180=False, yoffset=0.8556,
         region_bones=["spine_01", "head", "upperarm_l", "upperarm_r", "thigh_l", "thigh_r"]),
    dict(slug="capuchino", label="Capuchino", kind="rigged", blob="balerina-capuchino.rset",
         head_bone="DEF-spine.006"),
]
for slug, head in [
    ("tc2-scout", "head"), ("tc2-soldier", "head"), ("tc2-pyro", "head"),
    ("tc2-demoman", "head"), ("tc2-heavy", "head"), ("tc2-engineer", "head"),
    ("tc2-medic", "head"), ("tc2-sniper", "head"), ("tc2-spy", "head"),
    ("tc2-saxton", "head"), ("peter-griffin", "head"),
]:
    SPECS.append(dict(slug=slug, label=slug, kind="rigged", blob=slug + ".rset", head_bone=head))


def main():
    q_src_inv = qconj(SRC_Q)
    q_h_inv = qconj(qnormalize(H_REF_Q))
    R = qmul(q_h_inv, SRC_Q)                      # hat -> head-local
    a_src = (SRC_CX, SRC_TOP, SRC_CZ)
    out = []
    for spec in SPECS:
        path = os.path.join(HOT, spec["blob"])
        if not os.path.exists(path):
            print("MISSING", path)
            continue
        if spec["kind"] == "rigid":
            _, head, info = parse_mset(path, spec)
        else:
            _, head, info = parse_rset(path, spec)
        root_lo, root_hi, root_c, root_size = box(head)
        # head-relative (origin at the game head bone), then rotated into each frame
        rel = [(p[0] - H_REF_P[0], p[1] - H_REF_P[1], p[2] - H_REF_P[2]) for p in head]
        hat = [qrot(q_src_inv, p) for p in rel]
        hlo, hhi, hc, hsize = box(hat)
        headloc = [qrot(q_h_inv, p) for p in rel]
        glo, ghi, gc, gsize = box(headloc)
        width = hsize[0]
        top = hhi[1]
        cx = hc[0]
        cz = hc[2]
        scale = width / SRC_W
        # localPos = R * (A_hat - A_src * scale)
        k = scale
        delta = (cx - a_src[0] * k, top - a_src[1] * k, cz - a_src[2] * k)
        offset = qrot(R, delta)
        anchor_rot = R
        out.append(dict(
            slug=spec["slug"], kind=spec["kind"], blob=spec["blob"], info=info,
            root_box=dict(lo=root_lo, hi=root_hi, centre=root_c, size=root_size),
            hat_box=dict(lo=hlo, hi=hhi, width=width, top=top, cx=cx, cz=cz, size=hsize),
            head_box=dict(lo=glo, hi=ghi, centre=gc, size=gsize),
            anchor=dict(offset=offset, scale=scale, rotation=anchor_rot),
        ))
    json.dump(out, open(os.path.join(os.path.dirname(__file__), "anchors.json"), "w"), indent=1)

    print(f"{'model':16s} {'kind':7s} {'head root box lo..hi (m)':44s} {'size':22s} "
          f"{'hatW':6s} {'hatTop':7s} {'scale':6s}  anchor offset (head-local, m)")
    for a in out:
        lo = a["root_box"]["lo"]; hi = a["root_box"]["hi"]; sz = a["root_box"]["size"]
        hb = a["hat_box"]; an = a["anchor"]
        print(f"{a['slug']:16s} {a['kind']:7s} "
              f"({lo[0]:6.3f},{lo[1]:6.3f},{lo[2]:6.3f})..({hi[0]:6.3f},{hi[1]:6.3f},{hi[2]:6.3f}) "
              f"({sz[0]:5.3f},{sz[1]:5.3f},{sz[2]:5.3f}) "
              f"{hb['width']:6.3f} {hb['top']:7.3f} {an['scale']:6.4f}  "
              f"({an['offset'][0]:7.4f},{an['offset'][1]:7.4f},{an['offset'][2]:7.4f})")
    print()
    print("R (hat -> head-local, xyzw) =", tuple(round(c, 5) for c in out[0]["anchor"]["rotation"]))


if __name__ == "__main__":
    main()
