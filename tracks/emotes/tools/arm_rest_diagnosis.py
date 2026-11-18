#!/usr/bin/env python3
"""Size the rotation-retarget rest-frame bias on the player's arm chain, offline.

The runtime transfers LC's *global* rotation deltas as

    global_i(t) = C * dR_i(t) * C^-1 * anchorRot_i        (anchorRot_i = this model's bind rot)

so the *direction* of each limb segment at time t is

    dir_i(t) = C * dR_i(t) * C^-1 * (bindPos_child - bindPos_i)_dir

while LC's own segment direction is `dR_i(t) * (srcRestPos_child - srcRestPos_i)_dir`. The two agree
at every frame only if the two *rest* segment directions agree after C, which they do not: LC's rest
arms hang at the sides, this model's bind arm is wider, and the forearm/wrist differ by 25-70 deg.
This tool measures that, then models the port's arm FK from the bind chain the runtime logs, and
re-runs it with each arm segment's rest direction conformed to LC's - a constant per-bone anchor
correction `Q_i = FromTo(target segment dir, C * source segment dir)` applied on the left of the
bind rotation - so the cost of that fix is a number, not an opinion.

Inputs: emotes/{manifest.txt,emote.bin} plus an emotes LogOutput.log containing the
`[emote:rig] rest chain pos:... rot:...` and `[emote:rig] basis quat=...` lines (bind time).

Usage: python3 tools/arm_rest_diagnosis.py <log> [--clip NAME] [--json out.json]
"""
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import source_metrics_RETIRED as S   # RETIRED tool: helpers only, not ground truth

# Hold duration of each sweep step in EmoteRuntime.Steps: the runtime stops the clip there, so the
# port's travel window is [0.35 s, hold], NOT [0.35 s, clip end].
HOLD = {"Salute": 1.6, "Middle_Finger": 1.2, "Sign": 1.6, "D_Sign": 1.6, "The_Griddy": 1.6,
        "Clap": 1.6, "Twerk": 1.2, "Prisyadka": 2.0, "Idle1Fix": 1.6}
TRAVEL_FROM = 0.35

CHAIN = ["pelvis", "spine_01", "spine_02", "spine_03",
         "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l",
         "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r"]
PARENT = {"pelvis": None, "spine_01": "pelvis", "spine_02": "spine_01", "spine_03": "spine_02",
          "clavicle_l": "spine_03", "upperarm_l": "clavicle_l", "lowerarm_l": "upperarm_l",
          "hand_l": "lowerarm_l",
          "clavicle_r": "spine_03", "upperarm_r": "clavicle_r", "lowerarm_r": "upperarm_r",
          "hand_r": "lowerarm_r"}
ARM = {"L": ("clavicle_l", "upperarm_l", "lowerarm_l", "hand_l"),
       "R": ("clavicle_r", "upperarm_r", "lowerarm_r", "hand_r")}
SEGMENTS = [(ARM[s][0], ARM[s][1]) for s in "LR"] + [(ARM[s][1], ARM[s][2]) for s in "LR"] + \
           [(ARM[s][2], ARM[s][3]) for s in "LR"]


def norm(v):
    n = math.sqrt(sum(c * c for c in v))
    return tuple(c / n for c in v) if n > 1e-12 else (0.0, 0.0, 0.0)


def dist(a, b):
    return math.sqrt(sum((a[i] - b[i]) ** 2 for i in range(3)))


def bbox(pts):
    lo = tuple(min(p[i] for p in pts) for i in range(3))
    hi = tuple(max(p[i] for p in pts) for i in range(3))
    return dist(lo, hi)


def sub(a, b):
    return tuple(a[i] - b[i] for i in range(3))


