#!/usr/bin/env python3
"""Verify the Unit D evidence: the reel idle-preview plate carries no text, and the odds the
plate used to print are still available as a diagnostic.

What changed (and what this guards):

  * `ReelArt.BuildPreviewPlate` used to draw, on its 400x170 plate, a disclaimer line
    `ITEM PREVIEW - NOT A RESULT` at y=112 and the deck's odds on two lines from y=128. Both
    drawing passes are gone. The three item cards, the plate size, the card boxes and the quad the
    plate is hung on are unchanged, and the rows the text occupied are left as plain plate
    background.
  * `PreviewOdds(deck)` still has to be computed and logged, the deck census has to survive, and
    neither may have moved into the artwork.

So the checks are mechanical:

  L1  a `[reel:preview] plate 400x170 px cards=[...] text=none odds='...' deck=[...]` line exists,
      names exactly three cards, and its odds numbers equal its census numbers (64 cells,
      blank 22 / item 21 / damage 10 / debuff 6 / double 5 = the shipped canonical deck);
  L2  the same line, and the idle `[reel:preview] show reason=idle ...` line, carry the odds and
      the census - removing the drawn text must not remove the only evidence of the composition;
  L3  no line claims plate text any more: `label='ITEM PREVIEW...`, `lines=[`, and the rendered
      two-line odds (`64: BLANK 22 ...`) are all gone from the log;
  P1  the dumped plate `reel_preview.png` is 400x170 with the 2 px GoldDim trim on all four sides;
  P2  the three card boxes are where they were (x 58/156/254, y 4, 88x104, gap 10) and the gaps
      between and around them are plate background - i.e. card geometry did not move to make room;
  P3  the former text band (rows 108..167, columns 2..397) is pure MaroonDeep: no label glyph, no
      gold odds line, no ink shadow - the artwork is background, not a faded or half-erased text
      block, and not a redesign of the plate;
  P4  the artifact's sha256 matches the manifest, when the manifest pins one.

Inputs are a scenario DIRECTORY per case:

    <case>/manifest.json        {"case": "plate-clean", "log": "log.txt",
                                 "artifacts": [{"path": "reel_preview.png", "sha256": "..."}]}
    <case>/log.txt              the log export (host prefix and `HH:MM:SS` stamps are tolerated)
    <case>/reel_preview.png     the plate dump (`[reel:dump] ... reel_preview.png`)

The plate dump is the procedurally drawn canvas, not a screenshot, so P1..P3 hold on any machine
(see also `--png`, which checks a bare plate file with no log).

Usage:
    verify_evidence.py --case DIR [DIR ...]
    verify_evidence.py --png PATH [PATH ...]
    verify_evidence.py --self-test

Exit: 0 all PASS, 1 otherwise.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
import tempfile
import zlib
from dataclasses import dataclass, field
from pathlib import Path

# --------------------------------------------------------------------------------------------
# the drawing contract, read off ReelArt.cs
# --------------------------------------------------------------------------------------------

PLATE_W, PLATE_H = 400, 170
CARD_W, CARD_H, CARD_GAP, CARD_Y = 88, 104, 10, 4
# Three cards, 88 wide with a 10 px gap, centred on a 400 px plate -> first card at x=58.
CARD_X = [(PLATE_W - (3 * CARD_W + 2 * CARD_GAP)) // 2 + i * (CARD_W + CARD_GAP) for i in range(3)]
TRIM = 2

MAROON_DEEP = (0x34, 0x10, 0x19, 255)   # ReelArt.MaroonDeep: the plate fill
GOLD_DIM = (0xB0, 0x82, 0x2E, 255)      # ReelArt.GoldDim: the 2 px plate trim
CREAM = (0xF7, 0xEF, 0xC0, 255)         # the removed label's colour
GOLD = (0xFF, 0xD2, 0x4A, 255)          # the removed odds lines' colour
TEXT_INKS = {CREAM, GOLD}

# The deck the booth composes (ReelDeck: 64 cells, canonical weights). The odds in the log have to
# describe exactly this deck; a plate without text must not lose that evidence.
CANON = {"blank": 22, "item": 21, "damage": 10, "debuff": 6, "double": 5}
CANON_CELLS = 64

LINE_TS = re.compile(r"^\s*(?:\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\s+)?")
HOST_PREFIX = re.compile(r"^\[(?:Info|Warning|Error|Debug)\s*:[^\]]*\]\s*")

ODDS_RE = re.compile(r"^(?P<cells>\d+): blank (?P<blank>\d+) \u00b7 item (?P<item>\d+) "
                     r"\u00b7 damage (?P<damage>\d+) \u00b7 debuff (?P<debuff>\d+) "
                     r"\u00b7 double (?P<double>\d+)$")
CENSUS_RE = re.compile(r"cells=(?P<cells>\d+) blank=(?P<blank>\d+) item=(?P<item>\d+) "
                       r"damage=(?P<damage>\d+) debuff=(?P<debuff>\d+) double=(?P<double>\d+)")

PATTERNS = {
    # The plate build line, in the exact shape the fix emits.
    "plate": re.compile(
        r"\[reel:preview\] plate (?P<w>\d+)x(?P<h>\d+) px cards=\[(?P<cards>[^\]]*)\] "
        r"text=(?P<text>\S+) odds='(?P<odds>[^']*)' deck=\[(?P<deck>[^\]]*)\]$"),
    # The idle show line. Its tail is optional so a partial export still parses.
    "show": re.compile(
        r"\[reel:preview\] show reason=(?P<why>\S+) items=\[(?P<items>[^\]]*)\] "
        r"text=(?P<text>\S+) odds='(?P<odds>[^']*)' deck=\[(?P<deck>[^\]]*)\]"),
    # Diagnostics the fix must have removed from the plate note / show line.
    "legacy_label": re.compile(r"\[reel:preview\] (?:plate|show) .*label='"),
    "legacy_lines": re.compile(r"\[reel:preview\] (?:plate|show) .*lines=\["),
    # The old two-line rendered odds text, anywhere in a reel:preview line.
    "legacy_rendered_odds": re.compile(r"\[reel:preview\] .*\d+: BLANK \d+ \u00b7 ITEM \d+ \u00b7 DMG \d+"),
    # Fallback, matched last: a plate line that exists but lost fields (the strict pattern above
    # then never fired). It exists to say WHICH field went missing instead of "no plate line".
    "plate_partial": re.compile(r"\[reel:preview\] plate \d+x\d+ px(?P<rest>.*)$"),
}

CARD_ENTRY_RE = re.compile(r"^[A-Z0-9_]{1,8}=[A-Za-z0-9_.:-]+$")


@dataclass
class Event:
    kind: str
    pos: int
    body: str
    fields: dict


def normalize(raw: str) -> str:
    body = HOST_PREFIX.sub("", LINE_TS.sub("", raw.rstrip()))
    return body.strip()


def parse_log(text: str):
    events = []
    for pos, raw in enumerate(text.splitlines()):
        body = normalize(raw)
        if "[reel:preview]" not in body:
            continue
        for kind, pat in PATTERNS.items():
            m = pat.search(body)
            if m:
                events.append(Event(kind, pos, body, m.groupdict()))
                break
    return events


def kinds(events, kind):
    return [e for e in events if e.kind == kind]


# --------------------------------------------------------------------------------------------
# minimal PNG reader (RGBA8 / RGB8, non-interlaced) - no Pillow dependency on the reviewer's box
# --------------------------------------------------------------------------------------------

def decode_png(data: bytes):
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    pos, idat, pal, trns = 8, bytearray(), None, None
    width = height = depth = color = interlace = None
    while pos + 8 <= len(data):
        length, ctype = struct.unpack(">I4s", data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if ctype == b"IHDR":
            width, height, depth, color, _comp, _filt, interlace = struct.unpack(">IIBBBBB", chunk)
        elif ctype == b"IDAT":
            idat += chunk
        elif ctype == b"PLTE":
            pal = chunk
        elif ctype == b"tRNS":
            trns = chunk
        elif ctype == b"IEND":
            break
    if depth != 8:
        raise ValueError(f"unsupported bit depth {depth}")
    if interlace not in (0, None):
        raise ValueError("interlaced PNG not supported")
    channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}.get(color)
    if channels is None:
        raise ValueError(f"unsupported colour type {color}")
    raw = zlib.decompress(bytes(idat))
    stride = width * channels
    rows, prev = [], bytearray(stride)
    p = 0
    for _y in range(height):
        ftype = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if ftype == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 0xFF
        elif ftype == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ftype == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ftype == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        elif ftype != 0:
            raise ValueError(f"bad PNG filter {ftype}")
        rows.append(bytes(line))
        prev = line
    px = []
    for line in rows:
        row = []
        for x in range(width):
            s = x * channels
            if color == 6:
                row.append(tuple(line[s:s + 4]))
            elif color == 2:
                row.append((line[s], line[s + 1], line[s + 2], 255))
            elif color == 0:
                v = line[s]
                row.append((v, v, v, 255))
            elif color == 4:
                v = line[s]
                row.append((v, v, v, line[s + 1]))
            else:                                    # palette
                i = line[s]
                row.append((pal[i * 3], pal[i * 3 + 1], pal[i * 3 + 2],
                            trns[i] if trns and i < len(trns) else 255))
        px.append(row)
    return width, height, px


# --------------------------------------------------------------------------------------------
# report
# --------------------------------------------------------------------------------------------

@dataclass
class Report:
    case: str
    failures: list = field(default_factory=list)
    warnings: list = field(default_factory=list)
    checks: int = 0

    def need(self, ok, msg):
        self.checks += 1
        if not ok:
            self.failures.append(msg)
        return ok

    def warn(self, ok, msg):
        if not ok:
            self.warnings.append(msg)
        return ok

    @property
    def verdict(self):
        return "PASS" if not self.failures else "FAIL"


# --------------------------------------------------------------------------------------------
# log checks
# --------------------------------------------------------------------------------------------

def parse_odds(text: str):
    m = ODDS_RE.match((text or "").strip())
    return {k: int(v) for k, v in m.groupdict().items()} if m else None


def parse_census(text: str):
    m = CENSUS_RE.search(text or "")
    return {k: int(v) for k, v in m.groupdict().items()} if m else None


def check_deck_numbers(rep: Report, label: str, odds: dict, census: dict):
    if not rep.need(odds is not None, f"{label}: the odds string is missing or malformed "
                                      "(expected '64: blank 22 · item 21 · ...')"):
        return
    rep.need(census is not None, f"{label}: the deck census is missing from the line")
    rep.need(odds["cells"] == CANON_CELLS,
             f"{label}: odds describe {odds['cells']} cells, the deck is {CANON_CELLS}")
    for key, want in CANON.items():
        rep.need(odds[key] == want,
                 f"{label}: odds say {key}={odds[key]}, the canonical deck has {key}={want}")
    rep.need(sum(odds[k] for k in CANON) == odds["cells"],
             f"{label}: the per-type odds do not add up to {odds['cells']} cells")
    if census:
        for key in ("cells",) + tuple(CANON):
            rep.need(census[key] == odds[key],
                     f"{label}: census {key}={census[key]} but the odds say {odds[key]} - the "
                     "diagnostic and the composed deck disagree")


def check_log(rep: Report, events, manifest):
    plates = kinds(events, "plate")
    shows = kinds(events, "show")
    partial = kinds(events, "plate_partial")
    if not rep.need(bool(plates), "no '[reel:preview] plate 400x170 px cards=[...] text=none "
                                  "odds='...' deck=[...]' line: the plate was never built, or the "
                                  "line lost the cards/odds/census fields"):
        for e in partial:
            missing = [name for name, probe in (("cards=[...]", "cards=["),
                                                ("text=", "text="),
                                                ("odds='...'", "odds='"),
                                                ("deck=[...]", "deck=["))
                       if probe not in e.body]
            rep.need(False, f"plate line {e.pos + 1} is missing {', '.join(missing) or 'a field'} "
                            f"that L1/L2 need: {e.body[:180]!r}")
    for e in plates:
        f = e.fields
        tag = f"plate line {e.pos + 1}"
        rep.need((f["w"], f["h"]) == (str(PLATE_W), str(PLATE_H)),
                 f"{tag}: plate is {f['w']}x{f['h']}, expected {PLATE_W}x{PLATE_H} - the texture "
                 "size is part of the contract and must not shrink with the text")
        cards = [c for c in f["cards"].split(",") if c]
        rep.need(len(cards) == 3, f"{tag}: {len(cards)} cards logged ({f['cards']!r}); the idle "
                                  "preview is three item cards")
        for c in cards:
            rep.need(bool(CARD_ENTRY_RE.match(c)),
                     f"{tag}: card entry {c!r} is not 'CODE=item_id'")
        rep.need(f["text"] == "none",
                 f"{tag}: text={f['text']!r} - the plate draws no text any more")
        check_deck_numbers(rep, tag, parse_odds(f["odds"]), parse_census(f["deck"]))

    for e in shows:
        f = e.fields
        tag = f"show line {e.pos + 1}"
        if f["why"] == "idle":
            rep.need(f["text"] == "none", f"{tag}: text={f['text']!r} on the idle preview")
            check_deck_numbers(rep, tag, parse_odds(f["odds"]), parse_census(f["deck"]))
            items = [i for i in f["items"].split(",") if i]
            rep.need(len(items) == 3, f"{tag}: items=[{f['items']}] - the idle preview shows three")

    # A plate/show line that still prints the label or the rendered two-line odds is the pre-fix
    # build: the artwork text was not actually removed, only hidden.
    for kind, what in (("legacy_label", "label='...'"),
                       ("legacy_lines", "lines=[...]"),
                       ("legacy_rendered_odds", "the rendered two-line odds ('64: BLANK 22 ...')")):
        for e in kinds(events, kind):
            rep.need(False, f"line {e.pos + 1} still logs {what} for the preview plate: "
                            f"{e.body[:160]!r}")
        if not kinds(events, kind):
            rep.checks += 1

    # The removed disclaimer must not survive in any form that the plate could still show.
    disclaimer = "ITEM PREVIEW"
    hits = [e for e in events if disclaimer in e.body.upper() and "NOT A RESULT" in e.body.upper()]
    rep.need(not hits, f"the disclaimer text is still logged on the preview path "
                       f"({hits[0].body[:120]!r})" if hits else "")

    if manifest.get("require_show", True):
        rep.warn(bool(shows), "no idle show line in the export - L2 evidence is partial (the plate "
                              "line still carries the odds and the census)")


# --------------------------------------------------------------------------------------------
# plate checks
# --------------------------------------------------------------------------------------------

def check_plate(rep: Report, px, width, height, tag="plate"):
    if not rep.need((width, height) == (PLATE_W, PLATE_H),
                    f"{tag}: {width}x{height}, expected {PLATE_W}x{PLATE_H}"):
        return
    # P1 - the 2 px GoldDim trim on all four sides.
    for y in (0, 1, PLATE_H - 2, PLATE_H - 1):
        bad = [x for x in range(width) if px[y][x][:4] != GOLD_DIM]
        rep.need(not bad, f"{tag}: trim row y={y} is not GoldDim at x={bad[:4]}")
    for x in (0, 1, PLATE_W - 2, PLATE_W - 1):
        bad = [y for y in range(height) if px[y][x][:4] != GOLD_DIM]
        rep.need(not bad, f"{tag}: trim column x={x} is not GoldDim at y={bad[:4]}")

    # P2 - the three card boxes sit where they always did, and only there.
    band = range(CARD_Y, CARD_Y + CARD_H)
    for i, x0 in enumerate(CARD_X):
        filled = sum(1 for y in band for x in range(x0, x0 + CARD_W)
                     if px[y][x][:4] != MAROON_DEEP)
        rep.need(filled >= 0.5 * CARD_W * CARD_H,
                 f"{tag}: card {i + 1} at x={x0}..{x0 + CARD_W - 1} y={CARD_Y}.."
                 f"{CARD_Y + CARD_H - 1} is mostly background ({filled} of "
                 f"{CARD_W * CARD_H} px drawn) - the card moved or vanished")
        # The card's own first row is the black card edge (ReelArt.DrawCard), not plate background.
        rep.need(px[CARD_Y][x0 + CARD_W // 2][:3] != MAROON_DEEP[:3],
                 f"{tag}: card {i + 1} does not start at y={CARD_Y}")
    gaps = [(CARD_X[0] - 2, CARD_X[0] - 1)]
    for i in range(2):
        gaps.append((CARD_X[i] + CARD_W, CARD_X[i + 1] - 1))
    gaps.append((CARD_X[-1] + CARD_W, PLATE_W - TRIM - 1))
    for lo, hi in gaps:
        bad = [(x, y) for y in band for x in range(lo, hi + 1)
               if px[y][x][:4] != MAROON_DEEP]
        rep.need(not bad, f"{tag}: columns {lo}..{hi} between/around the cards are not background "
                          f"(first: {bad[:3]}) - the card boxes moved")
    above = [(x, y) for y in range(TRIM, CARD_Y) for x in range(TRIM, PLATE_W - TRIM)
             if px[y][x][:4] != MAROON_DEEP]
    rep.need(not above, f"{tag}: rows above the cards are no longer background ({above[:3]})")

    # P3 - the former text band is plate background: no glyph, no halo, no faded remnant.
    band_lo, band_hi = CARD_Y + CARD_H, PLATE_H - TRIM - 1
    ink = [(x, y, px[y][x][:4]) for y in range(band_lo, band_hi + 1)
           for x in range(TRIM, PLATE_W - TRIM) if px[y][x][:4] != MAROON_DEEP]
    if ink:
        colours = sorted({c for _x, _y, c in ink})
        rep.need(False,
                 f"{tag}: {len(ink)} px in the former text band (rows {band_lo}..{band_hi}) are not "
                 f"plate background; colours={colours[:4]} first={ink[:3]} - the label/odds were "
                 "cleared to background, not redrawn")
        rep.need(not any(c in TEXT_INKS for c in colours),
                 f"{tag}: the band still contains the removed text colours (Cream/Gold)")
    else:
        rep.checks += 2


def check_artifact(rep: Report, case_dir: Path, manifest):
    arts = manifest.get("artifacts") or []
    if not arts:
        rep.warn(False, "manifest pins no artifact - the plate itself was not checked (P1..P4)")
        return
    for art in arts:
        rel = art.get("path")
        if not rel:
            rep.need(False, "artifact entry without a path")
            continue
        path = case_dir / rel
        if not rep.need(path.exists(), f"artifact '{rel}' is missing"):
            continue
        data = path.read_bytes()
        want = art.get("sha256")
        if want:
            got = hashlib.sha256(data).hexdigest()
            rep.need(got == want, f"artifact '{rel}' sha256 {got[:12]}... != {want[:12]}... "
                                  "(the chain of custody does not match the manifest)")
        if path.suffix.lower() != ".png":
            continue
        try:
            width, height, px = decode_png(data)
        except Exception as e:
            rep.need(False, f"artifact '{rel}' is not a readable PNG: {e}")
            continue
        check_plate(rep, px, width, height, tag=f"artifact '{rel}'")
        if art.get("cards") is not None:
            rep.need(art["cards"] == len(CARD_X),
                     f"artifact '{rel}' manifest says cards={art['cards']}, expected {len(CARD_X)}")


# --------------------------------------------------------------------------------------------
# driver
# --------------------------------------------------------------------------------------------

def load_case(case_dir: Path):
    manifest_path = case_dir / "manifest.json"
    if not manifest_path.exists():
        raise SystemExit(f"error: {manifest_path} is missing")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("case") != "plate-clean":
        raise SystemExit(f"error: {manifest_path}: unknown case {manifest.get('case')!r} "
                         "(known: plate-clean)")
    log_path = case_dir / manifest.get("log", "log.txt")
    if not log_path.exists():
        raise SystemExit(f"error: {log_path} is missing (needed by {manifest_path})")
    return manifest, log_path.read_text(encoding="utf-8", errors="replace")


def verify_case(case_dir: Path) -> Report:
    manifest, log_text = load_case(case_dir)
    rep = Report(manifest.get("case", "plate-clean"))
    check_log(rep, parse_log(log_text), manifest)
    check_artifact(rep, case_dir, manifest)
    return rep


def verify_png(path: Path) -> Report:
    rep = Report(f"png:{path.name}")
    try:
        width, height, px = decode_png(path.read_bytes())
    except Exception as e:
        rep.need(False, f"{path}: not a readable PNG: {e}")
        return rep
    check_plate(rep, px, width, height, tag=str(path))
    return rep


def print_report(rep: Report, stream=sys.stdout):
    stream.write(f"VERDICT {rep.verdict} {rep.case} ({rep.checks} checks)\n")
    for msg in rep.failures:
        stream.write(f"  FAIL: {msg}\n")
    for msg in rep.warnings:
        stream.write(f"  WARN: {msg}\n")


# --------------------------------------------------------------------------------------------
# self-test: fabricated good/bad scenario directories
# --------------------------------------------------------------------------------------------

GOOD_PLATE_LINE = (
    "[Info   :Alterum Model Swap (host)] [reel:preview] plate 400x170 px "
    "cards=[TFD=ss_thirstfordeath,DRS=dagger_rusty-stiletto,EXB=ss_exit_B] text=none "
    "odds='64: blank 22 \u00b7 item 21 \u00b7 damage 10 \u00b7 debuff 6 \u00b7 double 5' "
    "deck=[cells=64 blank=22 item=21 damage=10 debuff=6 double=5 "
    "debuffs=SLOW:2/FATIG:2/DMID=BLIND:2]")
GOOD_SHOW_LINE = (
    "[Info   :Alterum Model Swap (host)] [reel:preview] show reason=idle "
    "items=[ss_thirstfordeath,dagger_rusty-stiletto,ss_exit_B] text=none "
    "odds='64: blank 22 \u00b7 item 21 \u00b7 damage 10 \u00b7 debuff 6 \u00b7 double 5' "
    "deck=[cells=64 blank=22 item=21 damage=10 debuff=6 double=5 debuffs=SLOW:2/FATIG:2/BLIND:2] "
    "marker=hidden strip=hidden film=False")


def paint_plate(with_text=False, cards=3, width=PLATE_W, height=PLATE_H):
    """A stand-in plate with the geometry P1..P3 expect, for the fabricated cases."""
    px = [[MAROON_DEEP for _x in range(width)] for _y in range(height)]
    for x in range(width):
        for y in (0, 1, height - 2, height - 1):
            if 0 <= y < height:
                px[y][x] = GOLD_DIM
    for y in range(height):
        for x in (0, 1, width - 2, width - 1):
            if 0 <= x < width:
                px[y][x] = GOLD_DIM
    for i in range(min(cards, len(CARD_X))):
        x0 = CARD_X[i]
        for y in range(CARD_Y, CARD_Y + CARD_H):
            for x in range(x0, x0 + CARD_W):
                px[y][x] = (0x72, 0x28, 0x32, 255) if y > CARD_Y else (0x08, 0x03, 0x05, 255)
    if with_text:
        for y in range(112, 126):
            for x in range(120, 280):
                px[y][x] = CREAM
        for y in range(128, 158):
            for x in range(60, 340):
                px[y][x] = GOLD
    return width, height, px


def encode_png(width, height, px) -> bytes:
    raw = bytearray()
    for row in px:
        raw.append(0)
        for p in row:
            raw.extend(p[:4])
    def chunk(ctype, payload):
        return (struct.pack(">I", len(payload)) + ctype + payload +
                struct.pack(">I", zlib.crc32(ctype + payload) & 0xFFFFFFFF))
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) +
            chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def write_case(root: Path, dirname: str, log: str, png: bytes, sha: bool = True, **manifest):
    d = root / dirname
    d.mkdir(parents=True, exist_ok=True)
    (d / "log.txt").write_text(log, encoding="utf-8")
    if png is not None:
        (d / "reel_preview.png").write_bytes(png)
    m = {"case": manifest.pop("case", "plate-clean")}
    if png is not None:
        art = {"path": "reel_preview.png"}
        if sha:
            art["sha256"] = hashlib.sha256(png).hexdigest()
        m["artifacts"] = manifest.pop("artifacts", [art])
    m.update(manifest)
    (d / "manifest.json").write_text(json.dumps(m, indent=2), encoding="utf-8")
    return d


def good_png(**kw):
    return encode_png(*paint_plate(**kw))


def self_test() -> int:
    ok = True
    with tempfile.TemporaryDirectory(prefix="reel-preview-evidence-") as tmp:
        root = Path(tmp)
        cases = []

        # ---- good -------------------------------------------------------------------------
        cases.append(("PASS", write_case(
            root, "good-plate-clean",
            "\n".join([GOOD_PLATE_LINE, GOOD_SHOW_LINE]) + "\n", good_png())))

        # A plate with no idle show line still passes (L2 is then only partial: a warning).
        cases.append(("PASS", write_case(
            root, "good-no-show", GOOD_PLATE_LINE + "\n", good_png(), require_show=False)))

        # ---- bad --------------------------------------------------------------------------
        # 1. The label survived on the plate note.
        cases.append(("FAIL", write_case(
            root, "bad-label-kept",
            GOOD_PLATE_LINE.replace("text=none", "text=some") + "\n", good_png())))

        cases.append(("FAIL", write_case(
            root, "bad-legacy-label-field",
            GOOD_PLATE_LINE.replace("text=none ", "text=none label='ITEM PREVIEW \u2014 NOT A RESULT' ")
            + "\n", good_png())))

        # 2. The rendered two-line odds are still drawn (and logged).
        cases.append(("FAIL", write_case(
            root, "bad-rendered-odds",
            GOOD_PLATE_LINE.replace(
                "deck=[cells=64 blank=22 item=21 damage=10 debuff=6 double=5 "
                "debuffs=SLOW:2/FATIG:2/DMID=BLIND:2]",
                "lines=[64: BLANK 22 \u00b7 ITEM 21 \u00b7 DMG 10 | DEBUFF 6 \u00b7 DOUBLE 5]")
            + "\n", good_png())))

        # 3. The odds were removed from the log with the artwork (the composition is unverifiable).
        cases.append(("FAIL", write_case(
            root, "bad-odds-gone",
            GOOD_PLATE_LINE.split(" odds='")[0] + " deck=[cells=64 blank=22 item=21 damage=10 "
            "debuff=6 double=5]\n", good_png(), require_show=False)))

        # 4. The census went with the text.
        cases.append(("FAIL", write_case(
            root, "bad-census-gone",
            GOOD_PLATE_LINE.split(" deck=[")[0] + "\n", good_png(), require_show=False)))

        # 5. A composed deck that is not the canonical one.
        cases.append(("FAIL", write_case(
            root, "bad-deck-off",
            GOOD_PLATE_LINE.replace("blank 22", "blank 21").replace("blank=22", "blank=21") + "\n",
            good_png(), require_show=False)))

        # 6. The texture shrank with the text.
        cases.append(("FAIL", write_case(
            root, "bad-plate-size",
            GOOD_PLATE_LINE.replace("400x170", "400x112") + "\n",
            encode_png(*paint_plate(width=400, height=112)), require_show=False)))

        # 7. The text was only "cleared" by drawing the cards lower / leaving ink behind.
        cases.append(("FAIL", write_case(
            root, "bad-text-still-drawn",
            GOOD_PLATE_LINE + "\n", good_png(with_text=True), require_show=False)))

        # 8. A card went missing.
        cases.append(("FAIL", write_case(
            root, "bad-two-cards",
            GOOD_PLATE_LINE.replace(",EXB=ss_exit_B", "") + "\n",
            good_png(cards=2), require_show=False)))

        # 9. The plate artifact does not match the pinned hash.
        cases.append(("FAIL", write_case(
            root, "bad-hash",
            GOOD_PLATE_LINE + "\n", good_png(), sha=False,
            artifacts=[{"path": "reel_preview.png", "sha256": "0" * 64}], require_show=False)))

        sys.stdout.write(f"self-test: {len(cases)} fabricated scenarios in {root}\n")
        for want, d in cases:
            rep = verify_case(d)
            got = rep.verdict
            print_report(rep)
            if got != want:
                ok = False
                sys.stdout.write(f"  SELFTEST MISMATCH: expected {want}, got {got}\n")
            else:
                sys.stdout.write(f"  selftest ok: expected {want}\n")
            sys.stdout.write("\n")

    sys.stdout.write(f"self-test: {'OK' if ok else 'FAILED'}\n")
    return 0 if ok else 1


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--case", nargs="+", metavar="DIR",
                    help="scenario directory (manifest.json + log + reel_preview.png)")
    ap.add_argument("--png", nargs="+", metavar="PATH",
                    help="check a bare plate dump (P1..P3 only, no log)")
    ap.add_argument("--self-test", action="store_true",
                    help="run the fabricated good/bad self-test")
    args = ap.parse_args(argv)

    if args.self_test:
        return self_test()
    if not args.case and not args.png:
        ap.error("pass --case DIR [DIR ...], --png PATH [PATH ...], or --self-test")

    bad = 0
    for d in args.case or []:
        rep = verify_case(Path(d))
        print_report(rep)
        if rep.verdict != "PASS":
            bad += 1
    for p in args.png or []:
        rep = verify_png(Path(p))
        print_report(rep)
        if rep.verdict != "PASS":
            bad += 1
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
