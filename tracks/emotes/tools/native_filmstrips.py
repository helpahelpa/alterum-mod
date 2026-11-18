#!/usr/bin/env python3
"""Build native-emote filmstrips + a contact sheet from the game's own off-screen captures.

Inputs (written by the mod on the T2 install, no desktop involved):
  <screens>/native_<tag>/frame_%04d.png   ClipRecorder showcase frames (640x360, 2 fps, 6 frames)
  <screens>/emote_native_<clip>.png        one RenderTexture still per clip from the armed sweep

Outputs: ~/assets/monorepo/alterum-data/tracks/emotes/shots/native-emotes/
  <short>.png           filmstrip: 6 crops + per-frame time labels + a header
  contact_sheet.png     every verified native emote still, labelled
  REPORT.txt            what was consumed, frame counts, crop boxes

Usage: python3 native_filmstrips.py [--screens DIR] [--out DIR]
"""
import argparse
import glob
import os
import re
import sys
from PIL import Image, ImageDraw, ImageFont

# The character is centred in both camera types; the crop keeps the body plus a little air.
FRAME_CROP = (205, 6, 450, 359)     # 640x360 showcase frame -> 245x353
STILL_CROP = (230, 70, 545, 715)    # 768x768 verify still -> 315x645


def font(size):
    for p in ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
              "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"):
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()


def short(name):
    n = name
    for pre in ("FemaleAdventure_Hands_TP_",):
        if n.startswith(pre):
            return n[len(pre):] or n
    if n.startswith("CommonAdventure_ThirdView_Movement_"):
        return "Body_Adventure_" + n[len("CommonAdventure_ThirdView_Movement_"):]
    if n.startswith("Common_ThirdView_Movement_"):
        return "Body_Common_" + n[len("Common_ThirdView_Movement_"):]
    return n


def offered(name):
    """Mirror of EmoteRuntime.ClassifyPlayerClip's offer rule, so the sheet cannot include rejects."""
    if name in ("Common_ThirdView_Movement_Idle", "CommonAdventure_ThirdView_Movement_Idle"):
        return True
    pre = "FemaleAdventure_Hands_TP_"
    if not name.startswith(pre):
        return False
    rest = name[len(pre):]
    if rest.endswith("_TakingOut"):
        return False
    return (rest.startswith("DoingSomething")
            or rest.endswith(("_Idle", "_Idle_ThrowPrepare", "_IdleLowered", "_Using",
                              "_Using_Quick", "_Purification", "_Burning",
                              "_Attack_1", "_Attack_2", "_Attack_Throw")))


def catalogue_from_log(log_path):
    """[emote:native] catalogue: offered=38 ... ids=[native:..., ...] -> ordered full clip names."""
    names = []
    try:
        with open(log_path, encoding="utf-8", errors="replace") as f:
            for line in f:
                if "catalogue: offered=" in line and "ids=[" in line:
                    body = line.split("ids=[", 1)[1].rsplit("]", 1)[0]
                    names = [x.strip()[len("native:"):] for x in body.split(",") if x.strip()]
    except OSError:
        pass
    return names


def filmstrip(frames, title, subtitle, out_path, fps):
    crops = []
    for f in frames:
        im = Image.open(f).convert("RGB")
        crops.append(im.crop(FRAME_CROP))
    w, h = crops[0].size
    pad, gap, header, footer = 8, 4, 46, 22
    total_w = pad * 2 + len(crops) * w + (len(crops) - 1) * gap
    img = Image.new("RGB", (total_w, header + h + footer + pad), (24, 24, 24))
    d = ImageDraw.Draw(img)
    d.text((pad, 6), title, fill=(255, 255, 255), font=font(17))
    d.text((pad, 26), subtitle, fill=(170, 200, 220), font=font(13))
    for i, c in enumerate(crops):
        x = pad + i * (w + gap)
        img.paste(c, (x, header))
        d.rectangle([x, header, x + w - 1, header + h - 1], outline=(80, 80, 80))
        d.text((x + 4, header + h + 3), f"{i + 1}/{len(crops)}  t={i / fps:.1f}s",
               fill=(200, 200, 200), font=font(12))
    img.save(out_path)
    return img.size


