#!/usr/bin/env python3
"""Extract Lethal Company emote motion from BetterEmotes 1.5.8 and bake it for Alterum.

Inputs (read only):
  ref/unz/BepInEx/plugins/BetterEmotes/animationsbundle   <- shipped compiled clips (Unity 2022.3.9f1)
  ../../tracks/cosmetics/unpack_bundle_70132              <- LC player model: metarig rest TRS
  src/lc-better-emotes-main/Unity/moreemotes/*.anim       <- authored curves (paths = CRC32 of bone path)

The compiled bundle stores CRC32 path hashes and a packed streamed clip (no path strings). This
tool reads the *authoritative* generic binding table from the bundle, resolves every path hash
against the real LC metarig hierarchy by CRC32-ing candidate path strings, and verifies that the
authored curve set covers the compiled binding set exactly. Curve values are baked from the
authored curves: they are numerically identical to the compiled stream (verified: every Euler
value of Clap's first key appears verbatim in the packed stream), and they carry explicit
tangents, so no port of Unity's packed polynomial layout is needed.

Output: emotes/manifest.json + emotes/emote.bin
  - per clip, per mapped source bone, per 60 Hz frame: the bone's *global* rotation delta from
    the source rest pose, expressed in the source model space with LC's metarig root included
    (Y-up, facing +Z).
  - per clip: pelvis vertical offset in metres (source model space).
  - per IK clip, per side: the arm IK target's global position *relative to its parent bone*
    (spine.003), its global rotation, and the clip's own ChainIKConstraint weight, at 60 Hz.
    The four IK clips drive their arms through those targets instead of through bone rotations;
    the runtime solves a two-bone IK from upperarm -> lowerarm -> hand to the ported target.
  - the source rest globals, so the runtime can compute the source->target basis.

Usage: python3 tools/extract_emotes.py [outdir]
"""
import json
import math
import os
import re
import struct
import sys
import zlib
from collections import OrderedDict

import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
TRACK = os.path.dirname(HERE)
BUNDLE = os.path.join(TRACK, "ref/unz/BepInEx/plugins/BetterEmotes/animationsbundle")
METARIG_BUNDLE = os.path.join(TRACK, "../../tracks/cosmetics/unpack_bundle_70132")
ANIM_DIR = os.path.join(TRACK, "src/lc-better-emotes-main/Unity/moreemotes")
FPS = 60.0

# LC metarig path -> Alterum player bone. Names come from the live rig dump
# (SK_Cowboy_2Avatar: pelvis / spine_01..03 / neck_01 / head / clavicle_l.. / upperarm_l.. /
#  lowerarm_l.. / hand_l.. / thigh_l.. / calf_l.. / foot_l.. / ball_l..).
# LC spine.004 is its neck bone (its child spine.004_end is the head top), so it maps to neck_01
# and the head inherits it; Alterum has no spine.004.
SEMANTIC = {
    "spine": "pelvis",
    "spine/spine.001": "spine_01",
    "spine/spine.001/spine.002": "spine_02",
    "spine/spine.001/spine.002/spine.003": "spine_03",
    "spine/spine.001/spine.002/spine.003/spine.004": "neck_01",
}
for side, lc, alt in (("L", "shoulder.L", "clavicle_l"), ("R", "shoulder.R", "clavicle_r")):
    base = "spine/spine.001/spine.002/spine.003"
    SEMANTIC[f"{base}/{lc}"] = alt
    SEMANTIC[f"{base}/{lc}/arm.{side}_upper"] = ("upperarm_" + side.lower())
    SEMANTIC[f"{base}/{lc}/arm.{side}_upper/arm.{side}_lower"] = ("lowerarm_" + side.lower())
    hand = f"{base}/{lc}/arm.{side}_upper/arm.{side}_lower/hand.{side}"
    SEMANTIC[hand] = "hand_" + side.lower()
    # LC finger1 is the thumb (shortest, offset innermost), finger2..5 = index..pinky.
    for n, altf in ((1, "thumb"), (2, "index"), (3, "middle"), (4, "ring"), (5, "pinky")):
        p1 = f"{hand}/finger{n}.{side}"
        SEMANTIC[p1] = f"{altf}_01_{side.lower()}"
        SEMANTIC[f"{p1}/finger{n}.{side}.001"] = f"{altf}_02_{side.lower()}"
        SEMANTIC[f"{p1}/finger{n}.{side}.001/finger{n}.{side}.001_end"] = f"{altf}_03_{side.lower()}"

