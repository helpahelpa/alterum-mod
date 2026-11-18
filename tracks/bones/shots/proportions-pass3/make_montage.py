#!/usr/bin/env python3
"""proportions-pass3 montages (simple, deterministic).

Every source is a 720x1080 orthographic render of the SAME frozen rig (one session per body), so
the camera, pose and lighting are identical by construction inside each body's montage.
"""
import os

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "raw")
BG = (18, 20, 24)
PAD, LABEL_H, TITLE_H, ROWLAB_H = 10, 22, 32, 18
TILE_W, TILE_H = 280, 420


def font(size):
    try:
        return ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", size)
    except Exception:
        return ImageFont.load_default()


def body(out, title, columns, views, crop=None, scale=1.0, col_labels=None, row_labels=None):
    """columns: list of (label, filename-prefix); views: list of view suffixes."""
    if crop:
        cw, ch = crop[2] - crop[0], crop[3] - crop[1]
        tw, th = int(cw * scale), int(ch * scale)
    else:
        tw, th = TILE_W, TILE_H
    w = PAD + len(columns) * (tw + PAD)
    h = TITLE_H + len(views) * (th + LABEL_H + ROWLAB_H + PAD) + PAD
    canvas = Image.new("RGB", (w, h), BG)
    d = ImageDraw.Draw(canvas)
    d.text((PAD, 7), title, fill=(238, 238, 238), font=font(15))
    y = TITLE_H
    for ri, view in enumerate(views):
        rowlab = row_labels[ri] if row_labels else view
        d.text((PAD, y), rowlab, fill=(150, 190, 220), font=font(13))
        y += ROWLAB_H
        x = PAD
        for ci, (label, prefix) in enumerate(columns):
            path = os.path.join(RAW, f"{prefix}-{view}.png")
            im = Image.open(path).convert("RGB")
            im = im.crop(crop).resize((tw, th), Image.LANCZOS) if crop else im.resize((tw, th), Image.LANCZOS)
            canvas.paste(im, (x, y))
            if ri == 0:
                d.text((x + 2, y + th + 2), label, fill=(210, 210, 210), font=font(11))
            x += tw + PAD
        y += th + LABEL_H + PAD
    canvas.save(os.path.join(HERE, out))
    print(out, canvas.size)


def main():
    diana = [("previous 1.55", "fin-base"),
             ("candidate 1.80 leaf-only (failed)", "fin-l180"),
             ("shipped 1.80 (cage 1.161)", "fin-180")]
    freya = [("previous 1.55", "freya-prev"),
             ("candidate 1.80 leaf-only", "freya-leaf180"),
             ("shipped 1.80 (cage 1.161)", "freya-ship")]
    views = ["front", "three", "side"]
    rows = ["front", "three-quarter", "side"]
    body("diana-previous-candidate-shipped.png",
         "Diana - one frozen pose, one camera, one light: previous / failed candidate / shipped",
         diana, views, row_labels=rows)
    body("freya-previous-candidate-shipped.png",
         "Freya - one frozen pose, one camera, one light: previous / candidate / shipped",
         freya, views, row_labels=rows)
    body("strap-closeup.png",
         "Strap / neckline at 4x (same pose/camera): shipped 1.80 (38 verts / 7.2 mm), 1.90 (45 / 7.7), leaf-only 1.80 (65 / 10.3)",
         [("shipped 1.80: 38 inside / 7.21 mm", "fin2-180"), ("1.90: 45 / 7.73 mm", "fin2-190"), ("leaf-only 1.80: 65 / 10.34 mm", "fin2-l180")],
         ["front"], crop=(300, 225, 470, 330), scale=4.0)
    body("chest-closeup.png",
         "Chest at 2.6x: previous vs shipped vs leaf-only 1.80",
         [("previous 1.55", "fin-base"), ("shipped 1.80", "fin-180"), ("leaf-only 1.80", "fin-l180")],
         ["front"], crop=(250, 200, 500, 400), scale=2.6)


if __name__ == "__main__":
    main()
