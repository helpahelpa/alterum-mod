#!/usr/bin/env python3
"""Build horizontal filmstrips (6 evenly spaced frames + labels) for each demo MP4."""
import os, subprocess, sys, tempfile, shutil
from PIL import Image, ImageDraw, ImageFont

SRC_DIRS = [
    ("clips", "*.mp4"),
    ("tracks/bones/clips", "*.mp4"),
    ("tracks/emotes/mod/demo", "*.mp4"),
    ("tracks/cosmetics/demo/out", "*.mp4"),
]
WIN = os.environ.get("ALTERUM_MODS_VIDEOS") or os.path.expanduser("~/Videos/AlterumMods")
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "filmstrips")
N = 6
FW = 240  # per-frame width in the strip
GAP = 8
PAD = 12
HDR_H = 26
LBL_H = 18

F_TITLE = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 15)
F_LBL = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 12)

BG = (18, 18, 20)
FG = (232, 232, 236)
DIM = (150, 152, 160)
ACC = (120, 180, 255)


def probe(path):
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height,nb_frames,r_frame_rate:format=duration",
         "-of", "default=nw=1", path],
        capture_output=True, text=True, check=True).stdout
    return dict(l.split("=", 1) for l in out.strip().splitlines() if "=" in l)


def strip_for(src, dst, label):
    p = probe(src)
    w, h, nb, dur = int(p["width"]), int(p["height"]), int(p["nb_frames"]), float(p["duration"])
    fr = p["r_frame_rate"]
    fw = FW
    fh = round(fw * h / w)
    # 6 evenly spaced frame indices, inclusive of first and last
    idx = sorted({round(i * (nb - 1) / (N - 1)) for i in range(N)})
    sel = "+".join(f"eq(n\\,{i})" for i in idx)
    tmp = tempfile.mkdtemp(prefix="fs_")
    try:
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y", "-i", src,
             "-vf", f"select='{sel}',scale={fw}:{fh}:flags=lanczos",
             "-vsync", "0", os.path.join(tmp, "f%03d.png")],
            check=True)
        frames = sorted(os.listdir(tmp))
        if len(frames) != N:
            print(f"  WARN {label}: got {len(frames)} frames, expected {N}", file=sys.stderr)
        W = PAD * 2 + N * fw + (N - 1) * GAP
        H = PAD + HDR_H + fh + LBL_H + PAD
        canvas = Image.new("RGB", (W, H), BG)
        d = ImageDraw.Draw(canvas)
        d.text((PAD, PAD - 4), label, font=F_TITLE, fill=FG)
        meta = f"{w}x{h}  {float(fr.split('/')[0]) / float(fr.split('/')[1]):.2f} fps  {dur:.2f}s  {nb} frames"
        tw = d.textlength(meta, font=F_LBL)
        d.text((W - PAD - tw, PAD + 2), meta, font=F_LBL, fill=DIM)
        d.line([(PAD, PAD + HDR_H - 6), (W - PAD, PAD + HDR_H - 6)], fill=(60, 62, 68))
        y = PAD + HDR_H
        for k, fn in enumerate(frames):
            x = PAD + k * (fw + GAP)
            im = Image.open(os.path.join(tmp, fn)).convert("RGB")
            canvas.paste(im, (x, y))
            d.rectangle([x, y, x + fw - 1, y + fh - 1], outline=(70, 74, 82))
            t = idx[k] / (nb - 1) * dur if nb > 1 else 0.0
            d.text((x + 3, y + fh + 2), f"#{idx[k]}  {t:.2f}s", font=F_LBL, fill=ACC if k in (0, N - 1) else DIM)
        canvas.save(dst, "PNG", optimize=True)
        if os.path.getsize(dst) > 600_000:
            canvas.convert("P", palette=Image.ADAPTIVE, colors=192).save(dst, "PNG", optimize=True)
        return canvas.size, os.path.getsize(dst)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    root = os.path.dirname(os.path.abspath(__file__))
    # Build strips from the normalised, Windows-side set: labels then match the
    # file the user actually opens (fps / duration after re-encoding).
    names = sorted(n for n in os.listdir(WIN) if n.endswith(".mp4"))
    if not names:
        names = sorted(n for n in os.listdir(os.path.join(root, "clips")) if n.endswith(".mp4"))
        src_default = os.path.join(root, "clips")
    else:
        src_default = WIN
    rows = []
    for name in names:
        src = os.path.join(src_default, name)
        dst = os.path.join(OUT_DIR, name[:-4] + ".png")
        dims, size = strip_for(src, dst, name[:-4])
        rows.append((dst, dims, size))
        print(f"{dst}  {dims[0]}x{dims[1]}  {size} B")
    print(f"\n{len(rows)} filmstrips")


if __name__ == "__main__":
    main()
