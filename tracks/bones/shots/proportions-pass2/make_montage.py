#!/usr/bin/env python3
"""Assemble the proportions-pass2 before/after montages from the propcap renders.

Sources (all 720x1080, orthographic, frozen pose, flat background):
  raw/p145-*.png            chest 1.45 / hip 1.22   (previous shipped values)
  raw/p180-*.png            chest 1.80 / hip 1.50   (pass-2 first try)
  raw/sweep2-c1_55-*.png    chest 1.55 / hip 1.50   (shipped)

Outputs in this directory:
  before-after.png   previous vs shipped, front / three-quarter / side
  iterations.png     first try vs shipped, same three views + chest close-ups
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")
BODY_W, BODY_H = 360, 540
PAD = 12
LABEL_H = 30
TITLE_H = 40

def src(tag, view):
    if tag == "p145":
        return os.path.join(RAW, f"p145-{view}.png")
    if tag == "p180":
        return os.path.join(RAW, f"p180-{view}.png")
    return os.path.join(RAW, f"sweep2-{tag}-{view}.png")

def font(size):
    try:
        return ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", size)
    except Exception:
        return ImageFont.load_default()

def row(images, labels, title):
    w = len(images) * (BODY_W + PAD) + PAD
    h = TITLE_H + BODY_H + LABEL_H + PAD
    canvas = Image.new("RGB", (w, h), (18, 20, 24))
    d = ImageDraw.Draw(canvas)
    d.text((PAD, 10), title, fill=(235, 235, 235), font=font(17))
    x = PAD
    for i, path in enumerate(images):
        im = Image.open(path).convert("RGB").resize((BODY_W, BODY_H), Image.LANCZOS)
        canvas.paste(im, (x, TITLE_H))
        d.text((x + 4, TITLE_H + BODY_H + 4), labels[i], fill=(210, 210, 210), font=font(13))
        x += BODY_W + PAD
    return canvas

def grid(rows_specs, out):
    # each row is (title, [(tag, view, label), ...])
    H = 0
    W = 0
    for title, items in rows_specs:
        H += TITLE_H + len(items[0][2]) if False else 0
    # build each row separately then stack
    imgs = []
    for title, items in rows_specs:
        # items: list of columns, each column is (tag, view, label)
        row_labels = [label for _, _, label in items]
        paths = [src(tag, view) for tag, view, _ in items]
        imgs.append(row(paths, row_labels, title))
    W = max(i.width for i in imgs)
    H = sum(i.height for i in imgs) + PAD
    canvas = Image.new("RGB", (W, H), (18, 20, 24))
    y = 0
    for i in imgs:
        canvas.paste(i, (0, y))
        y += i.height
    canvas.save(out)
    print(out, canvas.size)

# exact-pose pair (same spawn, same frozen snapshot, same camera): previous vs first try
grid([
    ("previous  1.45 / 1.22   (identical camera + frozen pose)",
     [("p145", "front", "previous - front"), ("p145", "three", "previous - 3/4"), ("p145", "side", "previous - side")]),
    ("first try  1.80 / 1.50   (identical camera + frozen pose)",
     [("p180", "front", "1.80 - front"), ("p180", "three", "1.80 - 3/4"), ("p180", "side", "1.80 - side")]),
], os.path.join(HERE, "before-after.png"))

# previous vs shipped; same camera/scene/lighting, pose re-captured after a hot reload
grid([
    ("previous  1.45 / 1.22",
     [("p145", "front", "previous - front"), ("p145", "three", "previous - 3/4"), ("p145", "side", "previous - side")]),
    ("shipped  1.55 / 1.50",
     [("c1_55", "front", "shipped - front"), ("c1_55", "three", "shipped - 3/4"), ("c1_55", "side", "shipped - side")]),
], os.path.join(HERE, "previous-vs-shipped.png"))

# iterations: first try vs shipped (same camera/pose)
grid([
    ("first try  1.80 / 1.50   (visible poke-through at the strap)",
     [("p180", "front", "1.80 - front"), ("p180", "three", "1.80 - 3/4"), ("p180", "side", "1.80 - side")]),
    ("shipped  1.55 / 1.50",
     [("c1_55", "front", "1.55 - front"), ("c1_55", "three", "1.55 - 3/4"), ("c1_55", "side", "1.55 - side")]),
], os.path.join(HERE, "iterations.png"))

# chest close-up: the evidence for stopping at 1.55
z = Image.new("RGB", (2 * 320 + 3 * PAD, 400 + TITLE_H), (18, 20, 24))
d = ImageDraw.Draw(z)
d.text((PAD, 10), "chest close-up, same pose: 1.80 (left) vs 1.55 (right)", fill=(235, 235, 235), font=font(15))
for i, (tag, path) in enumerate([("1.80", "raw/sweep2-c1_80-front.png"), ("1.55", "raw/sweep2-c1_55-front.png")]):
    im = Image.open(os.path.join(HERE, path)).convert("RGB").crop((270, 240, 590, 640)).resize((320, 400))
    z.paste(im, (PAD + i * (320 + PAD), TITLE_H))
z.save(os.path.join(HERE, "chest-closeup.png"))
print("chest-closeup.png", z.size)