def load_bind(log_path):
    txt = open(log_path, encoding="utf-8", errors="replace").read()
    m = re.search(r"\[emote:rig\] rest chain pos:(.*?) rot:(.*?)\n", txt)
    if not m:
        sys.exit(f"no '[emote:rig] rest chain' line in {log_path}")
    pos = {}
    for name, x, y, z in re.findall(r"(\w+)=\(([-\d.]+),([-\d.]+),([-\d.]+)\)", m.group(1)):
        pos[name] = (float(x), float(y), float(z))
    rot = {}
    for name, x, y, z, w in re.findall(r"(\w+)=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\)",
                                      m.group(2)):
        rot[name] = (float(x), float(y), float(z), float(w))
    b = re.search(r"\[emote:rig\] basis quat=\(([-\d.]+),([-\d.]+),([-\d.]+),([-\d.]+)\)", txt)
    if not b:
        sys.exit(f"no basis quat in {log_path}")
    basis = tuple(float(x) for x in b.groups())
    legs = re.search(r"bound model generation \d+: .*?legScale=([\d.]+)", txt)
    leg = float(legs.group(1)) if legs else 1.0
    return pos, rot, basis, leg


def src_of(maps):
    return {dst: src for src, dst in maps.items()}


def conform_quats(maps, rig, basis, bind_pos):
    """Q per arm bone: rotates the target's rest segment direction onto LC's (C-mapped)."""
    src = src_of(maps)
    Q = {}
    for a, c in SEGMENTS:
        if a.startswith("clavicle"):
            # mimic the shipped build: the arm root is left where `spine_03 + target offset` puts
            # it (see PosePlayer.ConformArmAnchors - conforming it moved the IK clips' shoulder)
            Q[a] = (0.0, 0.0, 0.0, 1.0)
            continue
        if a not in src or c not in src:
            Q[a] = (0.0, 0.0, 0.0, 1.0)
            continue
        o_t = norm(sub(bind_pos[c], bind_pos[a]))
        o_s = norm(S.qrot(basis, sub(rig.rest_pos[src[c]], rig.rest_pos[src[a]])))
        Q[a] = S.from_to(o_t, o_s)
    for s in "LR":
        Q.setdefault(ARM[s][3], (0.0, 0.0, 0.0, 1.0))
    return Q


def arm_fk(clip, data, maps, rig, bind_pos, bind_rot, basis, leg, Q):
    """Port arm FK: LC deltas on the logged bind chain, optional per-bone conform.

    The runtime never writes localPosition, so joint offsets are the bind offsets rotated by the
    parent's driven global rotation; the pelvis additionally takes the scaled source vertical.
    """
    src = src_of(maps)
    idx = {b: i for i, b in enumerate(clip.bones)}
    ident = (0.0, 0.0, 0.0, 1.0)
    off = {}
    for b, pr in PARENT.items():
        if pr is None:
            continue
        off[b] = S.qrot(S.qconj(bind_rot[pr]), sub(bind_pos[b], bind_pos[pr]))
    frames = []
    for fi in range(clip.frames):
        g = {}
        for b in CHAIN:
            sp = src.get(b)
            dr = S.delta(data, clip, fi, idx[sp]) if (sp and sp in idx) else ident
            q = S.qmul(S.qmul(S.qmul(basis, dr), S.qconj(basis)), Q.get(b, ident))
            g[b] = S.qnorm(S.qmul(q, bind_rot[b]))
        y = S.pelvis_y(data, clip, fi) * leg
        pos = {"pelvis": (bind_pos["pelvis"][0], bind_pos["pelvis"][1] + y,
                          bind_pos["pelvis"][2])}
        for b in CHAIN[1:]:
            pv = S.qrot(g[PARENT[b]], off[b])
            pos[b] = tuple(pos[PARENT[b]][i] + pv[i] for i in range(3))
        frames.append((g, pos))
    return frames


