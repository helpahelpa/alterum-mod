#!/usr/bin/env python3
"""Source-side reference numbers for the ported emotes, computed from the *baked pack* only.

`EmoteRuntime` logs a fixed metric set per clip (`pelvisY`, `pelvisHead`, `pelvisHandL/R`,
`handLR`, `headHandR`, `legL`, plus the arm reach utilisation). This tool reconstructs the same
numbers on LC's own rig from `emotes/manifest.txt` + `emotes/emote.bin`, and additionally solves
the same two-bone arm IK the runtime solves, so "source" and "target" are directly comparable.

Two variants per clip:
  raw  - arm rotations only (what the port reproduced before the IK pass; LC's authored bones)
  ik   - arm rotations + two-bone IK to the ported `LeftArm_target`/`RightArm_target`, blended by
         the clip's own ChainIKConstraint weight (the pose LC shows in game)

Usage:
  python3 tools/source_metrics.py [--clip NAME] [--all]
"""
import math
import sys
import os

HERE = os.path.dirname(os.path.abspath(__file__))
TRACK = os.path.dirname(HERE)
PACK = os.path.join(TRACK, "emotes")

FPS_FALLBACK = 60.0
# Same sample fractions as EmoteRuntime.StripAt (samples land on the first frame at/after them).
STRIPS = (0.06, 0.25, 0.45, 0.65, 0.85)
# Documentation only; which clips have baked IK targets is read from the pack itself.
IK_CLIPS = ("Clap", "Twerk", "Prisyadka", "Idle1Fix", "Middle_Finger")


# ----------------------------------------------------------------------------- quaternion helpers
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


def from_to(a, b):
    """Shortest-arc quaternion taking direction a onto direction b (Unity's FromToRotation)."""
    a = norm3(a)
    b = norm3(b)
    d = sum(x * y for x, y in zip(a, b))
    if d > 0.999999:
        return (0.0, 0.0, 0.0, 1.0)
    if d < -0.999999:
        # 180 deg: any perpendicular axis
        ax = (1.0, 0.0, 0.0) if abs(a[0]) < 0.9 else (0.0, 1.0, 0.0)
        c = cross(a, ax)
        return qnorm((c[0], c[1], c[2], 0.0))
    c = cross(a, b)
    return qnorm((c[0], c[1], c[2], 1.0 + d))


def norm3(v):
    n = math.sqrt(sum(c * c for c in v))
    return tuple(c / n for c in v) if n > 1e-12 else (0.0, 0.0, 0.0)


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


# ----------------------------------------------------------------------------- pack
class Clip:
    pass


