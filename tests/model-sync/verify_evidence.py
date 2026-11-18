#!/usr/bin/env python3
"""Verify the evidence for the model-identity work (body, HUD and wardrobe must agree).

The reported bug (2026-11-17, actor B with Golubiro assigned): B's bottom-right HUD portrait showed
Ryan, B's body did not render its pieces correctly, and player A's wardrobe marked B on the Ryan
row. Four surfaces, four sources:

  local body   from the wanted entry            (SahurWardrobe -> SahurModel/SahurRig)
  remote body  from the vouched slug            (RemoteModels; no index ever crosses the wire)
  HUD          from PlayerModelData.BustPortraitSprite of the game's APPLIED row
  A's wardrobe from the native PlayersModelSetuper index

The fix is one canonical resolver (ModelPresentation) plus DISPLAY-ONLY corrections. The verifier
therefore proves, from a scenario directory, exactly that:

  * a `[model:identity]` line exists per actor, and the canonical row agrees with the body;
  * where HUD/wardrobe disagreed, a display correction names the canonical row, and NO
    `SetupModel`/`SetBaseModelIndex` was issued to refresh a display surface;
  * the local wanted record is repaired by SLUG (slug wins; pending never downgrades; withdrawn
    invalidates and the replacement is persisted; a pre-slug record's index is never reused);
  * the random-on-join record is persisted/recovered by slug, and an old index-only state file is
    ignored rather than reinterpreted;
  * a remote actor's state never rewrites the local wanted cfg;
  * the rigid body is built with native bindposes into its OWN mesh clone, and a failed build
    restores the native body.

Inputs (all optional unless a check needs them):

    <case>/manifest.json                       case + expectations
    <case>/log.txt                             the scenario's log export
    <case>/before/alterum-wanted-model.cfg     wanted record before
    <case>/after/alterum-wanted-model.cfg      wanted record after
    <case>/after/alterum-random-model-onjoin.txt   roll state after

Usage:
    verify_evidence.py --case DIR [DIR ...]
    verify_evidence.py --self-test

Exit: 0 all declared expectations met, 1 otherwise.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import tempfile

# The row fields carry PlayerModelData NAMES, and our custom rows are labelled with spaces
# ("TC2 Demoman"). A \S+ capture stops at the space and the whole identity line stops parsing -
# exactly how the real 2026-11-17 incident was invisible to this verifier. The row fields are
# therefore captured up to the next " key=" boundary; slugs/booleans stay token-captured.
IDENTITY_RE = re.compile(
    r"\[model:identity\] actor=(\S+) wantedSlug=(\S+) advertisedSlug=(\S+) resolvedDb=(\S+) "
    r"appliedRow=(.*?) bodySlug=(\S+) hudRow=(.*?) wardrobeRow=(.*?) provenance=(\S+)"
    r"(?: readiness=(\S+))?(?: bodyState=(\S+))?")
HUD_RE = re.compile(r"\[model:hud\] presented canonical row '([^']*)' db=(\S+) in (\d+) portrait")
HUD_NOMATCH_RE = re.compile(r"\[model:hud\] no portrait Image carries")
WARDROBE_RE = re.compile(
    r"\[model:wardrobe\] actor=(\S+) occupancy '([^']*)' -> '([^']*)' nickname='([^']*)'")
WANTED_RESTORE_RE = re.compile(
    r"wanted entry restored from '[^']*': (\d+) '([^']*)' \(([^)]*)\)")
WANTED_INVALID_RE = re.compile(r"\[model:wanted\] invalidated wanted record: ([^\n]+)")
WANTED_PENDING_RE = re.compile(r"wanted entry '([^']*)' is PENDING")
WANTED_LEGACY_INDEX_RE = re.compile(r"legacy index (\S+) is NOT reinterpreted")
WANTED_LEGACY_LABEL_RE = re.compile(r"recovered the only entry labelled '([^']*)' -> (\d+) '([^']*)' slug='([^']*)'")
BIND_RE = re.compile(
    r"\[sahur:bind\] '([^']*)' native=(\d+)/(\d+) live=(\d+) worstLiveVsNative=(\S+) worstBone='([^']*)' mesh=(\S+)")
BUILD_FAIL_RE = re.compile(r"\[sahur:build\] FAILED '([^']*)'")
RIG_FAIL_RE = re.compile(r"\[sahurrig:build\] FAILED '([^']*)'")
BODY_RESTORED_RE = re.compile(r"\[sahur:body\] restored (\d+) body renderer\(s\) \(why=([^)]*)\)")
RIG_RESTORED_RE = re.compile(r"\[sahurrig:body\] restored (\d+) body renderer\(s\) \(why=([^)]*)\)")
DUMP_HIDDEN_RE = re.compile(r"\[sahur:dump\].*hidden=(\d+)")
ROLL_RE = re.compile(r"\[random:join\] ROLL .*chosenSlug='([^']*)'")
LEGACY_STATE_RE = re.compile(r"\[random:join\] legacy state record has no slug \(entry=(\S+)\)")
RESUME_RE = re.compile(r"\[random:join\] no roll: .*chosenSlug='([^']*)'")
REMOTE_DISPLAY_PICK_RE = re.compile(r"SetBaseModelIndex|SetupModel")
# An actual CALL, not the disclaimer text that names both methods ("no SetupModel, no selection RPC").
SELECTION_FOR_DISPLAY_RE = re.compile(r"\[model:(hud|wardrobe)\][^\n]*(SetBaseModelIndex|SetupModel)\s*\(")

WARN_ONLY: set[str] = set()


def read(path):
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return f.read()


def cfg_get(text, key):
    for ln in text.splitlines():
        ln = ln.strip()
        if ln.startswith("#") or "=" not in ln:
            continue
        k, v = ln.split("=", 1)
        if k.strip().lower() == key:
            return v.strip()
    return None


def identities(log):
    out = []
    for m in IDENTITY_RE.finditer(log):
        out.append({
            "actor": m.group(1),
            "wanted": m.group(2),
            "advertised": m.group(3),
            "db": m.group(4),
            "applied": m.group(5),
            "body": m.group(6),
            "hud": m.group(7),
            "wardrobe": m.group(8),
            "provenance": m.group(9),
            "readiness": m.group(10) or "-",
            "bodyState": m.group(11) or "",
        })
    return out


def is_slug(v):
    return v not in ("", "-", "<native>")


def final_states(log):
    """The LAST identity line per actor. Transient pending/rejected states are historical truth
    (body_state_truthful still checks every line); convergence is about the final state of each
    actor, not about whichever actor happened to log first."""
    order = []
    last = {}
    for i in identities(log):
        if i["actor"] not in last:
            order.append(i["actor"])
        last[i["actor"]] = i
    return [last[a] for a in order]


def final_local(log):
    locals_ = [i for i in identities(log) if i["actor"] == "local"]
    return locals_[-1] if locals_ else None


# ------------------------------------------------------------------------------ checks

def check_identity_lines(ctx):
    ids = identities(ctx["log"])
    if not ids:
        return False, "no [model:identity] line"
    local = [i for i in ids if i["actor"] == "local"]
    if not local:
        return False, "no local identity line"
    if ctx["expect_remote"] and not [i for i in ids if i["actor"].startswith("remote=")]:
        return False, "no remote identity line"
    return True, "%d identity line(s)" % len(ids)


def check_local_canonical_agrees(ctx):
    # The FINAL local line: the first line is often the pre-selection native state, which used to
    # make this check pass without ever examining the episode it claims to verify.
    local = final_local(ctx["log"])
    if local is None:
        return False, "no local identity line"
    if local["bodyState"] in ("pending", "failed") and local["wanted"] in ("-", ""):
        pass
    if local["readiness"] == "ready" and is_slug(local["wanted"]) and local["bodyState"] not in ("pending", "failed"):
        if local["body"] != local["wanted"]:
            return False, "bodySlug=%s != wantedSlug=%s while ready" % (local["body"], local["wanted"])
        if local["db"] in ("-", "-1"):
            return False, "ready canonical row has no local database index"
    if local["readiness"] == "pending":
        if local["provenance"] not in ("wanted:pending", "wanted:explicit", "wanted:adopted"):
            return False, "pending row has provenance %s" % local["provenance"]
        if local["body"] not in (local["wanted"], ""):
            return False, "pending wanted entry was replaced by %s" % local["body"]
    return True, "local canonical row agrees with the body (wanted=%s body=%s readiness=%s)" % (
        local["wanted"], local["body"], local["readiness"])


def check_remote_canonical_agrees(ctx):
    # Final per-actor state only. A truthful `bodyState=pending` line before the peer's build is
    # evidence of convergence in progress, not a final disagreement (body_state_truthful covers
    # every historical line).
    ids = [i for i in final_states(ctx["log"]) if i["actor"].startswith("remote=")]
    if not ids:
        if ctx["expect_remote"]:
            return False, "no remote identity line"
        return True, "no remote actor in this scenario"
    for i in ids:
        if is_slug(i["advertised"]):
            if i["hud"] not in (i["advertised"], "-"):
                return False, "%s hudRow=%s != advertised=%s" % (i["actor"], i["hud"], i["advertised"])
            if i["wardrobe"] not in (i["advertised"], "-"):
                return False, "%s wardrobeRow=%s != advertised=%s" % (i["actor"], i["wardrobe"], i["advertised"])
            if i["body"] not in (i["advertised"], ""):
                return False, "%s bodySlug=%s != advertised=%s" % (i["actor"], i["body"], i["advertised"])
    return True, "%d remote row(s) use the vouched slug" % len(ids)


def check_hud_projection(ctx):
    local = final_local(ctx["log"])
    if local is None:
        return False, "no local identity line"
    mismatched = local["applied"] not in ("-", local["hud"]) and is_slug(local["hud"])
    projected = HUD_RE.search(ctx["log"]) or HUD_NOMATCH_RE.search(ctx["log"])
    if mismatched and not projected:
        return False, "applied row '%s' != hud row '%s' but no display correction/nomatch line" % (
            local["applied"], local["hud"])
    if projected and SELECTION_FOR_DISPLAY_RE.search(ctx["log"]):
        return False, "a display line also issued a selection/SetupModel call"
    hud_lines = list(HUD_RE.finditer(ctx["log"]))
    if hud_lines:
        m = hud_lines[-1]                       # the latest projection is the current display
        if is_slug(local["hud"]) and m.group(1) != local["hud"]:
            return False, "HUD presented '%s' but the identity's hudRow is '%s'" % (m.group(1), local["hud"])
    return True, "HUD satisfies the canonical row (applied=%s hud=%s)" % (local["applied"], local["hud"])


def check_wardrobe_projection(ctx):
    ids = [i for i in identities(ctx["log"]) if i["actor"].startswith("remote=")]
    moves = {m.group(1): (m.group(2), m.group(3)) for m in WARDROBE_RE.finditer(ctx["log"])}
    for i in ids:
        if not is_slug(i["advertised"]):
            continue
        if i["applied"] in ("-", "") or i["applied"] == i["advertised"]:
            continue
        if i["actor"] not in moves:
            return False, "%s applied row '%s' != canonical '%s' but no occupancy move" % (
                i["actor"], i["applied"], i["advertised"])
    if "display only" not in ctx["log"] and moves:
        return False, "occupancy move is not marked display-only"
    return True, "%d occupancy move(s) onto the canonical row" % len(moves)


def check_display_only(ctx):
    if SELECTION_FOR_DISPLAY_RE.search(ctx["log"]):
        return False, "display projection issued SetupModel/SetBaseModelIndex"
    if ctx["remote_only"]:
        before, after = ctx["before_cfg"], ctx["after_cfg"]
        if before and after and read(before) != read(after):
            return False, "remote state changed the local wanted cfg"
    return True, "display-only corrections; local persistence untouched by remote state"


def check_bind_native(ctx):
    m = BIND_RE.search(ctx["log"])
    if not m:
        if ctx["require_bind"]:
            return False, "no [sahur:bind] line"
        return True, "no rigid build in this scenario"
    label, native, total, live, worst, bone, mesh = m.groups()
    if int(total) == 0:
        return False, "bind line has zero bones"
    if int(native) != int(total):
        return False, "%s used %s/%s native bindposes (live=%s)" % (label, native, total, live)
    if mesh != "private-clone":
        return False, "bindposes written to %r, not a private clone" % mesh
    return True, "native bindposes %s/%s into a private mesh clone (worst live-vs-native %s)" % (native, total, worst)


def check_build_failure_restores(ctx):
    rigid = BUILD_FAIL_RE.search(ctx["log"])
    rig = RIG_FAIL_RE.search(ctx["log"])
    m = rigid or rig
    if not m:
        return True, "no failed build in this scenario"
    # The rig path restores the native body BEFORE it logs FAILED (restore first, then report), so
    # the lifecycle window starts a little before the failure line, not strictly after it.
    tail = ctx["log"][max(0, m.start() - 4000):]
    rm = BODY_RESTORED_RE.search(tail) or RIG_RESTORED_RE.search(tail)
    if not rm or "build-failed" not in rm.group(2):
        return False, "failed build did not restore the native body"
    dumps = DUMP_HIDDEN_RE.findall(tail)
    if dumps and int(dumps[-1]) != 0:
        return False, "native body is still hidden after the failed build (hidden=%s)" % dumps[-1]
    if rig is not None:
        # Bounded lifecycle of the rig failure path: a retry deadline and a truthful failed state,
        # not a per-frame retry and not a silent success.
        if "retry in" not in tail or "bodyState=failed" not in tail:
            return False, "rig build failure has no bounded retry/bodyState=failed line"
    return True, "failed build restored the native body (hidden=0, bounded retry)"


def check_wanted_slug_wins(ctx):
    m = WANTED_RESTORE_RE.search(ctx["log"])
    if not m:
        if ctx["require_wanted_restore"]:
            return False, "no slug-keyed wanted restore line"
        return True, "no restore in this scenario"
    entry, label, slug = m.groups()
    if not slug:
        return False, "restore line carries no slug"
    bad = re.search(r"wanted entry restored from '[^']*': \d+ '[^']*' \((?:[^)]*)\).*?label mismatch", ctx["log"])
    if bad:
        return False, "restore was label/index driven"
    return True, "wanted restore keyed by slug '%s'" % slug


def check_pending_no_downgrade(ctx):
    m = WANTED_PENDING_RE.search(ctx["log"])
    if not m:
        return True, "no pending wanted row in this scenario"
    tail = ctx["log"][m.start():]
    if "not downgraded" not in tail.split("\n", 1)[0]:
        return False, "pending line does not state the no-downgrade rule"
    if re.search(r"wanted model -> <native>", tail):
        return False, "a pending custom choice was downgraded to native"
    return True, "pending row kept the custom choice"


def check_withdrawn_invalidated(ctx):
    m = WANTED_INVALID_RE.search(ctx["log"])
    if not m:
        if ctx["expect_withdrawn"]:
            return False, "no invalidation line for a withdrawn slug"
        return True, "no withdrawn slug in this scenario"
    line = m.group(1)
    if "explicit precedence cleared" not in ctx["log"][m.start():].split("\n", 1)[0]:
        return False, "invalidation did not clear explicit precedence"
    after = ctx["after_cfg"]
    if not after:
        return False, "invalidation scenario has no after cfg"
    text = read(after)
    slug = cfg_get(text, "slug")
    if slug is None:
        return False, "after cfg has no slug record (missing is not the same as native)"
    if ctx["withdrawn_slug"] and slug == ctx["withdrawn_slug"]:
        return False, "after cfg still names the withdrawn slug"
    return True, "withdrawn slug invalidated; replacement slug='%s' persisted" % slug


def check_legacy_record(ctx):
    lm = WANTED_LEGACY_INDEX_RE.search(ctx["log"])
    rec = WANTED_LEGACY_LABEL_RE.search(ctx["log"])
    if ctx["expect_legacy_unusable"] and not lm:
        return False, "pre-slug record was not reported as NOT reinterpreted"
    if ctx["expect_legacy_recover"] and not rec:
        return False, "unique legacy label was not recovered by slug"
    if lm:
        after = ctx["after_cfg"]
        if after:
            slug = cfg_get(read(after), "slug")
            if slug and not is_slug(slug):
                return False, "after cfg wrote a non-slug record"
    return True, "legacy-index handling matches the declaration"


def check_roll_by_slug(ctx):
    m = ROLL_RE.search(ctx["log"])
    if not m:
        if ctx["expect_roll"]:
            return False, "no roll line"
        return True, "no roll in this scenario"
    slug = m.group(1)
    if not slug:
        return False, "roll line carries no slug"
    state = ctx["after_state"]
    if ctx["expect_roll"]:
        if not state:
            return False, "roll scenario has no after state file"
        text = read(state)
        if cfg_get(text, "slug") != slug:
            return False, "state file slug=%r != rolled %r" % (cfg_get(text, "slug"), slug)
        if "entry=" in text and not cfg_get(text, "slug"):
            return False, "state file is index-only"
    if LEGACY_STATE_RE.search(ctx["log"]) and ctx["expect_legacy_state_ignored"]:
        return True, "legacy index-only state ignored"
    return True, "roll persisted by slug '%s'" % slug


def check_legacy_state_ignored(ctx):
    if not ctx["expect_legacy_state_ignored"]:
        return True, "not a legacy state scenario"
    m = LEGACY_STATE_RE.search(ctx["log"])
    if not m:
        return False, "legacy state record was not reported as ignored"
    if RESUME_RE.search(ctx["log"]):
        return False, "a legacy index-only state was resumed"
    return True, "legacy state record (entry=%s) ignored" % m.group(1)


def check_body_state_truthful(ctx):
    """bodySlug is the EFFECTIVE body. A body module that merely WANTS a spec (binding, retrying,
    build failed) must not report that spec as the body: the native donor is what is on screen."""
    ids = identities(ctx["log"])
    checked = 0
    for i in ids:
        st = i["bodyState"]
        if not st:
            continue
        checked += 1
        if st == "live" and i["body"] in ("-", ""):
            return False, "%s bodyState=live but bodySlug=%s" % (i["actor"], i["body"])
        if st in ("pending", "failed") and i["body"] not in ("-", ""):
            return False, "%s bodyState=%s but bodySlug=%s claims a body that is not built" % (
                i["actor"], st, i["body"])
        if st == "native" and i["body"] not in ("-", ""):
            return False, "%s bodyState=native but bodySlug=%s" % (i["actor"], i["body"])
        if st == "failed" and not (BUILD_FAIL_RE.search(ctx["log"]) or RIG_FAIL_RE.search(ctx["log"])):
            return False, "%s bodyState=failed but no build FAILED line" % i["actor"]
    return True, "%d identity line(s) report an effective body state" % checked


def check_assignment_converges(ctx):
    """The game's applied row is what the HUD portrait and the wardrobe tab show. When it names one
    of OUR custom rows, the body must be that row - or the log must state a truthful non-live body
    state and carry the assignment as the DESIRED slug. The reported red (B, 2026-11-17 03:50) held
    an explicit native wanted record against the game's 'TC2 Demoman': appliedRow=TC2 Demoman,
    bodySlug=-, wantedSlug=-, readiness=native. Display said Demoman, both bodies were James."""
    rows = ctx["custom_rows"]
    if not rows:
        return True, "no custom-row map in this case"
    checked = 0
    for i in final_states(ctx["log"]):
        slug = rows.get(i["applied"])
        if not slug:
            continue
        checked += 1
        # This actor's final body/display state. The applied row is the game's last row on THIS
        # actor; it is not evidence about another actor.
        if i["body"] == slug:
            continue                                    # body follows the applied row
        if i["bodyState"] in ("pending", "failed"):
            desired = i["wanted"] if i["actor"] == "local" else i["advertised"]
            if desired != slug:
                return False, "%s applied '%s' (%s) is not the desired slug (%s) either" % (
                    i["actor"], i["applied"], slug, desired)
            continue
        # No "it must have been another actor" waiver: this is the FINAL line of THIS actor. If the
        # actor still carries an applied custom row that neither its body nor its pending/failed
        # desired slug accounts for, the convergence is not established.
        return False, ("%s applied row '%s' resolves to '%s' but the effective body is '%s' "
                       "(wanted='%s' hud='%s' state='%s'): the display/body agreement the bug was "
                       "about is not established" % (
                           i["actor"], i["applied"], slug, i["body"], i["wanted"], i["hud"],
                           i["bodyState"] or "unreported"))
    return True, "%d final applied custom row(s) have a matching body/display" % checked



CHECKS = [
    ("body_state_truthful", check_body_state_truthful),
    ("assignment_converges", check_assignment_converges),
    ("identity_lines", check_identity_lines),
    ("local_canonical_agrees", check_local_canonical_agrees),
    ("remote_canonical_agrees", check_remote_canonical_agrees),
    ("hud_projection", check_hud_projection),
    ("wardrobe_projection", check_wardrobe_projection),
    ("display_only", check_display_only),
    ("bind_native", check_bind_native),
    ("build_failure_restores", check_build_failure_restores),
    ("wanted_slug_wins", check_wanted_slug_wins),
    ("pending_no_downgrade", check_pending_no_downgrade),
    ("withdrawn_invalidated", check_withdrawn_invalidated),
    ("legacy_record", check_legacy_record),
    ("roll_by_slug", check_roll_by_slug),
    ("legacy_state_ignored", check_legacy_state_ignored),
]


def load_case(case_dir):
    with open(os.path.join(case_dir, "manifest.json"), "r", encoding="utf-8") as f:
        m = json.load(f)

    def resolve(key):
        rel = m.get(key)
        if not rel:
            return None
        p = os.path.join(case_dir, rel)
        return p if os.path.exists(p) else None

    return {
        "case": m.get("case", os.path.basename(case_dir)),
        "log": read(resolve(m.get("log", "log.txt")) or os.path.join(case_dir, "log.txt")),
        "before_cfg": resolve("before_cfg"),
        "after_cfg": resolve("after_cfg"),
        "after_state": resolve("after_state"),
        "expect_remote": bool(m.get("expect_remote", True)),
        "remote_only": bool(m.get("remote_only", False)),
        "require_bind": bool(m.get("require_bind", False)),
        "require_wanted_restore": bool(m.get("require_wanted_restore", False)),
        "expect_withdrawn": bool(m.get("expect_withdrawn", False)),
        "withdrawn_slug": m.get("withdrawn_slug"),
        "expect_legacy_unusable": bool(m.get("expect_legacy_unusable", False)),
        "expect_legacy_recover": bool(m.get("expect_legacy_recover", False)),
        "expect_roll": bool(m.get("expect_roll", False)),
        "expect_legacy_state_ignored": bool(m.get("expect_legacy_state_ignored", False)),
        "custom_rows": m.get("custom_rows", {}),
        "expect": m.get("checks", {}),
    }


def run_case(ctx):
    results = {}
    for name, fn in CHECKS:
        try:
            ok, detail = fn(ctx)
        except Exception as e:
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
        print("  [%s] %-22s %s" % (status, name, detail))
    return bad


def verify(case_dir):
    ctx = load_case(case_dir)
    print("case %s" % ctx["case"])
    return report(ctx, run_case(ctx))


# ---------------------------------------------------------------------------- self-test

BASE = """\
[model:identity] actor=local wantedSlug=golubiro advertisedSlug=golubiro resolvedDb=12 appliedRow=Male_5_Ryan bodySlug=golubiro hudRow=golubiro wardrobeRow=Male_5_Ryan provenance=wanted:explicit readiness=ready
[model:hud] presented canonical row 'golubiro' db=12 in 1 portrait image(s) (the game's applied row is 'Male_5_Ryan'); display only - PlayerModelData untouched, no SetupModel, no selection RPC
[model:identity] actor=remote=2 wantedSlug=- advertisedSlug=golubiro resolvedDb=12 appliedRow=Male_5_Ryan bodySlug=golubiro hudRow=golubiro wardrobeRow=golubiro provenance=remote:vouched readiness=ready
[model:wardrobe] actor=remote=2 occupancy 'Male_5_Ryan' -> 'golubiro' nickname='B' (display only; the game's applied row is unchanged)
[sahur:map] wanted entry restored from 'cfg/alterum-wanted-model.cfg': 2 'Golubiro' (golubiro) [explicit] db=12 row-state=ready
[sahur:bind] 'Golubiro' native=6/6 live=0 worstLiveVsNative=7.41deg/12.3mm worstBone='head' mesh=private-clone
[random:join] ROLL lobby=1 client=2 pool=16 (all custom wardrobe entries, db 10..25) chosenEntry=2 chosenSlug='golubiro' chosenDbIndex=12 chosenLabel='Golubiro' reason=lobby membership active
"""

WANTED_BEFORE = "version=1\nslug=golubiro\nlabel=Golubiro\nwhy=deliberate wardrobe pick\nexplicit=1\nat=2026-11-17T00:00:00Z\n"
WANTED_AFTER = WANTED_BEFORE
STATE_AFTER = "version=1\nactive=1\nslug=golubiro\nentry=2\n"


def _mk(dirpath, log, manifest, cfg_before=None, cfg_after=None, state_after=None):
    os.makedirs(dirpath, exist_ok=True)
    with open(os.path.join(dirpath, "log.txt"), "w", encoding="utf-8") as f:
        f.write(log)
    manifest.setdefault("case", os.path.basename(dirpath))
    if cfg_before is not None:
        os.makedirs(os.path.join(dirpath, "before"), exist_ok=True)
        with open(os.path.join(dirpath, "before", "alterum-wanted-model.cfg"), "w", encoding="utf-8") as f:
            f.write(cfg_before)
        manifest["before_cfg"] = "before/alterum-wanted-model.cfg"
    if cfg_after is not None:
        os.makedirs(os.path.join(dirpath, "after"), exist_ok=True)
        with open(os.path.join(dirpath, "after", "alterum-wanted-model.cfg"), "w", encoding="utf-8") as f:
            f.write(cfg_after)
        manifest["after_cfg"] = "after/alterum-wanted-model.cfg"
    if state_after is not None:
        os.makedirs(os.path.join(dirpath, "after"), exist_ok=True)
        with open(os.path.join(dirpath, "after", "alterum-random-model-onjoin.txt"), "w", encoding="utf-8") as f:
            f.write(state_after)
        manifest["after_state"] = "after/alterum-random-model-onjoin.txt"
    with open(os.path.join(dirpath, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)


def self_test():
    root = tempfile.mkdtemp(prefix="model-sync-")
    cases = []

    def add(name, log, manifest=None, **kwargs):
        d = os.path.join(root, name)
        mf = manifest or {}
        mf.setdefault("case", name)
        _mk(d, log, mf, **kwargs)
        cases.append((name, d, mf.get("checks", {})))

    add("ok", BASE, {"expect_roll": True}, cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER, state_after=STATE_AFTER)

    add("body-live-mismatch",
        BASE.replace("bodySlug=golubiro hudRow=golubiro wardrobeRow=Male_5_Ryan",
                     "bodySlug=Male_5_Ryan hudRow=golubiro wardrobeRow=Male_5_Ryan"),
        {"checks": {"local_canonical_agrees": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    add("hud-wrong-row",
        BASE.replace("presented canonical row 'golubiro'", "presented canonical row 'Male_5_Ryan'"),
        {"checks": {"hud_projection": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    add("no-hud-line",
        "\n".join(l for l in BASE.splitlines() if "[model:hud]" not in l),
        {"checks": {"hud_projection": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    add("no-wardrobe-move",
        "\n".join(l for l in BASE.splitlines() if "[model:wardrobe]" not in l),
        {"checks": {"wardrobe_projection": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    add("display-selection",
        BASE + "[model:hud] presented canonical row 'golubiro' db=12 in 1 portrait image(s) via SetupModel(Male_5_Ryan)\n",
        {"checks": {"display_only": "fail", "hud_projection": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    add("remote-rewrote-cfg", BASE, {"remote_only": True, "checks": {"display_only": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_BEFORE.replace("why=deliberate wardrobe pick", "why=adopted from remote"))

    add("live-bind", BASE.replace("native=6/6 live=0", "native=0/6 live=6"),
        {"checks": {"bind_native": "fail"}}, cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    add("failed-build-hidden",
        BASE + "[sahur:build] FAILED 'Golubiro': boom - tearing the partial body down and restoring the native body\n"
               "[sahur:dump] on=0 bat=1 builds=0 binds=0 hides=1 restores=0 bodyRoot=none bodyRenderersSnapshot=6 hidden=6\n",
        {"checks": {"build_failure_restores": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    add("failed-build-restored",
        BASE + "[sahur:build] FAILED 'Golubiro': boom - tearing the partial body down and restoring the native body\n"
               "[sahur:body] restored 6 body renderer(s) (why=build-failed)\n"
               "[sahur:dump] on=0 bat=1 builds=0 binds=0 hides=6 restores=6 bodyRoot=none bodyRenderersSnapshot=0 hidden=0\n",
        {}, cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    pending = BASE.replace("readiness=ready", "readiness=pending") + \
        "[sahur:map] wanted entry 'Golubiro' is PENDING (row not registered yet): no row is forced and the choice is not downgraded to native\n"
    add("pending-downgraded", pending + "[sahur:map] wanted model -> <native> (why=lobby: native row applied)\n",
        {"checks": {"pending_no_downgrade": "fail"}}, cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)
    add("pending-kept", pending, {}, cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER)

    withdrawn = BASE + \
        "[model:wanted] invalidated wanted record: slug is not in this build's roster (label='Labubu'); " \
        "explicit precedence cleared - the normal default selection chooses the replacement and persists it atomically\n"
    add("withdrawn-replaced", withdrawn, {"expect_withdrawn": True, "withdrawn_slug": "labubu"},
        cfg_before="version=1\nslug=labubu\nlabel=Labubu\nexplicit=1\n",
        cfg_after="version=1\nslug=golubiro\nlabel=Golubiro\nexplicit=1\n")
    add("withdrawn-kept", withdrawn, {"expect_withdrawn": True, "withdrawn_slug": "labubu",
                                      "checks": {"withdrawn_invalidated": "fail"}},
        cfg_before="version=1\nslug=labubu\nlabel=Labubu\nexplicit=1\n",
        cfg_after="version=1\nslug=labubu\nlabel=Labubu\nexplicit=1\n")

    legacy = BASE + \
        "[model:wanted] record had no slug and label='Nope' names no unique entry; the legacy index 12 is NOT reinterpreted - record dropped\n"
    add("legacy-ignored", legacy, {"expect_legacy_unusable": True},
        cfg_before="version=1\nlabel=Nope\nexplicit=1\n", cfg_after="version=1\nslug=golubiro\nexplicit=1\n")

    legacy_ok = BASE + \
        "[model:wanted] record had no slug (legacy entry=-); recovered the only entry labelled 'Golubiro' -> 2 'Golubiro' slug='golubiro' pending=False\n"
    add("legacy-recovered", legacy_ok, {"expect_legacy_recover": True},
        cfg_before="version=1\nlabel=Golubiro\nexplicit=1\n", cfg_after=WANTED_AFTER)

    add("roll-index-only", BASE,
        {"expect_roll": True, "checks": {"roll_by_slug": "fail"}},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER, state_after="version=1\nactive=1\nentry=2\n")

    legacy_state = BASE + \
        "[random:join] legacy state record has no slug (entry=2) - ignored: a roster index is not stable across a roster change, so it is never resumed\n"
    add("legacy-state-ignored", legacy_state, {"expect_legacy_state_ignored": True, "expect_roll": True},
        cfg_before=WANTED_BEFORE, cfg_after=WANTED_AFTER, state_after=STATE_AFTER)

    bad = 0
    for name, d, expect in cases:
        print("self-test case %s" % name)
        ctx = load_case(d)
        results = run_case(ctx)
        for cname, _ in CHECKS:
            want = expect.get(cname)
            if want is None:
                want = "warn" if cname in WARN_ONLY else "pass"
            ok, detail = results[cname]
            if want == "warn":
                continue
            if want == "fail" and ok:
                print("  ! %s: expected to FAIL but passed (%s)" % (cname, detail))
                bad += 1
            elif want == "pass" and not ok:
                print("  ! %s: expected to PASS but failed (%s)" % (cname, detail))
                bad += 1

    shutil.rmtree(root, ignore_errors=True)
    if bad:
        print("SELFTEST FAILED (%d case(s))" % bad)
        return 1
    print("SELFTEST OK (%d fabricated cases behaved as declared)" % len(cases))
    return 0


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--case", nargs="+", metavar="DIR")
    ap.add_argument("--self-test", action="store_true")
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
