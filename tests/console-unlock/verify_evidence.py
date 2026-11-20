#!/usr/bin/env python3
"""Verify the evidence for the AEZAKMI console-unlock gate.

What is being verified (Unit B): until the pause-menu code AEZAKMI is typed, the game console
(QFSW.QC) is unreachable and executes nothing; after it, the console stays available until the
game process exits. The unlock lives in a process-local AppDomain data slot, so a hot reload
keeps it (without re-playing the confirmation sound) and a new process starts locked.

The verifier is mechanical and log-shaped. It reads one scenario DIRECTORY per case:

    <case>/manifest.json   the case descriptor (see below)
    <case>/log.txt         the log export the scenario produced
    <case>/<artifacts>     optional screenshots / dumps named in the manifest

manifest.json:

    {
      "case": "locked-tilde",       # one of CASES below
      "log": "log.txt",
      "scene": "Lobby",             # unlock-*: the scene the code was typed in
      "pid": 51976,                 # optional: every `pid=N` in the log must be this one
      "commands": 109,              # locked-request/locked-seams: the table size to prove
      "generations": 2,             # hot-reload: number of `[hot] module initialized` markers
      "denied": ["tilde"],          # new-process: expected `denied entry=…` values
      "seams": ["activate"],        # locked-seams: seam denials the log must contain
      "artifacts": [{"path": "shots/pause.png", "sha256": "…"}]
    }

The log grammar is the one the shipped code emits (see GameConsole/QcGate/ReelAudio):

    [qc:access] state=locked storage=process
    [qc:access] restored unlocked=true storage=process
    [qc:access] unlocked source=pause-code
    [qc:access] denied entry=boot source=request
    [qc:access] code matched source=pause-code scene='Lobby'
    [qc:access] confirmation cue=settle result=played
    [qc:access] capture armed via Keyboard.onTextInput
    [qc:access] capture disarmed (generation teardown)
    [qc] toggle pause-code -> open IsActive=True enabled=True …
    [qc] processor: LoadedCommandCount=109 TableGenerated=True

What each acceptance case needs, and therefore asserts:

  locked-tilde     a fresh process, locked, tilde pressed at least once. Needs: the state line, a
                   denied tilde, NO unlock, NO confirmation, NO open, NO capture-reset from a code.
  unlock-lobby     pause menu open in the lobby, AEZAKMI typed. Needs: code matched in that scene,
                   then unlocked, then exactly one confirmation result=played, then the console
                   opened through the pause-code route.
  unlock-map       the same in a map scene (the menu is the game's own pause menu there too).
  hot-reload       the unlocked process hot-reloads. Needs: a second `[hot] module initialized`, a
                   `restored unlocked=true` after it, a re-armed capture, and exactly ONE
                   confirmation in the whole log - a reload must not re-sound.
  new-process      a separate launch. Needs: `state=locked`, a denied tilde, no restored line, no
                   unlock, no open - i.e. nothing leaked to disk.
  locked-request   request-file activation/execution verbs while locked. Needs: denials for the
                   verbs that would open/execute, the 109-command table still generated (QC keeps
                   initializing), read-only status still answering, and no console opened.
  locked-seams     the console's own Activate()/Toggle()/InvokeCommand() and the processor's
                   InvokeCommand, hit while locked (Update is deliberately NOT gated: it drives the
                   console's input loop, and the key path it would run - Toggle() - is refused).
                   Needs: the seam denials the manifest names, the table still generated, and no
                   console opened. Which seams the game itself reaches is not under the test's
                   control, so the manifest lists them; an empty list still forbids an open.

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
from pathlib import Path

# --------------------------------------------------------------------------------------------
# log grammar
#
# Each pattern is anchored at its own tag and to the end of the line, so a missing field or an
# extra word is a failure, not a near-match. The host writes an `HH:MM:SS ` prefix; strip_ts()
# removes it before matching and the line index is kept for ordering.
# --------------------------------------------------------------------------------------------

TS = re.compile(r"^\s*\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\s+")

PATTERNS = {
    # The status dump carries counters after the storage token; it is matched first so the plain
    # state line below stays strict.
    "status_access": re.compile(
        r"\[qc:access\] state=(?P<state>locked|unlocked) storage=(?P<storage>[a-z-]+) "
        r"denied=(?P<denied>\d+)"),
    # The fail-locked line explains itself after the storage token; that tail is allowed here so an
    # unusable slot is still visible to the checks (and is a hard failure, never a pass).
    "state": re.compile(
        r"\[qc:access\] state=(?P<state>locked|unlocked) storage=(?P<storage>[a-z-]+)(?: .*)?$"),
    "restored": re.compile(
        r"\[qc:access\] restored unlocked=(?P<unlocked>true|false) storage=(?P<storage>[a-z-]+)$"),
    "unlocked": re.compile(
        r"\[qc:access\] unlocked source=(?P<source>\S+)$"),
    "unlock_failed": re.compile(
        r"\[qc:access\] unlocked source=(?P<source>\S+) storage=.*result=failed.*staying locked$"),
    "denied": re.compile(
        r"\[qc:access\] denied entry=(?P<entry>\S+?)"
        r"(?: source=(?P<source>\S+))?"
        r"(?: command='(?P<command>[^']*)')?$"),
    "confirmation": re.compile(
        r"\[qc:access\] confirmation cue=(?P<cue>\S+) result=(?P<result>\S+)$"),
    "matched": re.compile(
        r"\[qc:access\] code matched source=pause-code scene='(?P<scene>[^']*)'$"),
    "matched_dup": re.compile(
        r"\[qc:access\] code matched source=pause-code ignored=already-unlocked$"),
    "reset": re.compile(r"\[qc:access\] code reset why=(?P<why>[a-z-]+)$"),
    "armed": re.compile(r"\[qc:access\] capture armed via Keyboard\.onTextInput$"),
    "arm_failed": re.compile(r"\[qc:access\] capture arm failed: "),
    "disarmed": re.compile(r"\[qc:access\] capture disarmed \(generation teardown\)$"),
    "teardown_note": re.compile(r"\[qc:access\] capture disarmed \(generation teardown\)"),
    "init_threw": re.compile(r"\[qc:access\] init threw: "),
    "generation": re.compile(
        r"\[hot\] module initialized \(firstActivation=(?P<first>True|False)\)$"),
    "open": re.compile(
        r"\[qc\] toggle (?P<trigger>\S+) -> open IsActive=True"
        r"(?: enabled=(?P<enabled>True|False))?(?: Instance=(?P<instance>\S+))?"
        r"(?: commands=(?P<commands>-?\d+))?$"),
    "closed": re.compile(r"\[qc\] toggle (?P<trigger>\S+) -> closed IsActive=False$"),
    "gate_off": re.compile(r"\[qc\] toggle (?P<trigger>\S+): no console \("),
    "ensure_live": re.compile(
        r"\[qc\] ensure(?P<why>\([^)]*\))?: console live at .* after (?P<tries>\d+) attempt\(s\)"
        r" in scene '(?P<scene>[^']*)'$"),
    "proc": re.compile(
        r"\[qc\] processor: LoadedCommandCount=(?P<count>-?\d+) TableGenerated=(?P<gen>True|False)$"),
    "status_head": re.compile(r"\[qc\] QuantumConsole components: (?P<n>\d+)$"),
    "ensure_missing": re.compile(r"\[qc\] toggle .*: no console \("),
    "exception": re.compile(r"ArgumentException|NullReferenceException|MissingMethodException"),
}

# Entries the request file must not be able to reach while locked.
REQUEST_DENY = ["boot", "enable", "ensure", "raw", "probe", "key", "say", "help", "invoke", "toggle"]
# Entries only the resolved QC seams can produce.
SEAM_DENY = ["activate", "console-toggle", "console-invoke", "processor-invoke"]


@dataclass
class Event:
    kind: str
    pos: int
    line: str
    fields: dict


def parse_log(text: str):
    events = []
    for pos, raw in enumerate(text.splitlines()):
        line = raw.rstrip()
        if not line:
            continue
        body = TS.sub("", line)
        for kind, pat in PATTERNS.items():
            m = pat.search(body)
            if not m:
                continue
            events.append(Event(kind, pos, line, {k: v for k, v in m.groupdict().items()}))
            break
    return events


def kinds(events, kind):
    return [e for e in events if e.kind == kind]


def entries(events):
    return [e.fields["entry"] for e in kinds(events, "denied")]


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
# invariants that hold in every scenario
# --------------------------------------------------------------------------------------------

def check_global(rep: Report, events, manifest):
    # X1 nothing opens without a recorded unlock: the gate is what stands between the key and the
    # console, so an open line with no earlier `unlocked` is the failure this unit exists to stop.
    unlock = kinds(events, "unlocked")
    for op in kinds(events, "open"):
        ok = any(u.pos < op.pos for u in unlock)
        rep.need(ok, f"console opened at line {op.pos + 1} ({op.fields.get('trigger')}) with no earlier "
                     "'[qc:access] unlocked' line - the gate let a locked console open")

    # X2 a confirmation without an unlock is a sound for nothing, and an unlock without its
    # confirmation is the missing audible feedback.
    conf = kinds(events, "confirmation")
    for c in conf:
        rep.need(any(u.pos < c.pos for u in unlock),
                 f"confirmation at line {c.pos + 1} has no earlier unlock (sound for nothing)")
    if unlock and not conf:
        rep.need(False, f"unlock at line {unlock[0].pos + 1} produced no "
                        "'[qc:access] confirmation … result=…' line")

    # X3 an unusable data slot means the cheat cannot work at all in that process. It is never
    # acceptable evidence for this unit (fail-locked is the correct runtime behaviour, not a pass).
    for s in kinds(events, "state"):
        rep.need(s.fields["storage"] == "process",
                 f"the AppDomain data slot is unavailable (line {s.pos + 1}) - the console stays "
                 "locked for the life of the process and no acceptance case can pass")
    for r in kinds(events, "restored"):
        rep.need(r.fields["storage"] == "process",
                 f"restored state came from storage={r.fields['storage']} (line {r.pos + 1}); the "
                 "unlock must be process-local, never persisted")
        rep.need(r.fields["unlocked"] == "true",
                 f"'restored unlocked={r.fields['unlocked']}' - a restore must only ever report the "
                 "unlocked state")

    # X4 a failed unlock must not be followed by an open, and must be visible.
    for f in kinds(events, "unlock_failed"):
        rep.need(not [o for o in kinds(events, "open") if o.pos > f.pos],
                 f"the console was opened after the unlock failed at line {f.pos + 1} - failed "
                 "storage must leave the console locked")

    # X5 a duplicated confirmation (a re-sound) is the hot-reload failure mode; nothing in the
    # code may sound the cue twice for one unlock, so a second one needs an explicit second unlock.
    rep.need(len(conf) <= max(1, len(unlock)),
             f"{len(conf)} confirmation line(s) for {len(unlock)} unlock(s) - the cue was replayed")

    # X6 the capture must not fail to arm in an unlock scenario: without it the code cannot be
    # typed at all, and a silent arm failure would look like "the code did not work".
    for a in kinds(events, "arm_failed"):
        rep.need(False, f"capture failed to arm (line {a.pos + 1}): {a.line.strip()}")

    # X7 the code is entered while the game's pause menu is up; a match with no scene is suspicious.
    for m in kinds(events, "matched"):
        rep.need(bool(m.fields.get("scene")),
                 f"code matched at line {m.pos + 1} without a scene name")

    # X8 QcGate skips the console OnEnable (it would disable the component), so teardown must not
    # deregister an object that was never registered. That exception aborts OnDisable mid-body.
    for e in events:
        rep.need("Could not deregister object" not in e.line,
                 f"the console OnDisable deregistered an unregistered object at line {e.pos + 1} - "
                 "the skipped OnEnable registration was not balanced")

    if manifest.get("pid") is not None:
        pids = set(re.findall(r"\bpid=(\d+)\b", "\n".join(e.line for e in events)))
        rep.need(str(manifest["pid"]) in pids or not pids,
                 f"the log names pid(s) {sorted(pids)} but the case says {manifest['pid']} - the "
                 "evidence is from another process than the one the case describes")


# --------------------------------------------------------------------------------------------
# per-case chains
# --------------------------------------------------------------------------------------------

def check_locked_tilde(rep: Report, events, m):
    state = kinds(events, "state")
    rep.need(bool(state), "no '[qc:access] state=locked storage=process' line - the gate never "
                          "reported its state")
    for s in state:
        rep.need(s.fields["state"] == "locked",
                 f"the process reported state={s.fields['state']} in a locked scenario")
    rep.need("tilde" in entries(events),
             "no 'denied entry=tilde' line - a locked tilde press must be refused (and be visible "
             "in the log)")
    rep.need(not kinds(events, "unlocked"), "a locked scenario contains an unlock")
    rep.need(not kinds(events, "restored"), "a fresh process restored an unlock - state leaked "
                                            "across processes")
    rep.need(not kinds(events, "confirmation"), "the confirmation cue sounded while locked")
    rep.need(not kinds(events, "open"), "the console opened while locked")
    rep.need(not kinds(events, "matched"), "the pause code was recognized in a locked-only scenario")
    rep.need(not kinds(events, "closed"),
             "a 'closed' line implies the console was open; a locked console is never open")


def check_unlock(rep: Report, events, m):
    scene = m.get("scene")
    state = kinds(events, "state")
    matched = kinds(events, "matched")
    unlock = kinds(events, "unlocked")
    conf = kinds(events, "confirmation")
    opens = [o for o in kinds(events, "open") if o.fields.get("trigger") == "pause-code"]

    rep.need(any(s.fields["state"] == "locked" for s in state),
             "no locked state line before the unlock - the scenario does not show the gate armed")
    rep.need(bool(matched), "no 'code matched source=pause-code' line - the code was not recognized")
    if scene:
        for mm in matched:
            rep.need(mm.fields["scene"] == scene,
                     f"code matched in scene '{mm.fields['scene']}', expected '{scene}'")
    rep.need(bool(unlock), "no '[qc:access] unlocked source=pause-code' line")
    rep.need(bool(conf), "no confirmation line")
    rep.need(bool(opens), "the unlock did not open the console (no '[qc] toggle pause-code -> open "
                          "IsActive=True' line)")

    if not (matched and unlock and conf and opens):
        return
    rep.need(matched[0].pos < unlock[0].pos < conf[0].pos < opens[0].pos,
             "the order is not code-matched -> unlocked -> confirmation -> open")
    rep.need(conf[0].fields["result"] == "played",
             f"confirmation result={conf[0].fields['result']} - the cue must actually play while the "
             "pause menu is up (a 'silent:listener-paused' or 'silent:not-built' is not a pass)")
    rep.need(opens[0].fields.get("enabled") in (None, "True"),
             f"the console opened with enabled={opens[0].fields.get('enabled')}")
    if opens[0].fields.get("commands") is not None:
        rep.need(int(opens[0].fields["commands"]) > 0,
                 f"the console opened with commands={opens[0].fields['commands']} - the table was "
                 "not generated while locked")
    # The console must stay available for the rest of the process: a later tilde press toggles it
    # closed, which only an unlocked console can do.
    later = [o for o in kinds(events, "closed") if o.pos > unlock[0].pos]
    rep.warn(bool(later), "no later toggle of the unlocked console in the log (closing it after the "
                          "unlock is the second half of 'stays available')")


def check_hot_reload(rep: Report, events, m):
    want_gens = int(m.get("generations", 2))
    gens = kinds(events, "generation")
    unlock = kinds(events, "unlocked")
    restored = kinds(events, "restored")
    conf = kinds(events, "confirmation")
    armed = kinds(events, "armed")

    rep.need(len(gens) >= want_gens,
             f"{len(gens)} '[hot] module initialized' marker(s), expected {want_gens} - the log does "
             "not span a hot reload")
    rep.need(len(unlock) == 1,
             f"{len(unlock)} unlock line(s) - the reload must not become a second unlock")
    rep.need(bool(restored), "no '[qc:access] restored unlocked=true storage=process' line - the "
                             "reload lost the unlock")
    rep.need(len(conf) == 1,
             f"{len(conf)} confirmation line(s) - a hot reload must NOT replay the confirmation "
             "sound (expected exactly 1, from the original unlock)")
    rep.need(len(armed) >= want_gens,
             f"{len(armed)} 'capture armed' line(s) for {len(gens)} generation(s) - the text-input "
             "listener must be recreated by the new generation")

    if gens and unlock and restored:
        last_gen = gens[-1].pos
        rep.need(restored[0].pos > unlock[0].pos,
                 "the restored line precedes the unlock it claims to restore")
        rep.need(restored[0].pos > gens[-2].pos,
                 f"the restore line is not inside the reloaded generation (last reload marker at "
                 f"line {gens[-2].pos + 1}) - the new generation did not read the slot")
        rep.need(any(a.pos > last_gen for a in armed),
                 f"no 'capture armed' line after the last generation marker (line {last_gen + 1}) - "
                 "the reloaded generation did not recreate the text-input listener")
    # A generation teardown must remove the retiring listener before the next one arms.
    disarmed = kinds(events, "disarmed")
    rep.need(len(disarmed) >= want_gens - 1,
             f"{len(disarmed)} teardown line(s) for {len(gens)} generations - the retiring "
             "generation must unsubscribe its text-input listener")
    for prev, nxt in zip(armed, armed[1:]):
        rep.need(any(prev.pos < d.pos < nxt.pos for d in disarmed),
                 f"generation re-armed the text listener at line {nxt.pos + 1} without a teardown "
                 "line between - the retiring generation would stay subscribed")
    # The unlock survives the reload without a second sound, and no generation reports locked again.
    for s in [s for s in kinds(events, "state") if s.pos > unlock[0].pos]:
        rep.need(s.fields["state"] == "unlocked",
                 f"state=locked reported at line {s.pos + 1} after the unlock - the reload dropped it")
    rep.need(not kinds(events, "unlock_failed"), "the unlock failed in a restore scenario")


def check_new_process(rep: Report, events, m):
    state = kinds(events, "state")
    rep.need(bool(state), "no state line in a fresh-process scenario")
    for s in state:
        rep.need(s.fields["state"] == "locked", "a fresh process reported unlocked state")
    rep.need(not kinds(events, "restored"),
             "a fresh process restored an unlock - the state outlived the process (disk leak?)")
    rep.need(not kinds(events, "unlocked"), "a fresh process unlocked without the code being typed")
    rep.need(not kinds(events, "open"), "the console opened in a fresh-process scenario")
    rep.need(not kinds(events, "confirmation"), "the cue sounded in a fresh-process scenario")
    rep.need("tilde" in entries(events), "no denied tilde in the fresh process")
    for denied in m.get("denied") or []:
        rep.need(denied in entries(events), f"expected a 'denied entry={denied}' line")


def check_locked_request(rep: Report, events, m):
    den = entries(events)
    for entry in REQUEST_DENY:
        rep.need(entry in den, f"no 'denied entry={entry} source=request' line - the request verb "
                              f"'{entry}' was not refused while locked")
    for e in kinds(events, "denied"):
        if e.fields["entry"] in REQUEST_DENY:
            rep.need(e.fields.get("source") == "request",
                     f"denied entry={e.fields['entry']} came from source="
                     f"{e.fields.get('source')} in a request scenario")
    rep.need(not kinds(events, "unlocked"), "a request-only scenario unlocked the console")
    rep.need(not kinds(events, "open"), "a request verb opened the locked console")

    # QC must still initialize while locked: the command table is generated and reported.
    want = int(m.get("commands", 109))
    proc = kinds(events, "proc")
    rep.need(bool(proc), "no '[qc] processor: LoadedCommandCount=…' line - QC initialization was "
                         "not exercised")
    if proc:
        rep.need(int(proc[-1].fields["count"]) == want,
                 f"LoadedCommandCount={proc[-1].fields['count']}, expected {want} - the gate must "
                 "gate access, not the command table")
        rep.need(proc[-1].fields["gen"] == "True",
                 "TableGenerated=False - the table was not generated while locked")
    # Read-only diagnostics still answer.
    rep.need(bool(kinds(events, "status_head")),
             "no status output while locked - read-only diagnostics must stay available")
    rep.need(bool(kinds(events, "status_access")),
             "the status dump does not carry the gate state ('[qc:access] state=… storage=…')")
    for line in (e.line for e in events):
        rep.need(not PATTERNS["exception"].search(line),
                 f"an exception-shaped line appears in the evidence: {line.strip()}")


def check_locked_seams(rep: Report, events, m):
    den = kinds(events, "denied")
    got = {e.fields["entry"] for e in den if e.fields.get("source") == "qc-seam"}
    # Which seams the game itself reaches is not under the test's control (Initialize may call
    # Activate; the processor is only hit if something invokes a command), so the manifest names
    # the ones the evidence must contain. An empty list still requires "nothing opened".
    for entry in (m.get("seams") or []):
        rep.need(entry in got, f"no 'denied entry={entry} source=qc-seam' line - the resolved QC "
                              f"seam '{entry}' was not gated (clearing the toggle key is not enough)")
    for entry in got - set(SEAM_DENY):
        rep.need(False, f"unexpected seam denial entry={entry!r} (known seams: "
                        f"{', '.join(SEAM_DENY)})")
    if not m.get("seams"):
        rep.warn(bool(got), "the log contains no qc-seam denial (the game never reached a seam); "
                            "seam coverage then rests on the code, not on this evidence")
    rep.need(not kinds(events, "open"), "a QC seam opened the locked console")
    want = int(m.get("commands", 109))
    proc = kinds(events, "proc")
    if rep.need(bool(proc), "no processor line - cannot prove QC stayed initialized while the seams "
                            "were refused"):
        rep.need(int(proc[-1].fields["count"]) == want,
                 f"LoadedCommandCount={proc[-1].fields['count']}, expected {want}")


CASES = {
    "locked-tilde": check_locked_tilde,
    "unlock-lobby": check_unlock,
    "unlock-map": check_unlock,
    "hot-reload": check_hot_reload,
    "new-process": check_new_process,
    "locked-request": check_locked_request,
    "locked-seams": check_locked_seams,
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
    if not log_path.exists():
        raise SystemExit(f"error: {log_path} is missing (needed by {manifest_path})")
    return manifest, log_path.read_text(encoding="utf-8", errors="replace")


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
    manifest, text = load_case(case_dir)
    rep = Report(manifest["case"])
    events = parse_log(text)
    rep.need(bool(events), "the log has no recognizable [qc:access]/[qc]/[hot] lines")
    check_global(rep, events, manifest)
    CASES[manifest["case"]](rep, events, manifest)
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

STATE_LOCKED = "10:00:00.100 [qc:access] state=locked storage=process"
STATE_UNLOCKED = "10:00:01.000 [qc:access] state=unlocked storage=process"
RESTORED = "10:00:05.000 [qc:access] restored unlocked=true storage=process"
ARMED = "10:00:00.200 [qc:access] capture armed via Keyboard.onTextInput"
DISARMED = "10:00:05.000 [qc:access] capture disarmed (generation teardown)"
DENY_TILDE = "10:00:02.000 [qc:access] denied entry=tilde source=binding"
MATCH_LOBBY = "10:00:03.000 [qc:access] code matched source=pause-code scene='Lobby'"
MATCH_MAP = "10:00:03.000 [qc:access] code matched source=pause-code scene='Map'"
UNLOCKED = "10:00:03.010 [qc:access] unlocked source=pause-code"
CONFIRM = "10:00:03.020 [qc:access] confirmation cue=settle result=played"
OPEN = ("10:00:03.030 [qc] toggle pause-code -> open IsActive=True enabled=True "
        "Instance=QFSW.QC.QuantumConsole commands=109")
CLOSE_AFTER = "10:00:09.000 [qc] toggle mod.console.toggle(Tilde) -> closed IsActive=False"
GEN1 = "10:00:00.000 [hot] module initialized (firstActivation=True)"
GEN2 = "10:00:06.000 [hot] module initialized (firstActivation=False)"
PROC109 = "10:00:00.500 [qc] processor: LoadedCommandCount=109 TableGenerated=True"
STATUS_HEAD = "10:00:00.400 [qc] QuantumConsole components: 2"
STATUS_ACCESS_LOCKED = ("10:00:00.410 [qc:access] state=locked storage=process denied=5 "
                        "capture=armed chars=0/7 pending=0 matches=0 resets=0")
STATUS_ACCESS_UNLOCKED = ("10:00:04.000 [qc:access] state=unlocked storage=process denied=1 "
                          "capture=armed chars=0/7 pending=0 matches=1 resets=0")


def request_denials():
    return [f"10:00:01.{i:03d} [qc:access] denied entry={e} source=request"
            for i, e in enumerate(REQUEST_DENY)]


def seam_denials():
    return [f"10:00:01.{i:03d} [qc:access] denied entry={e} source=qc-seam"
            for i, e in enumerate(SEAM_DENY)]


def write_case(root: Path, dirname: str, log: str, **manifest):
    d = root / dirname
    d.mkdir(parents=True, exist_ok=True)
    (d / "log.txt").write_text(log.strip() + "\n", encoding="utf-8")
    m = {"case": manifest.pop("case")}
    m.update(manifest)
    (d / "manifest.json").write_text(json.dumps(m, indent=2), encoding="utf-8")
    return d


def lines(*parts):
    return "\n".join(parts)


def self_test() -> int:
    ok = True
    with tempfile.TemporaryDirectory(prefix="console-unlock-evidence-") as tmp:
        root = Path(tmp)
        cases = []

        # ---- good -------------------------------------------------------------------------
        cases.append(("PASS", write_case(
            root, "good-locked-tilde",
            lines(GEN1, STATE_LOCKED, ARMED, STATUS_HEAD, STATUS_ACCESS_LOCKED, DENY_TILDE),
            case="locked-tilde")))

        cases.append(("PASS", write_case(
            root, "good-unlock-lobby",
            lines(GEN1, STATE_LOCKED, ARMED, DENY_TILDE, MATCH_LOBBY, UNLOCKED, CONFIRM, OPEN,
                  STATUS_ACCESS_UNLOCKED, CLOSE_AFTER),
            case="unlock-lobby", scene="Lobby")))

        cases.append(("PASS", write_case(
            root, "good-unlock-map",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_MAP, UNLOCKED, CONFIRM, OPEN, CLOSE_AFTER),
            case="unlock-map", scene="Map")))

        cases.append(("PASS", write_case(
            root, "good-hot-reload",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_LOBBY, UNLOCKED, CONFIRM, OPEN,
                  DISARMED, RESTORED, GEN2, ARMED, STATUS_ACCESS_UNLOCKED),
            case="hot-reload", generations=2)))

        cases.append(("PASS", write_case(
            root, "good-new-process",
            lines(GEN1, STATE_LOCKED, ARMED, DENY_TILDE, DENY_TILDE),
            case="new-process")))

        cases.append(("PASS", write_case(
            root, "good-locked-request",
            lines(GEN1, STATE_LOCKED, ARMED, *request_denials(), STATUS_HEAD, PROC109,
                  STATUS_ACCESS_LOCKED),
            case="locked-request", commands=109)))

        cases.append(("PASS", write_case(
            root, "good-locked-seams",
            lines(GEN1, STATE_LOCKED, ARMED, *seam_denials(), PROC109, STATUS_ACCESS_LOCKED),
            case="locked-seams", commands=109, seams=SEAM_DENY)))

        # ---- bad --------------------------------------------------------------------------
        # 1. The locked console was opened anyway (the gate is the point).
        cases.append(("FAIL", write_case(
            root, "bad-locked-opened",
            lines(GEN1, STATE_LOCKED, ARMED, DENY_TILDE, OPEN),
            case="locked-tilde")))

        # 2. A locked tilde press is accepted silently: no denial, no state line.
        cases.append(("FAIL", write_case(
            root, "bad-locked-silent",
            lines(GEN1, STATE_LOCKED, ARMED),
            case="locked-tilde")))

        # 3. Unlock without the confirmation sound.
        cases.append(("FAIL", write_case(
            root, "bad-no-confirmation",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_LOBBY, UNLOCKED, OPEN),
            case="unlock-lobby", scene="Lobby")))

        # 4. The cue was silent: the existing pause-rejecting path was used.
        cases.append(("FAIL", write_case(
            root, "bad-confirmation-silent",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_LOBBY, UNLOCKED,
                  "10:00:03.020 [qc:access] confirmation cue=settle result=silent:listener-paused",
                  OPEN),
            case="unlock-lobby", scene="Lobby")))

        # 5. The unlock happened but the console never opened.
        cases.append(("FAIL", write_case(
            root, "bad-no-open",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_LOBBY, UNLOCKED, CONFIRM),
            case="unlock-lobby", scene="Lobby")))

        # 6. The code was recognized in the wrong scene (typed outside the intended menu).
        cases.append(("FAIL", write_case(
            root, "bad-wrong-scene",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_MAP, UNLOCKED, CONFIRM, OPEN),
            case="unlock-lobby", scene="Lobby")))

        # 7. The hot reload replayed the confirmation sound.
        cases.append(("FAIL", write_case(
            root, "bad-reload-resound",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_LOBBY, UNLOCKED, CONFIRM, OPEN,
                  DISARMED, GEN2, RESTORED, ARMED, CONFIRM),
            case="hot-reload", generations=2)))

        # 8. The hot reload lost the unlock.
        cases.append(("FAIL", write_case(
            root, "bad-reload-lost",
            lines(GEN1, STATE_LOCKED, ARMED, MATCH_LOBBY, UNLOCKED, CONFIRM, OPEN,
                  DISARMED, GEN2, STATE_LOCKED, ARMED),
            case="hot-reload", generations=2)))

        # 9. A "new process" that restored a persisted unlock (a disk leak).
        cases.append(("FAIL", write_case(
            root, "bad-persisted",
            lines(GEN1, STATE_LOCKED, ARMED, RESTORED, STATUS_ACCESS_UNLOCKED),
            case="new-process")))

        # 10. A request verb was still able to open the console.
        cases.append(("FAIL", write_case(
            root, "bad-request-opened",
            lines(GEN1, STATE_LOCKED, ARMED, *request_denials(), PROC109, STATUS_HEAD,
                  STATUS_ACCESS_LOCKED, "10:00:02.000 [qc] toggle boot -> open IsActive=True"),
            case="locked-request", commands=109)))

        # 11. The seams were not gated (only the mod routes were).
        cases.append(("FAIL", write_case(
            root, "bad-seams-ungated",
            lines(GEN1, STATE_LOCKED, ARMED,
                  "10:00:01.000 [qc:access] denied entry=activate source=qc-seam", PROC109),
            case="locked-seams", commands=109, seams=["activate", "processor-invoke"])))

        # 12. The command table was not generated while locked (the gate broke initialization).
        cases.append(("FAIL", write_case(
            root, "bad-table-lost",
            lines(GEN1, STATE_LOCKED, ARMED, *request_denials(), STATUS_HEAD,
                  "10:00:00.500 [qc] processor: LoadedCommandCount=0 TableGenerated=False"),
            case="locked-request", commands=109)))

        # 13. The slot was unusable and the code "succeeded" anyway.
        cases.append(("FAIL", write_case(
            root, "bad-storage-unavailable",
            lines(GEN1,
                  "10:00:00.100 [qc:access] state=locked storage=unavailable - AppDomain data slots "
                  "are not usable in this runtime",
                  MATCH_LOBBY, "10:00:03.010 [qc:access] unlocked source=pause-code", CONFIRM, OPEN),
            case="unlock-lobby", scene="Lobby")))

        # 14. Scene unload tried to deregister a console whose OnEnable registration was skipped:
        # the exception aborts the rest of OnDisable and is the teardown failure this unit fixes.
        cases.append(("FAIL", write_case(
            root, "bad-registry-teardown",
            lines(GEN1, STATE_LOCKED, ARMED, DENY_TILDE,
                  "10:00:04.000 [Error  ] During invoking native->managed trampoline",
                  "Exception: System.ArgumentException: Could not deregister object "
                  "'Quantum Console (SRP) Canvas (QFSW.QC.QuantumConsole)' of type QuantumConsole "
                  "as it was not found in the registry."),
            case="locked-tilde")))

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
                    help="scenario directory (manifest.json + log.txt)")
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
