#!/usr/bin/python3
"""Generate the Reel of Fortune card pool for the Alterum mod.

The mod cannot parse JSON at runtime, so the 55-item pool is compiled into
`mod/AlterumModelSwap.Hot/ReelPool.g.cs` at build time from `data/item-pool.json`
(see `data/item-pool.md` for how that JSON was extracted).

    tools/gen-reel-pool.py            # C# table only (no game install needed)
    tools/gen-reel-pool.py --icons    # also extract the icon sheet from install A

Icon sheet: 51 of the 55 items name a Sprite in the game's atlas. This script
crops each one (at the card's native icon size) out of its atlas Texture2D and
packs them into `mod/AlterumModelSwap.Hot/reel_pool_icons.png`, an embedded
resource. The rects go into the table, so the runtime can decode the sheet and
slice per item. The 4 items without a sprite get `IconSheet = -1` and are drawn
with their 3-letter code instead (the brief's explicit fallback).

Category weights are OUR DESIGN, not values read out of the game (the extraction
pass deliberately skipped `*_PlacerSpawnProbabilities`): consumables/utility 100,
LQWeapon 60, spell scrolls 35, HQWeapon 25, occult kit 20, exit fragments 10,
keys 8, debug/test 0 (excluded from the strip).
"""
import argparse
import json
import os
import re
import sys
import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                       # tracks/merged
POOL = os.path.join(ROOT, "data", "item-pool.json")
OUT_CS = os.path.join(ROOT, "mod", "AlterumModelSwap.Hot", "ReelPool.g.cs")
OUT_PNG = os.path.join(ROOT, "mod", "AlterumModelSwap.Hot", "reel_pool_icons.png")
INSTALL = "/mnt/c/AlterumPinned"
ASSETS = os.path.join(INSTALL, "Alterum_Data", "sharedassets0.assets")

# ---- our design table (per item, within its category) -------------------------------
CATEGORY_WEIGHT = {
    "consumable": 100,
    "LQWeapon": 60,
    "scroll": 35,
    "HQWeapon": 25,
    "occult": 20,
    "exit": 10,
    "key": 8,
    "debug": 0,
}

CONSUMABLES = {
    "adrenalinemushroom", "flashlight", "healthflask", "healthmushroom",
    "healthsyringe", "lantern", "rope", "walkietalkie",
}
OCCULT = {"ceremonial_candle", "crucifix", "ritualdagger", "rune", "spellbook"}
DEBUG = {"flashlight_test", "test_lagcomprensationgun"}

# ---- never bettable, never winnable ------------------------------------------------
# Two whole classes of item are excluded from the card pool itself (weight 0, Excluded flag),
# and ReelLogic refuses them as the inserted item too:
#
#   rune*   quest items. The Curse Dispelling Rune is consumed by a quest step; betting it can
#           make that quest impossible. Matched on id/name by the rules below, so a new
#           `*_rune` item in the extraction is caught without a code change.
#   lantern  the quest/special light the player selects with X (the game's dedicated "Lantern
#           Slot", key X, `LanternProperties`, quest `LightTheLantern`). There is exactly one
#           lantern item (`lantern`, prefab "Lantern Handheld"); it is not an ordinary
#           consumable and is never a card.
RUNES = ("rune",)
LANTERN_IDS = {"lantern"}


def exclusion_reason(item):
    iid = item["itemId"].lower()
    name = item["displayName"].lower()
    if iid in LANTERN_IDS or "lantern" in name or "lantern" in iid:
        return "quest-lantern: the X-selected special light, never bet or won"
    if any(r in iid for r in RUNES) or "rune" in name or "curse dispelling" in name:
        return "quest-rune: consumed by the curse-dispelling quest step, never bet or won"
    return ""


def category(item_id):
    if item_id in DEBUG:
        return "debug"
    if item_id.startswith("ss_exit"):
        return "exit"
    if item_id.startswith("ss_"):
        return "scroll"
    if item_id.startswith("key-"):
        return "key"
    if item_id.startswith("axe_"):
        return "HQWeapon"
    if item_id.startswith("dagger_") or item_id.startswith("throwingdagger_"):
        return "LQWeapon"
    if item_id in OCCULT:
        return "occult"
    if item_id in CONSUMABLES:
        return "consumable"
    raise SystemExit("unclassified item id: " + item_id)