def source_hand_track(clip, data, maps, rig, ik_meta, f0, f1):
    """LC's own rig, IK solved from the baked targets (what LC shows in game)."""
    rev = {v: k for k, v in maps.items()}
    base = rig.pose(clip, data)
    out = {"hand_l": [], "hand_r": [], "upperarm_l": [], "upperarm_r": []}
    for fi in range(f0, f1 + 1):
        rot, pos = base[fi]
        rot, pos = dict(rot), dict(pos)
        for side in ("L", "R"):
            m = (ik_meta or {}).get(side)
            if not m:
                continue
            w = S.ik_value(data, m, fi, 1, "w")
            if w <= 1e-4:
                continue
            up, lo, ha = rev["upperarm_" + side.lower()], rev["lowerarm_" + side.lower()], \
                rev["hand_" + side.lower()]
            l1 = dist(rig.rest_pos[up], rig.rest_pos[lo])
            l2 = dist(rig.rest_pos[lo], rig.rest_pos[ha])
            off_t = S.ik_value(data, m, fi, 3, "pos")
            tgt = tuple(pos[m["parent"]][k] + off_t[k] for k in range(3))
            nu, nl, nh, _err, _cl = S.solve_ik(rot, pos, up, lo, ha, tgt, l1, l2, w)
            rot[up], rot[lo], rot[ha] = nu, nl, nh
            for p, rr in ((up, nu), (lo, nl), (ha, nh)):
                pr = rig.parent[p]
                while pr != "" and pr not in rot:
                    pr = rig.parent[pr]
                acc = (0.0, 0.0, 0.0)
                q = p
                while q != pr and q != "":
                    acc = tuple(acc[i] + S.qrot(rot[pr], rig.local_rest_offset(q))[i]
                                for i in range(3))
                    q = rig.parent[q]
                pos[p] = tuple(pos[pr][i] + acc[i] for i in range(3))
        out["hand_l"].append(pos[rev["hand_l"]])
        out["hand_r"].append(pos[rev["hand_r"]])
        out["upperarm_l"].append(pos[rev["upperarm_l"]])
        out["upperarm_r"].append(pos[rev["upperarm_r"]])
    return out


