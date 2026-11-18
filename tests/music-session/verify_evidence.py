#!/usr/bin/env python3
"""Verify the evidence for the lobby-music transport sync (Unit C: items 3 and 4).

What this guards (the defect it was written against): the old state frame carried a sender-local
stopwatch, the follower compared it with its own process clock, and the "anchor" was recorded but
never applied - so a follower played from wherever its own download happened to start, drift was
only logged between 250 and 1500 ms, a pause-before-buffer-ready was applied after the first frame of
audio, and a host resume resumed the follower at its OLD position. Item 4: a client's `play` started
locally and was refused by `Publish`, so only that client heard its track.

The claims are mechanics, so the checks are mechanical. Two groups:

  Item 3 - transport and position
    W1  every published state frame carries nonce + revision + sequence + intent + anchorMs +
        hostMs + durMs and stays inside the bridge payload ceiling (1024 bytes), with the URL
        inside the 400-byte ceiling the 16-bit length exists for.
    W2  the follower's clock offset was MEASURED (probe -> reply), never assumed: probes out,
        replies with rtt/offset in, and every reconcile line carries offsetMs + bestRttMs.
    W3  convergence: the first `result=ok` arrives within `converge_s` of the revision's target
        becoming reachable (`coveredMs`), and it is inside the no-op band.
    W4  after convergence the audio difference stays inside `max_drift_ms`: the last reconcile
        line is `result=ok` with |driftMs| <= max_drift_ms, and no `result=ok` line ever reports a
        bigger drift (a fake success).
    W5  pause/resume propagate within `pause_ms`, measured on the offset-corrected clock
        (`applied ... afterMs=`), not on log timestamp precision.
    W6  a follower that joins a paused session creates its channel PAUSED: no reconcile line with
        `actualPaused=False` precedes the first `result=paused`.
    W7  the panel is within `panel_ms` of the target: every `[music:ui] mirror ... pos=` line is
        paired with the reconcile target in force at that moment.
    W8  no seek is faked: every committed correction (`result=seek(a->b)`) has a matching engine
        readback line with `ok=True` and a delta inside the 300 ms tolerance, and an `ok=False`
        seek is never counted as a commit.
    W9  follower transport controls are host-committed requests: a follower log contains no
        `[music:ui] PLAY pressed -> our channel resumed` / `PAUSE pressed -> our channel paused`
        local commit; every `PAUSE/PLAY/SEEK click: requested from the host` is followed by a
        matching host `control ... accepted` (same id/action/base generation), a control ack, a
        published state with the committed intent, and the follower's `applied` after it. The
        last mirror reports pending=False (the bounded pending always resolved).

  Item 4 - a client's start becomes the session track
    R1  submit -> host accept (same request id) -> the accepted revision is followed by every peer,
        including the requester (no stream handoff: each peer starts its own download).
    R2  no optimistic start: in the requester's log the first event that names the new track is the
        FOLLOW of the accepted revision, after the host's accept - never a local start before it.
    R3  revisions are host-assigned and monotonic: every accepted start has a distinct, increasing
        revision, and with two URLs requested in the same second every peer ends on the higher one
        (never resolved by wall-clock seconds).
    R4  a retransmitted request id is deduped: a `dedupe id=` line, and no second accepted start
        for the same URL.
    R5  an invalid URL leaves the current track intact: the host rejects it and no accepted start
        or FOLLOW happens between the submit and the reject.
    R6  a host adoption failure is broadcast (`intent=failed`) and the client stops its copy instead
        of starting a private replacement.

Inputs are a scenario DIRECTORY per case (one per two-peer run):

    <case>/manifest.json
    <case>/log-<peer>.txt        the peers' log exports, one file per peer

manifest.json:

    {
      "case": "steady",                  # steady | pause | request | conflict | reject | failed
      "peers": [
        {"name": "host", "role": "host", "log": "log-host.txt"},
        {"name": "B", "role": "follower", "log": "log-B.txt"}
      ],
      "side": "B",                       # the follower that acts (request/conflict/reject/failed)
      "url_a": "https://www.youtube.com/watch?v=X",
      "url_b": "https://www.youtube.com/watch?v=Y",
      "max_drift_ms": 750, "converge_s": 2.0, "pause_ms": 500, "panel_ms": 750,
      "expect_paused_first": false
    }

Usage:
    verify_evidence.py --case DIR [DIR ...]
    verify_evidence.py --self-test

Exit: 0 all PASS (and, for --self-test, every fabricated case behaved as declared), 1 otherwise.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

# --------------------------------------------------------------------------------------------
# log grammar
#
# One pattern per emitted line, each anchored at its tag. The game writes a `HH:MM:SS ` prefix;
# normalize() strips it, and keeps it for ordering only. Ordering inside one peer file is the log's
# own order, and ordering between peers is only ever taken from the timestamp when both carry one.
# --------------------------------------------------------------------------------------------

LINE_TS = re.compile(r"^\s*\d{2}:\d{2}:\d{2}(?:[.,]\d+)?\s+")
TS_FULL = re.compile(r"^\s*(\d{2}):(\d{2}):(\d{2})(?:[.,](\d+))?\s+")

PATTERNS = {
    # ---- host authoring
    "tx_state": re.compile(
        r"\[music:sync\] tx state why=(?P<why>\S+) nonce=(?P<nonce>\d+) rev=(?P<rev>\d+) "
        r"seq=(?P<seq>\d+) ctrlGen=(?P<ctrlGen>\d+) intent=(?P<intent>\w+) url='(?P<url>[^']*)' "
        r"anchorMs=(?P<anchor>-?\d+) hostMs=(?P<host>\d+) durMs=(?P<dur>-?\d+) bytes=(?P<bytes>\d+) "
        r"peers=(?P<peers>\d+)$"),
    "tx_state_late": re.compile(
        r"\[music:sync\] tx state why=(?P<why>late-join) client=(?P<client>\d+) ok=(?P<ok>\w+) "
        r"nonce=(?P<nonce>\d+) rev=(?P<rev>\d+) seq=(?P<seq>\d+) ctrlGen=(?P<ctrlGen>\d+) "
        r"intent=(?P<intent>\w+) url='(?P<url>[^']*)' anchorMs=(?P<anchor>-?\d+) hostMs=(?P<host>\d+) "
        r"durMs=(?P<dur>-?\d+) bytes=(?P<bytes>\d+)$"),
    "accepted": re.compile(
        r"\[music:sync\] tx accepted-start rev=(?P<rev>\d+) url='(?P<url>[^']*)' why=(?P<why>\S+) "
        r"restart=(?P<restart>\w+)$"),
    "start_refused": re.compile(
        r"\[music:sync\] start refused url='(?P<url>[^']*)' rev=(?P<rev>\d+) reason='(?P<reason>[^']*)'; "
        r"the session track is unchanged$"),
    "rx_probe": re.compile(
        r"\[music:sync\] rx probe id=(?P<id>\d+) from=(?P<from>\d+) clientSendMs=(?P<t0>\d+) "
        r"recvMs=(?P<t1>\d+) replyMs=(?P<t2>\d+) ok=(?P<ok>\w+)$"),
    # ---- follower clock + state
    "tx_probe": re.compile(
        r"\[music:sync\] tx probe id=(?P<id>\d+) sendMs=(?P<send>\d+) host=(?P<host>\d+) "
        r"ok=(?P<ok>\w+) bytes=(?P<bytes>\d+)$"),
    "rx_reply": re.compile(
        r"\[music:sync\] rx probe-reply id=(?P<id>\d+) rttMs=(?P<rtt>-?\d+) offsetMs=(?P<offset>-?\d+) "
        r"bestRttMs=(?P<best>-?\d+) usingOffsetMs=(?P<using>-?\d+) samples=(?P<samples>\d+) "
        r"hostRecvMs=(?P<t1>\d+) hostReplyMs=(?P<t2>\d+)$"),
    "rx_follow": re.compile(
        r"\[music:sync\] rx FOLLOW rev=(?P<rev>\d+) seq=(?P<seq>\d+) intent=(?P<intent>\w+) "
        r"url='(?P<url>[^']*)' title='(?P<title>[^']*)' anchorMs=(?P<anchor>-?\d+) "
        r"hostMs=(?P<host>\d+) rev-flag=(?P<flag>\w+) ctrlGen=(?P<ctrlGen>\d+) -> targetMs=(?P<target>-?\d+)$"),
    "rx_state": re.compile(
        r"\[music:sync\] rx state rev=(?P<rev>\d+) seq=(?P<seq>\d+) intent=(?P<intent>\w+) "
        r"anchorMs=(?P<anchor>-?\d+) hostMs=(?P<host>\d+) durMs=(?P<dur>-?\d+) ctrlGen=(?P<ctrlGen>\d+)$"),
    "rx_stopped": re.compile(
        r"\[music:sync\] rx state rev=(?P<rev>\d+) seq=(?P<seq>\d+) intent=stopped "
        r"url='(?P<url>[^']*)' from=(?P<from>\d+)$"),
    "rx_failed": re.compile(
        r"\[music:sync\] rx state rev=(?P<rev>\d+) seq=(?P<seq>\d+) intent=failed "
        r"url='(?P<url>[^']*)' from=(?P<from>\d+): the host could not play this track$"),
    "reconcile": re.compile(
        r"\[music:sync\] reconcile targetMs=(?P<target>-?\d+) actualMs=(?P<actual>-?\d+) "
        r"driftMs=(?P<drift>-?\d+) desiredPaused=(?P<desired>\w+) actualPaused=(?P<actualPaused>\w+) "
        r"coverageMs=(?P<coverage>-?\d+) result=(?P<result>\S+) rev=(?P<rev>\d+) intent=(?P<intent>\w+) "
        r"offsetMs=(?P<offset>-?\d+) bestRttMs=(?P<best>-?\d+) coveredMs=(?P<covered>-?\d+)$"),
    "applied": re.compile(
        r"\[music:sync\] applied desiredPaused=(?P<desired>\w+) afterMs=(?P<after>-?\d+) "
        r"rev=(?P<rev>\d+) anchorMs=(?P<anchor>-?\d+) offsetMs=(?P<offset>-?\d+) "
        r"bestRttMs=(?P<best>-?\d+) actualPaused=(?P<actualPaused>\w+)$"),
    # ---- requests
    "req_submit_host": re.compile(
        r"\[music:request\] submit id=(?P<id>\d+) from=(?P<from>\d+) url='(?P<url>[^']*)'$"),
    "req_submit_client": re.compile(
        r"\[music:request\] submit id=(?P<id>\d+) url='(?P<url>[^']*)' -> host=(?P<host>\d+) "
        r"ok=(?P<ok>\w+) bytes=(?P<bytes>\d+) source=(?P<source>\S+); .*$"),
    "req_accept_host": re.compile(
        r"\[music:request\] accept id=(?P<id>\d+) -> client=(?P<client>\d+) rev=(?P<rev>\d+) "
        r"ok=(?P<ok>\w+) bytes=(?P<bytes>\d+) reason='(?P<reason>[^']*)'$"),
    "req_reject_host": re.compile(
        r"\[music:request\] reject id=(?P<id>\d+) -> client=(?P<client>\d+) rev=(?P<rev>\d+) "
        r"ok=(?P<ok>\w+) bytes=(?P<bytes>\d+) reason='(?P<reason>[^']*)'$"),
    "req_accept_client": re.compile(
        r"\[music:request\] accept id=(?P<id>\d+) rev=(?P<rev>\d+) url='(?P<url>[^']*)': the host "
        r"will publish the session state.*$"),
    "req_accept_follow": re.compile(
        r"\[music:request\] accept url='(?P<url>[^']*)' rev=(?P<rev>\d+): the host adopted our request$"),
    "req_reject_client": re.compile(
        r"\[music:request\] reject id=(?P<id>\d+) url='(?P<url>[^']*)' reason='(?P<reason>[^']*)'; "
        r"keeping the current track$"),
    "req_dedupe": re.compile(
        r"\[music:request\] dedupe id=(?P<id>\d+) from=(?P<from>\d+) verdict=(?P<verdict>\w+) "
        r"\(already handled; not starting again\)$"),
    # ---- follower transport control (protocol 3)
    "ui_pause_req": re.compile(
        r"\[music:ui\] PAUSE click: requested from the host; the local transport is unchanged "
        r"until the accepted state is published$"),
    "ui_play_req": re.compile(
        r"\[music:ui\] PLAY click: requested from the host; the local transport is unchanged "
        r"until the accepted state is published$"),
    "ui_seek_req": re.compile(
        r"\[music:ui\] SEEK click: requested from the host; the local transport is unchanged "
        r"until the accepted state is published$"),
    "tx_control": re.compile(
        r"\[music:request\] tx control id=(?P<id>\d+) action=(?P<action>\w+) targetMs=(?P<target>-?\d+) "
        r"rev=(?P<rev>\d+) gen=(?P<gen>\d+) nonce=(?P<nonce>\d+) bytes=(?P<bytes>\d+) "
        r"source=(?P<source>.*?); .*$"),
    "host_control": re.compile(
        r"\[music:request\] control id=(?P<id>\d+) from=(?P<from>\d+) action=(?P<action>\w+) "
        r"rev=(?P<rev>\d+) gen=(?P<gen>\d+) accepted commitGen=(?P<commitGen>\d+) "
        r"appliedMs=(?P<applied>-?\d+) bytes=(?P<bytes>\d+)$"),
    "host_control_reject": re.compile(
        r"\[music:request\] control id=(?P<id>\d+) from=(?P<from>\d+) action=(?P<action>\w+) "
        r"rev=(?P<rev>\d+) gen=(?P<gen>\d+) REJECTED: (?P<reason>.*)$"),
    "control_accept_client": re.compile(
        r"\[music:request\] control id=(?P<id>\d+) accepted action=(?P<action>\w+) rev=(?P<rev>\d+) "
        r"gen=(?P<gen>\d+) appliedMs=(?P<applied>-?\d+): the host committed the transport.*$"),
    "control_reject_client": re.compile(
        r"\[music:request\] control id=(?P<id>\d+) rejected action=(?P<action>\w+) rev=(?P<rev>\d+) "
        r"gen=(?P<gen>\d+) verdict=(?P<verdict>\w+) reason='(?P<reason>[^']*)'; the transport is still "
        r"the host's$"),
    "control_dedupe": re.compile(
        r"\[music:request\] control dedupe id=(?P<id>\d+) from=(?P<from>\d+) verdict=(?P<verdict>\w+) "
        r"\(already handled; not committing again\)$"),
    "control_timeout": re.compile(
        r"\[music:request\] control timeout id=(?P<id>\d+) action=(?P<action>\w+) after .*$"),
    # ---- presentation / engine
    "mirror": re.compile(
        r"\[music:ui\] mirror status=(?P<status>\S*) state=(?P<state>-?\d+) play=(?P<play>\w+) "
        r"pause=(?P<pause>\w+) seek=(?P<seek>\w+) growing=(?P<growing>\w+) pos=(?P<pos>-?\d+) "
        r"total=(?P<total>-?\d+) pending=(?P<pending>\w+) title='(?P<title>[^']*)'.*$"),
    "ui_play": re.compile(r"\[music:ui\] PLAY pressed -> our channel resumed at (?P<pos>-?\d+)ms.*$"),
    "ui_pause": re.compile(r"\[music:ui\] PAUSE pressed -> our channel paused at (?P<pos>-?\d+)ms.*$"),
    "play_remote": re.compile(
        r"\[music:host\] playAt url='(?P<url>[^']*)' anchor=(?P<anchor>-?\d+)ms paused=(?P<paused>\w+) "
        r"source=(?P<source>\S+)$"),
    "play_url": re.compile(
        r"\[music:host\] PLAY url='(?P<url>[^']*)' source=(?P<source>\S+); .*$"),
    "engine_seek": re.compile(
        r"\[music:stream\] seek\((?P<asked>-?\d+)ms\) -> \S+ \(.*?\) in \d+ms; getPosition=\S+ "
        r"now=(?P<now>-?\d+)ms delta=(?P<delta>-?\d+)ms tolerance=(?P<tol>\d+)ms ok=(?P<ok>\w+) "
        r"snapLen=-?\d+ms$"),
    "stop_remote_failed": re.compile(
        r"\[music:sync\] the host failed this track; stopping our copy.*$"),
}

# Values the result field of a reconcile line can take. Anything else is a grammar failure: the
# evidence must not be guessed at.
RESULT_OK = "ok"
RESULT_BAND = "band"
RESULT_SEEK = re.compile(r"^seek\((?P<before>-?\d+)->(?P<after>-?\d+)\)$")


@dataclass
class Event:
    kind: str
    pos: int                       # line number in the peer's log
    line: str
    fields: dict
    ts: object = None              # seconds-of-day, sub-second where the log has it


@dataclass
class Peer:
    name: str
    role: str
    path: str
    events: list = field(default_factory=list)
    error: str = ""

    def of(self, *kinds):
        return [e for e in self.events if e.kind in kinds]

    @property
    def is_host(self) -> bool:
        return self.role == "host"


def parse_ts(line: str):
    m = TS_FULL.match(line)
    if not m:
        return None
    hh, mm, ss, frac = m.groups()
    seconds = int(hh) * 3600 + int(mm) * 60 + int(ss)
    if frac:
        seconds += float("0." + frac)
    return seconds


def normalize(line: str) -> str:
    return LINE_TS.sub("", line)


def parse_log(path: Path) -> list:
    events = []
    text = path.read_text(encoding="utf-8", errors="replace")
    for pos, raw in enumerate(text.splitlines()):
        line = raw.rstrip()
        if not line:
            continue
        body = normalize(line)
        for kind, pat in PATTERNS.items():
            m = pat.search(body)
            if not m:
                continue
            events.append(Event(kind, pos, line, {k: v for k, v in m.groupdict().items()},
                                parse_ts(line)))
            break
    return events


def same_url(a: str, b: str) -> bool:
    """The mod logs URLs through a 90-character Short(), so the tail may be an ellipsis."""
    if not a or not b:
        return False
    a = a.rstrip("…")
    b = b.rstrip("…")
    return a.startswith(b) or b.startswith(a)


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


def load_case(d: Path):
    manifest_path = d / "manifest.json"
    if not manifest_path.is_file():
        return None, None, f"no manifest.json in {d}"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        return None, None, f"manifest.json is not JSON: {e}"
    peers = []
    for spec in manifest.get("peers", []):
        role = spec.get("role", "follower")
        name = spec.get("name", role)
        rel = spec.get("log")
        if not rel:
            return None, None, f"peer '{name}' has no log"
        path = d / rel
        if not path.is_file():
            peers.append(Peer(name, role, str(path), [], f"missing log file {rel}"))
            continue
        peers.append(Peer(name, role, str(path), parse_log(path)))
    if not peers:
        return None, None, "manifest.json declares no peers"
    return manifest, peers, None


# --------------------------------------------------------------------------------------------
# item 3: the transport
# --------------------------------------------------------------------------------------------

def check_wire(rep: Report, peers):
    """W1: the state frame carries the whole anchor, inside the bridge ceiling."""
    states = [(p, e) for p in peers for e in p.of("tx_state", "tx_state_late")]
    if not rep.need(bool(states), "no state frame was published at all (`tx state`) - nothing to sync"):
        return
    for p, e in states:
        f = e.fields
        rep.need(p.is_host, f"{p.name}: a non-host peer published a state frame (line {e.pos + 1})")
        for name in ("nonce", "rev", "seq", "intent", "anchor", "host", "dur", "bytes"):
            rep.need(f.get(name) not in (None, ""),
                     f"{p.name}: state frame at line {e.pos + 1} has no {name} field")
        rep.need(int(f["bytes"]) <= 1024,
                 f"{p.name}: state frame is {f['bytes']} bytes, over the 1024-byte bridge ceiling")
        rep.need(int(f["anchor"]) >= 0,
                 f"{p.name}: negative anchorMs={f['anchor']} at line {e.pos + 1}")
        rep.need(int(f["host"]) >= 0,
                 f"{p.name}: negative hostMs={f['host']} at line {e.pos + 1}")
        rep.need(len(f["url"].encode("utf-8")) <= 400,
                 f"{p.name}: url is longer than the accepted 400-byte ceiling (line {e.pos + 1})")
    intents = {e.fields["intent"] for _, e in states}
    # The old frame encoded preflight/buffering as `stopped`, which is what made a peer that joined
    # during preflight sit on a track that had already begun. An explicit transient intent must exist.
    rep.need(bool(intents & {"buffering", "playing", "paused"}),
             f"no state frame ever announced a transient transport intent (saw {sorted(intents)}) - "
             "buffering is still being encoded as stopped")
    rep.warn("playing" in intents, "no state frame carried intent=playing in this run")


def check_clock(rep: Report, peers, followers):
    """W2: the offset between the two monotonic clocks was measured, not assumed."""
    for name in followers:
        p = next((x for x in peers if x.name == name), None)
        if p is None:
            rep.need(False, f"follower '{name}' is declared but has no parsed log")
            continue
        probes = p.of("tx_probe")
        replies = p.of("rx_reply")
        recon = p.of("reconcile")
        rep.need(bool(probes), f"{p.name}: no clock probe was ever sent (`tx probe`)")
        rep.need(bool(replies),
                 f"{p.name}: no `rx probe-reply` - the host clock offset was never measured, so every "
                 "target computed here came from an unmeasured comparison")
        for e in replies:
            rep.need(int(e.fields["rtt"]) >= 0,
                     f"{p.name}: probe reply at line {e.pos + 1} reports a negative round trip")
            rep.need(int(e.fields["samples"]) >= 1,
                     f"{p.name}: probe reply at line {e.pos + 1} was not counted as a sample")
        first_reply = replies[0].pos if replies else 10 ** 9
        if recon and replies:
            rep.need(first_reply < recon[0].pos,
                     f"{p.name}: the first reconcile at line {recon[0].pos + 1} happened before any "
                     "probe reply - it cannot have used a measured offset")
        # A missing offset field means the build still reasons about positions without one.
        for e in recon:
            rep.need("offset" in e.fields and "best" in e.fields,
                     f"{p.name}: reconcile at line {e.pos + 1} carries no clock offset")


def check_convergence(rep: Report, peers, followers, converge_ms: int, max_drift_ms: int):
    """W3 + W4 + W8: converge inside the window, then stay inside the audio-difference bound."""
    for name in followers:
        p = next((x for x in peers if x.name == name), None)
        if p is None:
            continue
        recon = p.of("reconcile")
        if not rep.need(bool(recon), f"{p.name}: no `[music:sync] reconcile` line - the follower "
                                     "never derived a target, so there is nothing to accept"):
            continue
        oks = [e for e in recon if e.fields["result"] == RESULT_OK]
        if not oks:
            if p.of("rx_failed", "rx_stopped"):
                rep.warn(True, "")          # the session never gave this peer anything playable
            else:
                rep.need(False, f"{p.name}: never reached result=ok (results: " +
                         ", ".join(sorted({e.fields['result'] for e in recon})) + ")")
            continue

        first = oks[0]
        covered = int(first.fields["covered"])
        rep.need(covered >= 0,
                 f"{p.name}: the first result=ok (line {first.pos + 1}) has coveredMs={covered}: the "
                 "target was never inside coverage before it converged")
        rep.need(0 <= covered <= converge_ms,
                 f"{p.name}: converged {covered} ms after coverage appeared, outside the "
                 f"{converge_ms} ms window")
        rep.need(abs(int(first.fields["drift"])) <= max_drift_ms,
                 f"{p.name}: the first convergence reports driftMs={first.fields['drift']}")

        # W4: an `ok` may never hide a drift outside the bound, and a run that is still following
        # must END converged (a failure/stop after the last convergence is the session's business).
        for e in oks:
            rep.need(abs(int(e.fields["drift"])) <= max_drift_ms,
                     f"{p.name}: result=ok at line {e.pos + 1} with driftMs={e.fields['drift']}, over "
                     f"the {max_drift_ms} ms audio-difference bound")
        ended = [e for e in p.of("rx_failed", "rx_stopped") if e.pos > oks[-1].pos]
        if ended:
            rep.warn(True, "")
        else:
            last = recon[-1]
            rep.need(last.fields["result"] == RESULT_OK,
                     f"{p.name}: the last reconcile (line {last.pos + 1}) is "
                     f"result={last.fields['result']}, not converged")
            if last.fields["result"] == RESULT_OK:
                rep.need(abs(int(last.fields["drift"])) <= max_drift_ms,
                         f"{p.name}: the final drift is {last.fields['drift']} ms, over "
                         f"{max_drift_ms} ms")

        # W8: every committed correction has a verified engine readback, and a refused one is never
        # terminal (the backoff retries it).
        seeks = p.of("engine_seek")
        for e in recon:
            m = RESULT_SEEK.match(e.fields["result"])
            if not m:
                continue
            after = int(m.group("after"))
            rep.need(abs(after - int(e.fields["target"])) <= 300,
                     f"{p.name}: a committed correction landed at {after} ms for a target of "
                     f"{e.fields['target']} ms (line {e.pos + 1})")
            paired = [x for x in seeks
                      if x.pos < e.pos and int(x.fields["now"]) == after and x.fields["ok"] == "True"]
            rep.need(bool(paired),
                     f"{p.name}: the seek committed at line {e.pos + 1} has no verified engine "
                     "readback (`[music:stream] seek(...) ok=True`) - a seek was faked")
        for x in seeks:
            if x.fields["ok"] == "True":
                continue
            later = any(y.pos > x.pos for y in seeks) or any(
                y.pos > x.pos and y.fields["result"] == "fail-seek" for y in recon)
            rep.need(later,
                     f"{p.name}: the seek refused at line {x.pos + 1} was treated as terminal "
                     "(no retry, no fail-seek) - a refused correction was silently accepted")


def check_pause_latency(rep: Report, peers, followers, pause_ms: int):
    """W5: the transport commit propagated inside the claim, on the corrected clock."""
    host = next((x for x in peers if x.is_host), None)
    published = [e for e in (host.of("tx_state", "tx_state_late") if host else [])
                 if e.fields["intent"] == "paused"]
    if not published:
        rep.warn(True, "")       # this run never paused, so there is no propagation to measure
        return
    for name in followers:
        p = next((x for x in peers if x.name == name), None)
        if p is None:
            continue
        mine = p.of("applied")
        if not rep.need(bool(mine),
                        f"{p.name}: the host published a pause but this log has no "
                        "`applied ... afterMs=` line - the propagation was never measured"):
            continue
        for e in mine:
            after = int(e.fields["after"])
            rep.need(after >= 0,
                     f"{p.name}: `applied` at line {e.pos + 1} reports afterMs={after}: no measured "
                     "clock offset")
            rep.need(after <= pause_ms,
                     f"{p.name}: desiredPaused={e.fields['desired']} was applied {after} ms after the "
                     f"host's anchor, over the {pause_ms} ms bound")
            want = "True" if e.fields["desired"] == "True" else "False"
            rep.need(e.fields["actualPaused"] == want,
                     f"{p.name}: `applied` at line {e.pos + 1} claims a commit the channel does not "
                     "report (actualPaused is unchanged)")


def check_created_paused(rep: Report, peers, followers):
    """W6: a follower of a paused session must not emit a frame before the pause is applied."""
    for name in followers:
        p = next((x for x in peers if x.name == name), None)
        if p is None:
            continue
        follows = p.of("rx_follow")
        if not follows:
            continue
        first = follows[0]
        if first.fields["intent"] != "paused":
            continue
        paused_at = next((e for e in p.of("reconcile")
                          if e.fields["result"] in ("paused", "paused-seek")), None)
        if not rep.need(paused_at is not None,
                        f"{p.name}: followed a paused session but never reported a pause commit"):
            continue
        unsound = [e for e in p.of("reconcile")
                   if e.pos < paused_at.pos and e.fields["actualPaused"] == "False"]
        rep.need(not unsound,
                 f"{p.name}: {len(unsound)} reconcile line(s) report an unpaused channel before the "
                 f"host's pause was applied (first at line {unsound[0].pos + 1 if unsound else 0}) - "
                 "the channel was created playing and paused after a frame of audio")


def check_panel(rep: Report, peers, followers, panel_ms: int):
    """W7: the panel's clock is the transport target, not a playhead of its own."""
    seen = 0
    for name in followers:
        p = next((x for x in peers if x.name == name), None)
        if p is None:
            continue
        recon = p.of("reconcile")
        for m in p.of("mirror"):
            if m.fields["state"] != "2":            # 2 = PlayerState.Playing
                continue
            before = [e for e in recon if e.pos < m.pos]
            if not before:
                continue
            target = int(before[-1].fields["target"])
            pos = int(m.fields["pos"])
            seen += 1
            rep.need(abs(pos - target) <= panel_ms,
                     f"{p.name}: the panel shows {pos} ms while the target is {target} ms "
                     f"(line {m.pos + 1}), over the {panel_ms} ms bound")
    rep.warn(seen > 0 or not followers, "no playing mirror line found, the panel bound was not exercised")


