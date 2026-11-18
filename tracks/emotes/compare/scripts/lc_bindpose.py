#!/usr/bin/env python3
"""Recover LC's true bind pose (mesh space) for the player body and compare it to the
metarig copy the retarget tooling used.

bindpose (Mesh.m_BindPose[i]) maps mesh space -> bone space, so inverse(bindpose) is the
bone's bind matrix in mesh space. SMR.m_Bones[i] gives the bone path, so the bind skeleton
can be put on the same bone names as the clips. Outputs compare/lc_bind.json.
"""
import json, math, os
import numpy as np
import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
D = "/mnt/c/Program Files (x86)/Steam/steamapps/common/Lethal Company/Lethal Company_Data"
S3 = os.path.join(D, "sharedassets3.assets")
S1 = os.path.join(D, "sharedassets1.assets")
OUT = os.path.normpath(os.path.join(HERE, "..", "lc_bind.json"))


def gid(tt, k):
    v = tt.get(k)
    return v.get("m_PathID", 0) if isinstance(v, dict) else 0


def main():
    e3 = UnityPy.load(S3)
    g, t, smrs = {}, {}, []
    for o in e3.objects:
        n = o.type.name
        if n == "GameObject": g[o.path_id] = o.read_typetree()
        elif n in ("Transform", "RectTransform"): t[o.path_id] = o.read_typetree()
        elif n == "SkinnedMeshRenderer": smrs.append((o.path_id, o.read_typetree()))
    print("load: transforms", len(t), "gameobjects", len(g), "smrs", len(smrs), "12810", 12810 in t, "minmax", min(t), max(t))
    def name(pid): return g.get(gid(t[pid], "m_GameObject"), {}).get("m_Name", "?")
    def full(pid):
        ch, c = [], pid
        while c:
            ch.append(name(c)); f = gid(t[c], "m_Father")
            if f not in t: break
            c = f
        return "/".join(reversed(ch))
    # player body LOD (43 bones, root Player/ScavengerModel/metarig/spine)
    smr = None
    for pid, tt in smrs:
        rb = gid(tt, "m_RootBone")
        if rb in t and full(rb) == "Player/ScavengerModel/metarig/spine" and len(tt.get("m_Bones") or []) == 43:
            smr = (pid, tt); break
    if not smr:
        raise SystemExit("player LOD SMR not found")
    pid, tt = smr
    bones = [full(b["m_PathID"]) for b in tt["m_Bones"]]
    bones = [b[len("Player/ScavengerModel/"):] if b.startswith("Player/ScavengerModel/") else b for b in bones]
    mesh_ref = tt["m_Mesh"]
    print("SMR", pid, "bones", len(bones), "mesh ref", mesh_ref)
    print("bone paths[:6]", bones[:6])
    # m_FileID 6 -> externals[5] = sharedassets1.assets (Unity: positive ids index externals)
    e1 = UnityPy.load(S1)
    mesh = None
    for o in e1.objects:
        if o.type.name == "Mesh" and o.path_id == mesh_ref["m_PathID"]:
            mesh = o.read_typetree(); break
    if mesh is None:
        raise SystemExit("mesh not found")
    bp = mesh.get("m_BindPose") or []
    print("mesh", mesh.get("m_Name"), "bindpose", len(bp), "bones", len(bones))
    mats = {}
    for i, p in enumerate(bones):
        m = np.array([bp[i][f"e{r}{c}"] for r in range(4) for c in range(4)], dtype=float).reshape(4, 4)
        mats[p] = np.linalg.inv(m)
    pel = mats["metarig/spine"][:3, 3]
    out = {"source": "LC retail bind pose", "mesh": mesh.get("m_Name"),
           "bones": {p: {"pos": [float((m[:3, 3] - pel)[k]) for k in range(3)],
                         "rot": [float(x) for x in _mat_quat(m[:3, :3])]} for p, m in mats.items()}}
    json.dump(out, open(OUT, "w"), indent=1)
    print("wrote", OUT, "bones", len(out["bones"]))
    # segments
    def rel(a, b):
        d = mats[b][:3, 3] - mats[a][:3, 3]
        n = np.linalg.norm(d)
        return d / n if n > 1e-9 else None
    base = "metarig/spine/spine.001/spine.002/spine.003/shoulder.L"
    for a, b in [("metarig/spine/thigh.L", "metarig/spine/thigh.L/shin.L"),
                 ("metarig/spine/thigh.L/shin.L", "metarig/spine/thigh.L/shin.L/foot.L"),
                 (base, base + "/arm.L_upper"),
                 (base + "/arm.L_upper", base + "/arm.L_upper/arm.L_lower"),
                 (base + "/arm.L_upper/arm.L_lower", base + "/arm.L_upper/arm.L_lower/hand.L")]:
        v = rel(a, b)
        print(f"{a.split('/')[-1]:14s}->{b.split('/')[-1]:14s}",
              None if v is None else "(%5.2f,%5.2f,%5.2f)" % tuple(v))


def _mat_quat(m):
    # robust rotation matrix -> quaternion
    t = np.trace(m)
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        w = 0.25 * s; x = (m[2, 1] - m[1, 2]) / s; y = (m[0, 2] - m[2, 0]) / s; z = (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w = (m[2, 1] - m[1, 2]) / s; x = 0.25 * s; y = (m[0, 1] + m[1, 0]) / s; z = (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w = (m[0, 2] - m[2, 0]) / s; x = (m[0, 1] + m[1, 0]) / s; y = 0.25 * s; z = (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w = (m[1, 0] - m[0, 1]) / s; x = (m[0, 2] + m[2, 0]) / s; y = (m[1, 2] + m[2, 1]) / s; z = 0.25 * s
    return (x, y, z, w)


if __name__ == "__main__":
    main()
