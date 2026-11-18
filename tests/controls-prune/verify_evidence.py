#!/usr/bin/env python3
"""Verify the evidence for the controls prune (exactly four Controls rows, no key-driven stubs).

What this guards against (2026-11-17 prune): the registry is EXECUTABLE dispatch, not a display
table. Removing a row from `ModBindings.Table` without removing its consumers leaves code that
polls an id that no longer exists (inert at best) or dereferences a null `Current` (a crash); and
leaving a surviving stub means the Controls list still advertises a key nothing should own.

So the verifier is narrow and mechanical. A scenario DIRECTORY holds:

    <case>/manifest.json              expectations for the case
    <case>/log.txt                    the scenario's log export
    <case>/before/alterum-bindings.txt   optional: the store before
    <case>/after/alterum-bindings.txt    optional: the store after

manifest.json:

    {
      "case": "prune",
      "log": "log.txt",
      "before": "before/alterum-bindings.txt",     # optional
      "after": "after/alterum-bindings.txt",       # optional
      "expect_failed_save": false,                 # a save failure scenario
      "checks": {"registry_four": "pass", ...}     # optional per-check expectation
    }

The checks:

  registry_four        `[bind] 4 actions registered (world=4 picker=0)` - exactly the four.
  legend_four          the `[keys] registry:` block names F7/U/F12/Backquote and NO removed key
                       (F1/F2/F3/F5/F6/F9/F10/F11), and the picker line says Esc is hardcoded.
  no_removed_dispatch  no log line fires a removed trigger id (the executable-dispatch half).
  esc_close            an overlay closed with `trigger=esc`.
  pause_guard          `[picker:esc] pause declined` (or a logged guard-install failure, which is
                       a WARN: Esc still closes, the menu may also open).
  stale_override       the stale ids are named once and never applied (`[bind] override id=<removed>`
                       must not exist); the applied/dropped counts are consistent.
  bindings_pruned      the AFTER store contains only surviving ids (dropped on the next save).
  save_failure_safe    a failed store write leaves the file byte-identical and applies nothing.
  mouse_click          a pick with `trigger=mouse-click`.
  mouse_wheel          a page step with `trigger=mouse-wheel`.
  cursor_restore       the cursor readback matches the saved pair on close.
  emote_open_keeps     opening mid-emote leaves the same clip playing (open/close `playing=` match).
  stop_button          the bottom-right button stops with `accepted=true`, leaving nothing playing.
  close_no_change      a close with no pick in the window does not change the emote state.

Usage:
    verify_evidence.py --case DIR [DIR ...]
    verify_evidence.py --self-test

Exit: 0 all declared expectations met (and, for --self-test, every fabricated case behaved as
declared), 1 otherwise.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile

SURVIVING_IDS = [
    "mod.pov.toggle",
    "mod.picker.emote",
    "mod.picker.cosmetic",
    "mod.console.toggle",
]

REMOVED_IDS = [
    "mod.sahur.rig",
    "mod.sahur.bat",
    "mod.sahur.body",
    "mod.clothes.toggle",
    "mod.clothes.all",
    "mod.emote.toggle",
    "mod.emote.cycle",
    "mod.shot.capture",
    "mod.clip.record",
    "mod.picker.close",
    "mod.picker.page.next",
    "mod.picker.page.prev",
    "mod.emote.cycle.prev",
    "mod.picker.select",
    "mod.picker.select.alt",
    "mod.picker.left",
    "mod.picker.right",
    "mod.picker.up",
    "mod.picker.down",
] + ["mod.picker.slot%d" % i for i in range(1, 10)] + ["mod.picker.slot0"]

# The default key text each removed row used to own. Matched with a word boundary so `F1` does not
# fire on `F12`.
REMOVED_KEY_TOKENS = ["F1", "F2", "F3", "F5", "F6", "F9", "F10", "F11"]
SURVIVING_KEY_TOKENS = ["F7", "U", "F12", "Backquote"]

REGISTERED_RE = re.compile(r"\[bind\]\s+(\d+)\s+actions registered \(world=(\d+) picker=(\d+)\)")
COUNTS_RE = re.compile(r"overrides loaded=(\d+) applied=(\d+) dropped=(\d+)")
STALE_LIST_RE = re.compile(r"\[bind\] stale overrides ignored[^:]*:\s*([^\n]+?)\s+-\s+the next normal store save")
OVERRIDE_RE = re.compile(r"\[bind\]\s+override id=([^\s']+)")
SAVE_FAILED_RE = re.compile(r"\[bind\] store save failed")
CURSOR_RESTORED_RE = re.compile(
    r"\[picker:cursor\] restored visible=(true|false) locked=(\w+) readback=(true|false)/(\w+)")
OPEN_RE = re.compile(r"\[picker:emote\] open trigger=(\S+) .*?playing='([^']*)' lease=(\w+)")
CLOSE_RE = re.compile(r"\[picker\] closed \((.*?)\) trigger=(\S+?)(?: playing='([^']*)' lease=(\w+))?$")
PICK_EMOTE_RE = re.compile(r"\[picker:emote\] pick id='([^']*)'.*trigger=(\S+)")
ACTION_STOP_RE = re.compile(
    r"\[picker:emote\] action='stop emote' trigger=(\S+) was='([^']*)' accepted=(true|false) "
    r"playing='([^']*)'")
WHEEL_RE = re.compile(r"\[picker:(?:emote|cosmetic)\] page \d+/\d+ trigger=mouse-wheel")
PAUSE_DECLINED_RE = re.compile(r"\[picker:esc\] pause declined")
PAUSE_GUARD_FAIL_RE = re.compile(r"\[picker:esc\] pause guard install failed")

WARN_ONLY = {"pause_guard"}


def read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return f.read()


def lines_of(text):
    return text.splitlines()


def check_registry_four(ctx):
    m = REGISTERED_RE.search(ctx["log"])
    if not m:
        return False, "no '[bind] N actions registered' line"
    total, world, picker = int(m.group(1)), int(m.group(2)), int(m.group(3))
    if total != 4 or world != 4 or picker != 0:
        return False, "registered=%d world=%d picker=%d (want 4/4/0)" % (total, world, picker)
    return True, "registered=4 world=4 picker=0"


def check_legend_four(ctx):
    lines = lines_of(ctx["log"])
    idx = None
    for i, ln in enumerate(lines):
        if "[keys] registry:" in ln:
            idx = i
            break
    if idx is None:
        return False, "no '[keys] registry:' line"
    block = lines[idx]
    # The legend is multi-line (world line + Picker line); include the continuation.
    if idx + 1 < len(lines) and lines[idx + 1].lstrip().startswith("Picker"):
        block += "\n" + lines[idx + 1]
    for token in SURVIVING_KEY_TOKENS:
        if not re.search(r"(?<![A-Za-z0-9])%s(?![0-9])" % re.escape(token), block):
            return False, "legend does not name surviving key %r" % token
    for token in REMOVED_KEY_TOKENS:
        if re.search(r"(?<![A-Za-z0-9])%s(?![0-9])" % re.escape(token), block):
            return False, "legend still advertises removed key %r" % token
    if "Esc close" not in block:
        return False, "picker legend line does not say Esc is hardcoded"
    return True, "legend: F7/U/F12/Backquote, no removed key, Esc hardcoded"


def check_no_removed_dispatch(ctx):
    bad = []
    for ln in lines_of(ctx["log"]):
        for rid in REMOVED_IDS:
            if ("trigger=" + rid) in ln or ("by " + rid) in ln or ("id=" + rid) in ln:
                bad.append(rid + " in: " + ln.strip()[:120])
                break
    if bad:
        return False, bad[0]
    return True, "no removed id dispatched"


def check_esc_close(ctx):
    closed = [ln for ln in lines_of(ctx["log"]) if "[picker] closed" in ln and "trigger=esc" in ln]
    if not closed:
        return False, "no '[picker] closed (...) trigger=esc' line"
    return True, "%d Esc close(s)" % len(closed)


def check_pause_guard(ctx):
    if PAUSE_DECLINED_RE.search(ctx["log"]):
        return True, "pause declined while the overlay was open"
    if PAUSE_GUARD_FAIL_RE.search(ctx["log"]):
        return False, "guard install failed (WARN): Esc closes but the pause menu may open"
    return False, "no pause-boundary evidence"


def check_stale_override(ctx):
    m = STALE_LIST_RE.search(ctx["log"])
    if not m:
        return False, "no 'stale overrides ignored' line naming the ids"
    named = [t.strip() for t in re.split(r"[,;]", m.group(1)) if t.strip()]
    if not named:
        return False, "stale line names no ids"
    for token in named:
        token = token.split("(")[0].strip()
        if token not in REMOVED_IDS:
            return False, "stale line names %r, which is not a removed id" % token
    for om in OVERRIDE_RE.finditer(ctx["log"]):
        if om.group(1) in REMOVED_IDS:
            return False, "removed id was APPLIED from the store: %s" % om.group(1)
    cm = COUNTS_RE.search(ctx["log"])
    if cm and int(cm.group(3)) <= 0:
        return False, "dropped=0 but a stale line was emitted"
    return True, "stale ids named and never applied"


def _store_ids(path):
    ids = []
    for ln in lines_of(read(path)):
        ln = ln.strip()
        if not ln or ln.startswith("#"):
            continue
        eq = ln.find("=")
        if eq > 0:
            ids.append(ln[:eq].strip())
    return ids


def check_bindings_pruned(ctx):
    after = ctx["after"]
    if not after:
        if not ctx["require_store"]:
            return True, "no store supplied (not required by this case)"
        return False, "no after store supplied"
    if ctx["expect_failed_save"]:
        return True, "failed-save scenario: the store is intentionally unchanged"
    ids = _store_ids(after)
    leftovers = [i for i in ids if i not in SURVIVING_IDS]
    if leftovers:
        return False, "after store still carries %s" % ",".join(leftovers)
    return True, "after store carries only surviving ids (%d override(s))" % len(ids)


def check_save_failure_safe(ctx):
    if not ctx["expect_failed_save"]:
        return True, "not a failed-save scenario"
    if not SAVE_FAILED_RE.search(ctx["log"]):
        return False, "scenario declares a failed save but the log has none"
    before, after = ctx["before"], ctx["after"]
    if not before or not after:
        return False, "failed-save scenario needs before/after stores"
    if read(before) != read(after):
        return False, "a failed save changed the store file"
    # Nothing may be re-activated after the failure.
    tail = ctx["log"].split("[bind] store save failed", 1)[1]
    for om in OVERRIDE_RE.finditer(tail):
        if om.group(1) in REMOVED_IDS:
            return False, "a removed override was re-applied after the failed save"
    return True, "failed save left the file identical and applied nothing"


def check_mouse_click(ctx):
    m = PICK_EMOTE_RE.search(ctx["log"])
    if not m or "mouse-click" not in m.group(2):
        return False, "no emote pick with trigger=mouse-click"
    return True, "click-select present"


def check_mouse_wheel(ctx):
    if not WHEEL_RE.search(ctx["log"]):
        return False, "no page step with trigger=mouse-wheel"
    return True, "wheel paging present"


def check_cursor_restore(ctx):
    m = CURSOR_RESTORED_RE.search(ctx["log"])
    if not m:
        return False, "no '[picker:cursor] restored ... readback=' line"
    sv, sl, rv, rl = m.group(1), m.group(2), m.group(3), m.group(4)
    if sv != rv or sl != rl:
        return False, "readback %s/%s != saved %s/%s" % (rv, rl, sv, sl)
    return True, "cursor restored and read back (%s/%s)" % (sv, sl)


def _windows(log):
    """Emote picker windows: (open line match, [lines until close], close match).

    A repeated open (a re-open without a logged close) starts a NEW window: the previous one is
    incomplete and must not borrow the next close.
    """
    out = []
    cur = None
    for ln in lines_of(log):
        om = OPEN_RE.search(ln)
        if om:
            if cur is not None and cur[2] is None:
                cur = [om, [], None]
                continue
            if cur is None:
                cur = [om, [], None]
                continue
        if cur is None:
            continue
        cm = CLOSE_RE.search(ln)
        if cm:
            cur[2] = cm
            out.append(cur)
            cur = None
            continue
        cur[1].append(ln)
    return out


def check_emote_open_keeps(ctx):
    wins = _windows(ctx["log"])
    if not wins:
        return False, "no emote picker open/close window"
    for om, inner, cm in wins:
        playing = om.group(2)
        if playing in ("", "-"):
            continue
        if any(PICK_EMOTE_RE.search(ln) for ln in inner):
            continue
        if cm.group(3) != playing:
            return False, "closing changed playing '%s' -> '%s'" % (playing, cm.group(3))
        return True, "opened mid-emote with '%s' still playing, closed unchanged" % playing
    return False, "no window opened while an emote was playing"


def check_stop_button(ctx):
    m = ACTION_STOP_RE.search(ctx["log"])
    if not m:
        return False, "no '[picker:emote] action='stop emote'' line"
    if m.group(3) != "true":
        return False, "stop was not accepted"
    if m.group(4) != "-":
        return False, "still playing after the stop button"
    return True, "stop button accepted=%s was='%s'" % (m.group(3), m.group(2))


def check_close_no_change(ctx):
    wins = _windows(ctx["log"])
    if not wins:
        return False, "no emote picker open/close window"
    for om, inner, cm in wins:
        if any(PICK_EMOTE_RE.search(ln) for ln in inner):
            continue
        if cm.group(2).endswith(":picked"):
            continue
        if (om.group(2) or "") != (cm.group(3) or ""):
            return False, "no-pick close changed playing '%s' -> '%s'" % (om.group(2), cm.group(3))
        return True, "closed without picking; playing unchanged"
    return False, "no no-pick close window"


CHECKS = [
    ("registry_four", check_registry_four),
    ("legend_four", check_legend_four),
    ("no_removed_dispatch", check_no_removed_dispatch),
    ("esc_close", check_esc_close),
    ("pause_guard", check_pause_guard),
    ("stale_override", check_stale_override),
    ("bindings_pruned", check_bindings_pruned),
    ("save_failure_safe", check_save_failure_safe),
    ("mouse_click", check_mouse_click),
    ("mouse_wheel", check_mouse_wheel),
    ("cursor_restore", check_cursor_restore),
    ("emote_open_keeps", check_emote_open_keeps),
    ("stop_button", check_stop_button),
    ("close_no_change", check_close_no_change),
]


def load_case(case_dir):
    manifest_path = os.path.join(case_dir, "manifest.json")
    with open(manifest_path, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    def resolve(key):
        rel = manifest.get(key)
        if not rel:
            return None
        path = os.path.join(case_dir, rel)
        return path if os.path.exists(path) else None

    return {
        "case": manifest.get("case", os.path.basename(case_dir)),
        "log": read(resolve(manifest.get("log", "log.txt")) or os.path.join(case_dir, "log.txt")),
        # Paths, not text: a check that needs the bytes reads them itself.
        "before": resolve("before"),
        "after": resolve("after"),
        "expect_failed_save": bool(manifest.get("expect_failed_save", False)),
        "require_store": bool(manifest.get("require_store", False)),
        "expect": manifest.get("checks", {}),
    }


def run_case(ctx):
    results = {}
    for name, fn in CHECKS:
        try:
            ok, detail = fn(ctx)
        except Exception as e:                        # a malformed case must fail, never crash
            ok, detail = False, "verifier error: %s" % e
        results[name] = (ok, detail)
    return results


def report(ctx, results):
    bad = 0
    for name, _ in CHECKS:
        ok, detail = results[name]
        want = ctx["expect"].get(name)
        if want is None:
            want = "warn" if name in WARN_ONLY else "pass"
        if want == "warn":
            status = "PASS" if ok else "WARN"
        elif want == "fail":
            status = "FAIL-AS-EXPECTED" if not ok else "UNEXPECTED-PASS"
            if ok:
                bad += 1
        else:
            status = "PASS" if ok else "FAIL"
            if not ok:
                bad += 1
        print("  [%s] %-20s %s" % (status, name, detail))
    return bad


def verify(case_dir):
    ctx = load_case(case_dir)
    print("case %s" % ctx["case"])
    return report(ctx, run_case(ctx))


# ---------------------------------------------------------------------------- self-test

BASE_LOG = """\
[bind] 4 actions registered (world=4 picker=0); overrides loaded=0 applied=0 dropped=1 store='cfg/alterum-bindings.txt'
[bind] stale overrides ignored (removed/unknown ids, never re-applied by position): mod.emote.toggle - the next normal store save rewrites the file without them
[keys] registry: Alterum mod | F7 Third-person view | U Emote picker | F12 Cosmetic picker | Backquote Game console (game commands) | E reel interact
Picker | Esc close, click select, wheel page (hardcoded)
[picker:cursor] map screen=640x360 -> surface=640x360 scaleX=1.000 scaleY=1.000 mouse=(320,180) from screen (320,180) src=mouse
[emote] requested 'Clap' by picker:mouse-click (allowlisted LC port)
[picker:emote] open trigger=mod.picker.emote(U) page=1/1 clips=2 selected='Clap' highlight='Clap' playing='Clap' lease=held
[picker:emote] pick id='Clap' index=5 trigger=mouse-click started=true page=1/1
[picker:emote] open trigger=mod.picker.emote(U) page=1/1 clips=2 selected='Clap' highlight='Clap' playing='Clap' lease=held
[picker] closed (emote page=1/1) trigger=esc playing='Clap' lease=held
[picker:cursor] restored visible=false locked=Locked readback=false/Locked trigger=esc
[picker:cosmetic] page 2/3 trigger=mouse-wheel
[picker:emote] action='stop emote' trigger=mouse-click was='Clap' accepted=true playing='-' lease=free
[picker:esc] pause declined (overlayOpen=True escThisFrame=True): Esc closes the overlay, not the menu
"""

BASE_BEFORE = "# alterum mod bindings v1\nmod.emote.toggle=<Keyboard>/f6\n"
BASE_AFTER = "# alterum mod bindings v1\n"


def _mk(dirpath, log, manifest, before=None, after=None):
    os.makedirs(dirpath, exist_ok=True)
    with open(os.path.join(dirpath, "log.txt"), "w", encoding="utf-8") as f:
        f.write(log)
    if before is not None:
        os.makedirs(os.path.join(dirpath, "before"), exist_ok=True)
        with open(os.path.join(dirpath, "before", "alterum-bindings.txt"), "w", encoding="utf-8") as f:
            f.write(before)
        manifest["before"] = "before/alterum-bindings.txt"
    if after is not None:
        os.makedirs(os.path.join(dirpath, "after"), exist_ok=True)
        with open(os.path.join(dirpath, "after", "alterum-bindings.txt"), "w", encoding="utf-8") as f:
            f.write(after)
        manifest["after"] = "after/alterum-bindings.txt"
    with open(os.path.join(dirpath, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)


def self_test():
    root = tempfile.mkdtemp(prefix="controls-prune-")
    cases = []

    def add(name, log, expect=None, before=None, after=None, failed_save=False):
        d = os.path.join(root, name)
        manifest = {"case": name, "expect_failed_save": failed_save}
        if expect:
            manifest["checks"] = expect
        _mk(d, log, manifest, before, after)
        cases.append((name, d, expect or {}))

    add("ok", BASE_LOG, None, BASE_BEFORE, BASE_AFTER)
    add("five-rows", BASE_LOG.replace("4 actions registered (world=4 picker=0)",
                                      "5 actions registered (world=4 picker=1)"),
        {"registry_four": "fail"})
    add("legend-f6", BASE_LOG.replace("| F7 Third-person view", "| F7 Third-person view | F6 Toggle emote"),
        {"legend_four": "fail"})
    add("dispatch-f6", BASE_LOG + "[emote] requested 'Clap' by mod.emote.toggle\n",
        {"no_removed_dispatch": "fail"})
    add("no-esc", BASE_LOG.replace("trigger=esc playing='Clap'", "trigger=mod.picker.emote(U) playing='Clap'"),
        {"esc_close": "fail"})
    add("no-guard", BASE_LOG.replace("[picker:esc] pause declined", "[picker:esc] nothing"), {"pause_guard": "fail"})
    add("stale-unnamed", BASE_LOG.replace(": mod.emote.toggle - the next", ": - the next"),
        {"stale_override": "fail"})
    add("stale-applied", BASE_LOG + "[bind] override id=mod.emote.toggle '<Keyboard>/f6' -> '<Keyboard>/g' source=controls\n",
        {"stale_override": "fail", "no_removed_dispatch": "fail"})
    add("not-pruned", BASE_LOG, {"bindings_pruned": "fail"}, BASE_BEFORE, BASE_BEFORE)
    add("save-failed-ok", BASE_LOG + "[bind] store save failed 'x': refused\n",
        None, BASE_BEFORE, BASE_BEFORE, failed_save=True)
    add("save-failed-changed", BASE_LOG + "[bind] store save failed 'x': refused\n",
        {"save_failure_safe": "fail"}, BASE_BEFORE, BASE_AFTER, failed_save=True)
    add("no-click", BASE_LOG.replace("trigger=mouse-click started=true", "trigger=request started=true"),
        {"mouse_click": "fail"})
    add("no-wheel", BASE_LOG.replace("trigger=mouse-wheel", "trigger=request"), {"mouse_wheel": "fail"})
    add("bad-cursor", BASE_LOG.replace("readback=false/Locked", "readback=true/None"), {"cursor_restore": "fail"})
    add("close-changes", BASE_LOG.replace("trigger=esc playing='Clap'", "trigger=esc playing='-'"),
        {"emote_open_keeps": "fail", "close_no_change": "fail"})
    add("stop-rejected", BASE_LOG.replace("accepted=true playing='-'", "accepted=false playing='Clap'"),
        {"stop_button": "fail"})

    bad = 0
    for name, d, expect in cases:
        print("self-test case %s" % name)
        ctx = load_case(d)
        results = run_case(ctx)
        # Compare per CHECK, not the case exit code: a case may declare one expected failure while
        # every other check still passes (that is exactly what the fabricated cases do).
        declared_any = False
        for cname, _ in CHECKS:
            want = expect.get(cname)
            if want is None:
                want = "warn" if cname in WARN_ONLY else "pass"
            ok, detail = results[cname]
            if want == "warn":
                continue
            declared_any = declared_any or want == "fail"
            if want == "fail" and ok:
                print("  ! %s: expected to FAIL but passed (%s)" % (cname, detail))
                bad += 1
            elif want == "pass" and not ok:
                print("  ! %s: expected to PASS but failed (%s)" % (cname, detail))
                bad += 1
        if not declared_any and not expect:
            # The clean case must pass every non-warn check.
            for cname, _ in CHECKS:
                if cname in WARN_ONLY:
                    continue
                if not results[cname][0]:
                    print("  ! clean case: %s failed (%s)" % (cname, results[cname][1]))
                    bad += 1
    shutil.rmtree(root, ignore_errors=True)
    if bad:
        print("SELFTEST FAILED (%d case(s))" % bad)
        return 1
    print("SELFTEST OK (%d fabricated cases behaved as declared)" % len(cases))
    return 0


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--case", nargs="+", metavar="DIR", help="scenario directory (manifest.json + log.txt)")
    ap.add_argument("--self-test", action="store_true", help="run the fabricated-case self-test")
    args = ap.parse_args(argv)

    if args.self_test:
        return self_test()
    if not args.case:
        ap.error("one of --case DIR or --self-test is required")

    bad = 0
    for d in args.case:
        bad += verify(d)
    if bad:
        print("%d check(s) failed" % bad)
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