# --------------------------------------------------------------------------------------------
# item 4: whose track is the session track
# --------------------------------------------------------------------------------------------

def accepted_starts(peers):
    return [(p, e) for p in peers if p.is_host for e in p.of("accepted")]


def check_revisions(rep: Report, peers):
    """R3: host-assigned, monotonic, distinct, and no wall-clock rule anywhere."""
    starts = accepted_starts(peers)
    if not starts:
        return
    revs = [int(e.fields["rev"]) for _, e in starts]
    rep.need(all(b > a for a, b in zip(revs, revs[1:])),
             f"accepted-start revisions are not increasing: {revs}")
    rep.need(len(set(revs)) == len(revs),
             f"two accepted starts share a revision: {revs} - a conflict would be unresolvable")
    rep.need(revs[0] >= 1, f"the first accepted revision is {revs[0]}, not a positive revision")
    for _, e in starts:
        rep.need(e.fields["why"].strip() != "", f"accepted start at line {e.pos + 1} has no reason")


def check_request_flow(rep: Report, peers, followers, side: str, url_b: str):
    """R1 + R2: submit -> host accept -> every peer follows that revision, the requester not before.

    Every ordering here is INTRA-log (one peer's own line order). Cross-peer order is never taken
    from line numbers, and only from timestamps where both logs carry one.
    """
    requester = next((p for p in peers if p.name == side), None)
    host = next((p for p in peers if p.is_host), None)
    if not rep.need(requester is not None and host is not None,
                    f"case declares side='{side}' but that peer or the host is missing"):
        return

    submits = [e for e in requester.of("req_submit_client") if same_url(e.fields["url"], url_b)]
    if not rep.need(bool(submits), f"{side}: never submitted '{url_b}' (`[music:request] submit`)"):
        return
    submit = submits[0]
    rep.need(submit.fields["ok"] == "True",
             f"{side}: the submit was not sent (ok={submit.fields['ok']})")
    rid = submit.fields["id"]

    acks = [e for e in host.of("req_accept_host") if e.fields["id"] == rid]
    rep.need(bool(acks),
             f"{side}: request id={rid} was never accepted by the host (no `[music:request] accept "
             f"id={rid}`) - a client's track cannot become the session track")
    if not acks:
        return
    accept = acks[0]
    rev = int(accept.fields["rev"])
    # The host received the request before it accepted it (one log, so this ordering is real).
    received = [e for e in host.of("req_submit_host") if e.fields["id"] == rid]
    rep.need(bool(received), f"host: never logged the receipt of request id={rid}")
    if received:
        rep.need(received[0].pos < accept.pos,
                 f"host: accepted request id={rid} at line {accept.pos + 1} before receiving it at "
                 f"line {received[0].pos + 1}")

    follows = [e for e in requester.of("rx_follow") if same_url(e.fields["url"], url_b)]
    if not rep.need(bool(follows),
                    f"{side}: never followed the accepted revision of '{url_b}' (no `rx FOLLOW`)"):
        return
    follow = follows[0]
    rep.need(int(follow.fields["rev"]) == rev,
             f"{side}: followed rev={follow.fields['rev']} but the host accepted rev={rev}")
    rep.need(submit.pos < follow.pos,
             f"{side}: the FOLLOW at line {follow.pos + 1} precedes its own submit at line "
             f"{submit.pos + 1}")

    # R2: no optimistic start. Within the requester's log the new track may only appear through the
    # FOLLOW first; a local start before it is exactly the reported defect.
    starts = [e for e in requester.of("play_remote", "play_url")
              if same_url(e.fields["url"], url_b)]
    early = [e for e in starts if e.pos < follow.pos]
    rep.need(not early,
             f"{side}: started '{url_b}' locally at line {early[0].pos + 1 if early else 0}, before "
             f"the host's accepted revision arrived (FOLLOW at line {follow.pos + 1}) - an optimistic "
             "start, and the very thing the host's revision is supposed to decide")
    for e in follows:
        rep.need(int(e.fields["rev"]) == rev or int(e.fields["rev"]) > rev,
                 f"{side}: followed rev={e.fields['rev']}, older than the accepted rev={rev}")

    # R1: every peer, not just the requester.
    for p in peers:
        if p.is_host:
            adopted = [e for e in p.of("accepted") if same_url(e.fields["url"], url_b)]
            rep.need(bool(adopted), "host: never published an accepted start for the requested URL")
            continue
        got = [e for e in p.of("rx_follow") if same_url(e.fields["url"], url_b)]
        rep.need(bool(got), f"{p.name}: never followed '{url_b}' - the accepted track did not become "
                            "everyone's track")


