#!/usr/bin/env python3
"""Audit *every* ported clip for Final IK arm-constraint driving, in one pass.

Motivation: three passes in a row each discovered another clip whose arms are driven by the Final
IK `ChainIKConstraint`s at `metarig/Rig 1/{Left,Right}Arm` instead of by authored bone rotations.
This tool answers, for all clips in the shipped pack at once:

  * the constraint weight time series per side (curve present? min/max/active frames),
  * the `{Left,Right}Arm_target` travel that the pack bakes (offset relative to spine.003),
  * whether the authored arm bone rotations are static or animated (max deviation from frame 0),
  * how much of the arm pose motion the IK produces, i.e. the IK share of the clip's motion.

The pose model is `source_metrics_RETIRED.SourceRig` (RETIRED tool: source-model units, not LC world): the ported global rotation deltas + the pelvis Y
offset, exactly what the target rig applies (it never transfers bone translations). Positions are
therefore comparable to both `source_metrics_RETIRED.py` and the runtime log, unlike raw authored globals
which also carry source translation curves the port drops.

Source-side only: reads the authored `.anim` curves the same way `tools/extract_emotes.py` does
(same parabolic key evaluation, same 60 Hz bake), the metarig rest, and the pack's baked deltas.
No pack writes, no runtime involvement. Output: a table on stdout plus optional --json.

Usage:
  python3 tools/ik_audit.py [--json out.json]
"""
import json
import math
import os
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import extract_emotes as E               # noqa: E402  (same module the pack is baked with)
import source_metrics_RETIRED as SM      # noqa: E402  RETIRED: helper math only, not ground truth

TRACK = E.TRACK
FPS = E.FPS
# Runtime acceptance sweep windows (EmoteRuntime.Steps `hold`). Used for the window-matched travel
# column so a source number is never taken over a longer window than the port measured.
HOLDS = {
    "Salute": 1.6, "Middle_Finger": 1.2, "Sign": 1.6, "D_Sign": 1.6, "The_Griddy": 1.6,
    "Clap": 1.6, "Twerk": 1.2, "Prisyadka": 2.0, "Idle1Fix": 1.01,
}
DEFAULT_HOLD = 1.6
FADE_END = 0.35      # EmoteRuntime.TrackTravel starts at _runTime >= 0.35


def pack_clip_names():
    names = []
    for raw in open(os.path.join(TRACK, "emotes/manifest.txt"), encoding="utf-8"):
        t = raw.split()
        if t and t[0] == "clip":
            names.append(t[1])
    return names


def crc(path):
    return zlib.crc32(path.encode()) & 0xffffffff


def q_angle(a, b):
    """Angle between two quaternions, degrees (0..180)."""
    d = abs(sum(x * y for x, y in zip(a, b)))
    return math.degrees(2.0 * math.acos(max(-1.0, min(1.0, d))))


def bbox_diag(points):
    if not points:
        return 0.0
    lo = [min(p[i] for p in points) for i in range(3)]
    hi = [max(p[i] for p in points) for i in range(3)]
    return math.sqrt(sum((hi[i] - lo[i]) ** 2 for i in range(3)))


def max_from_first(points):
    if not points:
        return 0.0
    return max(math.dist(points[0], p) for p in points)


def path_len(points):
    """Total variation of a trajectory: the frame-to-frame motion the eye sees, independent of how
    far apart the two extreme frames are. Looping clips include the wrap."""
    return sum(math.dist(points[i], points[i + 1]) for i in range(len(points) - 1))


def travel_windows(hand, nframes, loop, hold):
    """Window-matched hand bbox + handLR min/max, exactly like EmoteRuntime.TrackTravel.

    It samples every runtime frame from 0.35 s to `hold` and maps run time to clip phase the way
    the pose player does (looping clips wrap, one-shot clips clamp at the end). Using run time (not
    clip time) is the fix for the window mismatch the third pass caught: the port measures the
    bbox over the *hold*, so the source has to as well.
    """
    if nframes < 2:
        return None

    def phase(run_t):
        f = int(math.floor(run_t * FPS))
        return f % nframes if loop else max(0, min(f, nframes - 1))

    start = int(math.ceil(FADE_END * FPS))
    end = max(start, int(math.floor(hold * FPS)))
    idx = [phase(fr / FPS) for fr in range(start, end + 1)]
    hl = [hand["L"][i] for i in idx]
    hr = [hand["R"][i] for i in idx]
    lrs = [math.dist(a, b) for a, b in zip(hl, hr)]
    return dict(handL=bbox_diag(hl), handR=bbox_diag(hr),
                handLR=(min(lrs), max(lrs)), window=(start / FPS, end / FPS), frames=len(hl))


