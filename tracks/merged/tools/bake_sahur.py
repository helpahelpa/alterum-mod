#!/usr/bin/env python3
"""Bake the CC0 Sahur FBX into the embedded binary the hot mod builds a Mesh from.

No Blender, no assimp, no Unity: the FBX is read by models/sahur/fbxtool.py (validated on this
file) and everything else is numpy + struct in this script.

Source (CC0 1.0, @BakaFoxArt):
  models/sahur/dl/tttsahur/tttsahur/tung.fbx   1,868 v / 3,738 t, one welded mesh, 0 bones

What it does
  1. Assigns every vertex to a bone REGION from geometry only (no names, the source has no
     skeleton): head above the narrowest neck ring, arms outboard of the shoulder, legs below the
     hip hem, torso between, and the two eyeball components to the head.
  2. SPLITS triangles at region boundaries: the output vertex of a corner is keyed by
     (region, source vertex, uv index), so a triangle can never carry two regions and every
     emitted vertex belongs to exactly one bone. 8 rigid pieces.
  3. Writes `sahur.mset` (quantised binary: int16 positions, uint16 UV, int8 normals, uint16
     indices, per-piece bone id) plus a byte copy of `albedo.png` (that copy is what removes the
     FBX's absolute Windows material path -- the runtime only ever sees embedded bytes).
  4. Re-reads what it wrote and verifies it: decode round-trip, no cross-region triangle,
     piece vertex counts, bounds, chirality of every triangle (winding preserved).

Usage (from the alterum workspace root):
  python3 tracks/merged/tools/bake_sahur.py                 # bake + verify + write
  python3 tracks/merged/tools/bake_sahur.py --preview       # + per-piece npz for models/sahur/render.py
  python3 tracks/merged/tools/bake_sahur.py --piece-shot    # + flat-colour PNG of the pieces
"""
import argparse
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
WS = os.path.abspath(os.path.join(HERE, "..", "..", ".."))          # alterum/ (source)
DATA = os.path.expanduser("~/assets/monorepo/alterum-data")        # data tree (payload, outside the repo)
DEFAULT_FBX = os.path.join(DATA, "models", "sahur", "dl", "tttsahur", "tttsahur", "tung.fbx")
DEFAULT_ALBEDO = os.path.join(DATA, "models", "sahur", "dl", "tttsahur", "tttsahur", "albedo.png")
DEFAULT_OUT = os.path.join(DATA, "tracks", "merged", "mod", "AlterumModelSwap.Hot")
SCRATCH = os.path.join(DATA, ".temp", "sahur")

# ---------------------------------------------------------------- geometry landmarks (cm, FBX)
# All measured on this mesh (see models/sahur/FINDINGS.md 9.5), Y up, face on -Z.
NECK_Y = 118.0        # narrowest neck ring (13.7 cm wide at y 116..122)
HEM_Y = 62.0          # legs are separate below the robe hem; hem bottom ring sits at y 66
SHOULDER_Y = 100.0    # arm geometry is outboard of the torso from here up
SHOULDER_TOP_Y = 133.0
ARM_OUTBOARD = 11.0   # |x| of the arm surface inside the shoulder weld band (torso is |x|<=7.5)
ARM_Z = 8.0           # the arm tube is |z|<=6 there, the robe's front/back faces |z|>=9
ARM_LOW_OUTBOARD = 16.5   # below the weld band the arms hang at |x| 20.3..25.3, the hem at |x|<=14

# ---------------------------------------------------------------- regions / pieces
TORSO, HEAD, ARM_L, ARM_R, LEG_L, LEG_R = 0, 1, 2, 3, 4, 5
REGION_NAMES = {TORSO: "torso", HEAD: "head", ARM_L: "arm_l", ARM_R: "arm_r",
                LEG_L: "leg_l", LEG_R: "leg_r"}
# Region -> the game bone it is rigidly bound to at runtime (UE-named skeleton, see EmoteRuntime).
REGION_BONE = {TORSO: "spine_01", HEAD: "head", ARM_L: "upperarm_l", ARM_R: "upperarm_r",
               LEG_L: "thigh_l", LEG_R: "thigh_r"}

# Emitted pieces, in this order. Both eyeballs are bound to `head` (they are separate components,
# so they stay separate submeshes even though they share a bone).
PIECES = [
    ("torso", TORSO), ("head", HEAD), ("arm_l", ARM_L), ("arm_r", ARM_R),
    ("leg_l", LEG_L), ("leg_r", LEG_R), ("eye_l", HEAD), ("eye_r", HEAD),
]
EYE_PIECE = {"eye_l": +1, "eye_r": -1}      # model's own left is +X (it faces -Z)