def check_conflict(rep: Report, peers, followers, url_a: str, url_b: str):
    """R3 (conflict): two URLs in the same second, distinct revisions, everyone on the higher."""
    starts = accepted_starts(peers)
    a = [e for _, e in starts if same_url(e.fields["url"], url_a)]
    b = [e for _, e in starts if same_url(e.fields["url"], url_b)]
    rep.need(bool(a) and bool(b),
             f"the host accepted {len(a)} start(s) for '{url_a}' and {len(b)} for '{url_b}': both "
             "requests must be adopted (receive order), then the later one wins")
    if a and b:
        rep.need(int(a[0].fields["rev"]) != int(b[0].fields["rev"]),
                 "both URLs were assigned the same revision")
    if starts:
        high = max(int(e.fields["rev"]) for _, e in starts)
        high_url = next(e.fields["url"] for _, e in starts if int(e.fields["rev"]) == high)
        for p in peers:
            if p.is_host:
                continue
            follows = p.of("rx_follow")
            rep.need(bool(follows), f"{p.name}: no FOLLOW at all")
            if follows:
                last = follows[-1]
                rep.need(int(last.fields["rev"]) == high and same_url(last.fields["url"], high_url),
                         f"{p.name}: ended on rev={last.fields['rev']} '{last.fields['url']}', but the "
                         f"host's highest accepted revision is {high} '{high_url}' - peers did not "
                         "converge on the last accepted request")


