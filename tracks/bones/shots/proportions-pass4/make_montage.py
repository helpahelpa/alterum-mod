#!/usr/bin/env python3
"""proportions pass 4 evidence: the chest patch at the shipped 1.80 value, before -> after.

All sources are 720x1080 (or 1066x1600) orthographic renders of ONE frozen rig in ONE session,
so pose/camera/light are identical by construction within an image.
"""
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
P3 = os.path.join(HERE, "..", "proportions-pass3", "strap-attrib")
ART = ("355,240,425,300")  # placeholder, replaced per image below


def font(size):
    try:
        return ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", size)
    except Exception:
        return ImageFont.load_default()


def strip(out, title, panels, crop, scale, sub=None):
    """panels: list of (label, path)."""
    cw, ch = crop[2] - crop[0], crop[3] - crop[1]
    tw, th = int(cw * scale), int(ch * scale)
    pad, title_h, label_h = 10, 30, 20
    w = pad + len(panels) * (tw + pad)
    h = title_h + (label_h if sub else 0) + th + pad
    canvas = Image.new("RGB", (w, h), (18, 20, 24))
    d = ImageDraw.Draw(canvas)
    d.text((pad, 7), title, fill=(238, 238, 238), font=font(14))
    x = pad
    y = title_h
    for label, path in panels:
        im = Image.open(path).convert("RGB").crop(crop)
        im = im.resize((tw, th), Image.LANCZOS)
        canvas.paste(im, (x, y))
        d.text((x + 2, y + th + 2), label, fill=(205, 205, 205), font=font(11))
        x += tw + pad
    canvas.save(os.path.join(HERE, out))
    print(out, canvas.size)


def main():
    # 1. attribution: same frozen 3/4 render at chest 1.80, one renderer hidden per panel.
    strip("attrib-patch-object.png",
          "Which object is the patch? chest 1.80 (cage 1.161), one frozen pose, 4x",
          [("all parts", os.path.join(P3, "att-full-three.png")),
           ("tank top hidden", os.path.join(P3, "att-nos-three.png")),
           ("shoulder strap/pouch hidden", os.path.join(P3, "att-nop-three.png")),
           ("body skin hidden", os.path.join(P3, "att-nob-three.png"))],
          (340, 225, 450, 320), 4.0)

    # 2. the value ladder: what each chest value renders at the strap junction.
    # one frozen pose for the whole ladder (the 13:01 session; the pass-4 session has its own pair)
    raw3 = os.path.join(HERE, "..", "proportions-pass3", "strap-attrib")
    sw = os.path.join(HERE, "..", "proportions-pass3", "strap-attrib2")
    strip("value-ladder.png",
          "Chest value vs the strap-junction patch (one frozen pose, 6x): 1.55 and 1.70 render the same, 1.75 up is new",
          [("1.55 (previous)", os.path.join(raw3, "v1_55-full-three.png")),
           ("1.70 (new default)", os.path.join(sw, "v1-70-full-three.png")),
           ("1.75", os.path.join(sw, "v1-75-full-three.png")),
           ("1.78", os.path.join(sw, "v1-78-full-three.png")),
           ("1.80 (was shipped)", os.path.join(sw, "v1-80-full-three.png"))],
          (355, 240, 425, 300), 6.0)

    # 3. before -> after, 4x close-up of the patch area (1066x1600 renders, same pose/camera).
    s = 1066 / 720.0
    crop = tuple(int(round(v * s)) for v in (355, 245, 420, 300))
    strip("patch-before-after-4x.png",
          "Diana, 3/4, one frozen pose and camera: chest 1.80 -> 1.70 at 3.7x",
          [("BEFORE  1.80 (cage 1.161)", os.path.join(HERE, "hi-before-three.png")),
           ("AFTER  1.70 (cage 1.097)", os.path.join(HERE, "hi-after-three.png"))],
          crop, 5.0)

    # 4. the shipped three-quarter panel, before vs after (full 720x1080 tiles).
    strip("three-quarter-before-after.png",
          "Shipped three-quarter panel: chest 1.80 (before) vs 1.70 (after)",
          [("BEFORE  1.80", os.path.join(HERE, "p4-180-full-three.png")),
           ("AFTER  1.70", os.path.join(HERE, "p4-170-full-three.png"))],
          (120, 60, 700, 800), 1.0)


if __name__ == "__main__":
    main()