for side in ("L", "R"):
    s = side.lower()
    thigh = f"spine/thigh.{side}"
    SEMANTIC[thigh] = f"thigh_{s}"
    SEMANTIC[f"{thigh}/shin.{side}"] = f"calf_{s}"
    leg = f"{thigh}/shin.{side}/foot.{side}"
    SEMANTIC[leg] = f"foot_{s}"
    SEMANTIC[f"{leg}/toe.{side}"] = f"ball_{s}"

PELVIS_SRC = "spine"
SRC_ROOT = "metarig"     # the object LC's Animator lives on; clip paths are relative to it

# LC drives the third-person arms through Final IK ChainIKConstraints on `metarig/Rig 1/LeftArm`
# and `/RightArm`. Their targets are `metarig/spine.003/{Left,Right}Arm_target` - nodes that exist
# on LC's prefab hierarchy but are not part of the skinned skeleton, so the first pass left them
# unmapped. The emote clips animate those targets' position/rotation directly and animate the
# constraint weight through MonoBehaviour float curves (script GUID c8047ffc, classID 114).
SPINE03 = "spine/spine.001/spine.002/spine.003"
IK_TARGETS = {"L": SPINE03 + "/LeftArm_target", "R": SPINE03 + "/RightArm_target"}
# ChainIKConstraint.weight bindings for the two third-person arm constraints.
IK_WEIGHT_PATH = {"L": "Rig 1/LeftArm", "R": "Rig 1/RightArm"}
IK_WEIGHT_CRC = {s: zlib.crc32(p.encode()) & 0xffffffff for s, p in IK_WEIGHT_PATH.items()}
CHAINIK_GUID = "c8047ffc"      # Final IK ChainIKConstraint, from the authored .anim script refs
# Clips whose arms are driven by those targets. The other LC clips animate arm bone rotations and
# some of them (Sign, D_Sign, Prisyadka) set the arm constraint weight to 0, so for them the target
# is baked but inert at runtime (the runtime blends by the baked weight).
#
# Middle_Finger was added in the third pass: both of its weights are 1 and its authored arm
# rotations are static (upperarm euler curves have a 0.00 deg spread, lower/hand have none), while
# its LeftArm_target rises 0.81 m from the hip to the face. Without the bake the port played a
# static pose (hand travel 0.001 m).
#
# The IK arrays are written in the order below, not in clip order, because `IK_CLIPS_PRIMARY` is
# what the second pass already shipped: emitting them first keeps every pre-existing blob at its
# old offset (the old emote.bin stays a byte prefix of the new one). New IK clips go last.
IK_CLIPS_PRIMARY = ("Clap", "Twerk", "Prisyadka", "Idle1Fix")
IK_CLIPS_EXTRA = ("Middle_Finger",)
IK_CLIPS = IK_CLIPS_PRIMARY + IK_CLIPS_EXTRA
IK_CLIP_ORDER = tuple(sorted(IK_CLIPS_PRIMARY)) + tuple(sorted(IK_CLIPS_EXTRA))
# Source bones of the two-bone chain (clavicle, upper arm, lower arm, hand), metarig-relative.
IK_CHAIN = {
    "L": (SPINE03 + "/shoulder.L", SPINE03 + "/shoulder.L/arm.L_upper",
          SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower",
          SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower/hand.L"),
    "R": (SPINE03 + "/shoulder.R", SPINE03 + "/shoulder.R/arm.R_upper",
          SPINE03 + "/shoulder.R/arm.R_upper/arm.R_lower",
          SPINE03 + "/shoulder.R/arm.R_upper/arm.R_lower/hand.R"),
}


# ----------------------------------------------------------------------------- math
def euler_to_quat(x, y, z):
    """Unity Quaternion.Euler: rotates z, then x, then y -> R = Ry * Rx * Rz."""
    rx, ry, rz = math.radians(x) * 0.5, math.radians(y) * 0.5, math.radians(z) * 0.5
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)
    # qz, qx, qy then q = qy*qx*qz
    qz = (0.0, 0.0, sz, cz)
    qx = (sx, 0.0, 0.0, cx)
    qy = (0.0, sy, 0.0, cy)
    return qmul(qmul(qy, qx), qz)


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q) if n > 1e-12 else (0.0, 0.0, 0.0, 1.0)