def check_dedupe(rep: Report, peers):
    """R4: a retransmitted request id must not start the track twice."""
    host = next((p for p in peers if p.is_host), None)
    if host is None:
        return
    dedupes = host.of("req_dedupe")
    if not dedupes:
        return
    receives = {}
    for e in host.of("req_submit_host"):
        receives[e.fields["id"]] = receives.get(e.fields["id"], 0) + 1
    accepts = {}
    for e in host.of("req_accept_host"):
        accepts[e.fields["id"]] = accepts.get(e.fields["id"], 0) + 1
    for d in dedupes:
        rid = d.fields["id"]
        rep.need(rid in receives,
                 f"host: `dedupe id={rid}` names a request id it never received")
        rep.need(accepts.get(rid, 0) <= 1,
                 f"host: request id={rid} was accepted {accepts.get(rid, 0)} times after a dedupe - "
                 "a retransmission started the track again")
    for rid, n in accepts.items():
        rep.need(n == 1, f"host: request id={rid} was accepted {n} times")


def check_reject(rep: Report, peers, side: str, url_b: str):
    """R5: an invalid URL leaves the current session track alone."""
    host = next((p for p in peers if p.is_host), None)
    requester = next((p for p in peers if p.name == side), None)
    if host is None or requester is None:
        rep.need(False, f"case declares side='{side}' but that peer or the host is missing")
        return
    submits = [e for e in host.of("req_submit_host") if same_url(e.fields["url"], url_b)]
    if not rep.need(bool(submits), f"host: never received the request for '{url_b}'"):
        return
    rejects = [e for e in host.of("req_reject_host") if e.fields["id"] == submits[0].fields["id"]]
    if not rep.need(bool(rejects), f"host: request id={submits[0].fields['id']} for '{url_b}' was "
                                   "never rejected"):
        return
    reject = rejects[0]
    rep.need(reject.fields["reason"].strip() != "",
             f"host: the rejection at line {reject.pos + 1} carries no reason")
    between = [e for e in host.of("accepted") if submits[0].pos < e.pos < reject.pos]
    rep.need(not between,
             f"host: an accepted start happened between the request and its rejection "
             f"(line {between[0].pos + 1 if between else 0})")
    early = [e for e in requester.of("req_reject_client") if e.fields["id"] == reject.fields["id"]]
    rep.need(bool(early),
             f"{side}: never saw the rejection for its own request - the reason was not shown")
    follows = [e for e in requester.of("rx_follow") if same_url(e.fields["url"], url_b)]
    rep.need(not follows,
             f"{side}: followed '{url_b}' after its request was rejected (line "
             f"{follows[0].pos + 1 if follows else 0})")
    # The session track must still be the one that was playing.
    stopped = [e for e in requester.of("rx_stopped")
               if e.pos > submits[0].pos]
    rep.need(not stopped,
             f"{side}: the session track was stopped by a rejected request (line "
             f"{stopped[0].pos + 1 if stopped else 0})")


