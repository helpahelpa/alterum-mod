#!/usr/bin/env python3
"""Bake the CC BY 4.0 Roblox Sahur FBX (Mixamo rig) into the embedded binary the hot mod builds
a SkinnedMeshRenderer from.

Source: models/sahur/variants/tung-sahur-game-3d-model-roblox/source/SahurWalk.fbx
  "Tung Sahur Game 3D model (Roblox)" by alex.cgwarrior, CC BY 4.0
  3,097 v / 6,162 t, 71-bone Mixamo rig, 8 islands (body, 2 arms, 2 legs, 2 eyes, bat on BIT),
  4K PBR maps, 1.81 m, Y-up, faces +Z, one in-place 2.37 s walk clip.

No Blender, no assimp, no Unity. The FBX is parsed by models/sahur/fbxtool.py (the same reader the
recon used), and everything else is numpy + struct here.

What this writes (`sahur_roblox.rset`, magic SAHRRG1):

  header        version, flags, counts, position bbox
  bones         71 x { parent u8, flags u8, local pos f32x3 (m), local rot f32x4 }
                + one UTF-8 name block (names are needed for the joint report and the debug dump;
                  the runtime maps by anatomy, never by these names)
  submeshes     8 x { triCount u32, material u8, islandKind u8 }  (index ranges are contiguous)
  vertices      int16x3 position (quantised over the bbox), uint16x2 uv,
                int8x3 normal, uint8x4 bone index, uint8x4 weight
  indices       uint16 x3 per triangle
  clip          1 x { nFrames u16, duration f32, int16x4 per bone per frame (rotation delta about
                the bind world rotation), int16x3 per frame (hips local translation delta, mm) }

  The bone local transforms ARE the bind pose: bindposes are derived from them at runtime by the
  same accumulation the hierarchy does, and this script proves that accumulation equals the FBX's
  own cluster TransformLink matrices.

Choices, stated rather than hidden:

  * 4 influences per vertex, strongest four, renormalised. Unity's classic `Mesh.boneWeights`
    path (BoneWeight, 4 slots) is what an injected IL2CPP module can set without NativeArray
    plumbing, and skinWeights is Bone4 in the shipped quality settings. The loss is measured and
    printed (the 7-influence vertices are the shoulder/hip bands).
  * roughness and AO are NOT baked as maps: one mean roughness from Sahur_Roughness.png becomes a
    constant URP smoothness, AO (Sahur_Mixed_AO.png) is dropped (occlusion = 1). Both maps are
    1/3 of the 4K payload and neither survives a 1024 downscale usefully on a 6 k-tri toy body.
  * base colour and normal are downscaled by an 8-tap box filter with PIL, sizes chosen so the
    whole embedded payload stays under ~1.5 MB.
  * the mesh is MIRRORED IN X by default. The rig is left-handed the other way round from the
    game's (`LeftHand` at model +X here, the game's `hand_l` at -X for a +Z-facing model). Without
    the mirror the arms retarget side-swapped and the bat lands in the visual left hand. Mirror =
    negate x, reverse winding, mirror normals / bone locals / clip rotations (q -> x,-y,-z,w).
    UVs are NOT flipped: the texture is pinned to its surface, so flipping u would paint the
    mirrored surface with doubly-mirrored artwork (the same rule the rigid bakes use).

Usage (from the alterum workspace root):
  python3 tracks/merged/tools/bake_sahur_roblox.py                  # bake + verify
  python3 tracks/merged/tools/bake_sahur_roblox.py --no-mirror      # keep the source handedness
  python3 tracks/merged/tools/bake_sahur_roblox.py --base 1024 --normal 512
"""
import argparse
import json
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
WS = os.path.abspath(os.path.join(HERE, "..", "..", ".."))              # alterum/ (source)
DATA = os.path.expanduser("~/assets/monorepo/alterum-data")       # data tree (payload)
VARIANT = os.path.join(DATA, "models", "sahur", "variants", "tung-sahur-game-3d-model-roblox")
DEFAULT_FBX = os.path.join(VARIANT, "source", "SahurWalk.fbx")
TEX_DIR = os.path.join(VARIANT, "textures")
DEFAULT_OUT = os.path.join(DATA, "tracks", "merged", "mod", "AlterumModelSwap.Hot")

MAGIC = b"SAHRRG1\x00"
KTIME = 46186158000.0                        # FBX KTime ticks per second
CLIP_FPS = 30.0
ZERO4 = np.array([0.0, 0.0, 0.0, 1.0])