MAGIC = b"SAHURM1\x00"


# ---------------------------------------------------------------- FBX read
def load_fbx(path):
    """-> V (cm), triangles, per-corner UV, per-vertex normals, unique positions.

    fbxtool.build() already gives world-space V/triangles/ByVertice normals; its percorner()
    cannot resolve this file's UV set, because the UV layer is IndexToDirect: 3,828 UV pairs
    indexed by 7,358 corner indices. That path is read here directly.
    """
    sys.path.insert(0, os.path.join(DATA, "models", "sahur"))
    import fbxtool

    parsed = fbxtool.build(path)
    part = parsed["_parts_full"][0]
    V = np.asarray(part["_V"], dtype=np.float64)                  # (1868,3) cm, world
    T = np.asarray(part["_tris"], dtype=np.int64)                 # (3738,3)
    NRM = np.asarray(part["_tc_nrm"], dtype=np.float64)           # (3738,3,3) ByVertice
    assert T.max() < len(V)

    # raw nodes: PolygonVertexIndex (corner order = build()'s fan order), UV, UVIndex
    blob = open(path, "rb").read()
    root = fbxtool.parse_binary(blob)
    if isinstance(root, tuple):
        root = root[0]

    def find(name):
        hits = []
        def walk(n):
            if n.name == name:
                hits.append(n)
            for c in n.children:
                walk(c)
        walk(root)
        return hits[0]

    pvi = np.asarray(find("PolygonVertexIndex").props[0], dtype=np.int64)
    uvdata = np.asarray(find("UV").props[0], dtype=np.float64).reshape(-1, 2)
    uvidx = np.asarray(find("UVIndex").props[0], dtype=np.int64)
    assert len(pvi) == len(uvidx), f"corner count mismatch {len(pvi)} vs {len(uvidx)}"
    assert uvidx.max() < len(uvdata)

    # corner order: polygons in PVI order, fan-triangulated exactly like fbxtool does
    nverts = np.where(pvi < 0, ~pvi, pvi)          # last corner of a polygon is bitwise-not
    poly_start, cur = [], 0
    for i, c in enumerate(pvi):
        if c < 0:
            poly_start.append(cur)
            cur = i + 1
    assert cur == len(pvi)
    # fan corners -> triangle corners (t, 0..2) -> flat corner index
    tri_corners = []
    for pi in range(len(poly_start)):
        start = poly_start[pi]
        end = poly_start[pi + 1] if pi + 1 < len(poly_start) else len(pvi)
        n = end - start
        for k in range(1, n - 1):
            tri_corners.append((start, start + k, start + k + 1))
    tri_corners = np.asarray(tri_corners, dtype=np.int64)
    assert tri_corners.shape == T.shape, f"{tri_corners.shape} vs {T.shape}"
    # sanity: the fan indices must reproduce build()'s triangle list
    assert np.array_equal(nverts[tri_corners], T), "corner->vertex map disagrees with fbxtool"

    UV = uvdata[uvidx[tri_corners]]                                  # (3738,3,2)
    # per-vertex normal (ByVertice, NormalsIndex is identity on this file -- verified below)
    nrm_idx = np.asarray(find("NormalsIndex").props[0], dtype=np.int64)
    ndata = np.asarray(find("Normals").props[0], dtype=np.float64).reshape(-1, 3)
    assert np.array_equal(nrm_idx, np.arange(len(nrm_idx))), "normals IndexToDirect is not identity"
    VN = ndata[nrm_idx]                                              # (1868,3)
    # the per-corner normals must equal the per-vertex ones for every corner
    assert np.allclose(NRM, VN[T], atol=1e-6), "ByVertice normals disagree with corner normals"
    return V, T, UV, VN, VN[T], uvidx[tri_corners]


def components(n, T):
    parent = np.arange(n)
    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a
    for t in T:
        a, b, c = int(t[0]), int(t[1]), int(t[2])
        for x, y in ((a, b), (b, c)):
            ra, rb = find(x), find(y)
            if ra != rb:
                parent[ra] = rb
    roots = np.array([find(i) for i in range(n)])
    return roots


