#!/usr/bin/env python3
"""RETIRED 2026-11-12 - DO NOT USE ITS NUMBERS FOR ANYTHING.

Retired because it is **at half scale**: the retail hierarchy is
  Player/ScavengerModel (localScale 2.0) -> metarig (localScale 0.5608)
so LC's world scale is 1.1216. This file's fk() multiplies the metarig-local positions by 0.5608
only and never applies the ScavengerModel 2.0 factor, so every position it writes (its CSV
`*_px/py/pz`, `ref_meta.json`, everything downstream of them) is exactly 0.500x LC's world.
On top of that its arm rest pose is wrong (the compiled bundle's metarig rest is not the retail
rest; hands end up at the belly instead of the sternum) and it has no procedural rig (no leg knee
hints, no LookHead), so its arm poses are not LC's either.

Evidence and measurements: `compare_vs_lc/EVIDENCE.txt` and `compare_vs_lc/REPORT.md` section 1.
Kept in the tree only as the record of the retired pipeline; the live comparison is
`compare_vs_lc/` (real LC frames + matched-phase RenderTexture captures).

---

ORIGINAL (superseded) docstring: Canonical reference evaluation: LC's real rig + the authored
emote clips + LC's own IK.

For every ported clip and every 60 Hz frame this writes the *posed* skeleton (not raw curves):
  - 22 canonical joints: pelvis, spine_01..03, neck_01, head, clavicle_l/r,
    upperarm/lowerarm/hand_l/r, thigh/calf/foot/ball_l/r
  - world position + world rotation in the source model frame (metarig root applied, Y up)
  - the pelvis offset relative to the model root (the translation the port currently clips)
  - the arm/leg IK target positions and whether the solve clamped
Output: compare/ref/<clip>.csv + compare/ref/ref_meta.json

Output of the RETIRED file: still written under the same names, but those CSV/JSON files are
HALF-SCALE and their arm joints are wrong. Do not compare them with the port.
"""
import json, math, os, sys, zlib
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "tools"))
import extract_emotes as E

HERE = os.path.dirname(os.path.abspath(__file__))
TRACK = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.normpath(os.path.join(HERE, "..", "ref"))
FPS = 60.0

CLIPS = ["Clap", "D_Clap", "D_Middle_Finger", "D_Sign", "Idle1Fix", "Middle_Finger",
         "Prisyadka", "Salute", "Shy", "Sign", "The_Griddy", "Twerk"]

SPINE03 = "spine/spine.001/spine.002/spine.003"
ARM_TARGET = {"l": SPINE03 + "/LeftArm_target", "r": SPINE03 + "/RightArm_target"}
LEG_TARGET = {"l": "Rig 1/LeftLeg/LeftLeg_target", "r": "Rig 1/RightLeg/RightLeg_target"}
ARM_CHAIN = {"l": (SPINE03 + "/shoulder.L/arm.L_upper", SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower",
                   SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower/hand.L"),
             "r": (SPINE03 + "/shoulder.R/arm.R_upper", SPINE03 + "/shoulder.R/arm.R_upper/arm.R_lower",
                   SPINE03 + "/shoulder.R/arm.R_upper/arm.R_lower/hand.R")}
LEG_CHAIN = {"l": ("spine/thigh.L", "spine/thigh.L/shin.L", "spine/thigh.L/shin.L/foot.L"),
             "r": ("spine/thigh.R", "spine/thigh.R/shin.R", "spine/thigh.R/shin.R/foot.R")}
# LC path -> canonical joint name
CANON = {
    "spine": "pelvis",
    "spine/spine.001": "spine_01",
    "spine/spine.001/spine.002": "spine_02",
    "spine/spine.001/spine.002/spine.003": "spine_03",
    "spine/spine.001/spine.002/spine.003/spine.004": "neck_01",
    "spine/spine.001/spine.002/spine.003/spine.004/spine.004_end": "head",
}
for s, lc, alt in (("l", "L", "l"), ("r", "R", "r")):
    b = SPINE03
    CANON[f"{b}/shoulder.{lc}"] = f"clavicle_{alt}"
    CANON[f"{b}/shoulder.{lc}/arm.{lc}_upper"] = f"upperarm_{alt}"
    CANON[f"{b}/shoulder.{lc}/arm.{lc}_upper/arm.{lc}_lower"] = f"lowerarm_{alt}"
    CANON[f"{b}/shoulder.{lc}/arm.{lc}_upper/arm.{lc}_lower/hand.{lc}"] = f"hand_{alt}"
    t = f"spine/thigh.{lc}"
    CANON[t] = f"thigh_{alt}"
    CANON[f"{t}/shin.{lc}"] = f"calf_{alt}"
    CANON[f"{t}/shin.{lc}/foot.{lc}"] = f"foot_{alt}"
    CANON[f"{t}/shin.{lc}/foot.{lc}/toe.{lc}"] = f"ball_{alt}"
JOINTS = ["pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "head",
          "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l",
          "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r",
          "thigh_l", "calf_l", "foot_l", "ball_l",
          "thigh_r", "calf_r", "foot_r", "ball_r"]
PATH_OF = {v: k for k, v in CANON.items()}

# ---------------------------------------------------------------- math
def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)
def qconj(q): return (-q[0], -q[1], -q[2], q[3])
def qnorm(q):
    n = math.sqrt(sum(c*c for c in q)); return tuple(c/n for c in q) if n > 1e-12 else (0, 0, 0, 1)