STOP = {"spell", "scroll", "handheld", "debug", "of", "the", "and", "a", "part"}
# The extracted display names carry one Cyrillic letter (`Сrucifix`); map the lookalikes
# instead of stripping them, so the code stays readable.
CYR_TO_LAT = {"\u0421": "C", "\u0441": "c", "\u0410": "A", "\u0430": "a",
              "\u0415": "E", "\u0435": "e", "\u041e": "O", "\u043e": "o",
              "\u0420": "P", "\u0440": "p", "\u0425": "X", "\u0445": "x",
              "\u0406": "I", "\u0456": "i", "\u0423": "Y", "\u0443": "y"}


def latinize(s):
    return "".join(CYR_TO_LAT.get(ch, ch) for ch in s)


def code_candidates(name, item_id):
    """3-character card codes, most readable first. Deterministic."""
    name = latinize(name)
    toks = [t for t in re.split(r"[^A-Za-z0-9]+", name) if t]
    # The extracted display names mix separators (`Axe_00_woodsman`); numeric-only
    # tokens are numbering, not words, so they never make it into a code.
    toks = [t for t in toks if not t.isdigit()]
    keep = [t for t in toks if t.lower() not in STOP] or toks or [item_id]

    cands = []

    def add(s):
        s = re.sub(r"[^A-Za-z0-9]", "", s).upper()
        if len(s) >= 3 and s[:3] not in cands:
            cands.append(s[:3])

    if len(keep) >= 3:
        add(keep[0][0] + keep[1][0] + keep[2][0])
        add(keep[0][:2] + keep[1][0])
        add(keep[1][:2] + keep[2][0])
    if len(keep) == 2:
        add(keep[0][0] + keep[1][:2])
        add(keep[0][:2] + keep[1][0])
        add(keep[1][:3])
    if len(keep) == 1:
        add(keep[0][:3])
        add(keep[0][0] + keep[0][2:4])
        add(keep[0][-3:])

    # Identifier fallback: `dagger_black-dagger` -> DBD, `ss_exit_A` -> EXA.
    idtoks = [t for t in re.split(r"[^A-Za-z0-9]+", item_id) if t]
    if len(idtoks) >= 3:
        add(idtoks[0][0] + idtoks[1][0] + idtoks[2][0])
    for t in idtoks:
        add(t[:3])
    if len(idtoks) >= 2:
        add(idtoks[0][0] + idtoks[1][:2])
    add(item_id[:3])
    for t in idtoks:
        for i in range(0, max(1, len(t) - 2)):
            add(t[i:i + 3])
    # Last resort: A00..Z99.
    for a in "ABCDEFGHIJKLMNOPQRSTUVWXYZ":
        for d in range(10):
            add(a + "0" + str(d))
    return cands


def assign_codes(items):
    used = set()
    for it in items:
        for c in code_candidates(it["displayName"], it["itemId"]):
            if c in used:
                continue
            it["code"] = c
            used.add(c)
            break
        else:
            raise SystemExit("no code for " + it["itemId"])
    return used