def load_pack():
    data = None
    fps = FPS_FALLBACK
    maps, rest, clips, ik = {}, {}, {}, {}
    pending = []
    for raw in open(os.path.join(PACK, "manifest.txt"), encoding="utf-8"):
        line = raw.strip()
        if not line or line[0] == "#":
            continue
        if line.startswith("fps "):
            fps = float(line[4:])
        elif line.startswith("file "):
            pass
        else:
            pending.append(line)
    blob = open(os.path.join(PACK, "emote.bin"), "rb").read()
    import struct
    data = struct.unpack("<%df" % (len(blob) // 4), blob)
    for line in pending:
        t = line.split(" ")
        if t[0] == "map":
            maps[t[1]] = t[2]
        elif t[0] == "rest":
            rest[t[1]] = tuple(float(x) for x in t[2:9])
        elif t[0] == "clip":
            c = Clip()
            c.name = t[1]
            c.duration = float(t[2])
            c.loop = float(t[3]) > 0.5
            c.frames = int(float(t[4]))
            c.fps = float(t[5])
            c.rot = int(float(t[6]))
            c.pelvis = int(float(t[7]))
            c.bones = t[8:]
            clips[c.name] = c
        elif t[0] == "ik":
            name, side = t[1], t[2]
            ik.setdefault(name, {})[side] = {
                "pos": int(t[3]), "rot": int(t[4]), "w": int(t[5]),
                "frames": int(t[6]), "parent": t[7],
            }
    return data, fps, maps, rest, clips, ik


def delta(data, clip, frame, bone_index):
    i = clip.rot + (frame * len(clip.bones) + bone_index) * 4
    return (data[i], data[i + 1], data[i + 2], data[i + 3])


def pelvis_y(data, clip, frame):
    return data[clip.pelvis + frame]


def wrap_frame(clip, frame):
    if clip.loop and clip.frames > 1:
        return frame % (clip.frames - 1)
    return max(0, min(frame, clip.frames - 1))


def ik_value(data, meta, frame, n, kind):
    off = meta[kind]
    stride = 3 if kind == "pos" else (4 if kind == "rot" else 1)
    i = off + frame * stride
    if n == 1:
        return data[i]
    return tuple(data[i + k] for k in range(n))


# ----------------------------------------------------------------------------- source FK
class SourceRig:
    def __init__(self, maps, rest):
        self.maps = maps
        self.rest = rest                  # srcPath -> (rot.xyzw + pos.xyz)
        self.parent = {}
        for p in rest:
            self.parent[p] = p.rsplit("/", 1)[0] if "/" in p else ""
        # reverse map: target bone -> source path
        self.rev = {}
        for src, dst in maps.items():
            self.rev[dst] = src
        # rest globals for mapped source bones; rest-local offsets for FK
        self.rest_rot = {p: self.rest[p][0:4] for p in rest}
        self.rest_pos = {p: self.rest[p][4:7] for p in rest}

    def local_rest_offset(self, p):
        pr = self.parent[p]
        if pr == "":
            return self.rest_pos[p]
        inv = qconj(self.rest_rot[pr])
        d = tuple(self.rest_pos[p][i] - self.rest_pos[pr][i] for i in range(3))
        return qrot(inv, d)

    def pose(self, clip, data, drive_pelvis_y=True):
        """Return per-frame {srcPath: (global rot, global pos)} for every mapped ancestor.

        Rotations come from the pack's global deltas applied to the source rest; positions are a
        rigid FK from those rotations (the clips animate only the pelvis position, and the runtime
        deliberately applies just its Y component).
        """
        frames = []
        bones = clip.bones
        pelvis_path = self.rev["pelvis"]
        # depth order of the mapped bones
        order = sorted(bones, key=lambda p: (p.count("/"), p))
        for fi in range(clip.frames):
            rot, pos = {}, {}
            for p in order:
                d = delta(data, clip, fi, bones.index(p))
                # ancestors that are mapped: every mapped bone's parent chain is mapped in this rig
                rot[p] = qnorm(qmul(d, self.rest_rot[p]))
            for p in order:
                pr = self.parent[p]
                # walk up to the nearest mapped ancestor
                while pr != "" and pr not in rot:
                    pr = self.parent[pr]
                base = pos[pr] if pr != "" else (0.0, 0.0, 0.0)
                base_r = rot[pr] if pr != "" else (0.0, 0.0, 0.0, 1.0)
                # accumulate the rest-local offset from the mapped ancestor through unmapped nodes
                off = (0.0, 0.0, 0.0)
                q = p
                while q != pr and q != "":
                    off = tuple(off[i] + qrot(base_r, self.local_rest_offset(q))[i] for i in range(3))
                    q = self.parent[q]
                pos[p] = tuple(base[i] + off[i] for i in range(3))
            if drive_pelvis_y:
                d = pelvis_y(data, clip, fi)
                pos[pelvis_path] = (pos[pelvis_path][0], pos[pelvis_path][1] + d, pos[pelvis_path][2])
                # children inherit the pelvis translation
                for p in order:
                    if p == pelvis_path:
                        continue
                    pos[p] = (pos[p][0], pos[p][1] + d, pos[p][2])
            frames.append((rot, pos))
        return frames


def metrics(rot, pos, arm):
    """Same numbers EmoteRuntime.Metrics() logs (model-space distances)."""
    pelvis = pos[arm["pelvis"]]
    head = pos[arm["head"]]
    hl = pos[arm["handL"]]
    hr = pos[arm["handR"]]
    fl = pos[arm["footL"]]
    reachL, reachR = arm["reachL"], arm["reachR"]
    utilL = dist(pos[arm["upperL"]], hl) / reachL if reachL > 0 else 0.0
    utilR = dist(pos[arm["upperR"]], hr) / reachR if reachR > 0 else 0.0
    return dict(pelvisY=pelvis[1], pelvisHead=dist(pelvis, head),
                pelvisHandL=dist(pelvis, hl), pelvisHandR=dist(pelvis, hr),
                handLR=dist(hl, hr), headHandR=dist(head, hr), legL=dist(pelvis, fl),
                utilL=utilL, utilR=utilR, handL=hl, handR=hr)


def dist(a, b):
    return math.sqrt(sum((a[i] - b[i]) ** 2 for i in range(3)))


def solve_ik(rot, pos, upper, lower, hand, target, l1, l2, weight):
    """Two-bone IK, same algorithm as PosePlayer.ApplyIk.

    Returns the blended global rotations (upper, lower, hand) and the diagnostics:
    distance from the chain root to the (clamped) target and the clamp amount in metres.
    """
    gu, gl, gh = rot[upper], rot[lower], rot[hand]
    a = pos[upper]
    b = pos[lower]
    c = pos[hand]
    d = tuple(target[i] - a[i] for i in range(3))
    dlen = math.sqrt(sum(x * x for x in d))
    dmax = l1 + l2
    dmin = abs(l1 - l2)
    clamped = 0.0
    dc = dlen
    if dc > dmax * 0.999:
        clamped = dc - dmax
        dc = dmax * 0.999
    if dc < max(dmin, 1e-3):
        dc = max(dmin, 1e-3)
    if dlen < 1e-6:
        v = (0.0, 0.0, 1.0)
    else:
        v = tuple(x / dlen for x in d)
    u = norm3(tuple(b[i] - a[i] for i in range(3)))
    proj = sum(u[i] * v[i] for i in range(3))
    p = tuple(u[i] - v[i] * proj for i in range(3))
    if sum(x * x for x in p) < 1e-6:
        ax = (1.0, 0.0, 0.0) if abs(v[0]) < 0.9 else (0.0, 1.0, 0.0)
        p = cross(v, ax)
    p = norm3(p)
    cosA = max(-1.0, min(1.0, (l1 * l1 + dc * dc - l2 * l2) / (2.0 * l1 * dc)))
    sinA = math.sqrt(max(0.0, 1.0 - cosA * cosA))
    bnew = tuple(a[i] + (v[i] * cosA + p[i] * sinA) * l1 for i in range(3))
    cnew = tuple(a[i] + v[i] * dc for i in range(3))
    qA = from_to(tuple(b[i] - a[i] for i in range(3)), tuple(bnew[i] - a[i] for i in range(3)))
    c1 = tuple(a[i] + qrot(qA, tuple(c[i] - a[i] for i in range(3)))[i] for i in range(3))
    qB = from_to(tuple(c1[i] - bnew[i] for i in range(3)), tuple(cnew[i] - bnew[i] for i in range(3)))
    nu = qnorm(qmul(qA, gu))
    nl = qnorm(qmul(qB, qmul(qA, gl)))
    nh = qnorm(qmul(qB, qmul(qA, gh)))
    if weight < 1.0:
        w = max(0.0, weight)
        nu = qslerp(gu, nu, w)
        nl = qslerp(gl, nl, w)
        nh = qslerp(gh, nh, w)
    return nu, nl, nh, dist(c, cnew), clamped


def main():
    data, fps, maps, rest, clips, ik = load_pack()
    rig = SourceRig(maps, rest)
    # The target rig has a separate `head` bone; LC's metarig has no head bone (its `spine.004`
    # is the neck and carries the head rotation, `spine.004_end` is the head top). `spine.004`
    # maps to `neck_01`, which is what the previous pass compared against, so use it as the
    # source head reference and say so.
    arm = {
        "pelvis": rig.rev["pelvis"], "head": rig.rev["neck_01"],
        "handL": rig.rev["hand_l"], "handR": rig.rev["hand_r"], "footL": rig.rev["foot_l"],
        "upperL": [s for s, d in maps.items() if d == "upperarm_l"][0],
        "upperR": [s for s, d in maps.items() if d == "upperarm_r"][0],
        "lowerL": [s for s, d in maps.items() if d == "lowerarm_l"][0],
        "lowerR": [s for s, d in maps.items() if d == "lowerarm_r"][0],
        "clavL": [s for s, d in maps.items() if d == "clavicle_l"][0],
        "clavR": [s for s, d in maps.items() if d == "clavicle_r"][0],
    }
    arm["reachL"] = dist(rig.rest_pos[arm["upperL"]], rig.rest_pos[arm["lowerL"]]) + \
                    dist(rig.rest_pos[arm["lowerL"]], rig.rest_pos[arm["handL"]])
    arm["reachR"] = dist(rig.rest_pos[arm["upperR"]], rig.rest_pos[arm["lowerR"]]) + \
                    dist(rig.rest_pos[arm["lowerR"]], rig.rest_pos[arm["handR"]])
    l1L = dist(rig.rest_pos[arm["upperL"]], rig.rest_pos[arm["lowerL"]])
    l2L = dist(rig.rest_pos[arm["lowerL"]], rig.rest_pos[arm["handL"]])
    l1R = dist(rig.rest_pos[arm["upperR"]], rig.rest_pos[arm["lowerR"]])
    l2R = dist(rig.rest_pos[arm["lowerR"]], rig.rest_pos[arm["handR"]])

    only = None
    if "--clip" in sys.argv:
        only = sys.argv[sys.argv.index("--clip") + 1]

    print(f"source pack: {len(clips)} clips, arm reach L={arm['reachL']:.3f} R={arm['reachR']:.3f} "
          f"(L1/L2 L={l1L:.3f}/{l2L:.3f} R={l1R:.3f}/{l2R:.3f})")
    for name in sorted(clips):
        if only and name != only:
            continue
        clip = clips[name]
        base = rig.pose(clip, data)
        has_ik = name in ik
        variants = ["raw", "ik"] if has_ik else ["raw"]
        for variant in variants:
            # solve the arm IK per frame when asked for the "ik" variant
            poses = []
            diags = []
            for fi in range(clip.frames):
                frame_diag = {}
                rot, pos = base[fi]
                rot = dict(rot)
                pos = dict(pos)
                if variant == "ik":
                    for side, upper, lower, hand, l1, l2 in (
                            ("L", arm["upperL"], arm["lowerL"], arm["handL"], l1L, l2L),
                            ("R", arm["upperR"], arm["lowerR"], arm["handR"], l1R, l2R)):
                        m = ik[name].get(side)
                        if not m:
                            continue
                        w = ik_value(data, m, fi, 1, "w")
                        if w <= 1e-4:
                            continue
                        off = ik_value(data, m, fi, 3, "pos")
                        parent_path = m["parent"]
                        tgt = tuple(pos[parent_path][k] + off[k] for k in range(3))
                        nu, nl, nh, err, clamped = solve_ik(rot, pos, upper, lower, hand, tgt, l1, l2, w)
                        rot[upper], rot[lower], rot[hand] = nu, nl, nh
                        # recompute positions of the arm chain from the solved rotations
                        for p, rr in ((upper, nu), (lower, nl), (hand, nh)):
                            pr = rig.parent[p]
                            while pr != "" and pr not in rot:
                                pr = rig.parent[pr]
                            base_p = pos[pr]
                            base_r = rot[pr]
                            acc = (0.0, 0.0, 0.0)
                            q = p
                            while q != pr and q != "":
                                acc = tuple(acc[i] + qrot(base_r, rig.local_rest_offset(q))[i] for i in range(3))
                                q = rig.parent[q]
                            pos[p] = tuple(base_p[i] + acc[i] for i in range(3))
                        # hand orientation changes do not move the wrist; nothing else to update
                        frame_diag[side] = (dist(pos[hand], tgt), clamped, w)
                diags.append(frame_diag)
                poses.append(metrics(rot, pos, arm))

            # strip samples, exactly at EmoteRuntime.StripAt (first frame at/after the fraction)
            print(f"\n== {name} [{variant}] dur={clip.duration:.3f}s loop={clip.loop} frames={clip.frames}"
                  + (" (baked IK weights)" if variant == "ik" else " (no IK)"))
            for frac in STRIPS:
                fi = min(clip.frames - 1, int(math.ceil(frac * clip.frames)))
                m = poses[fi]
                line = (f"   t={fi / clip.fps:6.3f}s f={fi:3d} pelvisY={m['pelvisY']:.3f} "
                        f"pelvisHead={m['pelvisHead']:.3f} pelvisHandL={m['pelvisHandL']:.3f} "
                        f"pelvisHandR={m['pelvisHandR']:.3f} handLR={m['handLR']:.3f} "
                        f"headHandR={m['headHandR']:.3f} legL={m['legL']:.3f} "
                        f"reachUtilL={m['utilL']:.3f} reachUtilR={m['utilR']:.3f}")
                print(line)
                if variant == "ik":
                    for side in ("L", "R"):
                        hit = diags[fi].get(side)
                        if hit:
                            print(f"            ik{side}: err={hit[0]:.3f}m clamp={hit[1]:.3f}m w={hit[2]:.2f}")
            # travel + hand separation over the post-fade window (t >= 0.25 s)
            # past the runtime's 0.2 s fade-in; for clips shorter than that window use the whole
            # cycle (the runtime holds a looping clip for >1 s, so it covers the same poses)
            start = 0 if clip.duration <= 0.45 else min(clip.frames - 1, int(math.ceil(0.35 * clip.fps)))
            win = poses[start:]
            if win:
                def bounds(key):
                    xs = [m[key] for m in win]
                    lo = tuple(min(v[i] for v in xs) for i in range(3))
                    hi = tuple(max(v[i] for v in xs) for i in range(3))
                    return dist(lo, hi)
                lrs = [m["handLR"] for m in win]
                print(f"   travel(post-fade) handL={bounds('handL'):.3f}m ({bounds('handL') / arm['reachL']:.2f}xReach) "
                      f"handR={bounds('handR'):.3f}m ({bounds('handR') / arm['reachR']:.2f}xReach) "
                      f"handLR={min(lrs):.3f}..{max(lrs):.3f}")


if __name__ == "__main__":
    main()
