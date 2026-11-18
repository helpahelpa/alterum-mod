#!/usr/bin/env python3
"""Compose the Reel of Fortune horizontal-scroll filmstrip.

Seven stills of the SAME pinned close-up camera from one reel 'film' request, each frozen at a
point on the real spin curve (see [reel:film] k=.. lines in the log for t, u, card and symbol).
Crops are the item window only, so the seven rows differ only by the scroll.
"""
import os, re, sys
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SCREENS = "/mnt/c/AlterumPinned/BepInEx/screens"
OUT = os.path.join(HERE, "filmstrips", "reel_scroll.png")
DATA = os.path.expanduser("~/assets/monorepo/alterum-data")
LOG = os.path.join(DATA, ".temp", "reel", "case", "fin_film.txt")
START = os.path.join(DATA, ".temp", "reel", "case", "fin_trace.txt")

CROP = (200, 196, 766, 436)     # the item window + its housing, 566x240
ROW_H = CROP[3] - CROP[1]
PAD, GAP, LBL_W, HDR_H = 14, 8, 300, 74

F_T = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 18)
F_L = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 13)
F_S = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 11)

BG = (16, 16, 18)
FG = (236, 236, 240)
DIM = (146, 148, 156)
ACC = (255, 196, 96)
GOOD = (120, 210, 150)


def parse_film(path):
    rows = []
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"\[reel:film\] k=(\d+) t=([\d.]+) u=([-\d.]+) travel=([-\d.]+)cards "
                          r"cell=(\d+) sym=(\d+) code=(\w+) centre=([\d.]+)", line)
            if m:
                rows.append(dict(k=int(m.group(1)), t=float(m.group(2)), u=float(m.group(3)),
                                 travel=float(m.group(4)), cell=int(m.group(5)),
                                 sym=int(m.group(6)), code=m.group(7), centre=float(m.group(8))))
    return rows


def header_facts(film_path, trace_path):
    """Film-curve facts from the film log; the live-spin facts from the trace log for the note."""
    facts = {}
    with open(film_path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"\[reel:film\] start .*targetCell=(\d+) from=([-\d.]+) to=([-\d.]+) clickTo=([-\d.]+) "
                          r"canonical=([-\d.]+) travel=([\d.]+)cards curve=(\S+) duration=([\d.]+)s", line)
            if m:
                facts["target"] = m.group(1); facts["travel"] = m.group(6)
                facts["curve"] = m.group(7); facts["duration"] = m.group(8)
    with open(trace_path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = re.search(r"\[reel\] spin start .*duration=([\d.]+)s ease=(\S+) click=([\d.]+)s overshoot=([\d.]+)card", line)
            if m:
                facts["ease"] = m.group(2); facts["click"] = m.group(3); facts["overshoot"] = m.group(4)
            m = re.search(r"\[reel\] settled symbol=(\d+) code=(\w+) .*cell=(\d+) .*total=([\d.]+)s grants=0", line)
            if m:
                facts["settled"] = f"sym {m.group(1)} {m.group(2)} cell {m.group(3)}"
                facts["total"] = m.group(4)
    return facts


def main():
    rows = parse_film(LOG)
    if not rows:
        sys.exit(f"no [reel:film] rows in {LOG}")
    facts = header_facts(LOG, START)

    W = PAD * 2 + (CROP[2] - CROP[0]) + 12 + LBL_W
    H = PAD + HDR_H + len(rows) * (ROW_H + GAP) - GAP + PAD
    canvas = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(canvas)

    d.text((PAD, PAD - 2), "Reel of Fortune - horizontal item scroll (case-opening row)", font=F_T, fill=FG)
    line1 = (f"pinned close-up 1.30 m fov 40, 960x540  |  {facts.get('travel','?')} cards (3.19 strips) in "
             f"{facts.get('duration','?')} s, curve {facts.get('curve','?')}")
    last = rows[-1]
    line2 = (f"click {facts.get('click','?')} s, overshoot {facts.get('overshoot','?')} card  |  target cell "
             f"{facts.get('target','?')}  |  settled cell {last['cell']} sym {last['sym']} {last['code']}, "
             f"match=True, grants=0")
    d.text((PAD, PAD + 26), line1, font=F_S, fill=DIM)
    d.text((PAD, PAD + 44), line2, font=F_S, fill=DIM)
    d.text((PAD, PAD + 60), "3 of 16 cards visible at a time, one fixed centre marker, "
                            "fade at both ends", font=F_S, fill=DIM)
    d.line([(PAD, PAD + HDR_H - 10), (W - PAD, PAD + HDR_H - 10)], fill=(58, 60, 66))

    y = PAD + HDR_H
    for r in rows:
        src = os.path.join(SCREENS, f"reel_film_{r['k']}", "frame_0001.png")
        im = Image.open(src).convert("RGB").crop(CROP)
        x = PAD
        canvas.paste(im, (x, y))
        d.rectangle([x, y, x + im.width - 1, y + im.height - 1], outline=(72, 76, 84))

        lx = x + im.width + 14
        d.text((lx, y + 6), f"t = {r['t']:.3f} s   k={r['k']}", font=F_L, fill=ACC)
        d.text((lx, y + 26), f"u    = {r['u']:.5f}", font=F_L, fill=FG)
        d.text((lx, y + 44), f"cell = {r['cell']:2d}  (sym {r['sym']} {r['code']})", font=F_L, fill=FG)
        d.text((lx, y + 62), f"travel = {r['travel']:.3f} cards", font=F_L, fill=DIM)
        d.text((lx, y + 80), f"centre factor = {r['centre']:.3f}", font=F_L, fill=DIM)
        if r["k"] == len(rows) - 1:
            d.text((lx, y + 104), "settled: match=True", font=F_L, fill=GOOD)
        else:
            v = ""
            if r["k"] > 0:
                dt = r["t"] - rows[r["k"] - 1]["t"]
                v = f"v = {(r['travel'] - rows[r['k'] - 1]['travel']) / dt:.2f} cards/s"
            d.text((lx, y + 104), v, font=F_L, fill=DIM)
        y += ROW_H + GAP

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    canvas.save(OUT, "PNG", optimize=True)
    size = os.path.getsize(OUT)
    if size > 600_000:
        canvas.convert("P", palette=Image.ADAPTIVE, colors=256).save(OUT, "PNG", optimize=True)
        size = os.path.getsize(OUT)
    print(f"{OUT}  {canvas.width}x{canvas.height}  {size} B")


if __name__ == "__main__":
    main()