def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx = 2.0 * (y * vz - z * vy)
    ty = 2.0 * (z * vx - x * vz)
    tz = 2.0 * (x * vy - y * vx)
    return (vx + w * tx + (y * tz - z * ty),
            vy + w * ty + (z * tx - x * tz),
            vz + w * tz + (x * ty - y * tx))


def qslerp(a, b, t):
    d = sum(x * y for x, y in zip(a, b))
    if d < 0:
        b = tuple(-c for c in b)
        d = -d
    if d > 0.9995:
        return qnorm(tuple(x + (y - x) * t for x, y in zip(a, b)))
    th0 = math.acos(max(-1.0, min(1.0, d)))
    th = th0 * t
    s0 = math.sin(th0 - th) / math.sin(th0)
    s1 = math.sin(th) / math.sin(th0)
    return qnorm(tuple(a[i] * s0 + b[i] * s1 for i in range(4)))


# ----------------------------------------------------------------------------- yaml curves
def parse_keys(body):
    """(time, tuple) keyframes of one curve block."""
    keys = []
    for m in re.finditer(
            r"- serializedVersion: 3\n\s+time: (\S+)\n\s+value: (\{[^}]*\}|-?[0-9.eE+]+)\n"
            r"(?:.*?inSlope: (\{[^}]*\}|-?[0-9.eE+]+)\n\s+outSlope: (\{[^}]*\}|-?[0-9.eE+]+)\n)?",
            body, re.S):
        t = float(m.group(1))
        val = m.group(2)
        if val.startswith("{"):
            v = tuple(float(re.search(rf"{ax}: (\S+?)[,}}]", val).group(1)) for ax in "xyz")
        else:
            v = (float(val),)
        def slopes(g):
            if not g:
                return tuple(0.0 for _ in v)
            if g.startswith("{"):
                out = []
                for ax in "xyz":
                    mm = re.search(rf"{ax}: (\S+?)[,}}]", g)
                    out.append(float(mm.group(1)) if mm else 0.0)
                return tuple(out)
            return (float(g),)
        ins, outs = slopes(m.group(3)), slopes(m.group(4))
        # Unity writes +/-Infinity for flat/clamped tangents on some curves; treat as zero.
        ins = tuple(c if math.isfinite(c) else 0.0 for c in ins)
        outs = tuple(c if math.isfinite(c) else 0.0 for c in outs)
        if not all(math.isfinite(c) for c in v):
            continue
        keys.append((t, v, ins, outs))
    return keys


def eval_curve(keys, t):
    if not keys:
        return None
    if t <= keys[0][0]:
        return keys[0][1]
    if t >= keys[-1][0]:
        return keys[-1][1]
    for i in range(len(keys) - 1):
        t0, v0, _, m0 = keys[i]
        t1, v1, m1, _ = keys[i + 1]
        if t0 <= t <= t1:
            dt = t1 - t0
            if dt <= 1e-9:
                return v1
            u = (t - t0) / dt
            u2, u3 = u * u, u * u * u
            h00 = 2 * u3 - 3 * u2 + 1
            h10 = u3 - 2 * u2 + u
            h01 = -2 * u3 + 3 * u2
            h11 = u3 - u2
            out = []
            for c in range(len(v0)):
                m0c = m0[c] * dt if c < len(m0) else 0.0
                m1c = m1[c] * dt if c < len(m1) else 0.0
                v1c = v1[c] if c < len(v1) else v0[c]
                out.append(h00 * v0[c] + h10 * m0c + h01 * v1c + h11 * m1c)
            return tuple(out)
    return keys[-1][1]