# ---------------------------------------------------------------- FBX object graph
class Fbx:
    def __init__(self, path):
        sys.path.insert(0, os.path.join(DATA, "models", "sahur"))
        import fbxtool
        self.ft = fbxtool
        raw = open(path, "rb").read()
        self.root, self.ver = fbxtool.parse_binary(raw)
        self.objects = self.root.find("Objects")
        self.conns = self.root.find("Connections")
        self.by_id = {}
        for n in self.objects.children:
            if n.props and isinstance(n.props[0], (int, np.integer)):
                self.by_id[int(n.props[0])] = n
        self.models = {i: n for i, n in self.by_id.items() if n.name == "Model"}

        # Model-only OO edges (exporters also connect a bone to its own Cluster Deformer)
        self.model_parent, self.parent_all, self.oo, self.op = {}, {}, [], []
        for c in self.conns.children:
            if c.name != "C":
                continue
            kind = str(c.props[0])
            ch, pa = int(c.props[1]), int(c.props[2])
            if kind == "OO":
                self.oo.append((ch, pa))
                self.parent_all.setdefault(ch, pa)
                if ch in self.models and pa in self.models:
                    self.model_parent.setdefault(ch, pa)
            elif kind == "OP":
                self.op.append((ch, pa, str(c.props[3]) if len(c.props) > 3 else "",
                                int(c.props[4]) if len(c.props) > 4 else -1))

        self.bones = [i for i, n in self.models.items()
                      if len(n.props) > 2 and n.props[2] == "LimbNode"]
        self.name = {i: fbxtool._short(n.props[1]) for i, n in self.models.items()}

    def local_matrix(self, oid, override=None):
        """fbxtool.local_matrix, with an optional (T, R, S) override for animation sampling."""
        if override is None:
            return self.ft.local_matrix(self.models[oid])
        ft = self.ft
        m = self.models[oid]
        t, r, s = override
        roff = ft._p70(m, "RotationOffset", [0, 0, 0])
        rp = ft._p70(m, "RotationPivot", [0, 0, 0])
        rpre = ft._p70(m, "PreRotation", [0, 0, 0])
        rpost = ft._p70(m, "PostRotation", [0, 0, 0])
        soff = ft._p70(m, "ScalingOffset", [0, 0, 0])
        sp = ft._p70(m, "ScalingPivot", [0, 0, 0])
        gt = ft._p70(m, "GeometricTranslation", [0, 0, 0])
        gr = ft._p70(m, "GeometricRotation", [0, 0, 0])
        gs = ft._p70(m, "GeometricScaling", [1, 1, 1])
        order = int(ft._p70(m, "RotationOrder", [0])[0])
        T = ft._m4(t=[float(v) for v in t])
        Roff = ft._m4(t=[float(v) for v in roff])
        Rp = ft._m4(t=[float(v) for v in rp])
        Rpre = ft._m4(r=ft._euler_np(*[float(v) for v in rpre], order=order))
        R = ft._m4(r=ft._euler_np(*[float(v) for v in r], order=order))
        Rpost = ft._m4(r=ft._euler_np(*[float(v) for v in rpost], order=order))
        Soff = ft._m4(t=[float(v) for v in soff])
        Sp = ft._m4(t=[float(v) for v in sp])
        S = ft._m4(s=[float(v) for v in s])
        geo = ft._m4(r=ft._euler_np(*[float(v) for v in gr], order=order),
                     t=[float(v) for v in gt], s=[float(v) for v in gs])
        return (T @ Roff @ Rp @ Rpre @ R @ np.linalg.inv(Rpost) @ Soff @ Sp @ S
                @ np.linalg.inv(Sp) @ geo)

    def world(self, oid, cache=None):
        chain, j = [], oid
        while j is not None:
            chain.append(j)
            j = self.model_parent.get(j)
        m = np.eye(4)
        for j in reversed(chain):
            m = m @ self.local_matrix(j, cache.get(j) if cache else None)
        return m


