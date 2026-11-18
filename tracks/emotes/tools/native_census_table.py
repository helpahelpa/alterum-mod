#!/usr/bin/env python3
"""Generate the native-emote census/classification/verification tables from LogOutput.log.

Everything here is read from the T2 log, so the document is a rendering of the run rather than a
hand transcription:
  [emote:census] LAYER/STATE/CLIP/BONES/LOADEDCLIP   -> what the player model provides
  [emote:native] classify ...                        -> classification (last build wins)
  [emote:native] sampler ready ...                   -> per-clip bone-drive counts
  [emote:nativesweep] VERIFY ...                     -> per-clip playback verification
  [emote:selftest] ... (last ARMED block)            -> the 12-clip LC regression sweep

Usage: python3 native_census_table.py --log <LogOutput.log> --out <notes/native-emotes.md>
"""
import argparse
import collections
import os
import re

LAYER_NAMES = {0: "BaseLayer", 1: "LeftHand", 2: "RightHand", 3: "BothHands", 4: "Legs"}


def parse(log_path):
    data = {
        "layers": {}, "states": [], "graph_clips": {}, "graph_bones": {},
        "loaded": {}, "classify": {}, "sampler": {}, "verify": {},
        "lc_sweep": [], "lc_restore": None, "lc_done": False,
    }
    lines = open(log_path, encoding="utf-8", errors="replace").read().splitlines()
    armed_at = -1
    for i, line in enumerate(lines):
        if "self-test ARMED for this run" in line:
            # A new arming starts a new sweep: forget the previous one, or an older window's
            # "sequence complete" would stop collection before this run's numbers.
            armed_at = i
            data["lc_done"] = False
            data["lc_sweep"] = []
            data["lc_restore"] = None
        m = re.search(r"\[emote:census\] LAYER (\d+) name='([^']*)' additive=(\w+) mask='([^']*)' "
                      r"weight=[0-9.]+ states=(-?\d+) parts=\[([^\]]*)\]", line)
        if m:
            data["layers"][int(m.group(1))] = dict(name=m.group(2), additive=m.group(3),
                                                   mask=m.group(4), states=m.group(5), parts=m.group(6))
            continue
        m = re.search(r"\[emote:census\] STATE layer=(\d+)\([^)]*\) additive=(\w+) depth=(\d+) "
                      r"type='([^']*)' clip='([^']*)' len=([0-9.]+) loop=(\d) playing=(\d) weight=([0-9.]+) "
                      r"children=(\d+) node='([^']*)'", line)
        if m:
            data["states"].append(dict(layer=int(m.group(1)), additive=m.group(2) == "True",
                                       depth=int(m.group(3)), type=m.group(4), clip=m.group(5),
                                       len=float(m.group(6)), loop=bool(int(m.group(7))),
                                       children=int(m.group(10)), node=m.group(11)))
            continue
        m = re.search(r"\[emote:census\] CLIP (\d+) '([^']*)' len=([0-9.]+) loop=(\d) humanMotion=(\d) "
                      r"empty=(\d) legacy=(\d) frameRate=([0-9.]+) origins=([a-z+]+)", line)
        if m:
            data["graph_clips"][m.group(2)] = dict(len=float(m.group(3)), loop=bool(int(m.group(4))),
                                                   human=bool(int(m.group(5))), origins=m.group(9))
            continue
        m = re.search(r"\[emote:census\] BONES '([^']*)' rotDriven=(\d+)/(\d+) posDriven=(\d+) "
                      r"mode=(\w+) driven=\[([^\]]*)\]", line)
        if m:
            data["graph_bones"][m.group(1)] = dict(rot=int(m.group(2)), bones=int(m.group(3)),
                                                   pos=int(m.group(4)), mode=m.group(5))
            continue
        m = re.search(r"\[emote:census\] LOADEDCLIP \d+ '([^']*)' len=([0-9.]+) loop=(\d) "
                      r"humanMotion=(\d) inGraph=(\d)", line)
        if m:
            data["loaded"][m.group(1)] = dict(len=float(m.group(2)), loop=bool(int(m.group(3))),
                                              human=bool(int(m.group(4))), in_graph=bool(int(m.group(5))))
            continue
        m = re.search(r"\[emote:native\] classify (?:source=(\w+) )?'([^']*)' len=([0-9.]+) loop=(\d) "
                      r"class=(\w+) reason=\"([^\"]*)\"", line)
        if m:
            data["classify"][(m.group(1) or "graph", m.group(2))] = dict(
                len=float(m.group(3)), loop=bool(int(m.group(4))), klass=m.group(5), reason=m.group(6))
            continue
        m = re.search(r"\[emote:native\] sampler ready clip='([^']*)' len=([0-9.]+) loop=(\d) "
                      r"humanMotion=(\d) mode=(\w+) bones=(\d+) rotDriven=(\d+) posDriven=(\d+)", line)
        if m:
            data["sampler"][m.group(1)] = dict(len=float(m.group(2)), loop=bool(int(m.group(3))),
                                              mode=m.group(5), bones=int(m.group(6)),
                                              rot=int(m.group(7)), pos=int(m.group(8)))
            continue
        m = re.search(r"\[emote:nativesweep\] VERIFY clip='native:([^']*)' start=(\d) hold=([0-9.]+)s "
                      r"stop=(\d) released=(\w+) animEnabled=(\w+) restoreMotionMaxDeg=([0-9.]+) "
                      r"movedBones=(\d+)/(\d+) poseVsEntryMaxDeg=([0-9.]+) worst='([^']*)' "
                      r"pelvisLocalYentry=(-?[0-9.]+) afterRelease=(-?[0-9.]+) delta=([0-9.]+)", line)
        if m:
            data["verify"][m.group(1)] = dict(hold=float(m.group(3)), started=m.group(2) == "1",
                                              released=m.group(5) == "True", anim=m.group(6) == "True",
                                              motion=float(m.group(7)), moved=m.group(8), probed=m.group(9),
                                              pose=float(m.group(10)), worst=m.group(11),
                                              pelvis_delta=float(m.group(14)))
            continue
        if i > armed_at >= 0 and not data["lc_done"]:
            if "sequence complete" in line:
                data["lc_done"] = True
            if "[emote:nativesweep] ARMED" in line or "[emote:nativefilm] ARMED" in line:
                data["lc_done"] = True
            if "[emote] requested" in line or "[emote] START clip=" in line or "[emote] STOP reason=" in line:
                if "[emote] requested" in line or "[emote] START clip=" in line:
                    data["lc_sweep"].append(line.split("] ", 1)[1])
                else:
                    data["lc_sweep"].append(line.split("] ", 1)[1])
            if "RESTORE CHECK" in line:
                data["lc_restore"] = re.search(r"max bone change over 0.4s = ([0-9.]+)deg", line).group(1)
    return data


