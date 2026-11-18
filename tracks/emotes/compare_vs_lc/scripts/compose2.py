#!/usr/bin/env python3
"""Iteration 2 of the LC-vs-port sheets.

Two things changed after iteration 1 (which paired every capture phase with an LC frame of the same
normalised layer time):

1. `Salute` and `Middle_Finger` are **non-looping** clips (pack `loop=False`). In the real game the
   emote state runs past the clip end and holds the last frame: the 75 captured frames contain only
   35/26 distinct images and the raised arm is pixel-identical at every phase. The phase pairing is
   therefore only meaningful for our port's end pose, so those two clips get a held-pose sheet
   instead of a phase sheet, plus a strip of what our clip does at phases the game never shows.
2. The crop window is anchored to the feet and a fixed per-clip body height, and the panels are
   labelled with the phase and what the game was doing (playing vs holding).
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

LC_BODY_H = 470.0     # suit head-top..boots in the 960x540 reference frames (measured, Clap)
PORT_BODY_H = 306.0   # our model incl. hat at the same camera geometry
PANEL_W, PANEL_H = 380, 300
BODY_PX = 262
ONESHOT = {"Salute", "Middle_Finger"}

def font(sz, bold=False):
    base = "/usr/share/fonts/truetype/dejavu/DejaVuSans%s.ttf" % ("-Bold" if bold else "")
    if os.path.exists(base): return ImageFont.truetype(base, sz)
    return ImageFont.load_default()

def clip_files(clip):
    lcf = sorted(glob.glob(os.path.join(LCR, "frames", clip, f"seq_{clip}_*.png")))
    ptf = sorted(glob.glob(os.path.join(PORT, clip, "*.png")))
    return lcf, ptf

def windows(clip):
    lcf, ptf = clip_files(clip)
    lb = [lc_box(p) for p in lcf]
    pb = [port_box(p) for p in ptf]
    lc_ground = max(b[3] for b in lb) + 0.06 * (max(b[3] - b[1] for b in lb))
    pt_ground = max(b[3] for b in pb)
    lc_cx = sorted((b[0] + b[2]) / 2 for b in lb)[len(lb) // 2]
    pt_cx = sorted((b[0] + b[2]) / 2 for b in pb)[len(pb) // 2]
    return lcf, ptf, lc_ground, lc_cx, pt_ground, pt_cx

def panel(img, body_h, ground_y, cx):
    s = BODY_PX / body_h
    w, h = PANEL_W / s, PANEL_H / s
    x0 = cx - w / 2
    y0 = ground_y - (PANEL_H - 26) / s
    out = Image.new("RGB", (PANEL_W, PANEL_H), (9, 10, 13))
    out.paste(img.transform((PANEL_W, PANEL_H), Image.AFFINE,
                            (1 / s, 0, x0, 0, 1 / s, y0), resample=Image.BICUBIC), (0, 0))
    return out

def pair(win, clip, lcpath, ptpath, phase):
    lcf, ptf, lc_ground, lc_cx, pt_ground, pt_cx = win
    a = panel(Image.open(lcpath).convert("RGB"), LC_BODY_H, lc_ground, lc_cx)
    b = panel(Image.open(ptpath).convert("RGB"), PORT_BODY_H, pt_ground, pt_cx)
    return a, b

def readme_header(d, W, title, sub):
    fb, fs = font(19, True), font(13)
    d.text((16, 12), title, font=fb, fill=(235, 235, 240))
    d.text((16 + d.textlength(title, font=fb) + 16, 15), sub, font=fs, fill=(150, 152, 160))

def phase_sheet(clip, phases, picks):
    win = windows(clip)
    lcf, ptf = win[0], win[1]
    n = 8
    step = max(1, len(phases) // n)
    sel = list(range(0, len(phases), step))[:n]
    cols, rows = 2, math.ceil(len(sel) / 2)
    cellw, cellh = PANEL_W * 2 + 30, PANEL_H + 30
    W = cols * cellw + 30
    H = 64 + rows * cellh + 12
    img = Image.new("RGB", (W, H), (16, 17, 21))
    d = ImageDraw.Draw(img)
    readme_header(d, W, clip,
                  "LC reference (left) | port (right)   matched normalised clip time   "
                  "equal standing height, fixed camera per clip")
    fl = font(12)
    for k, i in enumerate(sel):
        c, r = k % cols, k // cols
        ox, oy = 16 + c * cellw, 64 + r * cellh
        a, b = pair(win, clip, lcf[picks[i]], ptf[i], phases[i])
        img.paste(a, (ox + 8, oy + 24)); img.paste(b, (ox + PANEL_W + 22, oy + 24))
        d.text((ox + 8, oy + 4), f"phase {phases[i]:.3f}   LC#{picks[i]:03d}", font=fl, fill=(200, 200, 210))
        d.text((ox + PANEL_W + 22, oy + 4), f"port p{i:02d}", font=fl, fill=(200, 200, 210))
    return save(img, f"{clip}_vs_LC.png")

def held_sheet(clip, phases):
    """Non-looping clip: the game holds the last frame; only the end pose is comparable."""
    win = windows(clip)
    lcf, ptf = win[0], win[1]
    end = hold = min(range(len(phases)), key=lambda i: abs(phases[i] - 1.0))
    W = PANEL_W * 2 + 46
    H = 64 + PANEL_H + 42 + 150 + 40
    img = Image.new("RGB", (W, H), (16, 17, 21))
    d = ImageDraw.Draw(img)
    fb, fl, fs = font(19, True), font(12), font(13)
    readme_header(d, W, clip,
                  "non-looping clip: the LC state holds the clip's last frame (no phase motion in the capture)")
    a, b = pair(win, clip, lcf[hold], ptf[end], 1.0)
    img.paste(a, (16, 64 + 20)); img.paste(b, (PANEL_W + 30, 64 + 20))
    d.text((16, 64 + 4), "LC reference: held pose (clip end, the only pose the game shows)", font=fl, fill=(200, 200, 210))
    d.text((PANEL_W + 30, 64 + 4), f"port: clip end (phase {phases[end]:.3f})", font=fl, fill=(200, 200, 210))
    # strip of our own clip at the other phases, for the record
    y0 = 64 + 20 + PANEL_H + 30
    d.text((16, y0 - 16), "our clip at the other matched phases - the game never leaves the hold for these:",
           font=fs, fill=(150, 152, 160))
    tw = (W - 32) // 7
    for k, i in enumerate([i for i in range(0, len(phases), max(1, len(phases) // 7))][:7]):
        s = tw / PANEL_W
        thumb = panel(Image.open(ptf[i]).convert("RGB"), PORT_BODY_H, win[4], win[5]).resize(
            (tw, int(PANEL_H * s)), Image.BICUBIC)
        img.paste(thumb, (16 + k * tw, y0))
        d.text((16 + k * tw + 3, y0 - 2 + thumb.size[1]), f"{phases[i]:.2f}", font=fl, fill=(180, 180, 190))
    return save(img, f"{clip}_vs_LC.png")

def save(img, name):
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, name)
    for colors in (192, 128, 96, 72):
        img.quantize(colors=colors, method=Image.MEDIANCUT).convert("RGB").save(path, optimize=True)
        if os.path.getsize(path) <= 600 * 1024:
            break
    return path, os.path.getsize(path)

if __name__ == "__main__" and os.environ.get("COMPOSE_MAIN") != "0":
    ph = json.load(open(os.path.join(HERE, "..", "phases.json")))
    only = os.environ.get("CLIPS", "").strip()
    want = {c.strip() for c in only.split(",") if c.strip()} if only else None
    for clip, v in ph.items():
        if want and clip not in want:
            continue
        if clip in ONESHOT:
            path, size = held_sheet(clip, v["phases"])
        else:
            path, size = phase_sheet(clip, v["phases"], v["frames"])
        print(f"{path}  {size/1024:.0f} KB")