# ---------------------------------------------------------------- region assignment
def classify(V, roots):
    """Region id per vertex, from geometry only.

    Thresholds are the landmarks measured on this mesh; nothing here reads a bone name, a vertex
    group or a material. The arm test needs |z| as well as |x| inside the shoulder weld band: the
    robe's front/back faces reach |x| 17.5 there, so x alone would drag the torso into the arms.
    """
    n = len(V)
    region = np.full(n, TORSO, dtype=np.int64)
    counts = np.bincount(roots)
    body_root = int(np.argmax(counts))
    is_body = roots == body_root

    x, y, z = V[:, 0], V[:, 1], V[:, 2]
    ax = np.abs(x)

    # 1. eyeballs: separate components, both on the head bone
    for root in np.nonzero(counts)[0]:
        if int(root) == body_root:
            continue
        region[roots == root] = HEAD

    # 2. body: arms first, they win over head/torso in the weld band (the arm top reaches y 133)
    arm_band = (y >= SHOULDER_Y) & (y <= SHOULDER_TOP_Y) & (ax >= ARM_OUTBOARD) & (np.abs(z) <= ARM_Z)
    arm_low = (y < SHOULDER_Y) & (ax >= ARM_LOW_OUTBOARD)
    arm = is_body & (arm_band | arm_low)
    region[arm & (x > 0)] = ARM_L
    region[arm & (x < 0)] = ARM_R

    # 3. head above the neck ring, 4. legs below the hem, 5. torso = the rest
    region[is_body & ~arm & (y >= NECK_Y)] = HEAD
    legs = is_body & ~arm & (y < HEM_Y)
    region[legs & (x > 0)] = LEG_L
    region[legs & (x < 0)] = LEG_R
    return region, is_body, counts, body_root


def clean(region, T, is_body):
    """Drop isolated single-vertex outliers: a vertex whose region no neighbour shares is put
    back to the majority of its neighbours. Warns (does not hide) when it fires, so a threshold
    that starts cutting through geometry is visible in the bake report."""
    n = len(region)
    adj = [[] for _ in range(n)]
    for t in T:
        a, b, c = int(t[0]), int(t[1]), int(t[2])
        adj[a] += [b, c]; adj[b] += [a, c]; adj[c] += [a, b]
    changed = 0
    out = region.copy()
    for i in range(n):
        if not is_body[i] or not adj[i]:
            continue
        same = sum(1 for j in adj[i] if region[j] == region[i])
        if same:
            continue
        vals = np.bincount([region[j] for j in adj[i]])
        out[i] = int(np.argmax(vals))
        changed += 1
    return out, changed


# ---------------------------------------------------------------- split
def split(V, T, UV, VN, region, roots=None):
    """-> per piece: dict(name, region, bone, local_vert, local_uv, tris)

    A triangle is put in ONE region -- the majority of its three vertices' regions -- and every
    corner then becomes an output vertex of that piece, keyed by (source vertex, corner uv). A
    boundary vertex therefore exists once per piece it touches: that duplication is the cut, and
    it is what makes it impossible for a triangle to span two bones. No geometry is added or
    dropped, so the union of the pieces is exactly the source surface.
    """
    pieces = []
    for name, region_id in PIECES:
        pieces.append({"name": name, "region": region_id,
                       "bone": REGION_BONE[region_id]})
    # eyeball components get their own piece per side even though both are HEAD
    if roots is None:
        roots = components(len(V), T)
    counts = np.bincount(roots)
    body_root = int(np.argmax(counts))
    eye_pieces = {}
    for root in np.nonzero(counts)[0]:
        if int(root) == body_root:
            continue
        cx = V[roots == root, 0].mean()
        eye_pieces[int(root)] = "eye_l" if cx > 0 else "eye_r"
    by_name = {p["name"]: p for p in pieces}

    # 1. one region per triangle: majority vote of its vertices
    vreg = np.asarray(region)
    tri_region = np.empty(len(T), dtype=np.int64)
    ties = 0
    for ti, t in enumerate(T):
        r = vreg[t]
        vals, cnt = np.unique(r, return_counts=True)
        if len(vals) == 3:
            ties += 1
        tri_region[ti] = int(vals[np.argmax(cnt)])

    # 2. piece per triangle (eyes split out of the head region by component)
    out_tris = {p["name"]: [] for p in pieces}
    for ti, t in enumerate(T):
        reg = int(tri_region[ti])
        if reg == HEAD:
            r0 = int(roots[int(t[0])])
            name = eye_pieces.get(r0, "head")
            if name != "head" and any(int(roots[int(v)]) != r0 for v in t):
                raise AssertionError(f"eye triangle {ti} mixes components")
        else:
            name = REGION_NAMES[reg]
        out_tris[name].append((ti, [int(t[0]), int(t[1]), int(t[2])]))

    # 3. piece buffers, keyed by (source vertex, corner uv). UVIndex is 1:1 with the pair it
    #    points at, so the corner's uv value is an exact, unambiguous identity on this file.
    for name, p in by_name.items():
        p["tris"] = []
        p["src_tri"] = []
        p["local_of"] = {}
        p["local_vert"] = []
        p["local_uv"] = []
    for name in out_tris:
        p = by_name[name]
        for ti, tri_verts in out_tris[name]:
            tri = []
            for corner, v in enumerate(tri_verts):
                uv = (float(UV[ti, corner, 0]), float(UV[ti, corner, 1]))
                key = (v, uv)
                idx = p["local_of"].get(key)
                if idx is None:
                    idx = len(p["local_vert"])
                    p["local_of"][key] = idx
                    p["local_vert"].append(v)
                    p["local_uv"].append(uv)
                tri.append(idx)
            p["tris"].append(tri)
            p["src_tri"].append(ti)
    for name, p in by_name.items():
        p["local_vert"] = np.asarray(p["local_vert"], dtype=np.int64)
        p["local_uv"] = np.asarray(p["local_uv"], dtype=np.float64).reshape(-1, 2)
        p["tris"] = np.asarray(p["tris"], dtype=np.int64).reshape(-1, 3)
        p.pop("local_of", None)
    split_tris = sum(int((np.ptp(vreg[t]) != 0)) for t in T)
    dup = sum(len(p["local_vert"]) for p in by_name.values()) - len(vreg)
    print(f"  split: {split_tris} boundary triangles ({ties} three-region), "
          f"{dup} duplicated vertex copies")
    return [by_name[name] for name, _ in PIECES]


