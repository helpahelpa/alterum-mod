#!/usr/bin/env python3
"""Per-clip real-motion evidence over a recorded PNG sequence (640x360 showcase frames).

usage: analyze_frames.py <frames-dir> [label]

Reports over a fixed body crop (torso + both arms, same rectangle for every clip):
  * frame count, distinct PNG count and visually-distinct frame count,
  * consecutive-frame mean absolute difference (MAD), statistics plus the full series,
  * a verdict: the crop must move more than the pre/post idle baseline, and a majority of
    consecutive pairs must differ, so a repeated still cannot pass.
"""
import hashlib
import pathlib
import sys

import numpy as np
from PIL import Image

# 640x360 showcase framing: body centred. This rectangle holds the torso, both arms and the
# hands as low as the twerk pose and as high as the salute, and leaves out the legs/floor.
CROP = (175, 35, 500, 320)   # x0, y0, x1, y1


def main():
    d = pathlib.Path(sys.argv[1])
    label = sys.argv[2] if len(sys.argv) > 2 else d.name
    files = sorted(d.glob("frame_*.png"))
    if not files:
        print(f"no frame_*.png in {d}", file=sys.stderr)
        return 1
    imgs = [np.asarray(Image.open(f).convert("RGB"), dtype=np.float32) for f in files]
    x0, y0, x1, y1 = CROP
    crops = [im[y0:y1, x0:x1] for im in imgs]

    hashes = {hashlib.sha256(f.read_bytes()).hexdigest() for f in files}
    print(f"[{label}] dir={d}")
    print(f"[{label}] frames={len(files)} crop=x{x0}..{x1} y{y0}..{y1} ({x1 - x0}x{y1 - y0} of {imgs[0].shape[1]}x{imgs[0].shape[0]})")
    print(f"[{label}] distinct_pngs={len(hashes)}/{len(files)}")

    distinct = 0
    for i in range(len(crops)):
        if all(np.abs(crops[i] - crops[j]).mean() > 0.5 for j in range(i)):
            distinct += 1
    print(f"[{label}] visually_distinct={distinct}/{len(files)} (>0.5 MAD against every other frame)")

    mads = [float(np.abs(crops[i] - crops[i - 1]).mean()) for i in range(1, len(crops))]
    arr = np.asarray(mads)
    print(f"[{label}] crop_mad n={len(arr)} min={arr.min():.3f} mean={arr.mean():.3f} "
          f"median={np.median(arr):.3f} max={arr.max():.3f} pairs_over_0.5={int((arr > 0.5).sum())}/{len(arr)}")
    print(f"[{label}] crop_mad_series=" + " ".join(f"{v:.2f}" for v in mads))

    # Pre-roll reference: the first frames are recorded before the emote request is written, so
    # they are the game's own idle pose. Deviation from it is what a held still cannot fake.
    ref = np.mean(crops[:4], axis=0)
    dev = np.asarray([float(np.abs(c - ref).mean()) for c in crops])
    idle = float(np.median(dev[:6]))
    print(f"[{label}] pose_dev_from_preroll max={dev.max():.3f} at_frame={int(dev.argmax()) + 1} "
          f"idle_baseline={idle:.3f} frames_over_1.0={int((dev > 1.0).sum())}/{len(dev)}")
    print(f"[{label}] pose_dev_series=" + " ".join(f"{v:.2f}" for v in dev))

    moving = int((dev > 1.0).sum())
    ok = (len(files) >= 2 and dev.max() > 3.0 and distinct >= max(2, int(0.4 * len(files)))
          and int((arr > 0.5).sum()) >= 1)
    print(f"[{label}] verdict=" + ("REAL MOTION" if ok else "DEGENERATE"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