# ---------------------------------------------------------------- mesh + skin read
def read_mesh(fb):
    """-> V (m, source space), tris, per-corner uv, per-corner normal, per-vertex weights, maps."""
    ft = fb.ft
    geos = {i: n for i, n in fb.by_id.items() if n.name == "Geometry"}
    assert len(geos) == 1, f"{len(geos)} geometry objects"
    gid, g = next(iter(geos.items()))
    gmodel = gid
    while gmodel is not None and gmodel not in fb.models:
        gmodel = fb.parent_all.get(gmodel)
    assert gmodel is not None, "geometry has no Model parent"


    V = np.asarray(g.find("Vertices").props[0], dtype=np.float64).reshape(-1, 3)
    PVI = np.asarray(g.find("PolygonVertexIndex").props[0], dtype=np.int64)
    nverts = np.where(PVI < 0, ~PVI, PVI)

    # polygon fan, identical to fbxtool.build()
    starts, cur = [], 0
    for i, c in enumerate(PVI):
        if c < 0:
            starts.append(cur)
            cur = i + 1
    assert cur == len(PVI)
    tc = []
    for pi, s in enumerate(starts):
        e = starts[pi + 1] if pi + 1 < len(starts) else len(PVI)
        for k in range(1, e - s - 1):
            tc.append((s, s + k, s + k + 1))
    tc = np.asarray(tc, dtype=np.int64)
    tri = nverts[tc]

    uvnode = g.find("LayerElementUV")
    UV = np.asarray(uvnode.find("UV").props[0], dtype=np.float64).reshape(-1, 2)
    uvidx = uvnode.find("UVIndex")
    uref = uvnode.find("ReferenceInformationType").props[0]
    if uvidx is not None and uref == "IndexToDirect":
        UVI = np.asarray(uvidx.props[0], dtype=np.int64)
        assert len(UVI) == len(PVI)
        corner_uv = UV[UVI[tc]]
    else:
        corner_uv = UV[tc]

    nrmnode = g.find("LayerElementNormal")
    NRM = np.asarray(nrmnode.find("Normals").props[0], dtype=np.float64).reshape(-1, 3)
    nidx = nrmnode.find("NormalsIndex")
    nmapping = nrmnode.find("MappingInformationType").props[0]
    nref = nrmnode.find("ReferenceInformationType").props[0]
    if nmapping == "ByPolygonVertex" and nref == "Direct":
        corner_nrm = NRM[tc]                      # one normal per polygon-vertex corner
    elif nidx is not None and nref == "IndexToDirect":
        corner_nrm = NRM[np.asarray(nidx.props[0], dtype=np.int64)[tc]]
    elif nmapping in ("ByVertice", "ByVertex"):
        corner_nrm = NRM[tri]
    else:
        raise SystemExit(f"unsupported normal layer {nmapping}/{nref}")

    # geometry -> armature space: both roots, unit scale, so the raw Vertices are already metres
    fb.geom_model = gmodel
    gm = fb.world(gmodel)
    Vm = (gm[:3, :3] @ V.T).T + gm[:3, 3]
    Vm = Vm / (np.linalg.norm(gm[:3, 0]))            # the exporter bakes the 100x on both roots
    nscale = 1.0 / np.linalg.norm(gm[:3, 0])
    print(f"  geometry '{fb.name.get(gmodel, gid)}' node scale={1 / nscale:.1f} "
          f"-> verts in metres, bbox {np.round(Vm.min(0), 3)} .. {np.round(Vm.max(0), 3)}")
    # normals: rotate by the normal part of the node matrix
    Nrm = (gm[:3, :3] @ corner_nrm.reshape(-1, 3).T).T.reshape(corner_nrm.shape)
    Nrm /= np.maximum(np.linalg.norm(Nrm, axis=2, keepdims=True), 1e-9)

    # clusters -> per-vertex influences
    clusters = {i: n for i, n in fb.by_id.items()
                if n.name == "Deformer" and n.props[2] == "Cluster"}
    joint_of = {}
    for ch, pa in fb.oo:
        if ch in clusters and pa in fb.models:
            joint_of[ch] = pa
        elif pa in clusters and ch in fb.models:
            joint_of[pa] = ch
    infl = [dict() for _ in range(len(V))]
    tlink = {}
    for cid, c in clusters.items():
        j = joint_of.get(cid)
        if j is None:
            continue
        idn, wn = c.find("Indexes"), c.find("Weights")
        if idn is None or wn is None or not idn.props or not wn.props:
            continue
        idx = np.asarray(idn.props[0], dtype=np.int64)
        w = np.asarray(wn.props[0], dtype=np.float64)
        tl = c.find("TransformLink")
        if tl is not None and tl.props:
            # FBX stores 4x4 properties column-major: the flat list is the transpose of the
            # usual row-major matrix (without this the translation lands in the last column).
            tlink[j] = np.asarray(tl.props[0], dtype=np.float64).reshape(4, 4).T
        for v, ww in zip(idx, w):
            infl[int(v)][j] = infl[int(v)].get(j, 0.0) + float(ww)
    return Vm, tri, corner_uv, Nrm, infl, tlink


# ---------------------------------------------------------------- clip read
def read_clip(fb):
    """-> {model_id: {T:(t,3x n,3), R:..., S:...}}, duration.  Sampled by linear key interpolation."""
    cn_target, cn_chan = {}, {}
    for ch, pa, prop, idx in fb.op:
        n = fb.by_id.get(ch)
        if n is not None and n.name == "AnimationCurveNode" and pa in fb.models:
            cn_target[ch] = pa
            cn_chan[ch] = {"Lcl Translation": "T", "Lcl Rotation": "R",
                           "Lcl Scaling": "S"}.get(prop, prop)
    curves = {ch: pa for ch, pa, prop, idx in fb.op
              if fb.by_id.get(ch) is not None and fb.by_id[ch].name == "AnimationCurve"}
    axis = {ch: prop for ch, pa, prop, idx in fb.op
            if fb.by_id.get(ch) is not None and fb.by_id[ch].name == "AnimationCurve"}
    chan = {}
    for ch, pa in curves.items():
        node = fb.by_id[ch]
        kt = np.asarray(node.find("KeyTime").props[0], dtype=np.float64) / KTIME
        kv = np.asarray(node.find("KeyValueFloat").props[0], dtype=np.float64)
        if pa not in cn_target:
            continue
        target, chn = cn_target[pa], cn_chan[pa]
        a = {"d|X": 0, "d|Y": 1, "d|Z": 2}.get(axis.get(ch, ""), None)
        if a is None:
            continue
        chan.setdefault(target, {}).setdefault(chn, {})[a] = (kt, kv)
    tmin = min(min(k.min() for k, v in axes.values()) for c in chan.values()
               for axes in c.values())
    tmax = max(max(k.max() for k, v in axes.values()) for c in chan.values()
               for axes in c.values())
    return chan, float(tmin), float(tmax)