def check_failed(rep: Report, peers, followers, url_b: str):
    """R6: a host adoption failure is broadcast, and no peer invents a replacement."""
    host = next((p for p in peers if p.is_host), None)
    if host is None:
        rep.need(False, "no host peer in the manifest")
        return
    failed = [e for e in host.of("tx_state", "tx_state_late")
              if e.fields["intent"] == "failed" and same_url(e.fields["url"], url_b)]
    if not rep.need(bool(failed),
                    f"host: never published intent=failed for '{url_b}' - a failed adoption was "
                    "silent"):
        return
    for name in followers:
        p = next((x for x in peers if x.name == name), None)
        if p is None:
            continue
        seen = [e for e in p.of("rx_failed") if same_url(e.fields["url"], url_b)]
        rep.need(bool(seen),
                 f"{p.name}: never received the intent=failed broadcast for '{url_b}'")
        if seen:
            after = [e for e in p.of("rx_follow", "play_remote")
                     if e.pos > seen[0].pos and same_url(e.fields["url"], url_b)]
            rep.need(not after,
                     f"{p.name}: started '{url_b}' itself after the host failed it (line "
                     f"{after[0].pos + 1 if after else 0}) - a private replacement")
            rep.need(bool(p.of("stop_remote_failed")) or not p.of("rx_follow"),
                     f"{p.name}: did not stop its copy of '{url_b}' when the host failed it")


# --------------------------------------------------------------------------------------------
# driver
# --------------------------------------------------------------------------------------------