def contact_sheet(stills, out_path):
    cell_w, cell_h = 165, 310
    label_h = 34
    cols = 6
    rows = (len(stills) + cols - 1) // cols
    pad, gap, header = 10, 6, 52
    W = pad * 2 + cols * cell_w + (cols - 1) * gap
    H = header + rows * (cell_h + label_h + gap)
    img = Image.new("RGB", (W, H), (22, 22, 22))
    d = ImageDraw.Draw(img)
    d.text((pad, 8), f"Alterum native emotes - verified sweep stills ({len(stills)} clips)",
           fill=(255, 255, 255), font=font(20))
    d.text((pad, 32), "each still is the release-time RenderTexture capture at 0.8 s into the clip; "
                      "all passed released/restore checks", fill=(170, 200, 220), font=font(12))
    for i, (name, path) in enumerate(stills):
        r, c = divmod(i, cols)
        x = pad + c * (cell_w + gap)
        y = header + r * (cell_h + label_h + gap)
        im = Image.open(path).convert("RGB").crop(STILL_CROP)
        im = im.resize((cell_w, cell_h), Image.LANCZOS)
        img.paste(im, (x, y))
        d.rectangle([x, y, x + cell_w - 1, y + cell_h - 1], outline=(70, 70, 70))
        words = short(name).replace("_", " ")
        lines = [words[j:j + 24] for j in range(0, len(words), 24)][:2]
        for k, ln in enumerate(lines):
            d.text((x + 2, y + cell_h + 2 + k * 14), ln, fill=(215, 215, 215), font=font(11))
    img.save(out_path)
    return img.size


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--screens", default="/mnt/c/AlterumT2/BepInEx/screens/t2")
    ap.add_argument("--out", default=os.path.join(os.path.expanduser("~/assets/monorepo/alterum-data"),
                                                  "tracks", "emotes", "shots", "native-emotes"))
    args = ap.parse_args()
    screens = os.path.abspath(args.screens)
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)
    report = []

    def pairs():
        # map frame dirs (tag sanitized) to the emote still with the same clip name
        stills = {os.path.basename(p)[len("emote_native_"):-4]: p
                  for p in glob.glob(os.path.join(screens, "emote_native_*.png"))
                  if re.search(r"_r\d+_f\d+_", os.path.basename(p)) is None}
        return stills

    stills = pairs()
    catalogue = catalogue_from_log(os.path.join(screens, "..", "..", "LogOutput.log"))
    dirs = sorted(glob.glob(os.path.join(screens, "native_*")))
    dirs = [d for d in dirs if os.path.isdir(d) and glob.glob(os.path.join(d, "frame_*.png"))]
    report.append(f"screens={screens}")
    report.append(f"frame dirs={len(dirs)} stills={len(stills)} catalogue={len(catalogue)}")
    for d in dirs:
        frames = sorted(glob.glob(os.path.join(d, "frame_*.png")))
        tag = os.path.basename(d)[len("native_"):]
        idx = -1
        m = re.match(r"(\d+)_", tag)
        if m:
            idx = int(m.group(1))
        full = catalogue[idx] if 0 <= idx < len(catalogue) else None
        title = f"native:{full or tag}"
        classname = "upper body (arms-only layer mask)" if full and "_Hands_TP_" in full else "full body"
        subtitle = f"{classname} | ClipRecorder showcase 640x360 @ 2 fps | 6 frames / 3 s | crop x{FRAME_CROP}"
        name = short(full or tag)
        out_png = os.path.join(out, f"{name}.png")
        size = filmstrip(frames, title, subtitle, out_png, 2.0)
        report.append(f"  {title}  frames={len(frames)}  -> {os.path.basename(out_png)} {size[0]}x{size[1]}")
        if full:
            Image.open(stills[full]).convert("RGB").crop(STILL_CROP).save(
                os.path.join(out, f"{name}_still.png"))

    sheet = [(name, p) for name, p in sorted(stills.items()) if offered(name)]
    if sheet:
        size = contact_sheet(sheet, os.path.join(out, "contact_sheet.png"))
        report.append(f"contact_sheet.png {size[0]}x{size[1]} cells={len(sheet)}")

    with open(os.path.join(out, "REPORT.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(report) + "\n")
    print("\n".join(report))


if __name__ == "__main__":
    sys.exit(main())
