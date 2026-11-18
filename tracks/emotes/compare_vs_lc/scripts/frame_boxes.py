"""Subject bbox in the LC reference frames and in the port's capture frames."""
import numpy as np
from PIL import Image

def _lc_mask(a):
    # The spacesuit torso/limbs are orange; the helmet and boots are dark, but they sit inside the
    # same x span as the suit, so a dark band above/below the orange bbox completes the silhouette.
    orange = ((a[:, :, 0] - a[:, :, 2]) > 18) & ((a[:, :, 0] - a[:, :, 1]) > 8) & (a[:, :, 0] > 45)
    return orange

def lc_box(path):
    a = np.asarray(Image.open(path).convert("RGB")).astype(int)
    m = _lc_mask(a)
    ys, xs = np.where(m)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    # dark helmet above and boots below, searched only in the suit's own column band
    band = a[:, max(0, x0 - 6):x1 + 7, :].max(axis=2)
    xs_lo, xs_hi = max(0, x0 - 6), x1 + 7
    dark = band < 70
    rows = np.where(dark.any(axis=1))[0]
    up = rows[rows < y0]
    if len(up): y0 = max(rows.min(), y0 - 140)
    dn = rows[rows > y1]
    if len(dn): y1 = min(rows.max(), y1 + 100)
    return xs_lo, y0, xs_hi, y1

def port_box(path):
    a = np.asarray(Image.open(path).convert("RGB")).astype(int)
    bg = (abs(a[:, :, 0] - 13) < 12) & (abs(a[:, :, 1] - 15) < 12) & (abs(a[:, :, 2] - 20) < 14)
    m = ~bg
    ys, xs = np.where(m)
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
