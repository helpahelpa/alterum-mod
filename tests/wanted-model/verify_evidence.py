#!/usr/bin/env python3
"""Verify the evidence for the wanted-model pick-provenance fix.

The bug this guards against (2026-11-16 04:54, player B): a wardrobe pick was applied by the
game, the module's `SyncSelection` guard re-applied the OLD wanted entry, and
`alterum-wanted-model.cfg` was never rewritten (it still said `slug=tc2-soldier explicit=1
at=2026-11-15T09:25:53Z`). The pick was therefore discarded, and the log line that explained it
away claimed the game's correct apply was a "stale join-time row".

So the verifier's job is narrow and mechanical:

  * a `[sahur:choice] local wardrobe pick ...` line MUST be followed by a `persisted` line with the
    same slug, then a `committed` line, then (when the click probe is in the evidence) exactly one
    `SetBaseModelIndex` for that row - in that order, because the implementation persists BEFORE it
    commits in memory and only then lets the game's own RPC run;
  * the AFTER cfg MUST carry that slug, `explicit=1`, and the SAME `at` the persisted line
    recorded - a commit that never reached the file is the reported failure;
  * a hold line (`[sahur:map] lobby: keeping the explicit wanted entry 'X' - the game re-applied
    'Y' (...)`) must name the entry the cfg holds, and the message must be the reworded
    "unprovenanced applied row" (the old wording shipped the heuristic that caused the bug);
  * a remote player's RPC must leave the cfg byte-identical, a failed write must not commit and
    must not be followed by an apply, and a fuse reset must not raise or disable the threshold.

Inputs are a scenario DIRECTORY per case:

    <case>/manifest.json                     the artifact manifest (case name + expectations)
    <case>/log.txt                           the scenario's log export
    <case>/before/alterum-wanted-model.cfg   the cfg before the scenario
    <case>/after/alterum-wanted-model.cfg    the cfg after the scenario
    <case>/<any artifacts named in manifest> screenshots, census dumps, ...

manifest.json:

    {
      "case": "local-pick",                  # one of CASES below
      "client": 2, "local": 2,               # ids the scenario ran with
      "picked_slug": "golubiro", "picked_label": "Golubiro", "picked_db": 12,
      "require_apply": true,                 # apply-after-commit is mandatory, not a warning
      "log": "log.txt",
      "before": "before/alterum-wanted-model.cfg",
      "after": "after/alterum-wanted-model.cfg",
      "artifacts": [{"path": "shots/wardrobe.png", "sha256": "..."}]
    }

Usage:
    verify_evidence.py --case DIR [DIR ...]
    verify_evidence.py --self-test

Exit: 0 all PASS (and, for --self-test, every fabricated case behaved as declared), 1 otherwise.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import tempfile
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

# --------------------------------------------------------------------------------------------
# log grammar
#
# One pattern per emitted line. Each is anchored at its own tag and to the end of the line, so a
# near-miss (a missing field, an extra word) is a failure, not a pass. The host writes a
# `HH:MM:SS ` prefix; normalize() strips it before matching and keeps it for ordering only.
# --------------------------------------------------------------------------------------------

LINE_TS = re.compile(r"^\s*\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\s+")

PATTERNS = {
    # W1/W2/W4 evidence: the four exact shapes the fix emits.
    "pick": re.compile(
        r"\[sahur:choice\] local wardrobe pick client=(?P<client>\d+) db=(?P<db>\d+) "
        r"slug='(?P<slug>[^']*)' source=(?P<source>\S+) scene='(?P<scene>[^']*)'$"),
    "persisted": re.compile(
        r"\[sahur:choice\] persisted path='(?P<path>[^']*)' slug='(?P<slug>[^']*)' "
        r"explicit=(?P<explicit>\d) why='(?P<why>[^']*)' at='(?P<at>[^']*)' "
        r"before-native-apply=yes$"),
    "committed": re.compile(
        r"\[sahur:choice\] committed client=(?P<client>\d+) slug='(?P<slug>[^']*)' fuse-reset=yes$"),
    "ignored": re.compile(
        r"\[sahur:choice\] ignored wardrobe RPC client=(?P<client>\d+) local=(?P<local>\d+) "
        r"reason=remote-owner$"),
    # Diagnostics that may accompany the four above.
    "rejected_persist": re.compile(
        r"\[sahur:choice\] rejected wardrobe pick client=(?P<client>\d+) db=(?P<db>\d+) "
        r"slug='(?P<slug>[^']*)' reason=persist-failed path='(?P<path>[^']*)'; "
        r"skipping the RPC and keeping the previous wanted state$"),
    "rejected_row": re.compile(
        r"\[sahur:choice\] rejected wardrobe pick client=(?P<client>\d+) db=(?P<db>\d+) "
        r"reason=(?P<reason>[a-z-]+) rows=(?P<rows>-?\d+) name='(?P<name>[^']*)' "
        r"scene='(?P<scene>[^']*)'; wanted state untouched$"),
    "native_row": re.compile(
        r"\[sahur:choice\] db=(?P<db>\d+) '(?P<name>[^']*)' is not one of ours -> the game's own "
        r"row, recorded as the native choice$"),
    "unattributed": re.compile(
        r"\[random:join\] wardrobe RPC client=(?P<client>\d+) index=(?P<index>\d+) not attributed: "
        r"no local network session -> counted, not adopted$"),
    # The random-on-join side of the contract.
    "roll": re.compile(r"\[random:join\] ROLL\b"),
    "no_roll_manual": re.compile(
        r"\[random:join\] no roll: reason=manual wardrobe pick before the roll\b"),
    "no_roll_explicit": re.compile(
        r"\[random:join\] no roll: reason=an explicit model choice is persisted\b"),
    # The map/lobby guard that must survive, in its reworded form.
    "hold": re.compile(
        r"\[sahur:map\] lobby: keeping the explicit wanted entry '(?P<wanted>[^']*)' - the game "
        r"re-applied (?P<applied>.+?) \(index=(?P<index>-?\d+)\), which is (?P<why>[^;]*); "
        r"not adopting it$"),
    "fuse": re.compile(r"\[sahur:map\] FUSE: .*\(limit (?P<limit>\d+)\)"),
    "fuse_cleared": re.compile(r"\[sahur:map\] fuse cleared by a new choice$"),
    "apply": re.compile(
        r"\[sahur:click\] PlayersAppearanceManager\.SetBaseModelIndex\(client=(?P<client>\d+), "
        r"index=(?P<index>\d+)\)"),
    "want_write_failed": re.compile(r"\[sahur:map\] wanted state write failed: "),
    "wanted_model": re.compile(r"\[sahur:map\] wanted model -> "),
}

# The pre-fix message. A build that still prints it is the build that discarded the pick.
OLD_WORDING = "stale join-time row"
NEW_WORDING = "unprovenanced applied row"

TS_RE = re.compile(r"^(?P<date>\d{4}-\d{2}-\d{2})T(?P<time>\d{2}:\d{2}:\d{2})"
                   r"(?:\.(?P<frac>\d+))?(?P<tz>Z|[+-]\d{2}:?\d{2})?$")


def parse_ts(text: str):
    """ISO-8601 (DateTime.UtcNow.ToString("o"), 7 fractional digits) -> naive UTC, else None.

    The C# side writes 7 fraction digits; strptime's %f takes at most 6, so the fraction is
    truncated here instead of dropping the value (comparing `at` is one of the checks).
    """
    if not text:
        return None
    m = TS_RE.match(text.strip())
    if not m:
        return None
    frac = (m.group("frac") or "0")[:6].ljust(6, "0")
    tz = m.group("tz") or ""
    tz = "+0000" if tz == "Z" else tz.replace(":", "")
    fmt = "%Y-%m-%dT%H:%M:%S.%f%z" if tz else "%Y-%m-%dT%H:%M:%S.%f"
    try:
        dt = datetime.strptime(f"{m.group('date')}T{m.group('time')}.{frac}{tz}", fmt)
    except ValueError:
        return None
    if dt.tzinfo is not None:
        dt = dt.astimezone(timezone.utc).replace(tzinfo=None)
    return dt


@dataclass
class Event:
    kind: str
    pos: int                 # line number in the log
    line: str
    fields: dict
    ts: object = None        # parsed `at` where the line carries one


def parse_log(text: str):
    events = []
    for pos, raw in enumerate(text.splitlines()):
        line = raw.rstrip()
        body = LINE_TS.sub("", line)
        for kind, pat in PATTERNS.items():
            m = pat.search(body)
            if not m:
                continue
            fields = {k: v for k, v in m.groupdict().items()}
            ts = parse_ts(fields.get("at", "")) if "at" in fields else None
            events.append(Event(kind, pos, line, fields, ts))
            break
    return events


def parse_cfg(text: str):
    cfg = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line or "=" not in line:
            continue
        key, _, val = line.partition("=")
        cfg[key.strip()] = val.strip()
    return cfg


def same_record(a: dict, b: dict) -> bool:
    keys = ("version", "slug", "label", "why", "explicit", "at")
    return all((a.get(k) or "") == (b.get(k) or "") for k in keys)


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
    def verdict(self) -> str:
        return "PASS" if not self.failures else "FAIL"


# --------------------------------------------------------------------------------------------
# generic invariants - the ordering the fix is required to have, in every scenario
# --------------------------------------------------------------------------------------------

def check_global(rep: Report, events, before, after, manifest):
    picks = [e for e in events if e.kind == "pick"]
    persisted = [e for e in events if e.kind == "persisted"]
    committed = [e for e in events if e.kind == "committed"]
    applies = [e for e in events if e.kind == "apply"]

    # G1 persist-before-commit: every commit has an earlier persist of the same slug.
    for c in committed:
        ok = any(p.fields["slug"] == c.fields["slug"] and p.pos < c.pos for p in persisted)
        rep.need(ok, f"commit at line {c.pos + 1} (slug='{c.fields['slug']}') has no earlier "
                     "'persisted' line with the same slug - committed a value that was not on disk")

    # G2 a persist that never committed: memory did not follow the file.
    for p in persisted:
        ok = any(c.fields["slug"] == p.fields["slug"] and c.pos > p.pos for c in committed)
        rep.need(ok, f"persist at line {p.pos + 1} (slug='{p.fields['slug']}') was never followed "
                     "by a 'committed' line - the file was rewritten but memory kept the old choice")

    # G3 the file is the last commit, and the timestamp is the one that was logged.
    if committed:
        last = committed[-1]
        rep.need(last.fields["slug"] == (after.get("slug") or ""),
                 f"last commit slug='{last.fields['slug']}' but the AFTER cfg says "
                 f"slug='{after.get('slug', '')}' - the record never landed")
        rep.need(after.get("explicit") == "1",
                 f"AFTER cfg explicit='{after.get('explicit', '')}', a deliberate pick must be "
                 "written explicit=1 or it is adopted over on the next join")
        src = next((p for p in reversed(persisted) if p.fields["slug"] == last.fields["slug"]), None)
        if src is not None:
            rep.need(src.fields["at"] == after.get("at"),
                     f"persisted at='{src.fields['at']}' but the cfg carries at='{after.get('at')}' "
                     "- not the record the pick wrote")

    # G4 apply-after-commit, exactly once: the original RPC is allowed only after the record lands.
    require_apply = bool(manifest.get("require_apply", True))
    probe_present = bool(applies) or bool(manifest.get("require_apply"))
    if committed and probe_present:
        last = committed[-1]
        after_commit = [a for a in applies if a.pos > last.pos]
        db = manifest.get("picked_db")
        if db is not None:
            after_commit = [a for a in after_commit if int(a.fields["index"]) == int(db)]
        check = rep.need if require_apply else rep.warn
        check(len(after_commit) == 1,
              f"the row was applied {len(after_commit)} time(s) after the commit (expected exactly "
              "1: the game's own RPC, not a second native rebuild)")
    # A failed write must not be followed by an apply at all (the RPC is skipped on purpose).
    rejects = [e for e in events if e.kind == "rejected_persist"]
    if rejects:
        late = [a for a in applies if a.pos > rejects[-1].pos]
        msg = (f"the row was applied at line {late[0].pos + 1} after the record failed to persist "
               "- a pick that cannot be recorded must not be applied") if late else "no apply after rejection"
        rep.need(not late, msg)

    # G5 the reworded hold message is the shipped one.
    all_text = "\n".join(e.line for e in events)
    rep.need(OLD_WORDING not in all_text,
             "the log still carries the pre-fix wording 'stale join-time row' - that message "
             "belongs to the build that discarded the pick")
    for h in (e for e in events if e.kind == "hold"):
        rep.need(NEW_WORDING in h.line,
                 f"hold line {h.pos + 1} is not the reworded message (got: {h.fields.get('why')!r})")

    # G6 a pick whose slug is not the one the cfg ended up with is the reported failure. A pick
    # whose record could not be written is the one legitimate case of "a pick without a commit":
    # it is covered by the rejected_persist chain instead.
    if picks and not committed and not rejects:
        rep.need(False, f"reported failure mode: pick slug='{picks[-1].fields['slug']}' at line "
                        f"{picks[-1].pos + 1} has NO commit line (the cfg was not rewritten)")
    for p in picks:
        if committed or rejects:
            break
        rep.need((after.get("slug") or "") == p.fields["slug"],
                 f"reported failure mode: pick slug='{p.fields['slug']}' but the AFTER cfg still "
                 f"says slug='{after.get('slug', '')}' - the pick was discarded")

    # G7 the hold line must name the entry the record holds - not the one that was replaced.
    picked_label = manifest.get("picked_label")
    for h in (e for e in events if e.kind == "hold"):
        if not rep.need(picked_label is None or h.fields.get("wanted") == picked_label,
                        f"hold line {h.pos + 1} kept '{h.fields.get('wanted')}' although the "
                        f"player picked '{picked_label}' - this is the discarded-pick pattern"):
            break


# --------------------------------------------------------------------------------------------
# per-case chains
# --------------------------------------------------------------------------------------------

def kinds(events, kind):
    return [e for e in events if e.kind == kind]


def check_local_pick(rep: Report, events, before, after, m):
    picks, persisted, committed = kinds(events, "pick"), kinds(events, "persisted"), kinds(events, "committed")
    rep.need(bool(picks), "no local wardrobe pick line - the scenario did not exercise the RPC prefix")
    rep.need(bool(persisted), "no 'persisted' line - the pick was never written to the cfg")
    rep.need(bool(committed), "no 'committed' line - the pick was never adopted in memory")
    if not (picks and persisted and committed):
        return
    pick, p, c = picks[0], persisted[0], committed[0]
    rep.need(pick.fields["slug"] == m.get("picked_slug", pick.fields["slug"]),
             f"pick logged slug='{pick.fields['slug']}', expected '{m.get('picked_slug')}'")
    rep.need(pick.fields["client"] == str(m.get("client", pick.fields["client"])),
             f"pick logged client={pick.fields['client']}, expected {m.get('client')}")
    if m.get("picked_db") is not None:
        rep.need(int(pick.fields["db"]) == int(m["picked_db"]),
                 f"pick logged db={pick.fields['db']}, expected {m['picked_db']} (row resolution "
                 "must use the CURRENT database, not a remembered index)")
    rep.need(pick.fields["source"] == "WardrobeController.OnModelSelectedServerRpc",
             f"provenance is not the wardrobe select RPC (source='{pick.fields['source']}')")
    rep.need(pick.pos < p.pos < c.pos,
             "the order is not pick -> persist -> commit")
    rep.need(p.fields["why"] == "deliberate wardrobe pick",
             f"persisted why='{p.fields['why']}', expected 'deliberate wardrobe pick'")
    rep.need(p.fields["slug"] == pick.fields["slug"], "persisted a different slug than the pick")
    rep.need((after.get("slug") or "") == pick.fields["slug"],
             f"AFTER cfg slug='{after.get('slug', '')}', pick slug='{pick.fields['slug']}'")
    rep.need((after.get("why") or "") == "deliberate wardrobe pick",
             f"AFTER cfg why='{after.get('why', '')}'")
    rep.need(not same_record(before, after),
             "the BEFORE and AFTER cfgs are identical - this is the reported failure (the pick "
             "was applied but never recorded)")
    bt, at = parse_ts(before.get("at")), parse_ts(after.get("at"))
    if bt and at:
        rep.need(at > bt, "AFTER cfg `at` did not advance - the record was not refreshed")
    rep.need(not kinds(events, "rejected_persist") and not kinds(events, "rejected_row"),
             "the pick was rejected, which this case does not allow")


def check_repick(rep: Report, events, before, after, m):
    check_local_pick(rep, events, before, after, m)
    rep.need((after.get("slug") or "") == (before.get("slug") or ""),
             "a re-selection of the already-wanted entry changed the slug")
    rep.need((after.get("why") or "") == "deliberate wardrobe pick",
             f"AFTER cfg why='{after.get('why', '')}' - a re-selection must refresh `why`")
    bt, at = parse_ts(before.get("at")), parse_ts(after.get("at"))
    rep.need(bt is not None and at is not None and at > bt,
             "a re-selection of the already-wanted entry did not refresh `at`")


def check_remote_owner(rep: Report, events, before, after, m):
    ig = kinds(events, "ignored")
    rep.need(bool(ig), "no remote-owner line - the scenario did not exercise ownership")
    if ig:
        rep.need(ig[0].fields["client"] != ig[0].fields["local"],
                 f"the ignored RPC was logged as remote-owner but client={ig[0].fields['client']} "
                 f"== local={ig[0].fields['local']}")
    rep.need(not kinds(events, "pick"),
             "a remote player's RPC produced a local pick line")
    rep.need(not kinds(events, "committed"), "a remote player's RPC committed a wanted entry")
    rep.need(same_record(before, after),
             f"a remote player's RPC rewrote the cfg (slug {before.get('slug')!r} -> "
             f"{after.get('slug')!r}); other players' picks must not move this instance's state")


def check_persist_failed(rep: Report, events, before, after, m):
    rp = kinds(events, "rejected_persist")
    rep.need(bool(rp), "no persist-failed rejection line")
    rep.need(not kinds(events, "persisted"), "a failed write still logged 'persisted'")
    rep.need(not kinds(events, "committed"),
             "the pick was committed after its record failed to persist")
    rep.need(same_record(before, after),
             "the cfg changed although the write failed - the previous record must stand")
    if rp:
        rep.need(kinds(events, "want_write_failed"),
                 "the rejection was logged without the underlying write failure")
        rep.need(not [a for a in kinds(events, "apply") if a.pos > rp[-1].pos],
                 "the original RPC was allowed after the rejection - it must be skipped")


def check_explicit_native(rep: Report, events, before, after, m):
    rep.need((before.get("slug") or "") == "",
             "the scenario's BEFORE cfg is not a native choice (slug='')")
    rep.need(before.get("explicit") == "1", "the scenario's BEFORE cfg is not explicit=1")
    rep.need(same_record(before, after), "the cfg changed; a suppressed roll writes nothing")
    rep.need(bool(kinds(events, "no_roll_explicit")),
             "an explicit native choice did not suppress the join roll (no "
             "'no roll: reason=an explicit model choice is persisted' line)")
    rep.need(not kinds(events, "roll"),
             "the join roll fired although the record is an explicit native choice")


def check_explicit_precedence(rep: Report, events, before, after, m):
    rep.need(before.get("explicit") == "1", "the scenario's BEFORE cfg is not explicit=1")
    rep.need(same_record(before, after), "the cfg changed; an explicit choice suppresses the roll")
    rep.need(bool(kinds(events, "no_roll_explicit")),
             "the persisted explicit choice did not outrank the roll")
    rep.need(not kinds(events, "roll"), "the join roll fired over an explicit choice")


def check_roll_skip_manual(rep: Report, events, before, after, m):
    rep.need(bool(kinds(events, "no_roll_manual")),
             "a local pick before the roll did not cancel it (no 'no roll: reason=manual "
             "wardrobe pick before the roll' line)")
    rep.need(not kinds(events, "roll"), "the roll fired although a local pick landed first")
    rep.need(same_record(before, after), "the cfg changed in a no-roll scenario")


def check_fuse_reset(rep: Report, events, before, after, m):
    check_local_pick(rep, events, before, after, m)
    fuse = kinds(events, "fuse")
    rep.need(bool(fuse), "the fuse never tripped in the scenario")
    for f in fuse:
        rep.need(f.fields["limit"] == "4",
                 f"the fuse threshold was changed (limit={f.fields['limit']}, expected 4)")
    cleared = kinds(events, "fuse_cleared")
    committed = kinds(events, "committed")
    rep.need(bool(committed), "the new choice after the fuse did not commit")
    rep.need(bool(cleared), "the new choice did not clear the fuse")
    if fuse and cleared:
        rep.need(fuse[0].pos < cleared[0].pos, "the fuse was cleared before it tripped")
    if cleared and committed:
        rep.need(cleared[0].pos <= committed[0].pos,
                 "the commit happened before the fuse was cleared")
    rep.need(not same_record(before, after), "the new choice did not reach the cfg")


def check_map_hold(rep: Report, events, before, after, m):
    holds = kinds(events, "hold")
    rep.need(bool(holds), "no hold line - the guard did not run")
    rep.need(same_record(before, after),
             "the hold rewrote the cfg; the guard is a hold, not a choice")
    for h in holds:
        rep.need(NEW_WORDING in h.line, "the hold line is not the reworded message")
        rep.need(h.fields["wanted"] == (after.get("label") or h.fields["wanted"]),
                 f"the hold kept '{h.fields['wanted']}' but the record holds "
                 f"'{after.get('label', '')}'")
        if m.get("applied_label"):
            rep.need(h.fields["applied"].strip("'\"") != h.fields["wanted"],
                     "the hold line treats the applied row as the wanted entry")
    rep.need(not kinds(events, "pick"),
             "a hold scenario must not contain a local pick - the guard exists for applied rows "
             "with no pick behind them")


CASES = {
    "local-pick": check_local_pick,
    "repick": check_repick,
    "remote-owner": check_remote_owner,
    "persist-failed": check_persist_failed,
    "explicit-native": check_explicit_native,
    "explicit-precedence": check_explicit_precedence,
    "roll-skip-manual": check_roll_skip_manual,
    "fuse-reset": check_fuse_reset,
    "map-hold": check_map_hold,
}


# --------------------------------------------------------------------------------------------
# driver
# --------------------------------------------------------------------------------------------

def load_case(case_dir: Path):
    manifest_path = case_dir / "manifest.json"
    if not manifest_path.exists():
        raise SystemExit(f"error: {manifest_path} is missing")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    name = manifest.get("case")
    if name not in CASES:
        raise SystemExit(f"error: {manifest_path}: unknown case {name!r} (known: "
                         f"{', '.join(sorted(CASES))})")
    log_path = case_dir / manifest.get("log", "log.txt")
    before_path = case_dir / manifest.get("before", "before/alterum-wanted-model.cfg")
    after_path = case_dir / manifest.get("after", "after/alterum-wanted-model.cfg")
    for p in (log_path, before_path, after_path):
        if not p.exists():
            raise SystemExit(f"error: {p} is missing (needed by {manifest_path})")
    return (manifest, log_path.read_text(encoding="utf-8", errors="replace"),
            parse_cfg(before_path.read_text(encoding="utf-8")),
            parse_cfg(after_path.read_text(encoding="utf-8")))


def check_artifacts(rep: Report, case_dir: Path, manifest):
    for art in manifest.get("artifacts") or []:
        rel = art.get("path")
        if not rel:
            rep.need(False, "artifact entry without a path")
            continue
        path = case_dir / rel
        if not rep.need(path.exists(), f"artifact '{rel}' is missing"):
            continue
        want = art.get("sha256")
        if want:
            got = hashlib.sha256(path.read_bytes()).hexdigest()
            rep.need(got == want, f"artifact '{rel}' sha256 {got[:12]}... != {want[:12]}... "
                                  "(the chain of custody does not match the manifest)")


def verify_case(case_dir: Path) -> Report:
    manifest, log_text, before, after = load_case(case_dir)
    name = manifest["case"]
    rep = Report(name)
    events = parse_log(log_text)

    check_global(rep, events, before, after, manifest)
    CASES[name](rep, events, before, after, manifest)
    check_artifacts(rep, case_dir, manifest)
    return rep


def print_report(rep: Report, stream=sys.stdout):
    stream.write(f"VERDICT {rep.verdict} {rep.case} ({rep.checks} checks)\n")
    for msg in rep.failures:
        stream.write(f"  FAIL: {msg}\n")
    for msg in rep.warnings:
        stream.write(f"  WARN: {msg}\n")


# --------------------------------------------------------------------------------------------
# self-test: fabricated inputs, good and bad, one directory per scenario
# --------------------------------------------------------------------------------------------

GOOD_CFG_BEFORE = """version=1
slug=tc2-soldier
label=TC2 Soldier
why=deliberate wardrobe pick
explicit=1
at=2026-11-15T09:25:53.0000000Z
"""

GOOD_CFG_AFTER = """version=1
slug=golubiro
label=Golubiro
why=deliberate wardrobe pick
explicit=1
at=2026-11-16T04:54:41.1245678Z
"""

PICK_LINE = ("04:54:40.100 [sahur:choice] local wardrobe pick client=2 db=12 slug='golubiro' "
             "source=WardrobeController.OnModelSelectedServerRpc scene='Lobby'")
PERSIST_LINE = ("04:54:41.120 [sahur:choice] persisted path='C:/Users/b/AppData/LocalLow/"
                "Alterum/alterum-wanted-model.cfg' slug='golubiro' explicit=1 "
                "why='deliberate wardrobe pick' at='2026-11-16T04:54:41.1245678Z' "
                "before-native-apply=yes")
COMMIT_LINE = "04:54:41.121 [sahur:choice] committed client=2 slug='golubiro' fuse-reset=yes"
APPLY_LINE = ("04:54:41.140 [sahur:click] PlayersAppearanceManager.SetBaseModelIndex(client=2, "
              "index=12) (the native wardrobe selection RPC, the game's own call)")


def write_case(root: Path, dirname: str, log: str, before: str, after: str, **manifest):
    d = root / dirname
    (d / "before").mkdir(parents=True, exist_ok=True)
    (d / "after").mkdir(parents=True, exist_ok=True)
    (d / "log.txt").write_text(log, encoding="utf-8")
    (d / "before" / "alterum-wanted-model.cfg").write_text(before, encoding="utf-8")
    (d / "after" / "alterum-wanted-model.cfg").write_text(after, encoding="utf-8")
    m = {"case": manifest.pop("case")}
    m.update(manifest)
    (d / "manifest.json").write_text(json.dumps(m, indent=2), encoding="utf-8")
    return d


def build_good(root: Path, name: str, log: str, before: str, after: str, tag: str = "",
               **manifest):
    suffix = f"-{tag}" if tag else ""
    return write_case(root, f"good-{name}{suffix}", log, before, after, case=name, **manifest)


def build_bad(root: Path, name: str, log: str, before: str, after: str, tag: str = "",
              **manifest):
    suffix = f"-{tag}" if tag else ""
    return write_case(root, f"bad-{name}{suffix}", log, before, after, case=name, **manifest)


def self_test() -> int:
    ok = True
    with tempfile.TemporaryDirectory(prefix="wanted-evidence-") as tmp:
        root = Path(tmp)
        cases = []

        # ---- good -------------------------------------------------------------------------
        cases.append(("PASS", build_good(
            root, "local-pick", "\n".join([PICK_LINE, PERSIST_LINE, COMMIT_LINE, APPLY_LINE]) + "\n",
            GOOD_CFG_BEFORE, GOOD_CFG_AFTER,
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2,
            require_apply=True)))

        cases.append(("PASS", build_good(
            root, "repick",
            "\n".join([
                PICK_LINE,
                PERSIST_LINE.replace("2026-11-16T04:54:41.1245678Z", "2026-11-16T05:10:02.0000001Z"),
                COMMIT_LINE, APPLY_LINE]) + "\n",
            GOOD_CFG_AFTER, GOOD_CFG_AFTER.replace("2026-11-16T04:54:41.1245678Z",
                                                   "2026-11-16T05:10:02.0000001Z"),
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2)))

        cases.append(("PASS", build_good(
            root, "remote-owner",
            "04:55:02.000 [sahur:choice] ignored wardrobe RPC client=1 local=2 reason=remote-owner\n",
            GOOD_CFG_AFTER, GOOD_CFG_AFTER, client=2, local=2)))

        cases.append(("PASS", build_good(
            root, "persist-failed",
            "\n".join([
                PICK_LINE,
                "04:54:40.900 [sahur:map] wanted state write failed: Access to the path is denied.",
                "04:54:40.901 [sahur:choice] rejected wardrobe pick client=2 db=12 "
                "slug='golubiro' reason=persist-failed path='C:/x/alterum-wanted-model.cfg'; "
                "skipping the RPC and keeping the previous wanted state"]) + "\n",
            GOOD_CFG_BEFORE, GOOD_CFG_BEFORE, picked_db=12, client=2)))

        cases.append(("PASS", build_good(
            root, "explicit-native",
            "04:56:00.000 [random:join] no roll: reason=an explicit model choice is persisted "
            "(the game's own (native) row, explicit=1), and it outranks the roll on a fresh join "
            "- re-applied=yes (pool=15, lobby=7, client=2, pid=51976)\n",
            GOOD_CFG_BEFORE.replace("slug=tc2-soldier", "slug=").replace(
                "label=TC2 Soldier", "label="), 
            GOOD_CFG_BEFORE.replace("slug=tc2-soldier", "slug=").replace(
                "label=TC2 Soldier", "label="))))

        cases.append(("PASS", build_good(
            root, "explicit-precedence",
            "04:56:10.000 [random:join] no roll: reason=an explicit model choice is persisted "
            "('Golubiro' entry=12, explicit=1), and it outranks the roll on a fresh join - "
            "re-applied=yes (pool=15, lobby=7, client=2, pid=51976)\n",
            GOOD_CFG_AFTER, GOOD_CFG_AFTER)))

        cases.append(("PASS", build_good(
            root, "roll-skip-manual",
            "04:56:20.000 [random:join] no roll: reason=manual wardrobe pick before the roll "
            "(picks 0 -> 1), manual wins (lobby=7 client=2)\n",
            GOOD_CFG_AFTER, GOOD_CFG_AFTER)))

        cases.append(("PASS", build_good(
            root, "fuse-reset",
            "\n".join([
                "04:57:00.000 [sahur:map] FUSE: the local body was rebuilt 5 times in 6s "
                "(limit 4) - STOPPING all row forcing and settling on the native body. "
                "wanted='Golubiro' applied='Model Selectable(Clone)' at t=812.4. A new choice "
                "(wardrobe click / select / body request) clears this.",
                "04:57:05.000 [sahur:map] wanted model -> <native> (why=fuse: settling on the "
                "native body)",
                "04:57:09.000 [sahur:map] fuse cleared by a new choice",
                PICK_LINE, PERSIST_LINE, COMMIT_LINE, APPLY_LINE]) + "\n",
            GOOD_CFG_BEFORE, GOOD_CFG_AFTER,
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2)))

        cases.append(("PASS", build_good(
            root, "map-hold",
            "05:00:00.000 [sahur:map] lobby: keeping the explicit wanted entry 'Golubiro' - the "
            "game re-applied 'TC2 Soldier' (index=6), which is an unprovenanced applied row (no "
            "wardrobe pick behind it); not adopting it\n",
            GOOD_CFG_AFTER, GOOD_CFG_AFTER, applied_label="TC2 Soldier")))

        # ---- bad --------------------------------------------------------------------------
        # 1. The reported failure, verbatim in shape: the pick is logged, the guard keeps the old
        #    entry, the cfg is untouched, no commit line.
        cases.append(("FAIL", build_bad(
            root, "local-pick", tag="reported",
            log="\n".join([
                PICK_LINE,
                "04:54:40.200 [sahur:map] lobby: keeping the explicit wanted entry 'TC2 Soldier' "
                "- the game re-applied 'Golubiro' (index=12), which is an unprovenanced applied "
                "row (no wardrobe pick behind it); not adopting it"]) + "\n",
            before=GOOD_CFG_BEFORE, after=GOOD_CFG_BEFORE,
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2)))

        # 2. Committed without a persisted line: memory moved, the file did not.
        cases.append(("FAIL", build_bad(
            root, "local-pick", tag="no-persist",
            log="\n".join([PICK_LINE, COMMIT_LINE, APPLY_LINE]) + "\n",
            before=GOOD_CFG_BEFORE, after=GOOD_CFG_AFTER,
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2)))

        # 3. Persisted but never committed.
        cases.append(("FAIL", build_bad(
            root, "local-pick", tag="no-commit",
            log="\n".join([PICK_LINE, PERSIST_LINE, APPLY_LINE]) + "\n",
            before=GOOD_CFG_BEFORE, after=GOOD_CFG_AFTER,
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2)))

        # 4. A remote player's RPC moved this instance's state.
        cases.append(("FAIL", build_bad(
            root, "remote-owner",
            "04:55:02.000 [sahur:choice] ignored wardrobe RPC client=1 local=2 reason=remote-owner\n",
            GOOD_CFG_BEFORE, GOOD_CFG_AFTER, client=2, local=2)))

        # 5. persist-failed but the RPC was allowed anyway.
        cases.append(("FAIL", build_bad(
            root, "persist-failed",
            "\n".join([
                PICK_LINE,
                "04:54:40.900 [sahur:map] wanted state write failed: Access to the path is denied.",
                "04:54:40.901 [sahur:choice] rejected wardrobe pick client=2 db=12 "
                "slug='golubiro' reason=persist-failed path='C:/x/alterum-wanted-model.cfg'; "
                "skipping the RPC and keeping the previous wanted state",
                APPLY_LINE]) + "\n",
            GOOD_CFG_BEFORE, GOOD_CFG_BEFORE, picked_db=12, client=2)))

        # 6. The pre-fix wording is still shipped.
        cases.append(("FAIL", build_bad(
            root, "local-pick", tag="old-wording",
            log="\n".join([
                PICK_LINE, PERSIST_LINE, COMMIT_LINE, APPLY_LINE,
                "04:54:42.000 [sahur:map] lobby: keeping the explicit wanted entry 'Golubiro' - "
                "the game re-applied 'TC2 Soldier' (index=6), which is the stale join-time row; "
                "not adopting it"]) + "\n",
            before=GOOD_CFG_BEFORE, after=GOOD_CFG_AFTER,
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2)))

        # 7. The applied row was applied twice (a second native rebuild).
        cases.append(("FAIL", build_bad(
            root, "local-pick", tag="double-apply",
            log="\n".join([PICK_LINE, PERSIST_LINE, COMMIT_LINE, APPLY_LINE, APPLY_LINE]) + "\n",
            before=GOOD_CFG_BEFORE, after=GOOD_CFG_AFTER,
            picked_slug="golubiro", picked_label="Golubiro", picked_db=12, client=2)))

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
                    help="scenario directory (manifest.json + log + before/after cfgs)")
    ap.add_argument("--self-test", action="store_true",
                    help="run the fabricated good/bad self-test")
    args = ap.parse_args(argv)

    if args.self_test:
        return self_test()
    if not args.case:
        ap.error("pass --case DIR [DIR ...] or --self-test")

    bad = 0
    for d in args.case:
        rep = verify_case(Path(d))
        print_report(rep)
        if rep.verdict != "PASS":
            bad += 1
    return 0 if bad == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