def sample(axes, t, default):
    out = np.array(default, dtype=np.float64)
    for a, (kt, kv) in axes.items():
        if len(kt) == 1 or t <= kt[0]:
            out[a] = kv[0]
        elif t >= kt[-1]:
            out[a] = kv[-1]
        else:
            i = int(np.searchsorted(kt, t) - 1)
            f = (t - kt[i]) / max(kt[i + 1] - kt[i], 1e-12)
            out[a] = kv[i] * (1 - f) + kv[i + 1] * f
    return out


# ---------------------------------------------------------------- transforms
def m4_trs(t, q):
    x, y, z, w = q / max(np.linalg.norm(q), 1e-12)
    r = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
    m = np.eye(4)
    m[:3, :3] = r
    m[:3, 3] = t
    return m


def rot_of(m):
    """Rotation part of a 4x4, as a unit quaternion (matrix is assumed scaled-uniform)."""
    r = m[:3, :3] / max(np.linalg.norm(m[:3, 0]), 1e-12)
    tr = r[0, 0] + r[1, 1] + r[2, 2]
    if tr > 0:
        s = np.sqrt(tr + 1.0) * 2
        q = np.array([(r[2, 1] - r[1, 2]) / s, (r[0, 2] - r[2, 0]) / s, (r[1, 0] - r[0, 1]) / s,
                      0.25 * s])
    elif r[0, 0] > r[1, 1] and r[0, 0] > r[2, 2]:
        s = np.sqrt(1.0 + r[0, 0] - r[1, 1] - r[2, 2]) * 2
        q = np.array([0.25 * s, (r[0, 1] + r[1, 0]) / s, (r[0, 2] + r[2, 0]) / s,
                      (r[2, 1] - r[1, 2]) / s])
    elif r[1, 1] > r[2, 2]:
        s = np.sqrt(1.0 + r[1, 1] - r[0, 0] - r[2, 2]) * 2
        q = np.array([(r[0, 1] + r[1, 0]) / s, 0.25 * s, (r[1, 2] + r[2, 1]) / s,
                      (r[0, 2] - r[2, 0]) / s])
    else:
        s = np.sqrt(1.0 + r[2, 2] - r[0, 0] - r[1, 1]) * 2
        q = np.array([(r[0, 2] + r[2, 0]) / s, (r[1, 2] + r[2, 1]) / s, 0.25 * s,
                      (r[1, 0] - r[0, 1]) / s])
    return q / np.linalg.norm(q)