def main():
    log = sys.argv[1]
    only = sys.argv[sys.argv.index("--clip") + 1] if "--clip" in sys.argv else None
    data, fps, maps, rest, clips, ik = S.load_pack()
    rig = S.SourceRig(maps, rest)
    bind_pos, bind_rot, basis, leg = load_bind(log)
    print(f"bind chain from {os.path.basename(log)}: C={tuple(round(c, 4) for c in basis)} "
          f"legScale={leg}")

    print("\nrest-frame arm segment directions in the frame they are compared in "
          "(model space; source mapped by C):")
    print(f"  {'segment':32s} {'angle':>7s}  {'target bind':>20s}  {'source rest':>20s}")
    src = src_of(maps)
    for a, c in SEGMENTS:
        o_t = norm(sub(bind_pos[c], bind_pos[a]))
        o_s = norm(S.qrot(basis, sub(rig.rest_pos[src[c]], rig.rest_pos[src[a]])))
        dot = max(-1.0, min(1.0, sum(o_t[i] * o_s[i] for i in range(3))))
        print(f"  {a + '->' + c:32s} {math.degrees(math.acos(dot)):6.1f}d  "
              f"({o_t[0]:5.2f},{o_t[1]:5.2f},{o_t[2]:5.2f})      "
              f"({o_s[0]:5.2f},{o_s[1]:5.2f},{o_s[2]:5.2f})")

    tgt_reach, arm_reach = {}, {}
    for s in "LR":
        lo, up, ha = s.lower(), "upperarm_" + s.lower(), "lowerarm_" + s.lower()
        arm_reach[s] = (dist(rig.rest_pos[src["upperarm_" + lo]], rig.rest_pos[src["lowerarm_" + lo]]) +
                        dist(rig.rest_pos[src["lowerarm_" + lo]], rig.rest_pos[src["hand_" + lo]]))
        tgt_reach[s] = (dist(bind_pos["upperarm_" + lo], bind_pos["lowerarm_" + lo]) +
                        dist(bind_pos["lowerarm_" + lo], bind_pos["hand_" + lo]))
    print(f"\narm reach: source L={arm_reach['L']:.3f} R={arm_reach['R']:.3f}  "
          f"target L={tgt_reach['L']:.3f} R={tgt_reach['R']:.3f}  "
          f"scale L={tgt_reach['L'] / arm_reach['L']:.3f} R={tgt_reach['R'] / arm_reach['R']:.3f}")

    Qc = conform_quats(maps, rig, basis, bind_pos)
    ident = {b: (0.0, 0.0, 0.0, 1.0) for b in CHAIN}

    print("\nhand travel over the runtime's own window ([0.35 s, hold], bbox diagonal, metres)")
    print(f"{'clip':14s} {'side':5s} {'win':>11s} {'source':>7s} {'xscale':>7s} "
          f"{'port now':>8s} {'conformed':>9s}  {'handLR source':>13s} {'port':>13s} "
          f"{'conformed':>13s}")
    out = {}
    for name in sorted(clips):
        if only and name != only:
            continue
        clip = clips[name]
        hold = HOLD.get(name, clip.duration)
        f0 = min(clip.frames - 1, int(math.ceil(TRAVEL_FROM * clip.fps)))
        f1 = min(clip.frames - 1, int(math.ceil(min(hold, clip.duration) * clip.fps)))
        src_track = source_hand_track(clip, data, maps, rig, ik.get(name), f0, f1)
        now = arm_fk(clip, data, maps, rig, bind_pos, bind_rot, basis, leg, ident)
        con = arm_fk(clip, data, maps, rig, bind_pos, bind_rot, basis, leg, Qc)
        def lr(frames):
            return [dist(f[1]["hand_l"], f[1]["hand_r"]) for f in frames[f0:f1 + 1]]
        lrs = [dist(a, b) for a, b in zip(src_track["hand_l"], src_track["hand_r"])]
        lrn, lrc = lr(now), lr(con)
        row = {"window": [f0 / clip.fps, f1 / clip.fps], "hold": hold,
               "reachUtil_source": {s: [min(dist(a, b) for a, b in
                                         zip(src_track["upperarm_" + s.lower()],
                                             src_track["hand_" + s.lower()])) / arm_reach[s],
                                     max(dist(a, b) for a, b in
                                         zip(src_track["upperarm_" + s.lower()],
                                             src_track["hand_" + s.lower()])) / arm_reach[s]]
                                   for s in "LR"},
               "source_reach": arm_reach, "target_reach": tgt_reach,
               "handLR": {"source": [min(lrs), max(lrs)], "port": [min(lrn), max(lrn)],
                          "conformed": [min(lrc), max(lrc)]}}
        out[name] = row
        for s in "LR":
            key = "hand_" + s.lower()
            a = bbox([p for p in src_track[key]])
            b = bbox([f[1][key] for f in now[f0:f1 + 1]])
            c = bbox([f[1][key] for f in con[f0:f1 + 1]])
            sc = tgt_reach[s] / arm_reach[s]
            row.setdefault("travel", {})[s] = {"source": a, "source_scaled": a * sc, "port": b,
                                               "conformed": c}
            w = (ik.get(name) or {}).get(s)
            wmax = max((S.ik_value(data, w, fi, 1, "w") for fi in range(f0, f1 + 1)),
                       default=0.0) if w else 0.0
            tag = f"{wmax:.2f}"
            print(f"{name if s == 'L' else '':14s} {s:5s} "
                  f"{f0 / clip.fps:5.2f}..{f1 / clip.fps:<5.2f} {a:7.3f} {a * sc:7.3f} "
                  f"{b:8.3f} {c:9.3f}  "
                  + (f"{min(lrs):.3f}..{max(lrs):.3f} {min(lrn):.3f}..{max(lrn):.3f} "
                     f"{min(lrc):.3f}..{max(lrc):.3f}" if s == "L" else "")
                  + ("" if wmax < 0.001 else f"   [baked IK w={tag} -> the port's column is IK'd;"
                     f" this model is rotation-only]"))
        row["ik_weight"] = {s: max((S.ik_value(data, (ik.get(name) or {}).get(s), fi, 1, "w")
                                    for fi in range(f0, f1 + 1)), default=0.0)
                            if (ik.get(name) or {}).get(s) else 0.0 for s in "LR"}

    if "--json" in sys.argv:
        json.dump(out, open(sys.argv[sys.argv.index("--json") + 1], "w"), indent=1)
        print(f"\nwrote {sys.argv[sys.argv.index('--json') + 1]}")


if __name__ == "__main__":
    main()
