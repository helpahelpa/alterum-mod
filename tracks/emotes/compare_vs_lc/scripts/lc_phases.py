#!/usr/bin/env python3
"""Pick matched phases from the real LC captures (resample by normalised clip time, not index).

The emote clip runs on animator layer 1; the CSV records its normalizedTime. A phase is
frac(normalizedTime). If the capture saw a loop boundary every phase is available and N phases are
taken evenly over [0,1); if it only saw one pass through part of the clip (Griddy/Prisyadka, whose
clip is 6.5 s and which was captured for 3 s) the N phases span the captured interval.
"""
import csv, json, math, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(os.path.expanduser("~/assets/monorepo/alterum-data"), "lc-reference")
CLIPS = ["Clap", "Twerk", "Salute", "Middle_Finger", "The_Griddy", "Prisyadka"]

def frames(clip):
    out = []
    with open(os.path.join(ROOT, "frames", clip, f"seq_{clip}.csv")) as f:
        for r in csv.DictReader(f):
            n = float(r["norm1"])
            out.append({"frame": int(r["frame"]), "t": float(r["time"]), "norm": n,
                        "phase": n - math.floor(n)})
    return out

def wrapped(fr):
    # The layer's normalizedTime is continuous across loops, so a span of one full cycle means
    # every phase was seen; a shorter span (Griddy/Prisyadka) only covers part of the clip.
    return (max(x["norm"] for x in fr) - min(x["norm"] for x in fr)) >= 0.999

def circ(a, b):
    d = abs(a - b) % 1.0
    return min(d, 1.0 - d)

def pick(clip, n):
    fr = frames(clip)
    if wrapped(fr):
        targets = [i / n for i in range(n)]
        score = lambda f, p: circ(f["phase"], p)
        span = "full-cycle"
    else:
        lo, hi = min(x["phase"] for x in fr), max(x["phase"] for x in fr)
        targets = [lo + (hi - lo) * i / (n - 1) for i in range(n)]
        score = lambda f, p: abs(f["phase"] - p)
        span = f"partial {lo:.3f}..{hi:.3f}"
    picks, used = [], set()
    for p in targets:
        best = min((f for f in fr if f["frame"] not in used), key=lambda f: score(f, p))
        used.add(best["frame"])
        picks.append((round(best["phase"], 4), best["frame"]))
    return picks, fr, span

if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 8
    out = {}
    for c in CLIPS:
        picks, fr, span = pick(c, n)
        print(f"{c:15s} captured={len(fr)}f {span}")
        print(f"  phases={','.join(f'{p:.4f}' for p, _ in picks)}")
        print(f"  lcframes={','.join(str(f) for _, f in picks)}")
        out[c] = {"phases": [p for p, _ in picks], "frames": [f for _, f in picks]}
    json.dump(out, open(os.path.join(HERE, "..", "phases.json"), "w"), indent=1)