# ---- icon sheet ---------------------------------------------------------------------
def extract_icons(items, cell=96):
    """Crop every item icon out of the game's own atlases -> one packed sheet."""
    try:
        import UnityPy
        from PIL import Image
    except Exception as e:                                   # noqa: BLE001
        raise SystemExit("icon extraction needs UnityPy + Pillow (system python3): %s" % e)
    if not os.path.exists(ASSETS):
        raise SystemExit("game assets not found: " + ASSETS)

    wanted = {}
    for it in items:
        if it.get("iconSprite"):
            wanted.setdefault(it["iconSprite"], []).append(it)
    print("reading %s for %d sprites" % (os.path.basename(ASSETS), len(wanted)))

    env = UnityPy.load(ASSETS)
    atlas_cache = {}
    got = 0
    for obj in env.objects:
        if obj.type.name != "Sprite":
            continue
        try:
            d = obj.read()
        except Exception:                                    # noqa: BLE001
            continue
        name = getattr(d, "m_Name", None)
        if name not in wanted:
            continue
        if wanted[name][0].get("_got"):
            continue
        rd = d.m_RD
        texptr = rd.texture
        if texptr is None or texptr.m_PathID == 0:
            continue
        key = texptr.m_PathID
        if key not in atlas_cache:
            tex = texptr.read()
            atlas_cache[key] = (getattr(tex, "m_Name", str(key)), tex.image.convert("RGBA"))
        tex_name, img = atlas_cache[key]
        r = rd.textureRect
        x, y, w, h = int(r.x), int(r.y), int(r.width), int(r.height)
        # textureRect is bottom-left origin; PIL is top-down.
        top = img.height - (y + h)
        crop = img.crop((x, top, x + w, top + h))
        for it in wanted[name]:
            it["_icon"] = crop
            it["_iconFrom"] = tex_name
            it["_got"] = True
        got += 1

    missing = sorted(n for n, its in wanted.items() if not its[0].get("_got"))
    if missing:
        print("WARNING: sprite names not found in the atlas: %s" % ", ".join(missing))

    pack = []
    for it in items:
        im = it.get("_icon")
        if im is None:
            it["icon"] = None
            continue
        scale = min(1.0, float(cell) / float(max(im.width, im.height)))
        w = max(1, int(round(im.width * scale)))
        h = max(1, int(round(im.height * scale)))
        pack.append((it, im.resize((w, h), Image.LANCZOS), w, h))

    if not pack:
        raise SystemExit("no icons extracted")

    cols = 8
    rows = (len(pack) + cols - 1) // cols
    sheet = Image.new("RGBA", (cols * cell, rows * cell), (0, 0, 0, 0))
    for i, (it, im, w, h) in enumerate(pack):
        cx = (i % cols) * cell + (cell - w) // 2
        cy = (i // cols) * cell + (cell - h) // 2
        sheet.paste(im, (cx, cy), im)
        it["icon"] = (cx, cy, w, h)
    sheet.save(OUT_PNG)
    print("icon sheet %s %dx%d %d icons (cell %dpx, %d without a sprite)" %
          (os.path.relpath(OUT_PNG, ROOT), sheet.width, sheet.height, len(pack), cell,
           len([i for i in items if i.get("icon") is None])))
    return [i["itemId"] for i in items if i.get("icon") is None]


# ---- C# table -----------------------------------------------------------------------
def cs_str(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def emit(items, now, no_icon):
    total = sum(0 if i.get("exclusion") else CATEGORY_WEIGHT[i["category"]] for i in items)
    usable = [i for i in items if CATEGORY_WEIGHT[i["category"]] > 0 and not i.get("exclusion")]
    excluded = [i for i in items if i.get("exclusion")]
    lines = []
    w = lines.append
    w("// <auto-generated>")
    w("//   by tracks/merged/tools/gen-reel-pool.py from tracks/merged/data/item-pool.json")
    w("//   generated %s" % now)
    w("//   items=%d usable=%d icons=%d (no-sprite items: %s)" %
      (len(items), len(usable), len(items) - len(no_icon), ", ".join(no_icon) or "none"))
    w("//")
    w("//   Category weights below are OUR DESIGN, not values read out of the game:")
    w("//   consumable/utility 100, LQWeapon 60, spell scroll 35, HQWeapon 25, occult kit 20,")
    w("//   exit fragment 10, key 8, debug/test 0 (excluded from the strip).")
    w("//")
    w("//   EXCLUDED FROM THE DECK, weight 0 and Exclusion set (never offered, never won, and")
    w("//   ReelLogic refuses them as the inserted item): %s" % (
        ", ".join("%s (%s)" % (i["itemId"], i["exclusion"].split(":")[0]) for i in excluded) or "none"))
    w("//   DO NOT EDIT BY HAND. Rerun the script; `--icons` also regenerates")
    w("//   reel_pool_icons.png from the game's sprite atlases.")
    w("// </auto-generated>")
    w("")
    w("namespace AlterumModelSwap.Hot;")
    w("")
    w("internal static class ReelPool")
    w("{")
    w("    internal sealed class Item")
    w("    {")
    w("        internal readonly string Id, Name, Code, Category, Sprite, Exclusion;")
    w("        internal readonly int Weight;                       // per item, within its category (our design)")
    w("        internal readonly int IconX, IconY, IconW, IconH;   // rect in the embedded sheet; -1 = no sprite")
    w("        internal Item(string id, string name, string code, string category, int weight, string sprite,")
    w("                      int iconX, int iconY, int iconW, int iconH, string exclusion)")
    w("        {")
    w("            Id = id; Name = name; Code = code; Category = category; Weight = weight; Sprite = sprite;")
    w("            IconX = iconX; IconY = iconY; IconW = iconW; IconH = iconH; Exclusion = exclusion;")
    w("        }")
    w("        internal bool HasIcon => IconX >= 0;")
    w("        internal bool Excluded => Weight <= 0 && !string.IsNullOrEmpty(Exclusion);")
    w("        internal bool Usable => Weight > 0;")
    w("    }")
    w("")
    w("    internal const string Generated = %s;" % cs_str(now))
    w("    // Embedded PNG the icon rects index into (mod/AlterumModelSwap.Hot/reel_pool_icons.png).")
    w("    internal const string IconSheetResource = \"AlterumModelSwap.Hot.reel_pool_icons.png\";")
    w("")
    w("    internal static readonly Item[] All =")
    w("    {")
    for it in items:
        ic = it.get("icon")
        rect = "%d, %d, %d, %d" % ic if ic else "-1, -1, 0, 0"
        name = it["displayName"].replace("\u0421", "C")   # one Cyrillic С in the source data
        weight = 0 if it.get("exclusion") else CATEGORY_WEIGHT[it["category"]]
        w("        new Item(%s, %s, %s, %s, %d, %s, %s, %s)," % (
            cs_str(it["itemId"]), cs_str(name), cs_str(it["code"]), cs_str(it["category"]),
            weight, cs_str(it.get("iconSprite") or ""), rect, cs_str(it.get("exclusion") or "")))
    w("    };")
    w("")
    w("    internal static int TotalWeight()")
    w("    {")
    w("        int t = 0;")
    w("        for (int i = 0; i < All.Length; i++) if (All[i].Weight > 0) t += All[i].Weight;")
    w("        return t;")
    w("    }")
    w("")
    w("    internal static int UsableCount()")
    w("    {")
    w("        int n = 0;")
    w("        for (int i = 0; i < All.Length; i++) if (All[i].Weight > 0) n++;")
    w("        return n;")
    w("    }")
    w("")
    w("    internal static int IndexOf(string id)")
    w("    {")
    w("        for (int i = 0; i < All.Length; i++) if (All[i].Id == id) return i;")
    w("        return -1;")
    w("    }")
    w("}")
    with open(OUT_CS, "w") as f:
        f.write("\n".join(lines) + "\n")
    print("wrote %s  (%d items, total weight %d, %d without a sprite)" %
          (os.path.relpath(OUT_CS, ROOT), len(items), total, len(no_icon)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--icons", action="store_true", help="also re-extract the icon sheet from install A")
    args = ap.parse_args()

    data = json.load(open(POOL))
    items = data["items"]
    assert len(items) == data["count"] == 55, (len(items), data["count"])
    for it in items:
        it["category"] = category(it["itemId"])
        it["exclusion"] = exclusion_reason(it)
    assign_codes(items)
    dup = len(items) - len({i["code"] for i in items})
    if dup:
        raise SystemExit("%d duplicate card codes" % dup)

    if args.icons:
        no_icon = extract_icons(items)
    else:
        for it in items:
            it["icon"] = None
        no_icon = [i["itemId"] for i in items if not i.get("iconSprite")]
        if os.path.exists(OUT_PNG):
            # Reuse the rects already recorded in the .g.cs when the sheet is not rebuilt.
            try:
                old = open(OUT_CS).read()
            except OSError:
                old = ""
            for it in items:
                m = re.search(r'new Item\("%s".*?(-?\d+), (-?\d+), (\d+), (\d+)\)' % re.escape(it["itemId"]), old)
                if m and int(m.group(1)) >= 0:
                    it["icon"] = tuple(int(g) for g in m.groups())

    now = datetime.datetime.now().strftime("%Y-%m-%dT%H:%M:%S")
    emit(items, now, no_icon)

    # Report (also the source of the numbers in the report).
    from collections import Counter
    cats = Counter(i["category"] for i in items)
    print("categories: " + ", ".join("%s=%d(w%d)" % (c, cats[c], CATEGORY_WEIGHT[c]) for c in sorted(cats)))
    print("excluded: " + (", ".join("%s=%s" % (i["itemId"], i["exclusion"].split(":")[0]) for i in items if i.get("exclusion")) or "none"))
    print("codes: " + " ".join("%s=%s" % (i["code"], i["itemId"]) for i in items))


if __name__ == "__main__":
    main()