KIND_GROUP = {
    "m_EulerCurves": "euler",
    "m_PositionCurves": "position",
    "m_ScaleCurves": "scale",
    "m_FloatCurves": "float",
    "m_RotationCurves": "rotation",
}


def parse_anim_file(path):
    txt = open(path, encoding="utf-8", errors="replace").read()
    name = re.search(r"\n  m_Name: (\S+)", txt).group(1)
    curves = {}
    for group, kind in KIND_GROUP.items():
        m = re.search(r"\n  " + group + r":(.*?)(?=\n  m_[A-Za-z]+:|\Z)", txt, re.S)
        if not m:
            continue
        block = m.group(1)
        for cm in re.finditer(r"- curve:\n(.*?)(?=\n  - curve:|\Z)", block, re.S):
            body = cm.group(1)
            pm = re.search(r"\n    path: (\S+)", body)
            if not pm:
                continue
            raw = pm.group(1).strip()
            hm = re.match(r"path_0x([0-9A-Fa-f]{1,8})_", raw)
            if not hm:
                # non-scrubbed path (plain relative path string) - hash it
                h = zlib.crc32(raw.encode()) & 0xffffffff
            else:
                h = int(hm.group(1), 16)
            curves[(h, kind)] = parse_keys(body)
    return name, curves


def parse_float_curves(path):
    """MonoBehaviour float curves of an authored .anim: {(path_crc, script_guid_prefix): keys}.

    Used for the arm ChainIKConstraint weight the emote clips animate. The authored files scrub
    the path and property names to `path_0x...`/`script_0x...`, but keep the CRC32 and the
    component script GUID, which is what identifies the binding.
    """
    txt = open(path, encoding="utf-8", errors="replace").read()
    out = {}
    m = re.search(r"\n  m_FloatCurves:(.*?)(?=\n  m_[A-Za-z]+:|\Z)", txt, re.S)
    if not m:
        return out
    for cm in re.finditer(
            r"- serializedVersion: 2\n    curve:\n(.*?)\n    attribute: (\S+)\n"
            r"    path: (\S+)\n    classID: (\d+)\n    script: \{fileID: \d+, guid: ([0-9a-fA-F]+)",
            m.group(1), re.S):
        body, _attr, raw, _cid, guid = cm.groups()
        hm = re.match(r"path_0x([0-9A-Fa-f]{1,8})_", raw)
        h = int(hm.group(1), 16) if hm else zlib.crc32(raw.encode()) & 0xffffffff
        out[(h, guid[:8].lower())] = parse_keys(body)
    return out


# ----------------------------------------------------------------------------- bundle
def bundle_clips():
    env = UnityPy.load(BUNDLE)
    out = {}
    for obj in env.objects:
        if obj.type.name != "AnimationClip":
            continue
        tt = obj.read_typetree()
        name = tt.get("m_Name")
        if not name:
            continue
        mc = tt["m_MuscleClip"]
        bind = [(b["path"], b["attribute"], b["typeID"], b.get("customType", 0))
                for b in tt["m_ClipBindingConstant"]["genericBindings"]]
        out[name] = {
            "stopTime": float(mc["m_StopTime"]),
            "loop": bool(mc["m_LoopTime"]),
            "bindings": bind,
            "sampleRate": float(tt.get("m_SampleRate") or 60.0),
        }
    return out


def binding_kind(attr, typeID, custom):
    if typeID == 4:
        return {1: "position", 2: "rotation", 3: "scale", 4: "euler"}.get(attr, f"attr{attr}")
    return f"type{typeID}"