def main():
    data, fps, maps, src_rest, clips, ikpack = SM.load_pack()
    rig = SM.SourceRig(maps, src_rest)
    rest = E.metarig_rest()                   # local rest TRS (for target local offset)
    sp_path = E.SPINE03                       # == rig.rev["spine_03"]
    assert rig.rev["spine_03"] == sp_path, (rig.rev["spine_03"], sp_path)

    anim_files, float_files = {}, {}
    for fn in sorted(os.listdir(E.ANIM_DIR)):
        if not fn.endswith(".anim"):
            continue
        try:
            name, curves = E.parse_anim_file(os.path.join(E.ANIM_DIR, fn))
            float_files[name] = E.parse_float_curves(os.path.join(E.ANIM_DIR, fn))
        except Exception as exc:
            print("skip", fn, exc)
            continue
        anim_files[name] = curves

    def chain_paths(side):
        base = sp_path + "/shoulder." + side
        return (base + "/arm." + side + "_upper",
                base + "/arm." + side + "_upper/arm." + side + "_lower",
                base + "/arm." + side + "_upper/arm." + side + "_lower/hand." + side)

    chain = {s: chain_paths(s) for s in ("L", "R")}
    L1, L2 = {}, {}
    for side in ("L", "R"):
        up, lo, ha = chain[side]
        L1[side] = math.dist(rig.rest_pos[up], rig.rest_pos[lo])
        L2[side] = math.dist(rig.rest_pos[lo], rig.rest_pos[ha])

    rows = []
    for name in pack_clip_names():
        clip = clips.get(name)
        if clip is None:
            print(f"  WARN {name}: not in the pack")
            continue
        yam = anim_files.get(name, {})
        flt = float_files.get(name, {})
        nframes, dur = clip.frames, clip.duration

        # ---- weight series (authored m_FloatCurves; no curve = prefab default 1.0)
        weights = {}
        for side in ("L", "R"):
            keys = flt.get((E.IK_WEIGHT_CRC[side], E.CHAINIK_GUID))
            if keys:
                wv = [max(0.0, min(1.0, E.eval_curve(keys, fi / FPS)[0])) for fi in range(nframes)]
                weights[side] = dict(curve=True, min=min(wv), max=max(wv), series=wv,
                                     active=sum(1 for w in wv if w > 1e-3),
                                     full=sum(1 for w in wv if w > 0.999))
            else:
                weights[side] = dict(curve=False, min=1.0, max=1.0, series=[1.0] * nframes,
                                     active=nframes, full=nframes)

        # ---- authored arm rotation spread (max deviation from the clip's first pose)
        rot_spread = {}
        for side in ("L", "R"):
            base = sp_path + "/shoulder." + side
            up = base + "/arm." + side + "_upper"
            lo = up + "/arm." + side + "_lower"
            ha = lo + "/hand." + side
            per = {}
            for bone, path in ((f"shoulder.{side}", base), (f"arm.{side}_upper", up),
                               (f"arm.{side}_lower", lo), (f"hand.{side}", ha)):
                keys = yam.get((crc(path), "euler"))
                qs = []
                for fi in range(nframes):
                    if keys:
                        qs.append(E.euler_to_quat(*E.eval_curve(keys, fi / FPS)))
                    else:
                        qs.append(tuple(rest[path]["rot"]) if path in rest else (0.0, 0.0, 0.0, 1.0))
                per[bone] = dict(curve=bool(keys), path=path,
                                 spread=max(q_angle(qs[0], q) for q in qs))
            rot_spread[side] = per

        # ---- target local offset per frame (the node's own position curve; else rest)
        #      + the global offset the pack bakes: R(spine.03) * local  (= target - spine.03)
        tgt_local = {}
        for side in ("L", "R"):
            p = E.IK_TARGETS[side]
            pk = yam.get((crc(p), "position"))
            tgt_local[side] = [tuple(E.eval_curve(pk, fi / FPS)) if pk else tuple(rest[p]["pos"])
                               for fi in range(nframes)]

        base = rig.pose(clip, data)   # [(rot, pos)] x nframes over mapped source bones
        rel_off = {"L": [], "R": []}
        tgt_global = {"L": [], "R": []}
        for fi in range(nframes):
            rot, pos = base[fi]
            sp, rsp = pos[sp_path], rot[sp_path]
            for side in ("L", "R"):
                off = E.qrot(rsp, tgt_local[side][fi])
                rel_off[side].append(off)
                tgt_global[side].append(tuple(sp[i] + off[i] for i in range(3)))

        # cross-check against the pack's own baked offsets where they exist
        pack_delta = {}
        if name in ikpack:
            for side in ("L", "R"):
                m = ikpack[name].get(side)
                if not m:
                    continue
                mx = 0.0
                for fi in range(min(nframes, m["frames"])):
                    packed = SM.ik_value(data, m, fi, 3, "pos")
                    mx = max(mx, math.dist(packed, rel_off[side][fi]))
                pack_delta[side] = mx

        tgt_travel = {}
        for side in ("L", "R"):
            tgt_travel[side] = dict(
                curve=bool(yam.get((crc(E.IK_TARGETS[side]), "position"))),
                relTravel=bbox_diag(rel_off[side]),
                relMaxFromFirst=max_from_first(rel_off[side]),
                authoredTravel=bbox_diag(tgt_local[side]),
                authoredMaxFromFirst=max_from_first(tgt_local[side]),
                restRel=math.dist(rig.rest_pos[E.IK_TARGETS[side]], rig.rest_pos[sp_path]),
            )

        # ---- rotation-only vs IK source poses, per frame
        hand_rot = {"L": [], "R": []}
        hand_ik = {"L": [], "R": []}
        body_rot = {"L": [], "R": []}
        body_ik = {"L": [], "R": []}
        ik_diag = {"L": [], "R": []}

        def to_body(p, pos, rot):
            """Wrist in the spine.03 frame: removes the clip's body rotation, which is what the
            'IK drives the arm pose' question is about (a spinning clip moves the wrist a metre in
            model space with a completely static arm)."""
            sp = pos[sp_path]
            inv = E.qconj(rot[sp_path])
            return E.qrot(inv, tuple(p[i] - sp[i] for i in range(3)))

        for fi in range(nframes):
            rot = dict(base[fi][0])
            pos = dict(base[fi][1])
            for side in ("L", "R"):
                hand_rot[side].append(pos[chain[side][2]])
                body_rot[side].append(to_body(pos[chain[side][2]], pos, rot))
            for side in ("L", "R"):
                up, lo, ha = chain[side]
                w = weights[side]["series"][fi]
                if w > 1e-4:
                    tgt = tgt_global[side][fi]
                    nu, nl, nh, err, clamp = SM.solve_ik(rot, pos, up, lo, ha, tgt,
                                                         L1[side], L2[side], w)
                    rot[up], rot[lo], rot[ha] = nu, nl, nh
                    for p, rr in ((up, nu), (lo, nl), (ha, nh)):
                        pr = rig.parent[p]
                        while pr != "" and pr not in rot:
                            pr = rig.parent[pr]
                        base_p, base_r = pos[pr], rot[pr]
                        acc = (0.0, 0.0, 0.0)
                        q = p
                        while q != pr and q != "":
                            acc = tuple(acc[i] + E.qrot(base_r, rig.local_rest_offset(q))[i]
                                        for i in range(3))
                            q = rig.parent[q]
                        pos[p] = tuple(base_p[i] + acc[i] for i in range(3))
                    ik_diag[side].append((math.dist(pos[ha], tgt), clamp, w))
                else:
                    ik_diag[side].append((0.0, 0.0, w))
                hand_ik[side].append(pos[ha])
                body_ik[side].append(to_body(pos[ha], pos, rot))

        hold = HOLDS.get(name, min(DEFAULT_HOLD, dur))
        win_rot = travel_windows(hand_rot, nframes, clip.loop, hold)
        win_ik = travel_windows(hand_ik, nframes, clip.loop, hold)
        bwin_rot = travel_windows(body_rot, nframes, clip.loop, hold)
        bwin_ik = travel_windows(body_ik, nframes, clip.loop, hold)

        # ---- IK pose offset and IK share of the arm pose motion (whole clip, body frame)
        motion = {}
        for side in ("L", "R"):
            offs = [math.dist(body_rot[side][fi], body_ik[side][fi]) for fi in range(nframes)
                    if weights[side]["series"][fi] > 1e-4]
            tr_rot = bbox_diag(body_rot[side])
            tr_ik = bbox_diag(body_ik[side])
            tv_rot = path_len(body_rot[side])
            tv_ik = path_len(body_ik[side])
            # Share of the visible wrist motion the clip shows with its own IK weight that the
            # rotation-only pose does not have. Negative (clamped to 0) means IK *removes* motion.
            share = (tv_ik - tv_rot) / tv_ik if tv_ik > 0.01 else 0.0
            motion[side] = dict(
                travelRot=tr_rot, travelIk=tr_ik, tvRot=tv_rot, tvIk=tv_ik,
                modelTravelRot=bbox_diag(hand_rot[side]),
                modelTravelIk=bbox_diag(hand_ik[side]),
                ikOffsetMean=(sum(offs) / len(offs)) if offs else 0.0,
                ikOffsetMax=max(offs) if offs else 0.0,
                share=max(0.0, min(1.0, share)),
                pinning=(tv_rot - tv_ik) / tv_rot if tv_rot > 0.01 else 0.0,
                winRot=win_rot, winIk=win_ik, bwinRot=bwin_rot, bwinIk=bwin_ik,
                ikErrMax=max((d[0] for d in ik_diag[side]), default=0.0),
                clamps=sum(1 for d in ik_diag[side] if d[1] > 0.0),
            )

        # ---- classification (per side)
        def cls():
            sides = []
            for side in ("L", "R"):
                w = weights[side]
                m = motion[side]
                # only wrist-affecting bones: a hand euler curve rotates the fingers, not the
                # wrist, so it must not turn an otherwise pure-IK clip into "IK+rot"
                rot_amp = max(rot_spread[side][b]["spread"]
                              for b in (f"shoulder.{side}", f"arm.{side}_upper",
                                        f"arm.{side}_lower"))
                if w["max"] <= 1e-4:
                    sides.append("rot(w=0)")
                elif tgt_travel[side]["authoredTravel"] < 0.03 and m["travelIk"] < 0.05:
                    # weight on, target body-fixed and still: IK defines the pose, not the motion
                    sides.append("IKpin")
                elif m["share"] >= 0.5:
                    sides.append("IK" + ("" if rot_amp < 3 else "+rot"))
                elif m["share"] >= 0.15:
                    sides.append(f"mix{round(m['share'] * 100)}%")
                elif rot_amp >= 3:
                    sides.append("rot")
                else:
                    sides.append("static")
            return sides

        rows.append(dict(
            clip=name, duration=dur, frames=nframes, loop=clip.loop,
            weights=weights, targets=tgt_travel, rotSpread=rot_spread, motion=motion,
            reach={"L": L1["L"] + L2["L"], "R": L1["R"] + L2["R"]},
            holds=hold, classSides=cls(), packDelta=pack_delta,
        ))

    # ------------------------------------------------------------------ print
    print(f"source rest reach L={rows[0]['reach']['L']:.3f} R={rows[0]['reach']['R']:.3f} m "
          f"(L1/L2 {rows[0]['reach']['L']:.3f}); weights from authored m_FloatCurves, "
          f"no curve = prefab default 1.0")
    print()
    hdr = (f"{'clip':17s} {'Lw':>13s} {'Rw':>13s} {'tgtTravel L/R':>14s} "
           f"{'armRotSpread L/R':>17s} {'armPoseTravel rot->ik L/R':>25s} {'IKoffset max L/R':>17s} "
           f"{'IKshare L/R':>11s} {'cls L/R':>16s}")
    print(hdr)
    print("-" * len(hdr))
    for r in rows:
        def wfmt(side):
            w = r["weights"][side]
            return f"{w['min']:.2f}..{w['max']:.2f}{'' if w['curve'] else '*'}"

        tl = r["targets"]
        tgt = f"{tl['L']['relTravel']:.3f}/{tl['R']['relTravel']:.3f}"
        rot = [max(v["spread"] for v in r["rotSpread"][s].values()) for s in ("L", "R")]
        rots = f"{rot[0]:.1f}/{rot[1]:.1f}deg"
        m = r["motion"]
        trav = (f"{m['L']['travelRot']:.3f}->{m['L']['travelIk']:.3f}/"
                f"{m['R']['travelRot']:.3f}->{m['R']['travelIk']:.3f}")
        off = f"{m['L']['ikOffsetMax']:.3f}/{m['R']['ikOffsetMax']:.3f}"
        sh = f"{m['L']['share'] * 100:.0f}%/{m['R']['share'] * 100:.0f}%"
        print(f"{r['clip']:17s} {wfmt('L'):>13s} {wfmt('R'):>13s} {tgt:>14s} {rots:>17s} "
              f"{trav:>25s} {off:>17s} {sh:>11s} "
              f"{r['classSides'][0] + '/' + r['classSides'][1]:>16s}")
    print("-" * len(hdr))
    print("weights: min..max, '*' = no weight curve in that clip (prefab default 1.0). "
          "tgtTravel = {L,R}Arm_target bbox relative to spine.003 (what the pack bakes).")
    print("armRotSpread = max |authored local rotation - frame 0| over shoulder/upper/lower/hand. "
          "armPoseTravel = body-frame wrist bbox (spine.03 frame), rotation-only -> clip's IK weight.")
    print("cls: rot(w=0) IK inert | IKpin = weight on, target body-fixed (IK sets pose, not motion) "
          "| IK = target-driven motion | mix = both contribute | static.")
    print("IKshare = (path length with IK - path length rotation-only) / path length with IK, whole "
          "clip, body frame. 100% = all visible wrist motion comes from the IK target.")
    print()
    print("per-clip detail")
    for r in rows:
        print(f"\n== {r['clip']}  dur={r['duration']:.3f}s frames={r['frames']} loop={r['loop']} "
              f"reach L={r['reach']['L']:.3f} R={r['reach']['R']:.3f} hold={r['holds']:.2f}s")
        for side in ("L", "R"):
            w = r["weights"][side]
            t = r["targets"][side]
            m = r["motion"][side]
            rs = r["rotSpread"][side]
            print(f"  {side}: weight curve={'yes' if w['curve'] else 'no(default 1)'} "
                  f"{w['min']:.3f}..{w['max']:.3f} active={w['active']}/{r['frames']} full={w['full']}"
                  f" | targetCurve={'yes' if t['curve'] else 'no(rest)'} "
                  f"relTravel={t['relTravel']:.3f}m (max-from-first {t['relMaxFromFirst']:.3f}) "
                  f"authored={t['authoredTravel']:.3f}m restRel={t['restRel']:.3f}m")
            print("     rotSpread " + " ".join(
                f"{b}={v['spread']:.1f}deg{'' if v['curve'] else '(no curve)'}"
                for b, v in rs.items()))
            wr, wi = m["winRot"], m["winIk"]
            br, bi = m["bwinRot"], m["bwinIk"]
            if wr and wi:
                print(f"     window[{wr['window'][0]:.2f},{wr['window'][1]:.2f}]s model travel rot "
                      f"L={wr['handL']:.3f} R={wr['handR']:.3f} -> ik L={wi['handL']:.3f} "
                      f"R={wi['handR']:.3f} | body travel rot L={br['handL']:.3f} R={br['handR']:.3f}"
                      f" -> ik L={bi['handL']:.3f} R={bi['handR']:.3f}")
                print(f"     window handLR rot {wr['handLR'][0]:.3f}..{wr['handLR'][1]:.3f} -> ik "
                      f"{wi['handLR'][0]:.3f}..{wi['handLR'][1]:.3f}")
            print(f"     whole-clip body travel rot={m['travelRot']:.3f} ik={m['travelIk']:.3f} "
                  f"pathlen rot={m['tvRot']:.3f} ik={m['tvIk']:.3f} IKshare={m['share'] * 100:.0f}% "
                  f"pin={m['pinning'] * 100:.0f}% ikOffset mean={m['ikOffsetMean']:.3f} "
                  f"max={m['ikOffsetMax']:.3f} ikErr={m['ikErrMax']:.3f} clamps={m['clamps']} "
                  f"(class {r['classSides'][0 if side == 'L' else 1]})")
        if r["packDelta"]:
            print("   pack-baked offset vs audit-computed offset: " + " ".join(
                f"{s} max|delta|={v:.6f}m" for s, v in r["packDelta"].items()))

    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        slim = []
        for r in rows:
            slim.append({
                "clip": r["clip"], "duration": r["duration"], "frames": r["frames"],
                "loop": r["loop"], "holds": r["holds"],
                "weights": {s: {k: v for k, v in r["weights"][s].items() if k != "series"}
                            for s in ("L", "R")},
                "targets": r["targets"], "rotSpread": r["rotSpread"], "motion": r["motion"],
                "classSides": r["classSides"], "packDelta": r["packDelta"],
            })
        json.dump(slim, open(out, "w"), indent=1)
        print(f"\nwrote {out}")


if __name__ == "__main__":
    main()
