#!/usr/bin/env python3
"""Alternate LC and port at matched phases, one clip after another: LC frame, port frame, LC, port...
Frames are the same equal-scale panels as the sheets, so the cut reads as a pose A/B."""
import glob, json, os, shutil, subprocess, sys
from PIL import Image, ImageDraw, ImageFont
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compose2 import windows, panel, font, LC_BODY_H, PORT_BODY_H, PANEL_W, PANEL_H, ONESHOT

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, ".."))
TMP = os.path.join(OUT, ".video_frames")
FPS = 4

def main():
    ph = json.load(open(os.path.join(HERE, "..", "phases.json")))
    shutil.rmtree(TMP, ignore_errors=True); os.makedirs(TMP)
    n = 0
    fb, fl = font(20, True), font(14)
    W, H = PANEL_W * 2 + 40, PANEL_H + 62
    for clip, v in ph.items():
        win = windows(clip)
        lcf, ptf = win[0], win[1]
        phases, picks = v["phases"], v["frames"]
        sel = list(range(0, len(phases), max(1, len(phases) // 8)))[:8]
        # title card
        for _ in range(FPS):
            im = Image.new("RGB", (W, H), (14, 15, 19)); d = ImageDraw.Draw(im)
            d.text((20, H // 2 - 34), clip, font=font(30, True), fill=(235, 235, 240))
            d.text((20, H // 2 + 8), "LC reference  |  port   (matched normalised clip time, equal standing height)",
                   font=fl, fill=(160, 162, 172))
            im.save(os.path.join(TMP, f"f{n:05d}.png")); n += 1
        for k, i in enumerate(sel):
            lc_i = picks[i]
            if clip in ONESHOT:
                lc_i = picks[min(range(len(phases)), key=lambda j: abs(phases[j] - 1.0))]
            a = panel(Image.open(lcf[lc_i]).convert("RGB"), LC_BODY_H, win[2], win[3])
            b = panel(Image.open(ptf[i]).convert("RGB"), PORT_BODY_H, win[4], win[5])
            for side, img, lab in ((0, a, "LC"), (1, b, "PORT")):
                fr = Image.new("RGB", (W, H), (14, 15, 19)); d = ImageDraw.Draw(fr)
                fr.paste(img, (20, 42))
                note = "held last frame" if (clip in ONESHOT and side == 0) else f"phase {phases[i]:.3f}"
                d.text((20, 12), f"{clip}   {lab}   {note}", font=fl, fill=(230, 230, 236))
                fr.save(os.path.join(TMP, f"f{n:05d}.png")); n += 1
    out = os.path.join(OUT, "lc_vs_port.mp4")
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i",
                    os.path.join(TMP, "f%05d.png"), "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                    "-c:v", "libx264", "-preset", "slow", "-crf", "21", "-pix_fmt", "yuv420p",
                    "-movflags", "+faststart", out], check=True)
    shutil.rmtree(TMP, ignore_errors=True)
    print(out, os.path.getsize(out))

if __name__ == "__main__": main()
