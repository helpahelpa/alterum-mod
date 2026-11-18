#!/usr/bin/env python3
"""Compare two acceptance sweeps (before/after logs) against the window-matched source numbers.

All three inputs are plain `LogOutput.log` copies and the `source_metrics.py` output:

  python3 tools/sweep_compare.py \
      --before verify/before_ikall_matched.log \
      --after  verify/after_ikall_final.log \
      --source verify/source_metrics.ikall.log

The table below is what the fourth pass reports: window handLR and hand travel (the runtime's own
`[emote:travel]` definition), strip-sampled handLR / pelvis->hand / reachUtil at the post-fade
strips (0.25..0.85, the strips the previous passes quoted), and the IK diagnostics. Source columns
are LC's pose with its baked constraint weight, scaled to the port's arm length (0.816 handLR,
0.808 L / 0.824 R travel) exactly like the earlier passes. Source variant `[ik]` is used when the
clip has a baked IK array; `[raw]` otherwise (weight-0 clips, whose LC pose is rotation-only).

Both logs must be full sweeps of the same `EmoteRuntime.Steps` list, otherwise the clip windows do
not line up.
"""
import math
import re
import sys

# distance scales (port arm reach / LC arm reach), from the pass-2 measurements
SCALE_LR = 0.816
SCALE_L = 0.808
SCALE_R = 0.824
POST_FADE_STRIPS = (1, 2, 3, 4)     # strip 0 lands inside the 0.2 s fade-in


def parse_log(path):
    lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    starts = [i for i, l in enumerate(lines) if "emote:sample" in l and "run=1 " in l]
    beg = starts[-1] - 12 if starts else 0
    out = {}
    for l in lines[max(0, beg):]:
        m = re.search(r"\[emote:travel\] clip='([^']+)' handL=([\d.]+)m .*?handR=([\d.]+)m "
                      r".*?handLR=([\d.]+)\.\.([\d.]+)", l)
        if m:
            out.setdefault(m.group(1), {})["travel"] = tuple(
                float(x) for x in m.groups()[1:])
            continue
        m = re.search(r"\[emote:sample\] run=\d+ clip='([^']+)' strip=(\d+) t=([\d.]+)s.*?"
                      r"pelvisHandL=([\d.]+) pelvisHandR=([\d.]+) handLR=([\d.]+).*?"
                      r"reachUtils\[L=([\d.]+) R=([\d.]+)\](.*)", l)
        if m:
            c = m.group(1)
            e = out.setdefault(c, {})
            strip = int(m.group(2))
            e.setdefault("strips", []).append(dict(
                strip=strip, t=float(m.group(3)),
                pL=float(m.group(4)), pR=float(m.group(5)), lr=float(m.group(6)),
                ruL=float(m.group(7)), ruR=float(m.group(8))))
            ik = re.search(r" ik\[L:dist=[\d.]+ reach=[\d.]+ err=([\d.]+) w=([\d.]+) clamps=(\d+).*?"
                           r"R:dist=[\d.]+ reach=[\d.]+ err=([\d.]+) w=([\d.]+) clamps=(\d+)",
                           m.group(9) + " ")
            if ik:
                e.setdefault("ik", []).append(tuple(float(x) for x in ik.groups()))
            continue
        # post-fade heartbeats (one per second): the only post-fade sample for clips whose strip
        # fractions all fall beyond the hold (The_Griddy, Prisyadka, Shy)
        m = re.search(r"\[emote:active\] .*?clip='([^']+)' t=([\d.]+)s .*?pelvisHandL=([\d.]+) "
                      r"pelvisHandR=([\d.]+) handLR=([\d.]+).*?reachUtils\[L=([\d.]+) R=([\d.]+)\]",
                      l)
        if m and "deltas-vs-entry" in l:
            c = m.group(1)
            if float(m.group(2)) >= 0.35:
                out.setdefault(c, {}).setdefault("beats", []).append(dict(
                    pL=float(m.group(3)), pR=float(m.group(4)), lr=float(m.group(5)),
                    ruL=float(m.group(6)), ruR=float(m.group(7))))
    return out