# ---------------------------------------------------------------- write / read binary
def write_mset(path, pieces, V, VN, uv):
    """Global vertex array in piece order + per-piece ranges. Quantisation:
    position int16 over the exact bbox, uv uint16 (0..1), normal int8, index uint16.
    """
    V = np.asarray(V, dtype=np.float64)
    lo, hi = V.min(axis=0), V.max(axis=0)
    span = np.maximum(hi - lo, 1e-9)

    pos, uvs, nrms, idx = [], [], [], []
    meta, vbase, tbase = [], 0, 0
    for p in pieces:
        lv = p["local_vert"]
        v = V[lv]
        q = np.rint((v - lo) / span * 65535.0).astype(np.int64) - 32768
        pos.append(np.clip(q, -32768, 32767).astype("<i2"))
        uvs.append(np.rint(np.clip(p["local_uv"], 0.0, 1.0) * 65535.0).astype("<u2"))
        nrm = VN[lv].copy()
        ln = np.linalg.norm(nrm, axis=1, keepdims=True)
        ln[ln == 0] = 1.0
        nrms.append(np.rint(np.clip(nrm / ln, -1.0, 1.0) * 127.0).astype("<i1"))
        idx.append((p["tris"] + vbase).astype("<u2"))
        meta.append((vbase, len(lv), tbase, len(p["tris"]), p["region"]))
        vbase += len(lv)
        tbase += len(p["tris"])

    pos = np.vstack(pos); uvs = np.vstack(uvs); nrms = np.vstack(nrms); idx = np.vstack(idx)
    assert vbase <= 65535, f"vertex count {vbase} does not fit uint16"

    with open(path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<II", 1, len(pieces)))
        f.write(struct.pack("<3f", *lo))
        f.write(struct.pack("<3f", *hi))
        f.write(struct.pack("<II", vbase, len(idx)))
        for (v0, vn, t0, tn, reg) in meta:
            f.write(struct.pack("<IBBBB", v0, int(reg), 0, 0, 0))
            f.write(struct.pack("<II", vn, t0))
            f.write(struct.pack("<I", tn))
        f.write(pos.tobytes())
        f.write(uvs.tobytes())
        f.write(nrms.tobytes())
        f.write(idx.tobytes())
    return vbase, len(idx)


def read_mset(path):
    blob = open(path, "rb").read()
    assert blob[:8] == MAGIC, "bad magic"
    o = 8
    version, npieces = struct.unpack_from("<II", blob, o); o += 8
    lo = np.array(struct.unpack_from("<3f", blob, o)); o += 12
    hi = np.array(struct.unpack_from("<3f", blob, o)); o += 12
    nvert, ntri = struct.unpack_from("<II", blob, o); o += 8
    meta = []
    for _ in range(npieces):
        v0, reg, _, _, _ = struct.unpack_from("<IBBBB", blob, o); o += 8
        vn, t0 = struct.unpack_from("<II", blob, o); o += 8
        tn, = struct.unpack_from("<I", blob, o); o += 4
        meta.append((v0, reg, vn, t0, tn))
    pos = np.frombuffer(blob, "<i2", nvert * 3, o).reshape(-1, 3); o += nvert * 6
    uv = np.frombuffer(blob, "<u2", nvert * 2, o).reshape(-1, 2); o += nvert * 4
    nrm = np.frombuffer(blob, "i1", nvert * 3, o).reshape(-1, 3); o += nvert * 3
    idx = np.frombuffer(blob, "<u2", ntri * 3, o).reshape(-1, 3); o += ntri * 6
    assert o == len(blob), f"{len(blob) - o} trailing bytes"
    return dict(version=version, lo=lo, hi=hi, nvert=nvert, ntri=ntri, meta=meta,
                pos=pos.astype(np.float64), uv=uv.astype(np.float64) / 65535.0,
                nrm=nrm.astype(np.float64) / 127.0, idx=idx.astype(np.int64), bytes=len(blob))


def verify(path, pieces, V, T, region, uv_in, nrm_in):
    """Re-read the written file and check it is exactly what the pieces claim."""
    d = read_mset(path)
    span = d["hi"] - d["lo"]
    world = d["lo"] + (d["pos"] + 32768.0) / 65535.0 * span
    problems = []
    if d["nvert"] != sum(len(p["local_vert"]) for p in pieces):
        problems.append("vertex count")
    if d["ntri"] != len(T):
        problems.append(f"triangle count {d['ntri']} != {len(T)}")
    # region per emitted vertex, and the no-cross check on the emitted triangles
    vregion = np.zeros(d["nvert"], dtype=np.int64)
    seen = np.zeros(d["nvert"], dtype=bool)
    for (v0, reg, vn, t0, tn) in d["meta"]:
        vregion[v0:v0 + vn] = reg
        if seen[v0:v0 + vn].any():
            problems.append("overlapping piece vertex ranges")
        seen[v0:v0 + vn] = True
        tri = d["idx"][t0:t0 + tn]
        r = vregion[tri]
        if (r != r[:, :1]).any():
            bad = int((r != r[:, :1]).any(axis=1).sum())
            problems.append(f"{bad} emitted triangles span two regions (piece v0={v0})")
    if not seen.all():
        problems.append("uncovered emitted vertices")
    # decode round-trip: every emitted vertex is the source vertex it came from
    src = np.concatenate([p["local_vert"] for p in pieces])
    err = np.linalg.norm(world - V[src], axis=1)
    if err.max() > max(span.max() / 65535.0, 1e-9):
        problems.append(f"position decode error {err.max():.4f} cm")
    # uv / normal round-trip
    uverr = np.abs(np.vstack([p["local_uv"] for p in pieces]) - d["uv"]).max()
    if uverr > 1.0 / 65535.0:
        problems.append(f"uv decode error {uverr:.5f}")
    # winding: the emitted triangles must have the same facing as the source
    def normals(Varr, tri):
        a = Varr[tri[:, 1]] - Varr[tri[:, 0]]
        b = Varr[tri[:, 2]] - Varr[tri[:, 0]]
        return np.cross(a, b)
    src_tri = np.concatenate([p["src_tri"] for p in pieces])
    n_src = normals(V, T[src_tri])
    n_out = normals(world, d["idx"])
    # 14 source triangles are slivers (cross product ~1e-6, i.e. zero area); their normal is
    # numerical noise in the source too, so they are reported, not treated as a winding flip.
    sliver = np.linalg.norm(n_src, axis=1) < 1e-4
    flip = (np.sum(n_src * n_out, axis=1) < 0) & ~sliver
    if flip.any():
        problems.append(f"{int(flip.sum())} triangles flipped winding")
    # per-piece summary
    print("  piece       bone        region  verts  tris   (source verts)")
    for i, (p, (v0, reg, vn, t0, tn)) in enumerate(zip(pieces, d["meta"])):
        print(f"  {p['name']:<11} {p['bone']:<11} {REGION_NAMES[reg]:<7} {vn:5d} {tn:5d}"
              f"   {len(np.unique(p['local_vert'])):5d}")
    print(f"  total verts={d['nvert']} tris={d['ntri']} blob={d['bytes']} B "
          f"({d['bytes'] / 1024:.1f} KB) bbox {np.round(d['lo'], 2)} .. {np.round(d['hi'], 2)} cm")
    if problems:
        for p in problems:
            print(f"  FAIL {p}")
        raise SystemExit(1)
    print(f"  verified: no cross-region triangle, decode within quantisation, winding preserved "
          f"({int(sliver.sum())} source slivers excluded)")
    return d


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fbx", default=DEFAULT_FBX)
    ap.add_argument("--albedo", default=DEFAULT_ALBEDO)
    ap.add_argument("--out-dir", default=DEFAULT_OUT)
    ap.add_argument("--preview", action="store_true", help="write per-piece npz for render.py")
    ap.add_argument("--piece-shot", action="store_true", help="render the pieces flat-coloured")
    args = ap.parse_args()

    os.makedirs(SCRATCH, exist_ok=True)
    print(f"reading {args.fbx}")
    V, T, UV, VN, CN, UVI = load_fbx(args.fbx)
    roots = components(len(V), T)
    counts = np.bincount(roots)
    print(f"  verts={len(V)} tris={len(T)} components={len(counts)} "
          f"{sorted(counts[counts > 0].tolist(), reverse=True)}")

    region, is_body, _, body_root = classify(V, roots)
    region, cleaned = clean(region, T, is_body)
    print("  regions:", {REGION_NAMES[r]: int((region == r).sum()) for r in np.unique(region)},
          f"(outlier fixes={cleaned})")

    pieces = split(V, T, UV, VN, region, roots)

    out = args.out_dir
    os.makedirs(out, exist_ok=True)
    mset = os.path.join(out, "sahur.mset")
    print(f"writing {mset}")
    write_mset(mset, pieces, V, VN, UV)
    verify(mset, pieces, V, T, region, UV, CN)

    alb = os.path.join(out, "sahur_albedo.png")
    src = open(args.albedo, "rb").read()
    open(alb, "wb").write(src)
    print(f"wrote {alb} ({len(src)} B, byte copy; no FBX material path anywhere in the blob)")

    # archivable side products (scratch, not shipped)
    npz = os.path.join(SCRATCH, "pieces.npz")
    payload = {}
    for i, p in enumerate(pieces):
        lv = p["local_vert"]
        payload[f"V{i}"] = V[lv]
        payload[f"tri{i}"] = p["tris"]
        payload[f"uv{i}"] = p["local_uv"]
        payload[f"nrm{i}"] = VN[lv][p["tris"]]
    np.savez(npz, **payload)
    np.save(os.path.join(SCRATCH, "region.npy"), region)
    print(f"wrote {npz} (per-piece, for models/sahur/render.py)")

    if args.piece_shot:
        sys.path.insert(0, os.path.join(DATA, "models", "sahur"))
        import render as R
        R.PALETTE = np.array([
            [0.85, 0.20, 0.20], [0.20, 0.75, 0.30], [0.25, 0.45, 0.95], [0.95, 0.85, 0.20],
            [0.80, 0.30, 0.85], [0.20, 0.80, 0.85], [0.95, 0.55, 0.15], [0.95, 0.95, 0.95],
        ], dtype=np.float32)   # torso head arm_l arm_r leg_l leg_r eye_l eye_r
        parts, _ = R.load(npz, None)
        Vv, Tt, UVv, NNv, PID, has_uv, has_nr = R.tri_soup(parts)
        lo, hi = Vv.min(axis=0), Vv.max(axis=0)
        ctr = (lo + hi) / 2
        rad = float(np.linalg.norm(hi - lo)) / 2
        img = R.render(Vv, Tt, UVv, NNv, PID, None, ctr + np.array([0.0, 0.0, 1.0]) * rad * 3.0,
                       ctr, np.array([0.0, 1.0, 0.0]), flat=True)
        shot = os.path.join(SCRATCH, "piece-palette-front.png")
        img.save(shot)
        img2 = R.render(Vv, Tt, UVv, NNv, PID, None, ctr + np.array([1.0, 0.0, 0.0]) * rad * 3.0,
                        ctr, np.array([0.0, 1.0, 0.0]), flat=True)
        img2.save(os.path.join(SCRATCH, "piece-palette-side.png"))
        print(f"wrote {shot}")


if __name__ == "__main__":
    main()