def verify_case(d: Path) -> Report:
    manifest, peers, err = load_case(d)
    if err:
        rep = Report(case=d.name)
        rep.need(False, err)
        return rep
    case = manifest.get("case", d.name)
    rep = Report(case=case)

    for p in peers:
        if p.error:
            rep.need(False, f"{p.name}: {p.error}")
        rep.need(bool(p.events) or bool(p.error),
                 f"{p.name}: the log has no music line at all - the mod was not installed, or the "
                 "export is not this run")

    followers_declared = manifest.get("followers")
    if followers_declared is None:
        followers_declared = [p.name for p in peers if not p.is_host]
    followers = [n for n in followers_declared if any(p.name == n for p in peers)]
    for n in followers_declared:
        rep.need(n in followers, f"follower '{n}' is declared but not present in the manifest's peers")

    max_drift = int(manifest.get("max_drift_ms", 750))
    converge = int(float(manifest.get("converge_s", 2.0)) * 1000)
    pause_ms = int(manifest.get("pause_ms", 500))
    panel_ms = int(manifest.get("panel_ms", 750))
    side = manifest.get("side", followers[0] if followers else "")
    url_a = manifest.get("url_a", "")
    url_b = manifest.get("url_b", "")

    # Universal: the frame, the measured clock, the transport convergence, the panel binding.
    # These hold for every scenario in which a follower exists. (The old follower-lock criterion
    # was removed: protocol 3 keeps follower controls interactable and sends explicit requests.)
    check_wire(rep, peers)
    if followers:
        check_clock(rep, peers, followers)
        check_convergence(rep, peers, followers, converge, max_drift)
        check_panel(rep, peers, followers, panel_ms)
        check_pause_latency(rep, peers, followers, pause_ms)
        check_created_paused(rep, peers, followers)
    check_revisions(rep, peers)

    if case == "steady":
        pass
    elif case == "pause":
        applied = [(p, e) for p in peers for e in p.of("applied")]
        rep.need(bool(applied), "no pause/resume commit was applied anywhere - the scenario did not "
                                "exercise the propagation bound")
    elif case == "request":
        check_request_flow(rep, peers, followers, side, url_b)
        check_dedupe(rep, peers)
    elif case == "conflict":
        check_conflict(rep, peers, followers, url_a, url_b)
        check_request_flow(rep, peers, followers, side, url_b)
    elif case == "reject":
        check_reject(rep, peers, side, url_b)
    elif case == "failed":
        check_failed(rep, peers, followers, url_b)
    else:
        rep.need(False, f"unknown case '{case}' in the manifest")

    for p, e in [(p, e) for p in peers for e in p.events]:
        if e.kind == "reconcile":
            m = e.fields["result"]
            if m not in (RESULT_OK, RESULT_BAND, "await-start", "await-coverage", "backoff",
                         "paused", "paused-seek", "resumed", "fail-pause", "fail-resume",
                         "fail-seek") and not RESULT_SEEK.match(m):
                rep.need(False, f"{p.name}: unparsed reconcile result '{m}' at line {e.pos + 1}")
    return rep


def print_report(rep: Report):
    sys.stdout.write(f"case={rep.case} verdict={rep.verdict} checks={rep.checks}\n")
    for w in rep.warnings:
        if w:
            sys.stdout.write(f"  warn: {w}\n")
    for f in rep.failures:
        sys.stdout.write(f"  FAIL: {f}\n")


# --------------------------------------------------------------------------------------------
# self-test: fabricated scenarios the verifier must accept, and the near-misses it must reject
# --------------------------------------------------------------------------------------------

TS = "{t} "

URL_A = "https://www.youtube.com/watch?v=aaaa"
URL_B = "https://www.youtube.com/watch?v=bbbb"


def _stamp(seconds: float) -> str:
    s = int(seconds)
    return f"{s // 3600:02d}:{(s % 3600) // 60:02d}:{s % 60:02d} "


def tx_state(t, why="cadence", nonce=7, rev=1, seq=2, intent="playing", url=URL_A,
             anchor=12000, host=1000, dur=200000, size=140, peers=1):
    return (f"{_stamp(t)}[music:sync] tx state why={why} nonce={nonce} rev={rev} seq={seq} "
            f"intent={intent} url='{url}' anchorMs={anchor} hostMs={host} durMs={dur} "
            f"bytes={size} peers={peers}")


def tx_probe(t, i=1):
    return f"{_stamp(t)}[music:sync] tx probe id={i} sendMs=1000 host=1 ok=True bytes=14"


def rx_reply(t, i=1, rtt=4, offset=500, samples=1):
    return (f"{_stamp(t)}[music:sync] rx probe-reply id={i} rttMs={rtt} offsetMs={offset} "
            f"bestRttMs={rtt} usingOffsetMs={offset} samples={samples} hostRecvMs=1000 "
            f"hostReplyMs=1000")


def rx_follow(t, rev=1, intent="playing", url=URL_A, anchor=12000, host=1000, target=12000):
    return (f"{_stamp(t)}[music:sync] rx FOLLOW rev={rev} seq=2 intent={intent} url='{url}' "
            f"title='Track' anchorMs={anchor} hostMs={host} rev-flag=False -> targetMs={target}")


def reconcile(t, target, actual, desired="False", actualPaused="False", coverage=200000,
              result="ok", rev=1, intent="playing", covered=100, offset=500):
    drift = actual - target
    return (f"{_stamp(t)}[music:sync] reconcile targetMs={target} actualMs={actual} "
            f"driftMs={drift} desiredPaused={desired} actualPaused={actualPaused} "
            f"coverageMs={coverage} result={result} rev={rev} intent={intent} offsetMs={offset} "
            f"bestRttMs=4 coveredMs={covered}")


def applied(t, desired="True", after=120, rev=1, actualPaused="True"):
    return (f"{_stamp(t)}[music:sync] applied desiredPaused={desired} afterMs={after} rev={rev} "
            f"anchorMs=12000 offsetMs=500 bestRttMs=4 actualPaused={actualPaused}")


def mirror(t, pos=12000, total=200000, state=2, status="STREAM", pending="False"):
    return (f"{_stamp(t)}[music:ui] mirror status={status} state={state} play=False pause=False "
            f"seek=False growing=False pos={pos} total={total} pending={pending} title='Track' "
            f"realGlyphs(play=False pause=False)")


def accepted(t, rev=1, url=URL_A, why="request:2"):
    return (f"{_stamp(t)}[music:sync] tx accepted-start rev={rev} url='{url}' why={why} "
            f"restart=False")


def req_submit_host(t, rid=1, sender=2, url=URL_B):
    return f"{_stamp(t)}[music:request] submit id={rid} from={sender} url='{url}'"


def req_accept_host(t, rid=1, client=2, rev=2):
    return (f"{_stamp(t)}[music:request] accept id={rid} -> client={client} rev={rev} ok=True "
            f"bytes=40 reason=''")


def req_reject_host(t, rid=1, client=2, rev=1, reason="not a streamable page link"):
    return (f"{_stamp(t)}[music:request] reject id={rid} -> client={client} rev={rev} ok=True "
            f"bytes=70 reason='{reason}'")


def req_submit_client(t, rid=1, url=URL_B):
    return (f"{_stamp(t)}[music:request] submit id={rid} url='{url}' -> host=1 ok=True bytes=48 "
            f"source=request; the current track keeps playing until an accepted revision arrives")


def req_accept_client(t, rid=1, rev=1, url=URL_B):
    return (f"{_stamp(t)}[music:request] accept id={rid} rev={rev} url='{url}': the host will "
            f"publish the session state; we start from that frame, not from here")


def req_reject_client(t, rid=1, url=URL_B, reason="not a streamable page link (direct audio stays with the game)"):
    return (f"{_stamp(t)}[music:request] reject id={rid} url='{url}' reason='{reason}'; "
            f"keeping the current track")


def engine_seek(t, asked, landed, ok="True"):
    return (f"{_stamp(t)}[music:stream] seek({asked}ms) -> RESULT.OK (FMOD_OK) in 1ms; "
            f"getPosition=RESULT.OK now={landed}ms delta={landed - asked}ms tolerance=300ms "
            f"ok={ok} snapLen=200000ms")


def rx_failed(t, rev=2, url=URL_B):
    return (f"{_stamp(t)}[music:sync] rx state rev={rev} seq=5 intent=failed url='{url}' from=1: "
            f"the host could not play this track")


def host_failed_state(t, rev=2, url=URL_B):
    return tx_state(t, why="transport:failed", rev=rev, intent="failed", url=url, anchor=0)