# ----------------------------------------------------------------------------- metarig rest
def metarig_rest():
    env = UnityPy.load(METARIG_BUNDLE)
    g, t = {}, {}
    for obj in env.objects:
        if obj.type.name == "GameObject":
            g[obj.path_id] = obj.read_typetree()
        elif obj.type.name == "Transform":
            t[obj.path_id] = obj.read_typetree()

    def father(tt):
        f = tt.get("m_Father")
        return f.get("m_PathID", 0) if isinstance(f, dict) else 0

    def gname(pid):
        go = t[pid].get("m_GameObject")
        gid = go.get("m_PathID", 0) if isinstance(go, dict) else 0
        return g.get(gid, {}).get("m_Name", "?")

    kids = {}
    for pid, tt in t.items():
        kids.setdefault(father(tt), []).append(pid)
    root = next(p for p in t if gname(p) == SRC_ROOT)
    rec = {}
    stack = [(root, "")]
    while stack:
        pid, prefix = stack.pop()
        n = gname(pid)
        path = n if prefix == "" else prefix + "/" + n
        tt = t[pid]
        r, pos, s = tt["m_LocalRotation"], tt["m_LocalPosition"], tt["m_LocalScale"]
        rel = "" if pid == root else (path[len(SRC_ROOT) + 1:])
        rec[rel] = {"rot": [r["x"], r["y"], r["z"], r["w"]],
                    "pos": [pos["x"], pos["y"], pos["z"]],
                    "scl": [s["x"], s["y"], s["z"]]}
        for c in kids.get(pid, []):
            stack.append((c, path))
    return rec


