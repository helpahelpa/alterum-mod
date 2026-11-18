#!/usr/bin/env python3
"""LC frame | port frame side-by-side sheets, sampled at matched normalised clip phases.

Both sides are shown at the same standing-body height in pixels (LC suit = 470 px at the reference
capture geometry, our model = 306 px, measured once), each in a fixed crop window per clip so the
camera does not follow the pose. Proportions/limb lengths are therefore NOT comparable between the
panels by design -- pose and motion are.
"""
import glob, json, math, os, sys
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from frame_boxes import lc_box, port_box

HERE = os.path.dirname(os.path.abspath(__file__))
TRACK = os.path.normpath(os.path.join(HERE, "..", ".."))
DATA = os.path.expanduser("~/assets/monorepo/alterum-data")
LCR = os.path.normpath(os.path.join(DATA, "lc-reference"))
OUT = os.path.normpath(os.path.join(HERE, "..", "sheets"))
PORT = os.path.normpath(os.path.join(HERE, "..", "port"))
PAL = os.path.normpath(os.path.join(DATA, "lc-reference"))

LC_BODY_H = 470.0          # spacesuit head-top..boots in the 960x540 reference frames
PORT_BODY_H = 306.0        # our model, hat included, same camera geometry
PANEL_W, PANEL_H = 360, 300
BODY_PX = 262              # standing height in the panel
PAIRS_PER_SHEET = 8

def font(sz, bold=False):
    for p in ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" if bold else
              "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
              "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf" if bold else
              "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf"):
        if os.path.exists(p): return ImageFont.truetype(p, sz)
    return ImageFont.load_default()

def clip_windows(clip):
    lcf = sorted(glob.glob(os.path.join(LCR, "frames", clip, f"seq_{clip}_*.png")))
    ptf = sorted(glob.glob(os.path.join(PORT, clip, "*.png")))
    lb = [lc_box(p) for p in lcf]
    pb = [port_box(p) for p in ptf]
    lc_ground = max(b[3] for b in lb) + 0.06 * (max(b[3] - b[1] for b in lb))
    pt_ground = max(b[3] for b in pb)
    lc_cx = sorted((b[0] + b[2]) / 2 for b in lb)[len(lb) // 2]
    pt_cx = sorted((b[0] + b[2]) / 2 for b in pb)[len(pb) // 2]
    return lcf, ptf, lc_ground, lc_cx, pt_ground, pt_cx

def panel(img, body_h, ground_y, cx, flip=False):
    s = BODY_PX / body_h
    # crop window in source pixels so that after scaling it is PANEL_W x PANEL_H
    w, h = PANEL_W / s, PANEL_H / s
    x0 = cx - w / 2
    y0 = ground_y - (PANEL_H - 26) / s
    out = Image.new("RGB", (PANEL_W, PANEL_H), (10, 11, 14))
    crop = img.transform((PANEL_W, PANEL_H), Image.AFFINE,
                         (1 / s, 0, x0, 0, 1 / s, y0), resample=Image.BICUBIC)
    out.paste(crop, (0, 0))
    if flip: out = out.transpose(Image.FLIP_LEFT_RIGHT)
    return out

def sheet(clip, phases, picks, maxpairs=PAIRS_PER_SHEET):
    lcf, ptf, lc_ground, lc_cx, pt_ground, pt_cx = clip_windows(clip)
    step = max(1, len(phases) // maxpairs)
    sel = list(range(0, len(phases), step))[:maxpairs]
    cols = 2
    rows = math.ceil(len(sel) / cols)
    cellw, cellh = PANEL_W * 2 + 30, PANEL_H + 30
    W = cols * cellw + 30
    H = 64 + rows * cellh + 14
    img = Image.new("RGB", (W, H), (16, 17, 21))
    d = ImageDraw.Draw(img)
    fb, fs, fl = font(19, True), font(13), font(12)
    d.text((16, 12), f"{clip}", font=fb, fill=(235, 235, 240))
    d.text((16 + d.textlength(clip, font=fb) + 16, 15),
           "LC reference (left)  |  port (right)   matched normalised clip time   "
           "scale: equal standing height, fixed camera per clip",
           font=fs, fill=(150, 152, 160))
    for k, i in enumerate(sel):
        c, r = k % cols, k // cols
        ox, oy = 16 + c * cellw, 64 + r * cellh
        ph = phases[i]
        lcimg = Image.open(lcf[picks[i]]).convert("RGB")
        ptimg = Image.open(ptf[i]).convert("RGB")
        a = panel(lcimg, LC_BODY_H, lc_ground, lc_cx)
        b = panel(ptimg, PORT_BODY_H, pt_ground, pt_cx)
        img.paste(a, (ox + 8, oy + 24))
        img.paste(b, (ox + PANEL_W + 22, oy + 24))
        d.text((ox + 8, oy + 4), f"phase {ph:.3f}   LC#{picks[i]:03d}", font=fl, fill=(200, 200, 210))
        d.text((ox + PANEL_W + 22, oy + 4), f"port p{i:02d}", font=fl, fill=(200, 200, 210))
        d.line([(ox + 8, oy + 24 + PANEL_H), (ox + 2 * PANEL_W + 22, oy + 24 + PANEL_H)],
               fill=(40, 42, 48), width=1)
    img = img.quantize(colors=192, method=Image.MEDIANCUT).convert("RGB")
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, f"{clip}_vs_LC.png")
    img.save(path, optimize=True)
    return path, os.path.getsize(path), [i for i in sel]

if __name__ == "__main__":
    ph = json.load(open(os.path.join(HERE, "..", "phases.json")))
    for clip, v in ph.items():
        path, size, sel = sheet(clip, v["phases"], v["frames"])
        print(f"{path}  {size/1024:.0f} KB  pairs={len(sel)}")