def write_case(root: Path, dirname: str, manifest: dict, logs: dict) -> Path:
    d = root / dirname
    d.mkdir(parents=True, exist_ok=True)
    (d / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    for name, lines in logs.items():
        (d / name).write_text("\n".join(lines) + "\n", encoding="utf-8")
    return d


def base_manifest(case="steady", side="B", **over):
    m = {"case": case,
         "peers": [{"name": "host", "role": "host", "log": "log-host.txt"},
                   {"name": "B", "role": "follower", "log": "log-B.txt"}],
         "side": side,
         "url_a": URL_A, "url_b": URL_B,
         "max_drift_ms": 750, "converge_s": 2.0, "pause_ms": 500, "panel_ms": 750}
    m.update(over)
    return m


def steady_logs():
    host = [tx_state(100.0, why="accepted-start", rev=1, seq=1, anchor=0, host=1000),
            tx_state(100.5, why="cadence", rev=1, seq=2),
            tx_state(101.0, why="cadence", rev=1, seq=3)]
    client = [rx_follow(100.2),
              tx_probe(100.2),
              rx_reply(100.3),
              reconcile(100.4, 12200, 12250, coverage=200000, result="await-start",
                        covered=-1),
              mirror(100.9, pos=12200),
              reconcile(101.0, 12800, 12780, coverage=200000, result="ok", covered=150),
              mirror(101.2, pos=12800),
              reconcile(101.5, 13300, 13320, coverage=200000, result="ok", covered=600)]
    return {"log-host.txt": host, "log-B.txt": client}


def pause_logs(paused_first=False):
    host = [tx_state(100.0, why="accepted-start", rev=1, seq=1, anchor=0, host=1000),
            tx_state(101.0, why="cadence", rev=1, seq=2, anchor=12000, host=2000,
                     intent="paused" if paused_first else "playing"),
            tx_state(101.5, why="transport:pause", rev=1, seq=3, anchor=13000, host=3000,
                     intent="paused")]
    client = [rx_follow(100.2, intent="paused" if paused_first else "playing",
                        anchor=12000 if paused_first else 0),
              tx_probe(100.2), rx_reply(100.3),
              reconcile(100.4, 12000, 12000, desired="True", actualPaused="True",
                        result="paused", covered=50),
              applied(100.4, desired="True", after=180),
              mirror(100.9, pos=12000, state=3, status="PAUSED"),
              reconcile(101.1, 12000, 12000, desired="True", actualPaused="True",
                        result="ok", covered=700),
              rx_follow(101.6, intent="paused", anchor=13000),
              applied(101.6, desired="True", after=140),
              reconcile(101.7, 13000, 13000, desired="True", actualPaused="True",
                        result="ok", covered=1300)]
    return {"log-host.txt": host, "log-B.txt": client}


def request_logs():
    host = [tx_state(100.0, why="accepted-start", rev=1, seq=1, anchor=0, host=1000),
            req_submit_host(110.0, rid=7, url=URL_B),
            accepted(110.1, rev=2, url=URL_B),
            tx_state(110.1, why="accepted-start", rev=2, seq=2, anchor=0, host=5000, url=URL_B),
            req_accept_host(110.2, rid=7, rev=2),
            tx_state(110.6, why="cadence", rev=2, seq=3, anchor=5000, host=5500, url=URL_B)]
    client = [rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
              reconcile(100.4, 1000, 1050, coverage=200000, result="ok", covered=100),
              mirror(100.9, pos=1500),
              req_submit_client(110.0, rid=7, url=URL_B),
              rx_follow(110.2, rev=2, url=URL_B, anchor=0, host=5000, target=0),
              reconcile(110.4, 800, 0, coverage=3000, result="await-start", covered=120),
              reconcile(111.0, 1200, 1200, coverage=200000, result="ok", covered=900, rev=2),
              mirror(111.2, pos=1300, pending="False"),
              req_accept_client(110.2, rid=7, rev=2, url=URL_B)]
    return {"log-host.txt": host, "log-B.txt": client}


def conflict_logs():
    # Two clients ask in the same second: A asks for URL_A, B for URL_B. The host takes them in
    # receive order, so B's request is the higher revision and that is where everyone ends up.
    host = [req_submit_host(200.0, rid=1, sender=3, url=URL_A),
            accepted(200.1, rev=1, url=URL_A, why="request:3"),
            req_accept_host(200.2, rid=1, client=3, rev=1),
            tx_state(200.2, why="accepted-start", rev=1, seq=1, anchor=0, host=1000, url=URL_A),
            req_submit_host(200.3, rid=2, sender=2, url=URL_B),
            accepted(200.4, rev=2, url=URL_B, why="request:2"),
            req_accept_host(200.5, rid=2, client=2, rev=2),
            tx_state(200.5, why="accepted-start", rev=2, seq=2, anchor=0, host=1010, url=URL_B),
            tx_state(201.0, why="cadence", rev=2, seq=3, anchor=1000, host=1510, url=URL_B)]
    client = [rx_follow(200.3, rev=1, url=URL_A, anchor=0, host=1000, target=0),
              tx_probe(200.3), rx_reply(200.4),
              reconcile(200.6, 300, 300, coverage=5000, result="ok", covered=250, rev=1),
              req_submit_client(200.3, rid=2, url=URL_B),
              rx_follow(200.7, rev=2, url=URL_B, anchor=0, host=1010, target=1000),
              reconcile(201.2, 1600, 1600, coverage=6000, result="ok", covered=400, rev=2),
              mirror(201.5, pos=1600)]
    return {"log-host.txt": host, "log-B.txt": client}


def reject_logs():
    host = [tx_state(300.0, why="accepted-start", rev=1, seq=1, anchor=0, host=1000),
            req_submit_host(310.0, rid=9, url=URL_B),
            req_reject_host(310.1, rid=9, rev=1, reason="not a streamable page link (direct audio stays with the game)"),
            tx_state(310.5, why="cadence", rev=1, seq=2, anchor=10000, host=1500)]
    client = [rx_follow(300.2), tx_probe(300.2), rx_reply(300.3),
              reconcile(300.4, 500, 500, result="ok", covered=100),
              mirror(300.9, pos=900),
              req_submit_client(310.0, rid=9, url=URL_B),
              req_reject_client(310.2, rid=9, url=URL_B),
              reconcile(311.0, 1600, 1600, result="ok", covered=700)]
    return {"log-host.txt": host, "log-B.txt": client}


def failed_logs():
    host = [accepted(400.0, rev=1, url=URL_B, why="request:2"),
            tx_state(400.0, why="accepted-start", rev=1, seq=1, anchor=0, host=1000, url=URL_B,
                     intent="buffering"),
            req_accept_host(400.1, rid=1, rev=1),
            host_failed_state(401.0, rev=1, url=URL_B)]
    client = [tx_probe(400.1), rx_reply(400.2),
              rx_follow(400.2, rev=1, url=URL_B, anchor=0, host=1000, target=0,
                        intent="buffering"),
              reconcile(400.5, 0, 0, desired="True", actualPaused="False", coverage=0,
                        result="await-start", covered=-1, rev=1, intent="buffering"),
              mirror(400.6, pos=0, state=1, status="BUF"),
              rx_failed(401.1, rev=1, url=URL_B),
              f"{_stamp(401.1)}[music:sync] the host failed this track; stopping our copy - a "
              f"private replacement is not what the session asked for"]
    return {"log-host.txt": host, "log-B.txt": client}


def self_test() -> int:
    ok = True
    with tempfile.TemporaryDirectory(prefix="music-evidence-") as tmp:
        root = Path(tmp)
        cases = []

        cases.append(("PASS", write_case(root, "good-steady", base_manifest("steady"), steady_logs())))
        cases.append(("PASS", write_case(root, "good-pause", base_manifest("pause"), pause_logs())))
        cases.append(("PASS", write_case(root, "good-pause-first",
                                         base_manifest("pause", expect_paused_first=True),
                                         pause_logs(paused_first=True))))
        cases.append(("PASS", write_case(root, "good-request", base_manifest("request"),
                                         request_logs())))
        cases.append(("PASS", write_case(root, "good-conflict", base_manifest("conflict"),
                                         conflict_logs())))
        cases.append(("PASS", write_case(root, "good-reject", base_manifest("reject"),
                                         reject_logs())))
        cases.append(("PASS", write_case(root, "good-failed", base_manifest("failed"),
                                         failed_logs())))

        # The reported defects, each as a log that must not pass.
        logs = steady_logs()
        logs["log-B.txt"] = [
            # no probe reply: the offset was never measured, the target came from nothing
            rx_follow(100.2),
            reconcile(100.4, 12200, 12250, result="ok", covered=-1),
            mirror(100.9, pos=12200),
            reconcile(101.0, 12800, 12780, result="ok", covered=150),
        ]
        cases.append(("FAIL", write_case(root, "bad-no-probe", base_manifest("steady"), logs)))

        logs = steady_logs()
        logs["log-B.txt"] = [
            rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
            reconcile(100.4, 12200, 12250, result="await-start", covered=-1),
            reconcile(101.0, 12800, 12800, result="ok", covered=5000),   # converged too late
            mirror(101.2, pos=12800),
        ]
        cases.append(("FAIL", write_case(root, "bad-slow-converge", base_manifest("steady"), logs)))

        logs = steady_logs()
        logs["log-B.txt"] = [
            rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
            reconcile(100.4, 12200, 12250, result="await-start", covered=-1),
            reconcile(101.0, 12800, 13500, result="ok", covered=150),     # 700 ms at the edge
            mirror(101.2, pos=13500),
        ]
        cases.append(("FAIL", write_case(root, "bad-drift", base_manifest("steady",
                                                                        max_drift_ms=600), logs)))

        logs = pause_logs()
        logs["log-B.txt"] = [rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
                             applied(100.4, desired="True", after=1200),
                             reconcile(100.5, 12000, 12000, desired="True",
                                       actualPaused="True", result="ok", covered=50),
                             mirror(100.9, pos=12000, state=3, status="PAUSED")]
        cases.append(("FAIL", write_case(root, "bad-pause-late", base_manifest("pause"), logs)))

        logs = pause_logs(paused_first=True)
        logs["log-B.txt"] = [rx_follow(100.2, intent="paused", anchor=12000),
                             tx_probe(100.2), rx_reply(100.3),
                             reconcile(100.35, 12000, 200, result="ok", actualPaused="False",
                                       covered=10),       # a frame of audio before the pause
                             reconcile(100.4, 12000, 12000, desired="True",
                                       actualPaused="True", result="paused", covered=50),
                             applied(100.4, desired="True", after=180),
                             mirror(100.9, pos=12000, state=3, status="PAUSED")]
        cases.append(("FAIL", write_case(root, "bad-created-playing",
                                         base_manifest("pause", expect_paused_first=True), logs)))

        logs = request_logs()
        logs["log-B.txt"] = [rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
                             reconcile(100.4, 1000, 1050, result="ok", covered=100),
                             mirror(100.9, pos=1500),
                             # the client starts the new URL locally, then the host accepts it
                             f"{_stamp(110.0)}[music:host] playAt url='{URL_B}' anchor=0ms "
                             f"paused=False source=remote-follow",
                             req_submit_client(110.0, rid=7, url=URL_B)]
        logs["log-host.txt"] = [tx_state(100.0, why="accepted-start", rev=1, seq=1, anchor=0,
                                        host=1000),
                                req_submit_host(110.0, rid=7, url=URL_B),
                                accepted(110.1, rev=2, url=URL_B),
                                req_accept_host(110.2, rid=7, rev=2),
                                tx_state(110.1, why="accepted-start", rev=2, seq=2, anchor=0,
                                         host=5000, url=URL_B)]
        cases.append(("FAIL", write_case(root, "bad-optimistic", base_manifest("request"), logs)))

        logs = request_logs()
        logs["log-host.txt"] = [req_submit_host(200.0, rid=1, sender=2, url=URL_A),
                                accepted(200.1, rev=1, url=URL_A),
                                req_accept_host(200.2, rid=1, rev=1),
                                req_submit_host(200.3, rid=2, sender=3, url=URL_B),
                                accepted(200.4, rev=1, url=URL_B),      # same revision: unresolvable
                                req_accept_host(200.5, rid=2, rev=1),
                                tx_state(200.5, why="accepted-start", rev=1, seq=1, anchor=0,
                                         host=1000, url=URL_B)]
        logs["log-B.txt"] = [rx_follow(200.3, rev=1, url=URL_A, anchor=0, host=1000, target=0),
                            tx_probe(200.3), rx_reply(200.4),
                            reconcile(200.6, 300, 300, result="ok", covered=250, rev=1),
                            rx_follow(200.7, rev=1, url=URL_B, anchor=0, host=1010, target=1000),
                            reconcile(201.2, 1600, 1600, result="ok", covered=400, rev=1)]
        cases.append(("FAIL", write_case(root, "bad-same-revision",
                                         base_manifest("conflict"), logs)))

        logs = reject_logs()
        logs["log-host.txt"] = [req_submit_host(310.0, rid=9, url=URL_B),
                                accepted(310.05, rev=2, url=URL_B),
                                req_reject_host(310.1, rid=9, rev=2,
                                                reason="not a streamable page link"),
                                tx_state(310.5, why="accepted-start", rev=2, seq=1, anchor=0,
                                         host=1500, url=URL_B)]
        logs["log-B.txt"] = [rx_follow(300.2), tx_probe(300.2), rx_reply(300.3),
                             reconcile(300.4, 500, 500, result="ok", covered=100),
                             mirror(300.9, pos=900),
                             req_submit_client(310.0, rid=9, url=URL_B),
                             rx_follow(310.2, rev=2, url=URL_B, anchor=0, host=1500, target=0),
                             reconcile(311.0, 1600, 1600, result="ok", covered=700, rev=2)]
        cases.append(("FAIL", write_case(root, "bad-reject-still-starts",
                                         base_manifest("reject"), logs)))

        logs = failed_logs()
        logs["log-host.txt"] = [accepted(400.0, rev=1, url=URL_B),
                                tx_state(400.0, why="accepted-start", rev=1, seq=1, anchor=0,
                                         host=1000, url=URL_B, intent="buffering"),
                                req_accept_host(400.1, rid=1, rev=1)]      # failure never broadcast
        cases.append(("FAIL", write_case(root, "bad-failed-silent",
                                         base_manifest("failed"), logs)))

        logs = steady_logs()
        logs["log-B.txt"] = [rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
                             reconcile(100.4, 12200, 12250, result="await-start", covered=-1),
                             reconcile(101.0, 12800, 12800, result="seek(12200->12800)",
                                       covered=150),
                             engine_seek(101.0, 12800, 12800, ok="False"),   # never verified
                             reconcile(101.5, 13300, 13300, result="ok", covered=600),
                             mirror(101.2, pos=12800)]
        cases.append(("FAIL", write_case(root, "bad-fake-seek", base_manifest("steady"), logs)))

        logs = steady_logs()
        logs["log-B.txt"] = [rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
                             reconcile(100.4, 12200, 12250, result="await-start", covered=-1),
                             reconcile(101.0, 12800, 12800, result="ok", covered=150),
                             mirror(101.2, pos=14000)]        # the panel is 1200 ms off
        cases.append(("FAIL", write_case(root, "bad-panel", base_manifest("steady"), logs)))

        logs = steady_logs()
        logs["log-B.txt"] = [rx_follow(100.2), tx_probe(100.2), rx_reply(100.3),
                             f"{_stamp(100.4)}[music:ui] PAUSE pressed -> our channel paused at "
                             f"12250ms (hit #1, real paused=True)",
                             reconcile(100.4, 12200, 12250, result="ok", covered=150),
                             mirror(101.2, pos=12250, pending="True")]
        cases.append(("FAIL", write_case(root, "bad-local-control", base_manifest("steady"), logs)))

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
                    help="scenario directory (manifest.json + one log per peer)")
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