def qrot(q, v):
    x, y, z, w = q; vx, vy, vz = v
    tx = 2*(y*vz - z*vy); ty = 2*(z*vx - x*vz); tz = 2*(x*vy - y*vx)
    return (vx + w*tx + (y*tz - z*ty), vy + w*ty + (z*tx - x*tz), vz + w*tz + (x*ty - y*tx))
def qang(a, b):
    d = abs(sum(x*y for x, y in zip(a, b)))
    return math.degrees(2*math.acos(max(-1.0, min(1.0, d))))
def slerp(a, b, t):
    if t <= 0: return a
    if t >= 1: return b
    d = sum(x*y for x, y in zip(a, b))
    if d < 0: b = tuple(-x for x in b); d = -d
    if d > 0.9995: return qnorm(tuple(a[i] + (b[i]-a[i])*t for i in range(4)))
    th = math.acos(max(-1, min(1, d))); s = math.sin(th)
    w1 = math.sin((1-t)*th)/s; w2 = math.sin(t*th)/s
    return qnorm(tuple(a[i]*w1 + b[i]*w2 for i in range(4)))
def from_to(a, b):
    a = vec_norm(a); b = vec_norm(b)
    d = sum(x*y for x, y in zip(a, b))
    if d > 0.999999: return (0, 0, 0, 1)
    if d < -0.999999:
        axis = (1, 0, 0) if abs(a[0]) < 0.9 else (0, 1, 0)
        c = vec_norm((a[1]*axis[2]-a[2]*axis[1], a[2]*axis[0]-a[0]*axis[2], a[0]*axis[1]-a[1]*axis[0]))
        return (c[0], c[1], c[2], 0.0)
    c = (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
    return qnorm((c[0], c[1], c[2], 1+d))
def vec_norm(v):
    n = math.sqrt(sum(c*c for c in v)); return tuple(c/n for c in v) if n > 1e-12 else (0, 0, 0)
def vadd(a, b): return (a[0]+b[0], a[1]+b[1], a[2]+b[2])
def vsub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def vmul(a, s): return (a[0]*s, a[1]*s, a[2]*s)
def vdot(a, b): return sum(x*y for x, y in zip(a, b))
def vlen(a): return math.sqrt(sum(c*c for c in a))
def vdist(a, b): return vlen(vsub(a, b))

# ---------------------------------------------------------------- rig / curves
class Rig:
    def __init__(self):
        # real install hierarchy + local offsets; rest rotations from the A-pose copy the
        # retarget uses, for bones the clips do not animate (see REPORT: the retail prefab's
        # serialized pose is a mid-animation frame, the mesh bindpose is a T-pose).
        self.real = json.load(open(os.path.join(TRACK, "compare", "lc_rig.json")))
        self.bundle = json.load(open(os.path.join(TRACK, "metarig_rest.json")))
        self.bundle = {("" if k == "metarig" else (k[len("metarig/"):] if k.startswith("metarig/") else k)): v for k, v in self.bundle.items()}
        self.parent = {}
        self.local_pos = {}
        self.local_rot = {}
        self.order = []
        # strip the retail "metarig/" prefix so keys match the authored clip paths
        strip = lambda p: "" if p == "metarig" else (p[len("metarig/"):] if p.startswith("metarig/") else p)
        for p0 in self.real["order"]:
            p = strip(p0)
            if p == "":
                self.parent[p] = None
            else:
                pr = strip(self.real["order"][self.real["order"].index(p0)] if False else p0.rsplit("/", 1)[0] if "/" in p0 else "metarig")
                self.parent[p] = pr
            b = self.real["bones"][p0]
            self.local_pos[p] = tuple(b["pos"])
            # prefer the A-pose copy's local rotation where it exists
            self.local_rot[p] = tuple(self.bundle[p]["rot"]) if p in self.bundle else tuple(b["rot"])
            self.order.append(p)
        self.root = ""
        self.root_rot = tuple(self.real["bones"]["metarig"]["rot"])
        self.root_scale = self.real["bones"]["metarig"]["scl"][0]

    def load_clip(self, name):
        fn = os.path.join(E.ANIM_DIR, name + ".anim")
        _, curves = E.parse_anim_file(fn)
        floats = E.parse_float_curves(fn)
        return curves, floats

def curve_at(curves, path, kind, t):
    k = (zlib.crc32(path.encode()) & 0xffffffff, kind)
    keys = curves.get(k)
    return E.eval_curve(keys, t) if keys else None

def float_at(floats, path, guid, t):
    keys = floats.get((zlib.crc32(path.encode()) & 0xffffffff, guid))
    return E.eval_curve(keys, t)[0] if keys else None

def fk(rig, curves, t):
    """Global rotations/positions for every real-rig node at time t (source model frame)."""
    glob, pos, locpos = {}, {}, {}
    for p in rig.order:
        if p == rig.root:
            glob[p] = rig.root_rot
            locpos[p] = (0.0, 0.0, 0.0)
            pos[p] = (0.0, 0.0, 0.0)
            continue
        pr = rig.parent[p]
        base_r = glob[pr]; base_p = pos[pr]
        e = curve_at(curves, p, "euler", t)
        lq = E.euler_to_quat(e[0], e[1], e[2]) if e else rig.local_rot[p]
        pk = curve_at(curves, p, "position", t)
        lp = tuple(pk) if pk else rig.local_pos[p]
        locpos[p] = tuple(c * rig.root_scale for c in lp)
        glob[p] = qnorm(qmul(base_r, lq))
        pos[p] = vadd(base_p, qrot(base_r, locpos[p]))
    return glob, pos, locpos

def two_bone(a, b, c, target, L1, L2, up_vec=None):
    """Same analytic solve the port uses: keep the current bend plane, clamp to reach."""
    d = vsub(target, a); dlen = vlen(d)
    dmax = (L1 + L2) * 0.999
    dmin = abs(L1 - L2) + 0.001
    dc = max(dmin, min(dmax, dlen))
    v = vmul(d, 1.0/dlen) if dlen > 1e-6 else vec_norm(vsub(c, a))
    u = vec_norm(vsub(b, a))
    p = vsub(u, vmul(v, vdot(u, v)))
    if vlen(p) < 1e-4:
        axis = (1, 0, 0) if abs(v[0]) < 0.9 else (0, 1, 0)
        p = vec_norm((v[1]*axis[2]-v[2]*axis[1], v[2]*axis[0]-v[0]*axis[2], v[0]*axis[1]-v[1]*axis[0]))
    p = vec_norm(p)
    cosA = max(-1.0, min(1.0, (L1*L1 + dc*dc - L2*L2) / (2*L1*dc)))
    sinA = math.sqrt(max(0.0, 1-cosA*cosA))
    bNew = vadd(a, vmul(vadd(vmul(v, cosA), vmul(p, sinA)), L1))
    cNew = vadd(a, vmul(v, dc))
    qA = from_to(vsub(b, a), vsub(bNew, a))
    c1 = vadd(a, qrot(qA, vsub(c, a)))
    qB = from_to(vsub(c1, bNew), vsub(cNew, bNew))
    return qA, qB, dlen > dmax

def apply_ik(rig, glob, pos, chains, targets, weights, t, tip_from_target=True):
    """Apply LC's two-bone IK (chainRotationWeight=tipRotationWeight=1) to one chain."""
    out = dict(glob)
    # rotate upper, lower, tip in parent order
    for root_p, mid_p, tip_p in chains:
        a, b, c = pos[root_p], pos[mid_p], pos[tip_p]
        tp = targets[root_p]
        w = weights[root_p]
        if w <= 1e-4:
            continue
        L1, L2 = vdist(a, b), vdist(b, c)
        target = tp["pos"]
        tgt_rot = tp["rot"]
        clamped = False
        if L1 > 1e-4 and L2 > 1e-4:
            qA, qB, clamped = two_bone(a, b, c, target, L1, L2)
            gA = qnorm(qmul(qA, out[root_p]))
            gB = qnorm(qmul(qmul(qB, qA), out[mid_p]))
            gT = tgt_rot if tip_from_target else out[tip_p]
            if w < 1:
                gA = slerp(out[root_p], gA, w)
                gB = slerp(out[mid_p], gB, w)
                gT = slerp(out[tip_p], gT, w)
            out[root_p] = gA; out[mid_p] = gB; out[tip_p] = gT
            # propagate to descendants
            _propagate(rig, out, pos, root_p, t)
        tp["clamped"] = clamped
    # recompute world positions from the new globals (local positions unchanged)
    return _repos(rig, out, pos)

def set_desc(rig, p, q):
    pass

def _propagate(rig, out, pos, root_p, t):
    # positions must be recomputed after rotations; handled by _repos
    pass

def _repos(rig, glob, pos):
    newpos = {}
    for p in rig.order:
        pr = rig.parent[p]
        if p == rig.root:
            newpos[p] = pos[p]
        else:
            local = vsub(pos[p], pos[pr])
            # local offsets are in the parent's *old* frame; recompute from hierarchy
            # (FK was done once, so use the local position directly from the rig)
            newpos[p] = vadd(newpos[pr], qrot(glob[pr], tuple(c*rig.root_scale for c in rig.local_pos[p])))
    return newpos

def eval_clip(rig, name, outdir):
    curves, floats = rig.load_clip(name)
    # clip length from the bundle manifest (the pack has it)
    pack = json.load(open(os.path.join(TRACK, "emotes", "manifest.json")))
    entry = next(c for c in pack["clips"] if c["name"] == name)
    dur = entry["duration"]; frames = entry["frames"]
    rows = []
    stats = {"clamps_arm": 0, "clamps_leg": 0, "targets": {}}
    for fi in range(frames):
        t = fi / FPS
        glob, pos, locpos = fk(rig, curves, t)
        # arm IK targets (3rd-person Rig 1/LeftArm|RightArm)
        for s in ("l", "r"):
            w = float_at(floats, "Rig 1/" + ("LeftArm" if s == "l" else "RightArm"), "c8047ffc", t)
            if w is None: w = 1.0
            tp = ARM_TARGET[s]
            tgt = {"pos": pos[tp], "rot": glob[tp]}
            if w > 1e-4:
                a, b, c = (pos[x] for x in ARM_CHAIN[s])
                L1, L2 = vdist(a, b), vdist(b, c)
                qA, qB, cl = two_bone(a, b, c, tgt["pos"], L1, L2) if L1 > 1e-4 and L2 > 1e-4 else ((0,0,0,1),(0,0,0,1),False)
                if cl: stats["clamps_arm"] += 1
                gA = qnorm(qmul(qA, glob[ARM_CHAIN[s][0]]))
                gB = qnorm(qmul(qmul(qB, qA), glob[ARM_CHAIN[s][1]]))
                gT = tgt["rot"]
                glob[ARM_CHAIN[s][0]] = gA if w >= 1 else slerp(glob[ARM_CHAIN[s][0]], gA, w)
                glob[ARM_CHAIN[s][1]] = gB if w >= 1 else slerp(glob[ARM_CHAIN[s][1]], gB, w)
                glob[ARM_CHAIN[s][2]] = gT if w >= 1 else slerp(glob[ARM_CHAIN[s][2]], gT, w)
            stats["targets"].setdefault("arm_" + s, []).append(tgt["pos"])
        # leg IK targets (Rig 1/LeftLeg|RightLeg)
        for s in ("l", "r"):
            w = float_at(floats, "Rig 1/" + ("LeftLeg" if s == "l" else "RightLeg"), "41636102", t)
            if w is None: w = 1.0
            tp = LEG_TARGET[s]
            tgt = {"pos": pos[tp], "rot": glob[tp]}
            if w > 1e-4:
                a, b, c = (pos[x] for x in LEG_CHAIN[s])
                L1, L2 = vdist(a, b), vdist(b, c)
                qA, qB, cl = two_bone(a, b, c, tgt["pos"], L1, L2) if L1 > 1e-4 and L2 > 1e-4 else ((0,0,0,1),(0,0,0,1),False)
                if cl: stats["clamps_leg"] += 1
                gA = qnorm(qmul(qA, glob[LEG_CHAIN[s][0]]))
                gB = qnorm(qmul(qmul(qB, qA), glob[LEG_CHAIN[s][1]]))
                gT = tgt["rot"]
                glob[LEG_CHAIN[s][0]] = gA if w >= 1 else slerp(glob[LEG_CHAIN[s][0]], gA, w)
                glob[LEG_CHAIN[s][1]] = gB if w >= 1 else slerp(glob[LEG_CHAIN[s][1]], gB, w)
                glob[LEG_CHAIN[s][2]] = gT if w >= 1 else slerp(glob[LEG_CHAIN[s][2]], gT, w)
            stats["targets"].setdefault("leg_" + s, []).append(tgt["pos"])
        # recompute positions from the final globals
        pos2 = {}
        for p in rig.order:
            pr = rig.parent[p]
            if p == rig.root:
                pos2[p] = pos[p]
            else:
                pos2[p] = vadd(pos2[pr], qrot(glob[pr], locpos[p]))
        pos = pos2
        rec = {"t": t, "root": (pos[rig.root], glob[rig.root])}
        for j, path in ((j, PATH_OF[j]) for j in JOINTS):
            rec[j] = (pos[path], glob[path])
        rows.append(rec)
    # write csv
    os.makedirs(outdir, exist_ok=True)
    with open(os.path.join(outdir, name + ".csv"), "w") as f:
        head = ["t", "root_px", "root_py", "root_pz", "root_qx", "root_qy", "root_qz", "root_qw"]
        for j in JOINTS:
            head += [j + "_px", j + "_py", j + "_pz", j + "_qx", j + "_qy", j + "_qz", j + "_qw"]
        f.write(",".join(head) + "\n")
        for r in rows:
            v = [r["t"]]
            v += list(r["root"][0]) + list(r["root"][1])
            for j in JOINTS:
                v += list(r[j][0]) + list(r[j][1])
            f.write(",".join(f"{x:.7g}" for x in v) + "\n")
    # ranges
    pel = [r["pelvis"][0] for r in rows]
    rootp = [r["root"][0] for r in rows]
    rel = [vsub(p, q) for p, q in zip(pel, rootp)]
    def rng(idx):
        vals = [v[idx] for v in rel]
        return (min(vals), max(vals))
    stats["pelvis_root_range"] = [rng(0), rng(1), rng(2)]
    stats["pelvis_root_bbox"] = [max(v[i] for v in rel) - min(v[i] for v in rel) for i in range(3)]
    print(f"  {name:16s} frames={frames} dur={dur:.2f} armClamps={stats['clamps_arm']} legClamps={stats['clamps_leg']} "
          f"pelvisRootRange=({stats['pelvis_root_bbox'][0]:.3f},{stats['pelvis_root_bbox'][1]:.3f},{stats['pelvis_root_bbox'][2]:.3f})")
    return stats

def main():
    rig = Rig()
    os.makedirs(OUT, exist_ok=True)
    meta = {"fps": FPS, "joints": JOINTS, "clips": {}}
    for name in CLIPS:
        meta["clips"][name] = eval_clip(rig, name, OUT)
    # rest heights / scales
    _, wp, _ = fk(rig, {}, 0.0)
    def dist(a, b): return vdist(wp[a], wp[b])
    meta["source_rest"] = {
        "pelvis_to_neck": dist("spine", SPINE03 + "/spine.004"),
        "pelvis_to_head_end": dist("spine", SPINE03 + "/spine.004/spine.004_end"),
        "thigh_to_foot_l": dist("spine/thigh.L", "spine/thigh.L/shin.L/foot.L"),
        "leg_sum_l": dist("spine/thigh.L", "spine/thigh.L/shin.L") + dist("spine/thigh.L/shin.L", "spine/thigh.L/shin.L/foot.L"),
        "leg_sum_r": dist("spine/thigh.R", "spine/thigh.R/shin.R") + dist("spine/thigh.R/shin.R", "spine/thigh.R/shin.R/foot.R"),
        "arm_reach_l": dist(SPINE03 + "/shoulder.L/arm.L_upper", SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower") + dist(SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower", SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower/hand.L"),
    }
    json.dump(meta, open(os.path.join(OUT, "ref_meta.json"), "w"), indent=1)
    print("wrote", OUT)

if __name__ == "__main__":
    main()
