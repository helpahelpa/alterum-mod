#!/usr/bin/env python3
"""proportions pass 5 evidence: the garment chest-weight fix and the chest sweep.

All sources are 720x1080 orthographic three-quarter renders of ONE frozen rig, one camera, one
pose per character; every capture in the sweep was taken twice and the second kept (a capture in
the same request as a scale/weight change renders the previous state - see
../proportions-pass4/capture-lag-proof/).

  ladder-fix.png          the strap junction at 3.7x: authored weights vs the fix, 1.70/1.80/1.90
  pokepix-190.png         the visible-skin pixel test at 1.90, authored vs fixed (magenta = skin
                          through the garment, as tools/patcheck5.py counts it)
  ship-3.7x.png           the shipped value (1.90 with the fix) at 3.7x, against pass 4's 1.70
"""
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
D = os.path.join(HERE, "diana")
SHIP = os.path.join(HERE, "ship")
P4 = os.path.join(HERE, "..", "proportions-pass4")


def font(size):
    try:
        return ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", size)
    except Exception:
        return ImageFont.load_default()


def strip(out, title, panels, crop, scale, cols=3, tile_h=None):
    cw, ch = crop[2] - crop[0], crop[3] - crop[1]
    tw, th = int(cw * scale), int(ch * scale)
    rows = (len(panels) + cols - 1) // cols
    pad, title_h, label_h = 10, 26, 18
    w = pad + cols * (tw + pad)
    h = title_h + rows * (th + label_h + pad) + pad
    canvas = Image.new("RGB", (w, h), (18, 20, 24))
    d = ImageDraw.Draw(canvas)
    d.text((pad, 6), title, fill=(238, 238, 238), font=font(14))
    for i, (label, path) in enumerate(panels):
        im = Image.open(path).convert("RGB").crop(crop)
        im = im.resize((tw, th), Image.LANCZOS)
        x = pad + (i % cols) * (tw + pad)
        y = title_h + (i // cols) * (th + label_h + pad)
        canvas.paste(im, (x, y))
        d.text((x + 2, y + th + 2), label, fill=(205, 205, 205), font=font(11))
    canvas.save(os.path.join(HERE, out))
    print(out, canvas.size)


def main():
    crop = (340, 240, 410, 300)   # strap junction / upper chest, 720x1080
    panels = []
    for total in ("1.70", "1.80", "1.90"):
        panels.append((f"{total} authored", os.path.join(D, f"d-{total}-fx0-three.png")))
        panels.append((f"{total} fix", os.path.join(D, f"d-{total}-fx1-three.png")))
    strip("ladder-fix.png",
          "Diana, one frozen pose+camera, 3.7x: chest weights authored (left) vs following the skin (right)",
          panels, crop, 3.7, cols=2)

    strip("pokepix-190.png",
          "1.90, magenta = skin counted through the tank top: authored (311 px) vs fixed (0 px)",
          [("1.90 authored (311 px in the chest)",
            os.path.join(D, "d-1.90-fx0-pokepix.png")),
           ("1.90 fixed (0 px)",
            os.path.join(D, "d-1.90-fx1-pokepix.png"))],
          (330, 225, 470, 320), 4.0, cols=2)

    strip("ship-3.7x.png",
          "Shipped 1.90 (chest weights following the skin) vs pass 4's 1.70, same rig/camera/pose, 3.7x",
          [("pass 4 shipped: 1.70, authored weights", os.path.join(D, "d-1.70-fx0-three.png")),
           ("now shipped: 1.90, fixed weights", os.path.join(SHIP, "ship-rest-three.png"))],
          (330, 225, 470, 330), 3.7, cols=2)


if __name__ == "__main__":
    main()