def qmul(a, b):
    """Hamilton product, (x,y,z,w) convention."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return np.array([aw * bx + bw * ax + ay * bz - az * by,
                     aw * by + bw * ay + az * bx - ax * bz,
                     aw * bz + bw * az + ax * by - ay * bx,
                     aw * bw - ax * bx - ay * by - az * bz])


def qconj(q):
    return np.array([-q[0], -q[1], -q[2], q[3]])


def mirror_q(q):
    """Conjugation by diag(-1,1,1): (x,y,z,w) -> (x,-y,-z,w)."""
    return np.array([q[0], -q[1], -q[2], q[3]])


# ---------------------------------------------------------------- bake
def bake(args):
    fb = Fbx(args.fbx)
    print(f"reading {args.fbx}")
    V, T, CUV, CNR, infl, tlink = read_mesh(fb)
    print(f"  verts={len(V)} tris={len(T)} clusters={len(infl[0]) > 0 and sum(1 for v in infl if v)}/"
          f"{len(tlink)} weighted={sum(1 for v in infl if v)}/{len(V)}")

    # ---- bone order: topological (parents first), Hips root first
    ids = sorted(fb.bones, key=lambda i: fb.name[i])
    parent_of = {}
    for i in ids:
        p = fb.model_parent.get(i)
        while p is not None and p not in fb.bones:
            p = fb.model_parent.get(p)
        parent_of[i] = p
    depth = {}
    def dep(i):
        if i in depth:
            return depth[i]
        p = parent_of[i]
        depth[i] = 0 if p is None else dep(p) + 1
        return depth[i]
    for i in ids:
        dep(i)
    order = sorted(ids, key=lambda i: (depth[i], fb.name[i]))
    bi = {oid: k for k, oid in enumerate(order)}
    nb = len(order)
    print(f"  bones={nb} roots={[fb.name[i] for i in order if parent_of[i] is None]}")

    # The BIND pose is what the skin clusters were bound in: their TransformLink, which is the
    # joint's global matrix at bind time (÷ the mesh node's 100x to get mesh space, metres). It is
    # NOT the same as the node's own Lcl properties -- this file's rest pose differs from its bind
    # pose (head ~2 cm, eye bones a large rotation), so the node chain alone would draw the eyes in
    # the wrong place. Nodes with no geometry keep their node-chain local.
    mesh_world = fb.world(fb.geom_model)
    mesh_scale = float(np.linalg.norm(mesh_world[:3, 0]))
    nodem = {i: fb.local_matrix(i) for i in order}
    bindw = {}
    for i in order:
        p = parent_of[i]
        if i in tlink:
            bindw[i] = tlink[i] / mesh_scale
        elif p is None:
            bindw[i] = nodem[i]
        else:
            bindw[i] = bindw[p] @ nodem[i]
    pose = {}
    for i in order:
        p = parent_of[i]
        pose[i] = bindw[i] if p is None else np.linalg.inv(bindw[p]) @ bindw[i]
    acc = {}
    worst_acc = 0.0
    for i in order:
        p = parent_of[i]
        acc[i] = pose[i] if p is None else acc[p] @ pose[i]
        worst_acc = max(worst_acc, float(np.abs(acc[i] - bindw[i]).max()))
    # how far the file's own rest pose (the node chain) sits from the bind pose
    rest = {}
    worst_rest, worst_rest_name = 0.0, ""
    for i in order:
        p = parent_of[i]
        rest[i] = nodem[i] if p is None else rest[p] @ nodem[i]
        d = float(np.abs(rest[i][:3, 3] - bindw[i][:3, 3]).max())
        if d > worst_rest:
            worst_rest, worst_rest_name = d, fb.name[i]
    print(f"  bind: {len(tlink)}/{nb} bones from cluster TransformLink, the rest from the node chain; "
          f"local accumulation worst |d|={worst_acc:.2e} m; "
          f"rest-vs-bind worst translation {worst_rest * 100:.2f} cm on '{worst_rest_name}'")

    # ---- bone arrays, mirrored
    bpos = np.zeros((nb, 3))
    brot = np.zeros((nb, 4))
    bparent = np.zeros(nb, np.int64)
    for k, i in enumerate(order):
        t = pose[i][:3, 3].copy()
        q = rot_of(pose[i])
        if args.mirror_x:
            t[0] = -t[0]
            q = mirror_q(q)
        if q[3] < 0:
            q = -q                                   # keep w >= 0 so slerp is unambiguous
        bpos[k] = t
        brot[k] = q
        bparent[k] = -1 if parent_of[i] is None else bi[parent_of[i]]

    # ---- weights: 4 strongest, renormalised
    vw = np.zeros((len(V), 4), np.int64)
    ww = np.zeros((len(V), 4))
    dropped = []
    zero = []
    for v, d in enumerate(infl):
        if not d:
            zero.append(v)
            continue
        items = sorted(d.items(), key=lambda kv: -kv[1])[:4]
        s = sum(x[1] for x in items)
        for k, (oid, w) in enumerate(items):
            vw[v, k] = bi.get(oid, 0)
            ww[v, k] = w / s
        tot = sum(d.values())
        dropped.append(1.0 - s / tot)
    if zero:
        print(f"  WARNING {len(zero)} unweighted vertices -> bone 0")
    dropped = np.asarray(dropped) if dropped else np.zeros(1)
    print(f"  weights: 4 influences kept, renormalised; mass dropped mean={dropped.mean() * 100:.2f}% "
          f"max={dropped.max() * 100:.2f}% on {int((dropped > 1e-4).sum())} verts; "
          f"max influences in source={max(len(d) for d in infl if d)}")

    # ---- split by (source vertex, uv, normal) -> emitted vertices
    #     key = (src, round(uv,6), round(normal,5))
    key = np.concatenate([T.reshape(-1, 1), np.round(CUV.reshape(-1, 2), 6),
                          np.round(CNR.reshape(-1, 3), 5)], axis=1)
    uq, inv = np.unique(key, axis=0, return_inverse=True)
    src = uq[:, 0].astype(np.int64)
    EUV = uq[:, 1:3].copy()          # a merged key's corners carry this exact uv / normal
    EUR = uq[:, 3:6].copy()
    ET = inv.reshape(-1, 3).astype(np.int64)
    EP = V[src].copy()
    if args.mirror_x:
        EP[:, 0] = -EP[:, 0]
        EUR[:, 0] = -EUR[:, 0]
        ET = ET[:, ::-1].copy()                      # negative scale flips winding
    for d in (EUR,):
        n = np.linalg.norm(d, axis=1, keepdims=True)
        d /= np.maximum(n, 1e-9)
    print(f"  split: {len(src)} source verts -> {len(uq)} emitted ({len(key)} corners), "
          f"{len(ET)} tris")

    # ---- islands -> submeshes, largest first, then the bat
    par = np.arange(len(V))
    def find(a):
        while par[a] != a:
            par[a] = par[par[a]]
            a = par[a]
        return a
    for a, b, c in T:
        for x, y in ((a, b), (b, c)):
            ra, rb = find(x), find(y)
            if ra != rb:
                par[ra] = rb
    roots = np.array([find(i) for i in range(len(V))])
    tri_root = roots[T[:, 0]]
    # per-island dominant bone by weight mass
    isl_mass = {}
    for v, d in enumerate(infl):
        isl_mass.setdefault(int(roots[v]), {}).setdefault("n", 0)
        isl_mass[int(roots[v])]["n"] += 1
        for oid, w in d.items():
            isl_mass[int(roots[v])][fb.name.get(oid, "?")] = \
                isl_mass[int(roots[v])].get(fb.name.get(oid, "?"), 0.0) + w
    islands = []
    for r in sorted(set(tri_root.tolist()), key=lambda r: -int((tri_root == r).sum())):
        mass = isl_mass.get(int(r), {})
        top = sorted(((k, v) for k, v in mass.items() if k != "n"), key=lambda kv: -kv[1])[:3]
        kind = "bat" if any(k == "BIT" for k, _ in top) else "body"
        islands.append({"root": int(r), "kind": kind,
                        "name": top[0][0] if top else "?",
                        "top": [k for k, _ in top]})

    # ---- clip: world-bind-relative rotation deltas per bone + hips translation
    chan, t0, t1 = read_clip(fb)
    dur = t1 - t0
    nframes = int(round(dur * CLIP_FPS)) + 1
    times = np.linspace(t0, t1, nframes)
    bind_world = {i: acc[i] for i in order}
    clip_rot = np.zeros((nframes, nb, 4))
    clip_pos = np.zeros((nframes, 3))
    clipw0 = None
    hips = [i for i in order if parent_of[i] is None][0]
    print(f"  clip: {dur:.4f}s at {CLIP_FPS:g} fps -> {nframes} frames, "
          f"{len(chan)} animated targets")
    for fi, t in enumerate(times):
        local = {}
        for i in order:
            c = chan.get(i, {})
            tt = sample(c["T"], t, fb.ft._p70(fb.models[i], "Lcl Translation", [0, 0, 0])) \
                if "T" in c else np.array(fb.ft._p70(fb.models[i], "Lcl Translation", [0, 0, 0]),
                                          dtype=np.float64)
            rr = sample(c["R"], t, fb.ft._p70(fb.models[i], "Lcl Rotation", [0, 0, 0])) \
                if "R" in c else np.array(fb.ft._p70(fb.models[i], "Lcl Rotation", [0, 0, 0]),
                                           dtype=np.float64)
            ss = sample(c["S"], t, fb.ft._p70(fb.models[i], "Lcl Scaling", [1, 1, 1])) \
                if "S" in c else np.array(fb.ft._p70(fb.models[i], "Lcl Scaling", [1, 1, 1]),
                                          dtype=np.float64)
            local[i] = fb.local_matrix(i, (list(tt), list(rr), list(ss)))
        wm, wq = {}, {}
        for i in order:
            p = parent_of[i]
            wm[i] = local[i] if p is None else wm[p] @ local[i]
            wq[i] = rot_of(wm[i])
        if clipw0 is None:
            # frame 0 is the clip's own neutral: the deltas are relative to it, not to the bind
            # pose, so a rest/bind mismatch in the file cannot displace the mesh at t=0.
            clipw0 = (dict(wq), {i: wm[i][:3, 3].copy() for i in order})
        for k, i in enumerate(order):
            d = qmul(wq[i], qconj(clipw0[0][i]))
            if args.mirror_x:
                d = mirror_q(d)
            clip_rot[fi, k] = d / np.linalg.norm(d)
        dpos = wm[hips][:3, 3] - clipw0[1][hips]      # already metres
        if args.mirror_x:
            dpos[0] = -dpos[0]
        clip_pos[fi] = dpos
    ang = np.degrees(2 * np.arccos(np.clip(np.abs(clip_rot[:, :, 3]), -1, 1)))
    moved = [(float(ang[:, k].max()), fb.name[order[k]]) for k in range(nb)]
    moved.sort(reverse=True)
    print(f"  clip: hips travel {np.round(clip_pos.min(0), 3)} .. {np.round(clip_pos.max(0), 3)} m "
          f"(in place); frame0 == identity: {ang[0].max() < 0.01}; "
          f"largest joint swings " + ", ".join(f"{n}={a:.0f}deg" for a, n in moved[:6]))

    # ---- quantise + write
    lo = EP.min(axis=0)
    hi = EP.max(axis=0)
    span = np.maximum(hi - lo, 1e-9)
    qpos = np.clip(np.rint((EP - lo) / span * 65535.0) - 32768.0, -32768, 32767).astype("<i2")
    uvlo = EUV.min(axis=0)
    uvhi = EUV.max(axis=0)
    uvspan = np.maximum(uvhi - uvlo, 1e-9)
    quv = np.clip(np.rint((EUV - uvlo) / uvspan * 65535.0), 0, 65535).astype("<u2")
    qnr = np.clip(np.rint(EUR * 127.0), -127, 127).astype("i1")
    qbi = np.clip(vw[src], 0, 255).astype("u1")
    qwt = np.clip(np.rint(ww[src] * 255.0), 0, 255).astype("u1")
    tri_order = np.concatenate([ET[tri_root == isl["root"]] for isl in islands])
    if len(tri_order) != len(ET):
        raise SystemExit(f"island tri coverage {len(tri_order)} != {len(ET)}")
    idx = tri_order.astype("<u2")
    assert len(uq) <= 65535, f"{len(uq)} verts do not fit uint16"

    # names are diagnostics only (the runtime maps by anatomy); the Mixamo prefix is dropped so
    # the table matches the rig vocabulary: Hips, LeftHand, BIT, EYE_L, ...
    names = "\n".join(fb.name[i].split(":")[-1] for i in order).encode("utf-8")
    out = os.path.join(args.out_dir, "sahur_roblox.rset")
    with open(out, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<II", 1, 1 if args.mirror_x else 0))
        f.write(struct.pack("<IIIII", len(uq), len(idx), nb, len(islands), 1))
        f.write(struct.pack("<3f", *lo))
        f.write(struct.pack("<3f", *hi))
        f.write(struct.pack("<2f", *uvlo))
        f.write(struct.pack("<2f", *uvhi))
        f.write(struct.pack("<I", len(names)))
        f.write(names)
        for k, i in enumerate(order):
            f.write(struct.pack("<bB", int(bparent[k]), 0))
            f.write(struct.pack("<3f", *bpos[k]))
            f.write(struct.pack("<4f", *brot[k]))
        tri0 = 0
        for isl in islands:
            n = int((tri_root == isl["root"]).sum())
            f.write(struct.pack("<IBBxx", n, 0, 1 if isl["kind"] == "bat" else 0))
            isl["tri0"] = tri0
            tri0 += n
        f.write(qpos.tobytes())
        f.write(quv.tobytes())
        f.write(qnr.tobytes())
        f.write(qbi.tobytes())
        f.write(qwt.tobytes())
        f.write(idx.tobytes())
        durpos = np.zeros((nframes, nb, 4), "<i2")
        durpos[:] = np.clip(np.rint(clip_rot * 32767.0), -32767, 32767)
        f.write(struct.pack("<Hf", nframes, dur))
        f.write(durpos.tobytes())
        f.write(np.clip(np.rint(clip_pos * 1000.0), -32767, 32767).astype("<i2").tobytes())
    size = os.path.getsize(out)
    print(f"wrote {out} {size} B ({size / 1024:.1f} KB)")
    for n, isl in enumerate(islands):
        print(f"  submesh {n}: tris={int((tri_root == isl['root']).sum()):5d}  kind={isl['kind']:<4} "
              f"bone={isl['name']:<22} top={','.join(isl['top'])}")

    # ---- textures
    from PIL import Image
    tex_out = []
    for src_name, dst_name, px in (("Sahur_Base_color.png", "sahur_roblox_albedo.png", args.base),
                                   ("Sahur_Normal_OpenGL.png", "sahur_roblox_normal.png", args.normal)):
        im = Image.open(os.path.join(TEX_DIR, src_name))
        im = im.convert("RGB")
        w, h = im.size
        if px and px < w:
            im = im.resize((px, px), Image.BOX)
        p = os.path.join(args.out_dir, dst_name)
        im.save(p, optimize=True)
        tex_out.append((dst_name, im.size, os.path.getsize(p)))
        print(f"  texture {src_name} {w}x{h} -> {dst_name} {im.size[0]}x{im.size[1]} "
              f"{os.path.getsize(p)} B")
    # mean roughness folded into a constant
    rough = np.asarray(Image.open(os.path.join(TEX_DIR, "Sahur_Roughness.png")).convert("L"),
                       np.float64) / 255.0
    print(f"  roughness map mean={rough.mean():.3f} -> constant URP smoothness="
          f"{1.0 - rough.mean():.3f} (map dropped); AO dropped")

    # ---- verify by reading back
    verify(out, EP, EUR, EUV, src, vw, ww, bpos, brot, bparent, islands, EP, ET, tri_root)
    payload = size + sum(s for _, _, s in tex_out)
    print(f"PAYLOAD total embedded = {payload} B ({payload / 1024:.1f} KB): "
          f"rset {size / 1024:.1f} KB + " +
          " + ".join(f"{n} {s / 1024:.1f} KB" for n, _, s in tex_out))
    return dict(size=size, textures=tex_out, payload=payload, bones=nb, verts=len(uq),
                tris=len(ET), islands=[{k: v for k, v in i.items() if k != "root"}
                                       for i in islands],
                mirror=bool(args.mirror_x), bbox=[lo.tolist(), hi.tolist()],
                dropped=float(dropped.max()), clip=dict(frames=int(nframes), duration=dur))


def verify(path, EP, EUR, EUV, src, vw, ww, bpos, brot, bparent, islands, *rest):
    b = open(path, "rb").read()
    assert b[:8] == MAGIC, "bad magic"
    o = 8
    ver, flags = struct.unpack_from("<II", b, o); o += 8
    nv, nt, nb, ns, nm = struct.unpack_from("<IIIII", b, o); o += 20
    lo = np.array(struct.unpack_from("<3f", b, o), np.float64); o += 12
    hi = np.array(struct.unpack_from("<3f", b, o), np.float64); o += 12
    uvlo = np.array(struct.unpack_from("<2f", b, o), np.float64); o += 8
    uvhi = np.array(struct.unpack_from("<2f", b, o), np.float64); o += 8
    namelen, = struct.unpack_from("<I", b, o); o += 4
    names = b[o:o + namelen].decode("utf-8").split("\n"); o += namelen
    par = np.zeros(nb, np.int64); bp = np.zeros((nb, 3)); bq = np.zeros((nb, 4))
    for k in range(nb):
        p, fl = struct.unpack_from("<bB", b, o); o += 2
        bp[k] = struct.unpack_from("<3f", b, o); o += 12
        bq[k] = struct.unpack_from("<4f", b, o); o += 16
        par[k] = p
    subs = []
    for _ in range(ns):
        tn, mat, kind = struct.unpack_from("<IBB", b, o); o += 8
        subs.append((tn, mat, kind))
    pos = np.frombuffer(b, "<i2", nv * 3, o).reshape(nv, 3); o += nv * 6
    uv = np.frombuffer(b, "<u2", nv * 2, o).reshape(nv, 2); o += nv * 4
    nr = np.frombuffer(b, "i1", nv * 3, o).reshape(nv, 3); o += nv * 3
    bi = np.frombuffer(b, "u1", nv * 4, o).reshape(nv, 4); o += nv * 4
    wt = np.frombuffer(b, "u1", nv * 4, o).reshape(nv, 4); o += nv * 4
    idx = np.frombuffer(b, "<u2", nt * 3, o).reshape(nt, 3); o += nt * 6
    nf, dur = struct.unpack_from("<Hf", b, o); o += 6
    cq = np.frombuffer(b, "<i2", nf * nb * 4, o).reshape(nf, nb, 4); o += nf * nb * 8
    cp = np.frombuffer(b, "<i2", nf * 3, o).reshape(nf, 3); o += nf * 6
    assert o == len(b), f"{len(b) - o} trailing bytes"
    span = hi - lo
    dec = lo + (pos + 32768.0) / 65535.0 * span
    err = float(np.abs(dec - EP).max())
    uverr = float(np.abs(uvlo + uv.astype(np.float64) / 65535.0 * (uvhi - uvlo) - EUV).max())
    wsum = wt.astype(np.float64).sum(1) / 255.0
    problems = []
    if nv != len(EP):
        problems.append(f"vertex count {nv} != {len(EP)}")
    if err > span.max() / 65535.0 + 1e-9:
        problems.append(f"position decode error {err:.5f} m")
    if abs(wsum - 1.0).max() > 2.0 / 255.0:
        problems.append(f"weight sum off by {abs(wsum - 1.0).max():.4f}")
    if idx.max() >= nv:
        problems.append("index out of range")
    if sum(s[0] for s in subs) != nt:
        problems.append(f"submesh tris {sum(s[0] for s in subs)} != {nt}")
    # every emitted bone index must be a real bone
    if bi.max() >= nb:
        problems.append("bone index out of range")
    # bone hierarchy must be a forest in parents-first order
    if (par >= np.arange(nb)).any():
        problems.append("bone parents are not parents-first")
    # the clip's first frame must be ~identity (an in-place walk that starts at the bind pose is
    # not guaranteed; report instead of failing)
    first = np.abs(cq[0]).max() / 32767.0
    print(f"  verify: verts={nv} tris={nt} bones={nb} submeshes={ns} materials={nm} "
          f"clip={nf}f/{dur:.3f}s mirror={bool(flags & 1)}")
    print(f"  verify: pos err={err * 1000:.3f} mm (quantum {span.max() / 65535.0 * 1000:.3f} mm) "
          f"uv err={uverr:.2e} uv bbox={np.round(uvlo, 4)}..{np.round(uvhi, 4)} "
          f"weight sum in [{wsum.min():.4f},{wsum.max():.4f}] triangles={len(idx)} "
          f"clip frame0 |rot|max={first:.3f}")
    if problems:
        for p in problems:
            print(f"  FAIL {p}")
        raise SystemExit(1)
    print("  verified: decode within quantisation, weights normalised, indices and bone indices in "
          "range, submesh ranges cover every triangle, hierarchy parents-first")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fbx", default=DEFAULT_FBX)
    ap.add_argument("--out-dir", default=DEFAULT_OUT)
    ap.add_argument("--base", type=int, default=1024, help="base colour size (px, 0 = keep 4K)")
    ap.add_argument("--normal", type=int, default=1024, help="normal map size (px, 0 = keep 4K)")
    ap.add_argument("--no-mirror", dest="mirror_x", action="store_false")
    ap.add_argument("--report", default=None, help="write the summary JSON here")
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)
    info = bake(args)
    if args.report:
        os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
        with open(args.report, "w") as f:
            json.dump(info, f, indent=1)


if __name__ == "__main__":
    main()