# ----------------------------------------------------------------------------- bake
def main():
    outdir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(TRACK, "emotes")
    rest = metarig_rest()
    clips = bundle_clips()

    # parent lookup from the metarig paths
    parent = {}
    for p in rest:
        if p == "":
            continue
        parent[p] = p.rsplit("/", 1)[0] if "/" in p else ""
    by_crc = {zlib.crc32(("" if p == "" else p).encode()) & 0xffffffff: p
              for p in rest}

    # Assert the documented source fixtures before anything is decoded.
    fixtures = {"spine": 2463100623, "spine/spine.001": 254801669,
                "spine/spine.001/spine.002": 4095213152,
                "spine/spine.001/spine.002/spine.003": 674009458,
                "spine/thigh.L": 49267413, "spine/thigh.R": 4175495094}
    bad = {p: (e, zlib.crc32(p.encode()) & 0xffffffff) for p, e in fixtures.items()
           if (zlib.crc32(p.encode()) & 0xffffffff) != e or p not in rest}
    print(f"metarig rest bones: {len(rest)}  clips in bundle: {len(clips)}  "
          f"crc32 fixtures: {'ALL 6 MATCH' if not bad else 'MISMATCH ' + str(bad)}")
    if bad:
        sys.exit("fixture mismatch - refusing to bake")

    report = {"armIk": {}, "unresolved": {}, "coverage": {}}
    anim_files = {}
    float_files = {}
    for fn in sorted(os.listdir(ANIM_DIR)):
        if not fn.endswith(".anim"):
            continue
        try:
            name, curves = parse_anim_file(os.path.join(ANIM_DIR, fn))
            float_files[name] = parse_float_curves(os.path.join(ANIM_DIR, fn))
        except Exception as e:
            print("  skip", fn, e)
            continue
        anim_files[name] = curves

    pack = OrderedDict()
    pack["version"] = 2
    pack["fps"] = FPS
    pack["sourceRoot"] = SRC_ROOT

    # ---- source global rest (metarig root rotation included, clips are relative to metarig)
    src_rest_rot, src_rest_pos = {}, {}
    order = sorted([p for p in rest if p != ""], key=lambda p: (p.count("/"), p))
    for p in order:
        pr = parent[p]
        base_r = (0.0, 0.0, 0.0, 1.0) if pr == "" else src_rest_rot[pr]
        # root bone ("") has no parent; its local rotation is metarig's own
        if pr == "":
            base_r = tuple(rest[""]["rot"])
            base_p = tuple(rest[""]["pos"])
        else:
            base_p = src_rest_pos[pr]
        src_rest_rot[p] = qnorm(qmul(base_r, tuple(rest[p]["rot"])))
        lp = tuple(rest[p]["pos"])
        rp = qrot(base_r, lp)
        src_rest_pos[p] = (base_p[0] + rp[0], base_p[1] + rp[1], base_p[2] + rp[2])
    pack["sourceRest"] = {p: list(src_rest_rot[p]) + list(src_rest_pos[p]) for p in src_rest_rot}
    pack["targetMap"] = {p: t for p, t in SEMANTIC.items() if p in rest}
    missing_map = [p for p in SEMANTIC if p not in rest]
    if missing_map:
        print("  WARN semantic paths not in metarig rest:", missing_map)

    blobs = []          # (label, float array)
    ik_blobs = []       # IK arrays go last so every pre-existing blob offset stays byte-identical
    clip_entries = []
    ik_order = {n: i for i, n in enumerate(IK_CLIP_ORDER)}   # emission order, see IK_CLIPS_PRIMARY

    for name in sorted(clips):
        info = clips[name]
        yam = anim_files.get(name)
        if yam is None:
            print(f"  MISSING authored curves for {name}: skipped")
            continue
        bset = {(b[0], binding_kind(b[1], b[2], b[3])) for b in info["bindings"]}
        yset = set((h, k) for (h, k) in yam)
        # bundle carries extra non-transform (MonoBehaviour) bindings; compare transform only
        bset_tf = {x for x in bset if x[1] in ("position", "rotation", "euler", "scale")}
        yset_tf = {x for x in yset if x[1] in ("position", "rotation", "euler", "scale")}
        unresolved = sorted(bset_tf - yset_tf)
        extra = sorted(yset_tf - bset_tf)
        report["coverage"][name] = {
            "bundleTransformBindings": len(bset_tf),
            "authoredCurves": len(yset_tf),
            "bundleOnly": len(unresolved),
            "authoredOnly": len(extra),
        }
        # Every transform binding is classified: resolved skeletal bone, or an explicitly
        # ignored node that is not part of the LC metarig skeleton (props, targets, attachments
        # and the first-person legs that only exist on LC's own prefab, not on Alterum's rig).
        unk = sorted({u[0] for u in bset_tf if u[0] not in by_crc})
        report.setdefault("ignoredHashes", {})[name] = [f"0x{h:08X}" for h in unk]
        if unresolved or extra:
            report["unresolved"][name] = {
                "bundleOnly": [[h, k] for h, k in unresolved][:40],
                "authoredOnly": [[h, k] for h, k in extra][:40],
            }
        # A mapped bone with no curve in this clip simply stays at its rest pose (delta = identity).
        semantic_bad = [p for p in SEMANTIC if p in rest and p not in {by_crc.get(h) for h, _ in bset_tf}]
        if semantic_bad:
            report.setdefault("bonesAtRest", {})[name] = semantic_bad
        if unk:
            print(f"  EMOTE_HASH_IGNORED {name}: {len(unk)} transform bindings on nodes absent from the "
                  f"LC metarig skeleton (props/targets/first-person legs) - not retargetable, ignored")

        dur = info["stopTime"]
        nframes = int(round(dur * FPS)) + 1
        bones = [p for p in order if p in SEMANTIC]
        need_anims = set()
        for p in bones:
            # every ancestor rotation matters for the global pose
            q = p
            while q != "":
                need_anims.add(q)
                q = parent[q]

        # ---- per-frame source global rotation + global position
        frames = []
        pos_frames = []
        for fi in range(nframes):
            t = fi / FPS
            glob, gpos = {}, {}
            for p in order:
                pr = parent[p]
                base = tuple(rest[""]["rot"]) if pr == "" else glob[pr]
                base_p = tuple(rest[""]["pos"]) if pr == "" else gpos[pr]
                keys = yam.get((zlib.crc32(p.encode()) & 0xffffffff, "euler"))
                if keys:
                    v = eval_curve(keys, t)
                    lq = euler_to_quat(v[0], v[1], v[2])
                else:
                    lq = tuple(rest[p]["rot"])
                glob[p] = qnorm(qmul(base, lq))
                pk = yam.get((zlib.crc32(p.encode()) & 0xffffffff, "position"))
                lp = tuple(eval_curve(pk, t)) if pk else tuple(rest[p]["pos"])
                rp = qrot(base, tuple(lp))
                gpos[p] = (base_p[0] + rp[0], base_p[1] + rp[1], base_p[2] + rp[2])
            frames.append(glob)
            pos_frames.append(gpos)

        # rest pose delta basis is the clip's own rest = metarig rest globals (t-independent)
        delta = {p: [qmul(frames[fi][p], qconj(src_rest_rot[p])) for fi in range(nframes)]
                 for p in bones}

        # ---- pelvis vertical offset (source model space, metres)
        pelvis_keys = yam.get((zlib.crc32(PELVIS_SRC.encode()) & 0xffffffff, "position"))
        rest_spine = tuple(rest[PELVIS_SRC]["pos"])
        pelvis_y = []
        for fi in range(nframes):
            if pelvis_keys:
                v = eval_curve(pelvis_keys, fi / FPS)
                lp = (v[0] - rest_spine[0], v[1] - rest_spine[1], v[2] - rest_spine[2])
            else:
                lp = (0.0, 0.0, 0.0)
            w = qrot(tuple(rest[""]["rot"]), lp)
            pelvis_y.append(w[1])

        rot_label = f"{name}.rot"
        blobs.append((rot_label, [c for fi in range(nframes) for p in bones for c in delta[p][fi]]))
        py_label = f"{name}.pelvisy"
        blobs.append((py_label, pelvis_y))

        clip_entries.append({
            "name": name,
            "duration": dur,
            "loop": info["loop"],
            "frames": nframes,
            "fps": FPS,
            "bones": bones,
            "rotBlob": rot_label,
            "pelvisBlob": py_label,
        })
        print(f"  {name:16s} dur={dur:6.3f}s loop={str(info['loop']):5s} frames={nframes:3d} "
              f"bones={len(bones):2d} cover={len(yset_tf)}/{len(bset_tf)} unresolved={len(unresolved)}")

        # ---- arm IK targets (only the clips that actually drive the arms through them)
        ik_info = {}
        flt = float_files.get(name, {})
        for side in ("L", "R"):
            byteset = {b[0] for b in info["bindings"] if binding_kind(b[1], b[2], b[3]) == "type114"}
            weights_keys = flt.get((IK_WEIGHT_CRC[side], CHAINIK_GUID))
            if name not in IK_CLIPS:
                continue
            path = IK_TARGETS[side]
            if path not in rest:
                print(f"  WARN IK target {path} not in metarig rest")
                continue
            pk = yam.get((zlib.crc32(path.encode()) & 0xffffffff, "position"))
            if not pk:
                print(f"  WARN {name}: no position curve for {path} - IK disabled for side {side}")
                continue
            if IK_WEIGHT_CRC[side] not in byteset:
                print(f"  WARN {name}: bundle has no weight binding for {IK_WEIGHT_PATH[side]}")
            pos = []
            for fi in range(nframes):
                parent_pos = pos_frames[fi][SPINE03]
                tp = pos_frames[fi][path]
                pos.extend((tp[0] - parent_pos[0], tp[1] - parent_pos[1], tp[2] - parent_pos[2]))
            rot = [c for fi in range(nframes) for c in frames[fi][path]]
            if weights_keys:
                wv = [max(0.0, min(1.0, eval_curve(weights_keys, fi / FPS)[0])) for fi in range(nframes)]
            else:
                # No curve for this clip: the prefab default (1.0). Evidenced by Salute/The_Griddy/
                # Shy authoring an explicit 0 and the reference mod restoring 1 after each emote.
                wv = [1.0] * nframes
            pos_label = f"{name}.ik{side}.pos"
            rot_label2 = f"{name}.ik{side}.rot"
            w_label = f"{name}.ik{side}.w"
            ik_blobs.append((pos_label, pos))
            ik_blobs.append((rot_label2, rot))
            ik_blobs.append((w_label, wv))
            up, lo, ha = IK_CHAIN[side][1], IK_CHAIN[side][2], IK_CHAIN[side][3]
            src_reach = (sum((src_rest_pos[up][i] - src_rest_pos[lo][i]) ** 2 for i in range(3)) ** 0.5 +
                         sum((src_rest_pos[lo][i] - src_rest_pos[ha][i]) ** 2 for i in range(3)) ** 0.5)
            ik_info[side] = {
                "posBlob": pos_label, "rotBlob": rot_label2, "weightBlob": w_label,
                "parent": SPINE03, "frames": nframes, "weightCurve": bool(weights_keys),
                "weightMin": round(min(wv), 3), "weightMax": round(max(wv), 3),
                "srcReach": round(src_reach, 4),
                "srcTargetDistance": [round((pos[fi * 3] ** 2 + pos[fi * 3 + 1] ** 2 + pos[fi * 3 + 2] ** 2) ** 0.5, 4)
                                      for fi in range(0, nframes, max(1, nframes // 4))],
            }
            print(f"    ik{side}: {IK_TARGETS[side]} weight={'curve' if weights_keys else 'default1'} " +
                  f"[{min(wv):.2f}..{max(wv):.2f}] srcReach={src_reach:.3f}")
        if ik_info:
            report["armIk"][name] = ik_info

    # ---- write
    # IK arrays in the shipped order first (old offsets unchanged), then any newly added clip's.
    # A stable sort keeps each clip's own L/R + pos/rot/w order untouched.
    ik_blobs.sort(key=lambda b: ik_order.get(b[0].split(".")[0], len(ik_order)))
    os.makedirs(outdir, exist_ok=True)
    bin_path = os.path.join(outdir, "emote.bin")
    offsets = {}
    with open(bin_path, "wb") as f:
        for label, arr in blobs + ik_blobs:
            offsets[label] = f.tell()
            f.write(struct.pack("<%df" % len(arr), *arr))
    for c in clip_entries:
        c["rotOffset"] = offsets[c.pop("rotBlob")]
        c["pelvisOffset"] = offsets[c.pop("pelvisBlob")]
    for clip_name, sides in report["armIk"].items():
        for side, meta in sides.items():
            meta["posOffset"] = offsets[meta["posBlob"]] // 4
            meta["rotOffset"] = offsets[meta["rotBlob"]] // 4
            meta["weightOffset"] = offsets[meta["weightBlob"]] // 4
    pack["clips"] = clip_entries
    pack["ik"] = report["armIk"]
    json.dump(pack, open(os.path.join(outdir, "manifest.json"), "w"), indent=1)

    # Line-based manifest for the hot assembly: no JSON dependency inside IL2CPP.
    txt = ["version 2", f"fps {FPS:.4f}", "file emote.bin"]
    for src, dst in sorted(pack["targetMap"].items()):
        txt.append(f"map {src} {dst}")
    for src, v in sorted(pack["sourceRest"].items()):
        txt.append("rest %s %.7g %.7g %.7g %.7g %.7g %.7g %.7g" % (src, *v))
    for c in clip_entries:
        txt.append("clip %s %.6f %d %d %.4f %d %d %s" % (
            c["name"], c["duration"], 1 if c["loop"] else 0, c["frames"], c["fps"],
            c["rotOffset"] // 4, c["pelvisOffset"] // 4, " ".join(c["bones"])))
    for clip_name in sorted(report["armIk"], key=lambda n: ik_order.get(n, len(ik_order))):
        sides = report["armIk"][clip_name]
        for side, meta in sorted(sides.items()):
            txt.append("ik %s %s %d %d %d %d %s" % (
                clip_name, side, meta["posOffset"], meta["rotOffset"], meta["weightOffset"],
                meta["frames"], meta["parent"]))
    open(os.path.join(outdir, "manifest.txt"), "w").write("\n".join(txt) + "\n")
    print(f"wrote manifest.txt ({len(txt)} lines)")
    json.dump(report, open(os.path.join(outdir, "coverage.json"), "w"), indent=1)
    print(f"\nwrote {bin_path} ({os.path.getsize(bin_path)} bytes), "
          f"{len(clip_entries)} clips, {len(pack['targetMap'])} mapped bones")
    print("coverage:", json.dumps(report["coverage"], indent=0)[:1200])


if __name__ == "__main__":
    main()