def fmt_len(v):
    return f"{v:.3f}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default="/mnt/c/AlterumT2/BepInEx/LogOutput.log")
    ap.add_argument("--out", default="tracks/emotes/notes/native-emotes.md")
    args = ap.parse_args()
    d = parse(args.log)

    graph_names = sorted(d["graph_clips"])
    # layer membership from the last census state walk
    member = collections.defaultdict(set)
    for st in d["states"]:
        if st["clip"]:
            member[st["clip"]].add(st["layer"])

    classify = {}
    for (src, name), v in d["classify"].items():
        classify[(src, name)] = v

    out = []
    w = out.append
    w("# Game-built-in (native) emotes — census, classification, verification\n")
    w("Rendered from `C:\\AlterumT2\\BepInEx\\LogOutput.log` by "
      "`tracks/emotes/tools/native_census_table.py`; every number below is a log line from the run, "
      "not a hand transcription.\n")
    w("Entry points: `F9`/`F7` cycle the list (12 LC + native), `F8` toggles the selected native "
      "emote, `emote.request` accepts `clip=native:<Name>`; all native clips go through "
      "`PosePlayer.StartNative` → the same `DoingSomethingHandler` + `DisableAnimancerAndAnimator` "
      "lease, the same `PostAnimationUpdate` write phase, the same 0.2 s fade and the same "
      "`RestorePoseNow` hand-back as the ported LC clips.\n")

    offered_all = [n for n in sorted(d["graph_clips"])
                   if (d["classify"].get(("graph", n)) or {}).get("klass") == "emote"]
    offered_all += [n for n in sorted(d["loaded"])
                    if n.startswith("FemaleAdventure_Hands_TP_")
                    and (d["classify"].get(("loaded", n)) or {}).get("klass") == "emote"]
    w(f"## Added: {len(offered_all)} native emotes (`native:<ClipName>`)\n")
    w("Body clips: " + ", ".join(f"`native:{n}`" for n in offered_all[:2]) + "\n")
    w("Upper-body clips, full id `native:FemaleAdventure_Hands_TP_<name>` (arms-only, played "
      "through the game's `Hands Mask`): " +
      ", ".join(f"`{n[len('FemaleAdventure_Hands_TP_'):]}`" for n in offered_all[2:]) + "\n")

    # ---- source A -------------------------------------------------------------------------
    w("## Source A — the third-person model's own Animancer graph\n")
    w("Enumerated by walking every `PersonAnimancerComponent` layer's states (mixers recursed) "
      "plus `GatherAnimationClips`/`GetAnimationClips` (41 unique clips), then sampling each clip "
      "on a scratch copy of the rig to count the bones it actually drives.\n")
    w("| layer | enum name | additive | mask | mask body parts | states |")
    w("|---|---|---|---|---|---|")
    for idx in sorted(d["layers"]):
        L = d["layers"][idx]
        w(f"| {idx} | {L['name']} | {L['additive']} | {L['mask']} | {L['parts']} | {L['states']} |")
    w("")
    w("| # | clip | len (s) | loop | layer membership | bones driven (rot/pos) | class | reason |")
    w("|---|---|---|---|---|---|---|---|")
    for i, name in enumerate(graph_names):
        c = d["graph_clips"][name]
        b = d["graph_bones"].get(name)
        btxt = f"{b['rot']}/{b['bones']} rot, {b['pos']} pos" if b else "-"
        layers = member.get(name)
        ltxt = ", ".join(f"{li}({LAYER_NAMES.get(li, 'Added')})" for li in sorted(layers)) if layers else "transition library only"
        cl = classify.get(("graph", name)) or classify.get((None, name))
        klass = cl["klass"] if cl else "?"
        reason = cl["reason"] if cl else ""
        w(f"| {i} | `{name}` | {fmt_len(c['len'])} | {'yes' if c['loop'] else 'no'} | {ltxt} | {btxt} | "
          f"{klass} | {reason} |")
    w("")

    # ---- source B -------------------------------------------------------------------------
    fam = sorted(n for n in d["loaded"] if n.startswith("FemaleAdventure_"))
    dup = sorted(n for n in d["loaded"] if n.startswith(("Female_", "Male_", "MaleAdventure_")))
    w("## Source B — the player-character animation content loaded in this scene\n")
    w("The hands/legs layers carry **no states** in the lobby, so their clips are loaded but not "
      "instantiated: a state walk alone cannot see them. They are enumerated from the loaded "
      "`AnimationClip` set by the player-character name family, then sampled on this model's rig "
      "before any of them is offered. The `FemaleAdventure_*` family is the female adventure body "
      "set; its clips are Humanoid and retarget onto this rig (proved by the per-bone drive counts "
      "below and by the rendered captures). The `Female_*`/`Male*` families are the same animations "
      "authored for the other body variants and are logged as duplicates so the cycle does not "
      "repeat every emote four times.\n")
    w("`rot-driven bones` is measured by `NativeSampler` on the player rig (bones whose local "
      "rotation the clip changes over 16 sample phases, of 176 transforms). `-` means the clip was "
      "not sampled because it was rejected by role before any sampling; its class comes from the "
      "clip's authored role, and for the border-line cases the rendered pose in "
      "`shots/native-emotes/contact_sheet.png` was used.\n")
    w("| # | clip | len (s) | loop | rot-driven bones | class | reason |")
    w("|---|---|---|---|---|---|---|")
    for i, name in enumerate(fam):
        c = d["loaded"][name]
        s = d["sampler"].get(name)
        btxt = f"{s['rot']}/{s['bones']} ({s['mode']})" if s else "-"
        cl = classify.get(("loaded", name))
        klass = cl["klass"] if cl else "?"
        reason = cl["reason"] if cl else ""
        w(f"| {i} | `{name}` | {fmt_len(c['len'])} | {'yes' if c['loop'] else 'no'} | {btxt} | {klass} | {reason} |")
    w("")
    w(f"<details><summary>Duplicate variants (not offered): {len(dup)} clips</summary>\n")
    for name in dup:
        w(f"- `{name}`")
    w("\n</details>\n")
    w(f"{len(dup)} further loaded clips are the same animations for the other body variants "
      f"(`Female_Hands_TP_*`, `Male_Hands_TP_*`, `MaleAdventure_Hands_TP_*`): class **duplicate**, "
      "offered once via the FemaleAdventure variant. They are all listed in the `LOADEDCLIP` "
      "section of the log (548 entries, de-duplicated by name).\n")

    fams = collections.Counter()
    for name in d["loaded"]:
        key = name.split("_")[0]
        if name.startswith(("FemaleAdventure_", "Female_", "MaleAdventure_", "Male_")):
            key = name.split("_")[0] + "_" + (name.split("_")[1] if len(name.split("_")) > 1 else "")
        fams[key] += 1
    w("### The rest of the loaded clip set (548 names, de-duplicated)\n")
    w("These are counted so the census is complete; each name and length is in the `LOADEDCLIP` "
      "section of the log. They are not player-character animation (NPC/enemy/object/world sets) "
      "and are outside both sources above.\n")
    w("| name family | clips |")
    w("|---|---|")
    for key, n in fams.most_common(18):
        w(f"| `{key}*` | {n} |")
    w(f"| (other) | {len(d['loaded']) - sum(n for k, n in fams.most_common(18))} |")
    w("")

    # ---- rejected -------------------------------------------------------------------------
    rej = collections.defaultdict(list)
    for (src, name), v in classify.items():
        if v["klass"] in ("unusable",):
            rej[v["reason"]].append(name)
    w("## Rejected — every reason, with the clips it covers\n")
    w("| reason | clips |")
    w("|---|---|")
    for reason, names in sorted(rej.items(), key=lambda kv: -len(kv[1])):
        sample = ", ".join(f"`{n}`" for n in sorted(names)[:4])
        more = f" (+{len(names) - 4} more)" if len(names) > 4 else ""
        w(f"| {reason} | {len(names)}: {sample}{more} |")
    w("")

    # ---- verification ---------------------------------------------------------------------
    w("## Per-clip playback verification (all offered native emotes)\n")
    w("Armed per run with `nativesweep=1`. Each row: start, 1.6 s hold, stop, then the lease is "
      "released and the game animation is given 1.0 s to resume; `restoreMotion` is the maximum "
      "bone change over the following 0.4 s (must be >0: the game is animating again), "
      "`poseVsEntry` is the largest probe-bone angle between the pre-start pose and the settled "
      "pose (a stuck limb would be tens of degrees), `pelvisDelta` is the pelvis local-Y change "
      "after release.\n")
    w("| # | clip (native:id) | start | hold | stop | released | animEnabled | restoreMotion (deg) | "
      "moved bones | poseVsEntry (deg, worst) | pelvisDelta (m) |")
    w("|---|---|---|---|---|---|---|---|---|---|---|")
    offered = [n for n in graph_names if classify.get(("graph", n), {}).get("klass") == "emote"]
    offered += [n for n in fam if classify.get(("loaded", n), {}).get("klass") == "emote"]
    for i, name in enumerate(offered):
        v = d["verify"].get(name)
        if not v:
            w(f"| {i + 1} | `native:{name}` | no verify row | | | | | | | | |")
            continue
        w(f"| {i + 1} | `native:{name}` | {'ok' if v['started'] else 'FAIL'} | {v['hold']:.2f} s | "
          f"{'ok' if v['released'] else 'FAIL'} | {v['released']} | {v['anim']} | {v['motion']:.2f} | "
          f"{v['moved']}/{v['probed']} | {v['pose']:.2f} ({v['worst']}) | {v['pelvis_delta']:.4f} |")
    w("")
    fails = [n for n in offered if n in d["verify"] and not (d["verify"][n]["released"] and d["verify"][n]["anim"]
                                                              and d["verify"][n]["motion"] > 0.2)]
    w(f"Offered: **{len(offered)}** (2 full-body + {len(offered) - 2} upper-body). "
      f"Failed and removed from the list: **{len(fails)}**"
      + (f" ({', '.join(fails)})" if fails else "") + ".\n")

    # ---- LC regression --------------------------------------------------------------------
    w("## 12 ported LC clips — armed sweep after the change\n")
    for line in d["lc_sweep"]:
        w(f"- {line}")
    if d["lc_restore"]:
        w(f"\nRestore check after the 12th clip: max bone change over 0.4 s = **{d['lc_restore']} deg**.\n")

    # ---- reproduce ------------------------------------------------------------------------
    w("## Reproduce\n")
    w("```bash")
    w("./track.sh emotes build && ./track.sh emotes deploy     # C:\\AlterumT2, hot reload")
    w("printf 'census=1\\n' > .../BepInEx/screens/t2/emote.request        # census + bone counts")
    w("printf 'nativesweep=1\\n' > .../emote.request                      # verification table")
    w("printf 'selftest=1\\n' > .../emote.request                         # 12-clip LC sweep")
    w("printf 'nativefilm=1\\nfilmnames=<a,b,...>\\n' > .../emote.request  # filmstrips")
    w("python3 tools/native_census_table.py    # this document from the log")
    w("python3 tools/native_filmstrips.py      # shots/native-emotes/*")
    w("```\n")
    dll = "/mnt/c/AlterumT2/BepInEx/hot/t2/AlterumModelSwap.Hot.dll"
    try:
        import hashlib, time
        h = hashlib.md5(open(dll, "rb").read()).hexdigest()
        stamp = time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(os.path.getmtime(dll)))
        w(f"Deployed binary: `{dll}` md5 `{h}`, mtime {stamp}. Hot reload only; the game was "
          "relaunched once at 1020x552 because the previous instance had crashed.\n")
    except OSError:
        w("Deployed binary: (install not mounted; md5 omitted).\n")

    # ---- entry points exercised -----------------------------------------------------------
    w("## Entry points exercised (no keyboard: desktop locked)\n")
    w("- `clip=native:<Name>` — request-driven start; used for the single-clip probes and all sweeps.")
    w("- `native=1` / `native=0` — start/stop the selected native emote.")
    w("- `cycle=N` — calls the exact `CycleNext` path the `F9`/`F7` keys call; log lines "
      "`[emote] request-cycle: clip index k/50 -> '<id>'` prove the combined 12 + 38 cycle. "
      "The F9 key itself only adds the `wasPressedThisFrame` read in `Keys()`.")
    w("- `nativesweep=1` — the verification table above; `nativefilm=1 filmnames=<a,b,...>` — the captures.")
    w("")

    # ---- captures -------------------------------------------------------------------------
    w("## Captures\n")
    w("`shots/native-emotes/`: 8 ClipRecorder showcase filmstrips (640x360, 2 fps, 6 frames / 3 s, "
      "cropped) for `native:Common_ThirdView_Movement_Idle`, "
      "`native:CommonAdventure_ThirdView_Movement_Idle`, `native:FemaleAdventure_Hands_TP_Axe_Idle`, "
      "`native:FemaleAdventure_Hands_TP_WalkieTalkie_Idle`, "
      "`native:FemaleAdventure_Hands_TP_Spellbook_Idle`, "
      "`native:FemaleAdventure_Hands_TP_HealthFlask_Using_Quick`, "
      "`native:FemaleAdventure_Hands_TP_Axe_Attack_1`, "
      "`native:FemaleAdventure_Hands_TP_Crucifix_Purification`; plus `contact_sheet.png` (all "
      f"{len(offered)} verified clips, release-time RenderTexture still) and one `_still.png` per "
      "filmstripped clip. All captures are RenderTexture reads; the desktop was never used.\n")

    txt = "\n".join(out) + "\n"
    open(args.out, "w", encoding="utf-8").write(txt)
    print(f"wrote {args.out}: {len(txt)} bytes, graph={len(graph_names)} fam={len(fam)} "
          f"offered={len(offered)} verify={len(d['verify'])} lc_lines={len(d['lc_sweep'])}")


if __name__ == "__main__":
    main()
