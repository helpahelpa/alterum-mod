#!/usr/bin/env python3
"""Prove a recorded PNG sequence is real motion, not N copies of one still or a flat screen.

usage: clip_check.py <frames-dir> [max_frames]

For every frame: size on disk, pixel std (a lock screen / flat colour is ~0) and the mean
absolute difference from the previous frame. Prints a compact table plus a verdict.
Exit code 0 when the sequence is non-degenerate, 1 otherwise.
"""
import sys
import pathlib

import numpy as np
from PIL import Image


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__.strip().splitlines()[0], file=sys.stderr)
        return 2
    d = pathlib.Path(sys.argv[1])
    limit = int(sys.argv[2]) if len(sys.argv) > 2 else 400
    files = sorted(d.glob("frame_*.png"))
    if not files:
        print(f"no frame_*.png in {d}", file=sys.stderr)
        return 1
    files = files[:limit]

    print(f"{'file':16} {'bytes':>8} {'WxH':>10} {'std':>7} {'mean':>7} {'mad_prev':>8} {'changed%':>8}")
    prev = None
    diffs, stds = [], []
    for f in files:
        img = Image.open(f).convert("RGB")
        a = np.asarray(img, dtype=np.float32)
        std = float(a.std())
        mean = float(a.mean())
        if prev is not None and prev.shape == a.shape:
            delta = np.abs(a - prev).mean(axis=2)
            mad = float(delta.mean())
            changed = float((delta > 2.0).mean()) * 100.0
            diffs.append(mad)
            print(f"{f.name:16} {f.stat().st_size:8d} {img.width:5d}x{img.height:<4d} {std:7.2f} {mean:7.2f} {mad:8.3f} {changed:8.2f}")
        else:
            print(f"{f.name:16} {f.stat().st_size:8d} {img.width:5d}x{img.height:<4d} {std:7.2f} {mean:7.2f}        -        -")
        stds.append(std)
        prev = a

    n = len(files)
    moving = sum(1 for v in diffs if v > 0.5)
    print()
    print(f"frames={n} distinct_sizes={len({f.stat().st_size for f in files})} "
          f"min_std={min(stds):.2f} max_std={max(stds):.2f}")
    if diffs:
        print(f"consecutive diffs: n={len(diffs)} min={min(diffs):.3f} mean={sum(diffs)/len(diffs):.3f} "
              f"max={max(diffs):.3f} pairs_over_0.5={moving}/{len(diffs)}")
        print(f"distinct frames (any pair differing > 0.5): {moving + 1}")
    ok = n >= 2 and max(stds) > 3.0 and moving >= max(1, (n - 1) // 2)
    print("verdict:", "REAL MOTION" if ok else "DEGENERATE (still frames or flat colour)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
