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
    Every clip whose authored weight is non-zero on at least one side (10 of the 12) gets these
    arrays; the runtime solves a two-bone IK from upperarm -> lowerarm -> hand to the ported
    target and blends by fade * that weight, so a weight-0 side (Salute's right) is a no-op and a
    weight-0 clip (Prisyadka, Sign, D_Sign) is still stored but never acted on.
  - the source rest globals, so the runtime can compute the source->target basis.
  - per clip: `armconform <clip> 0|1` (rest-frame arm conform, default 1), `leghint <clip> <L|R>
    x y z` (LC's leg knee-pole node, model-root relative, constant) and, for the clips in
    `AO_ARM_CLIPS`, `armao <clip> <offset> <frames>` + the global `aobone`/`aorest` lines: the
    arms-only arm chain's global deltas, which are the visible arm pose of a clip whose arm IK
    weight is 0 (Prisyadka).

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
#
# Fourth pass: `tools/ik_audit.py` classified all 12 ported clips in one pass. Every clip whose
# ChainIKConstraint weight is non-zero on at least one frame now has a bake; the only clips left
# out are the three that author weight 0 for both sides (Prisyadka, Sign, D_Sign), whose IK is
# therefore inert in LC too.
#   D_Clap             L/R 1..1   target 0.082/0.024 m  (same clip as Clap, with the prop)
#   D_Middle_Finger    L/R 1..1   target 0.827/0.839 m
#   Salute             L 1       target 0.171 m; R weight 0 -> the baked R array is a runtime no-op
#   Shy                L/R 1..1   target 0.028/0.047 m  (pins an authored 34 deg left-arm swing)
#   The_Griddy         L/R 1..1   target 1.226/1.214 m
IK_CLIPS_PRIMARY = ("Clap", "Twerk", "Prisyadka", "Idle1Fix")
IK_CLIPS_EXTRA = ("Middle_Finger",)
IK_CLIPS_NEW = ("D_Clap", "D_Middle_Finger", "Salute", "Shy", "The_Griddy")
IK_CLIPS = IK_CLIPS_PRIMARY + IK_CLIPS_EXTRA + IK_CLIPS_NEW
IK_CLIP_ORDER = (tuple(sorted(IK_CLIPS_PRIMARY)) + tuple(sorted(IK_CLIPS_EXTRA)) +
                 tuple(sorted(IK_CLIPS_NEW)))
# Source bones of the two-bone chain (clavicle, upper arm, lower arm, hand), metarig-relative.
IK_CHAIN = {
    "L": (SPINE03 + "/shoulder.L", SPINE03 + "/shoulder.L/arm.L_upper",
          SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower",
          SPINE03 + "/shoulder.L/arm.L_upper/arm.L_lower/hand.L"),
    "R": (SPINE03 + "/shoulder.R", SPINE03 + "/shoulder.R/arm.R_upper",
          SPINE03 + "/shoulder.R/arm.R_upper/arm.R_lower",
          SPINE03 + "/shoulder.R/arm.R_upper/arm.R_lower/hand.R"),
}

# The legs are driven the same way: Unity Animation Rigging TwoBoneIKConstraints on
# `metarig/Rig 1/{Left,Right}Leg`, root=thigh, mid=shin, tip=foot, target=`...Leg_target`,
# targetPositionWeight=targetRotationWeight=1, constraint weight 1 (authored weight curves are
# constant 1 on every ported clip). The retail prefab has these nodes; the MoreCompany copy the
# first passes used does not, which is why they were never baked. The target positions are in the
# metarig frame (Rig 1 is a child of metarig), i.e. root-relative, not pelvis-relative - the legs
# stay planted while the hips translate.
LEG_TARGETS = {"L": "Rig 1/LeftLeg/LeftLeg_target", "R": "Rig 1/RightLeg/RightLeg_target"}
LEG_HINTS = {"L": "Rig 1/LeftLeg/LeftLeg_hint", "R": "Rig 1/RightLeg/RightLeg_hint"}
LEG_WEIGHT_PATH = {"L": "Rig 1/LeftLeg", "R": "Rig 1/RightLeg"}

# Per-clip rest-frame arm conform (see PosePlayer.ConformArmAnchors). Default on (1): the target
# mesh's bind pose is a T-pose while the source rest is an A-pose, so each arm bone's anchor is
# pre-rotated onto the source rest direction. Off (0) keeps the target's own bind arm and applies
# only the clip's delta; that is what the arm-IK-weight-0 clips show, so it is a per-clip decision.
# Prisyadka is measured with it off (see compare_vs_lc/REPORT.md).
ARM_CONFORM = {}   # default on; every measured clip keeps it

LEG_WEIGHT_GUID = "41636102"      # Unity.Animation.Rigging TwoBoneIKConstraint
LEG_CHAIN = {
    "L": ("spine/thigh.L", "spine/thigh.L/shin.L", "spine/thigh.L/shin.L/foot.L"),
    "R": ("spine/thigh.R", "spine/thigh.R/shin.R", "spine/thigh.R/shin.R/foot.R"),
}

# The visible arm pose of a clip whose ChainIK weight is 0 does NOT come from the third-person arm
# curves. The prefab carries a second, independent arm skeleton, `ScavengerModelArmsOnly/metarig`
# (the first-person arms model), and the emote animates both: for Prisyadka the third-person
# shoulder swings 139 deg while the arms-only chain keeps the forearms crossed in front of the
# chest -- which is exactly what the real frames show, at every phase. When the arm IK is inert,
# the arms-only chain is therefore the pose source, baked as the same global rotation deltas
# (relative to its own rest) for these eight bones.
AO_ROOT = "ScavengerModelArmsOnly/metarig/spine.003"
AO_ARM_BONES = [
    ("clavicle_l", AO_ROOT + "/shoulder.L"),
    ("upperarm_l", AO_ROOT + "/shoulder.L/arm.L_upper"),
    ("lowerarm_l", AO_ROOT + "/shoulder.L/arm.L_upper/arm.L_lower"),
    ("hand_l", AO_ROOT + "/shoulder.L/arm.L_upper/arm.L_lower/hand.L"),
    ("clavicle_r", AO_ROOT + "/shoulder.R"),
    ("upperarm_r", AO_ROOT + "/shoulder.R/arm.R_upper"),
    ("lowerarm_r", AO_ROOT + "/shoulder.R/arm.R_upper/arm.R_lower"),
    ("hand_r", AO_ROOT + "/shoulder.R/arm.R_upper/arm.R_lower/hand.R"),
]
# Clips whose arm IK weight is 0 on both sides (so the authored arm chain is the visible pose) and
# which therefore take the arms-only bake. Extend only after checking the weights with ik_audit.
AO_ARM_CLIPS = ("Prisyadka",)


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
    # The bundle copy is the A-pose rest copy used for retargeting, but it does not contain the
    # Rig 1 target nodes (leg IK targets, leg hints, arm hints). Add every retail node it lacks
    # from the extracted install rig, keeping the copy for all shared bone paths.
    real_path = os.path.join(TRACK, "compare", "lc_rig.json")
    retail_only = set()
    if os.path.exists(real_path):
        real = json.load(open(real_path))
        for p0 in real["order"]:
            rel = "" if p0 == "metarig" else (p0[len("metarig/"):] if p0.startswith("metarig/") else p0)
            if rel in rec:
                continue
            b = real["bones"][p0]
            rec[rel] = {"rot": list(b["rot"]), "pos": list(b["pos"]), "scl": list(b["scl"])}
            retail_only.add(rel)
        if retail_only:
            print(f"metarig rest: +{len(retail_only)} retail-only nodes (Rig 1 targets/hints) merged into the copy")
    return rec, retail_only


# ----------------------------------------------------------------------------- bake
def main():
    outdir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(TRACK, "emotes")
    rest, retail_only = metarig_rest()
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

    report = {"armIk": {}, "legIk": {}, "unresolved": {}, "coverage": {}}
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
    # Keep the pre-existing manifest lines byte-identical: the retail-only nodes (Rig 1 targets,
    # hints) are needed to bake the leg IK, but they are not target bones and no runtime path reads
    # their source rest, so they are not written to the pack's sourceRest.
    pack["sourceRest"] = {p: list(src_rest_rot[p]) + list(src_rest_pos[p])
                          for p in src_rest_rot if p not in retail_only}
    pack["targetMap"] = {p: t for p, t in SEMANTIC.items() if p in rest}
    missing_map = [p for p in SEMANTIC if p not in rest]
    if missing_map:
        print("  WARN semantic paths not in metarig rest:", missing_map)

    blobs = []          # (label, float array)
    ik_blobs = []       # arm IK arrays go last so every pre-existing blob offset stays byte-identical
    new_blobs = []      # pelvis3 + leg IK arrays, appended after every pre-existing byte
    ao_blobs = []       # arms-only arm-chain arrays, appended after every existing new_blob
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
        pelvis3 = []
        for fi in range(nframes):
            if pelvis_keys:
                v = eval_curve(pelvis_keys, fi / FPS)
                lp = (v[0] - rest_spine[0], v[1] - rest_spine[1], v[2] - rest_spine[2])
            else:
                lp = (0.0, 0.0, 0.0)
            w = qrot(tuple(rest[""]["rot"]), lp)
            pelvis_y.append(w[1])
            pelvis3.extend(w)

        rot_label = f"{name}.rot"
        blobs.append((rot_label, [c for fi in range(nframes) for p in bones for c in delta[p][fi]]))
        py_label = f"{name}.pelvisy"
        blobs.append((py_label, pelvis_y))
        p3_label = f"{name}.pelvis3"
        new_blobs.append((p3_label, pelvis3))

        clip_entries.append({
            "name": name,
            "duration": dur,
            "loop": info["loop"],
            "frames": nframes,
            "fps": FPS,
            "bones": bones,
            "rotBlob": rot_label,
            "pelvisBlob": py_label,
            "pelvis3Blob": p3_label,
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
            # Target offset relative to the chain root (upperarm), not spine.003. Anchoring the
            # IK triangle at the target's own chain root removes this model's clavicle/chest
            # proportion bias from the shoulder->target direction.
            up = IK_CHAIN[side][1]
            pos_root = []
            for fi in range(nframes):
                rp = pos_frames[fi][up]
                tp = pos_frames[fi][path]
                pos_root.extend((tp[0] - rp[0], tp[1] - rp[1], tp[2] - rp[2]))
            root_label = f"{name}.ik{side}.root"
            new_blobs.append((root_label, pos_root))
            up, lo, ha = IK_CHAIN[side][1], IK_CHAIN[side][2], IK_CHAIN[side][3]
            src_reach = (sum((src_rest_pos[up][i] - src_rest_pos[lo][i]) ** 2 for i in range(3)) ** 0.5 +
                         sum((src_rest_pos[lo][i] - src_rest_pos[ha][i]) ** 2 for i in range(3)) ** 0.5)
            ik_info[side] = {
                "posBlob": pos_label, "rotBlob": rot_label2, "weightBlob": w_label,
                "rootPosBlob": root_label,
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

        # ---- leg IK targets: TwoBoneIKConstraint on Rig 1/{Left,Right}Leg, weight 1, target
        # position root-relative and target rotation applied to the foot (both weights = 1).
        leg_info = {}
        for side in ("L", "R"):
            path = LEG_TARGETS[side]
            if path not in rest:
                print(f"  WARN leg IK target {path} not in metarig rest/retail merge")
                continue
            pos = []
            root_pos = tuple(rest[""]["pos"])
            for fi in range(nframes):
                tp = pos_frames[fi][path]
                pos.extend((tp[0] - root_pos[0], tp[1] - root_pos[1], tp[2] - root_pos[2]))
            rot = [c for fi in range(nframes) for c in frames[fi][path]]
            wk = flt.get((zlib.crc32(LEG_WEIGHT_PATH[side].encode()) & 0xffffffff, LEG_WEIGHT_GUID))
            wv = ([max(0.0, min(1.0, eval_curve(wk, fi / FPS)[0])) for fi in range(nframes)]
                  if wk else [1.0] * nframes)
            pos_label = f"{name}.leg{side}.pos"
            rot_label2 = f"{name}.leg{side}.rot"
            w_label = f"{name}.leg{side}.w"
            new_blobs.append((pos_label, pos))
            new_blobs.append((rot_label2, rot))
            new_blobs.append((w_label, wv))
            th = LEG_CHAIN[side][0]
            pos_root = []
            for fi in range(nframes):
                rp = pos_frames[fi][th]
                tp = pos_frames[fi][path]
                pos_root.extend((tp[0] - rp[0], tp[1] - rp[1], tp[2] - rp[2]))
            root_label = f"{name}.leg{side}.root"
            new_blobs.append((root_label, pos_root))
            th, sh, ft = LEG_CHAIN[side]
            src_reach = (sum((src_rest_pos[th][i] - src_rest_pos[sh][i]) ** 2 for i in range(3)) ** 0.5 +
                         sum((src_rest_pos[sh][i] - src_rest_pos[ft][i]) ** 2 for i in range(3)) ** 0.5)
            # LC's TwoBoneIKConstraint has a hint (knee pole) node; it is not animated, so one
            # model-root-relative position per clip/side is all the runtime needs. Without it the
            # port used the current knee direction, which for a deep squat degenerates and folds
            # the knee under/inside instead of forward-out.
            hint_path = LEG_HINTS[side]
            hint = None
            if hint_path in pos_frames[0]:
                hp = pos_frames[0][hint_path]
                hint = (hp[0] - root_pos[0], hp[1] - root_pos[1], hp[2] - root_pos[2])
            leg_info[side] = {
                "posBlob": pos_label, "rotBlob": rot_label2, "weightBlob": w_label,
                "rootPosBlob": root_label,
                "parent": "", "frames": nframes, "weightCurve": bool(wk),
                "weightMin": round(min(wv), 3), "weightMax": round(max(wv), 3),
                "srcReach": round(src_reach, 4),
                "hint": [round(x, 6) for x in hint] if hint else None,
            }
            print(f"    leg{side}: {path} weight={'curve' if wk else 'default1'} "
                  f"[{min(wv):.2f}..{max(wv):.2f}] srcReach={src_reach:.3f} "
                  f"hint={None if hint is None else tuple(round(x, 3) for x in hint)}")
        if leg_info:
            report["legIk"][name] = leg_info

        # ---- arms-only arm chain: the visible arm pose of an arm-IK-inert clip (see AO_ARM_BONES)
        if name in AO_ARM_CLIPS:
            ao = []
            for fi in range(nframes):
                for _target, src in AO_ARM_BONES:
                    d = qmul(frames[fi][src], qconj(src_rest_rot[src]))
                    ao.extend(d)
            label = f"{name}.armao"
            ao_blobs.append((label, ao))
            clip_entries[-1]["armAoBlob"] = label
            clip_entries[-1]["armAoFrames"] = nframes
            report.setdefault("armAo", {})[name] = {
                "blob": label, "frames": nframes, "bones": [b for b, _ in AO_ARM_BONES],
                "srcPaths": [p for _, p in AO_ARM_BONES]}
            print(f"  arm-ao: {name} arms-only chain baked ({len(AO_ARM_BONES)} bones x {nframes}f)")

    # ---- write
    # IK arrays in the shipped order first (old offsets unchanged), then any newly added clip's.
    # A stable sort keeps each clip's own L/R + pos/rot/w order untouched.
    ik_blobs.sort(key=lambda b: ik_order.get(b[0].split(".")[0], len(ik_order)))
    os.makedirs(outdir, exist_ok=True)
    bin_path = os.path.join(outdir, "emote.bin")
    offsets = {}
    with open(bin_path, "wb") as f:
        for label, arr in blobs + ik_blobs + new_blobs + ao_blobs:
            offsets[label] = f.tell()
            f.write(struct.pack("<%df" % len(arr), *arr))
    for c in clip_entries:
        c["rotOffset"] = offsets[c.pop("rotBlob")]
        c["pelvisOffset"] = offsets[c.pop("pelvisBlob")]
        c["pelvis3Offset"] = offsets[c.pop("pelvis3Blob")] // 4
        if c.get("armAoBlob"):
            c["armAoOffset"] = offsets[c.pop("armAoBlob")] // 4
    for clip_name, sides in report["armIk"].items():
        for side, meta in sides.items():
            meta["posOffset"] = offsets[meta["posBlob"]] // 4
            meta["rootPosOffset"] = offsets[meta.pop("rootPosBlob")] // 4
            meta["rotOffset"] = offsets[meta.pop("rotBlob")] // 4
            meta["weightOffset"] = offsets[meta.pop("weightBlob")] // 4
    for clip_name, sides in report.get("legIk", {}).items():
        for side, meta in sides.items():
            meta["posOffset"] = offsets[meta.pop("posBlob")] // 4
            meta["rotOffset"] = offsets[meta.pop("rotBlob")] // 4
            meta["weightOffset"] = offsets[meta.pop("weightBlob")] // 4
            meta["rootPosOffset"] = offsets[meta.pop("rootPosBlob")] // 4
    pack["clips"] = clip_entries
    pack["ik"] = report["armIk"]
    pack["legIk"] = report.get("legIk", {})
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
    # arms-only arm chain: which source path each arm bone comes from, then the per-clip blob
    # (after the clip lines, so the loader already knows the clip when it reads `armao`)
    for slot, (target, src) in enumerate(AO_ARM_BONES):
        txt.append("aobone %d %s %s" % (slot, target, src))
        # The arms-only nodes are retail-only, so they are not in `sourceRest` above; the AO bake
        # needs their own rest (delta reference + conform rest).
        rr = src_rest_rot[src]
        rp = src_rest_pos[src]
        txt.append("aorest %d %.7g %.7g %.7g %.7g %.7g %.7g %.7g" % (slot, *rr, *rp))
    for c in clip_entries:
        if c.get("armAoOffset") is not None:
            txt.append("armao %s %d %d" % (c["name"], c["armAoOffset"], c["armAoFrames"]))
    for clip_name in sorted(report["armIk"], key=lambda n: ik_order.get(n, len(ik_order))):
        sides = report["armIk"][clip_name]
        for side, meta in sorted(sides.items()):
            txt.append("ik %s %s %d %d %d %d %s" % (
                clip_name, side, meta["posOffset"], meta["rotOffset"], meta["weightOffset"],
                meta["frames"], meta["parent"]))
    for clip_name in sorted(report["armIk"], key=lambda n: ik_order.get(n, len(ik_order))):
        for side, meta in sorted(report["armIk"][clip_name].items()):
            txt.append("ikroot %s %s %d %d" % (clip_name, side, meta["rootPosOffset"], meta["frames"]))
    for c in clip_entries:
        txt.append("pelvis3 %s %d %d" % (c["name"], c["pelvis3Offset"], c["frames"]))
    for clip_name in sorted(report.get("legIk", {})):
        for side, meta in sorted(report["legIk"][clip_name].items()):
            txt.append("legik %s %s %d %d %d %d" % (
                clip_name, side, meta["posOffset"], meta["rotOffset"], meta["weightOffset"], meta["frames"]))
    for clip_name in sorted(report.get("legIk", {})):
        for side, meta in sorted(report["legIk"][clip_name].items()):
            txt.append("legroot %s %s %d %d" % (clip_name, side, meta["rootPosOffset"], meta["frames"]))
    # Formally per-clip decision (default 1); the runtime applies the conform only for these.
    for c in clip_entries:
        txt.append("armconform %s %d" % (c["name"], 0 if ARM_CONFORM.get(c["name"], 1) == 0 else 1))
    # LC's leg knee-pole (hint) node, model-root relative, constant per clip/side.
    for clip_name in sorted(report.get("legIk", {})):
        for side, meta in sorted(report["legIk"][clip_name].items()):
            if meta.get("hint"):
                txt.append("leghint %s %s %.6f %.6f %.6f" % ((clip_name, side) + tuple(meta["hint"])))
    open(os.path.join(outdir, "manifest.txt"), "w").write("\n".join(txt) + "\n")
    print(f"wrote manifest.txt ({len(txt)} lines)")
    json.dump(report, open(os.path.join(outdir, "coverage.json"), "w"), indent=1)
    print(f"\nwrote {bin_path} ({os.path.getsize(bin_path)} bytes), "
          f"{len(clip_entries)} clips, {len(pack['targetMap'])} mapped bones")
    print("coverage:", json.dumps(report["coverage"], indent=0)[:1200])


if __name__ == "__main__":
    main()