def parse_source(path):
    txt = open(path, encoding="utf-8", errors="replace").read()
    out = {}
    for m in re.finditer(r"== (\S+) \[(ik|raw)\](.*?)travel\(sweep \[[\d.]+,([\d.]+)\]s\) "
                         r"handL=([\d.]+)m .*?handR=([\d.]+)m .*?handLR=([\d.]+)\.\.([\d.]+)",
                         txt, re.S):
        body = m.group(3)
        strips = [dict(pL=float(x)) for x in
                  re.findall(r"pelvisHandL=([\d.]+) pelvisHandR=", body)]
        out[m.group(1)] = dict(variant=m.group(2), hold=float(m.group(4)),
                               L=float(m.group(5)), R=float(m.group(6)),
                               lr=(float(m.group(7)), float(m.group(8))),
                               strips=strips)
    return out


def rng(vals):
    return f"{min(vals):.3f}..{max(vals):.3f}" if vals else "-"


def main():
    argv = sys.argv
    def opt(name, default=None):
        return argv[argv.index(name) + 1] if name in argv else default

    before = parse_log(opt("--before"))
    after = parse_log(opt("--after"))
    source = parse_source(opt("--source"))
    order = opt("--order", "").split(",") if opt("--order") else list(after)

    print(f"{'clip':17s} | {'handLR before':>13s} {'after':>15s} {'source':>14s} | "
          f"{'travel L/R before':>18s} {'after':>14s} {'source':>14s} | "
          f"{'pHandL b/a/src':>41s} | {'handLR strips b/a':>26s} | {'reacUtil L/R after':>18s} | ikErr/clamps")
    print("-" * 235)
    for c in order:
        b = before.get(c, {})
        a = after.get(c, {})
        s = source.get(c)
        bt, at = b.get("travel"), a.get("travel")
        def tr(t):
            return f"{t[0]:.3f}/{t[1]:.3f}" if t else "-"
        def lr(t):
            return f"{t[2]:.3f}..{t[3]:.3f}" if t else "-"
        src_lr = f"{s['lr'][0] * SCALE_LR:.3f}..{s['lr'][1] * SCALE_LR:.3f}" if s else "-"
        src_tr = f"{s['L'] * SCALE_L:.3f}/{s['R'] * SCALE_R:.3f}" if s else "-"

        def strips(d):
            return {x["strip"]: x for x in d.get("strips", [])}
        bs, as_ = strips(b), strips(a)
        blr = [bs[k]["lr"] for k in POST_FADE_STRIPS if k in bs]
        alr = [as_[k]["lr"] for k in POST_FADE_STRIPS if k in as_]
        # post-fade samples: strips present in both runs, plus heartbeats (>=0.35 s)
        spL = [bs[k]["pL"] for k in POST_FADE_STRIPS if k in bs] + [x["pL"] for x in b.get("beats", [])]
        apL = [as_[k]["pL"] for k in POST_FADE_STRIPS if k in as_] + [x["pL"] for x in a.get("beats", [])]
        ruL = [as_[k]["ruL"] for k in POST_FADE_STRIPS if k in as_] + [x["ruL"] for x in a.get("beats", [])]
        ruR = [as_[k]["ruR"] for k in POST_FADE_STRIPS if k in as_] + [x["ruR"] for x in a.get("beats", [])]
        ik = a.get("ik") or []
        iks = "-"
        if ik:
            iks = (f"err {max(x[0] for x in ik):.3f}/{max(x[3] for x in ik):.3f} m, "
                   f"clamps {max(int(x[2]) for x in ik)}/{max(int(x[5]) for x in ik)}")
        # source pHandL over the *same* strip indices the port sampled
        src_pL = "-"
        if s and s.get("strips"):
            idx = [k for k in POST_FADE_STRIPS if k in as_ and k < len(s["strips"])]
            if idx:
                src_pL = rng([s["strips"][k]["pL"] * SCALE_L for k in idx])
        print(f"{c:17s} | {lr(bt):>13s} {lr(at):>15s} {src_lr:>14s} | "
              f"{tr(bt):>18s} {tr(at):>14s} {src_tr:>14s} | {rng(spL):>13s} {rng(apL):>13s} "
              f"{src_pL:>13s} | {rng(blr) + ' / ' + rng(alr):>26s} | "
              f"{rng(ruL) + '/' + rng(ruR):>18s} | {iks}")
    print()
    print("handLR/travel = the runtime's own [emote:travel] metric over [0.35 s, hold] of run time.")
    print("handLR strips = post-fade strips 1..4 (clip fractions 0.25/0.45/0.65/0.85).")
    print("source = source_metrics.py window-matched variant ([ik] where the pack bakes a weight),")
    print(f"scaled x{SCALE_LR} (handLR) and x{SCALE_L}/{SCALE_R} (travel L/R).")


if __name__ == "__main__":
    main()
