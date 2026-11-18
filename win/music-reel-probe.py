#!/usr/bin/env python3
"""Bounded two-instance music/Reel repros. No OS input, restarts or forced readiness.

Use `status` first. `reel` requires both peers in the same map and mutates inventory
and position. `eof` requires a lobby and uses the panel Load path for a 13s source.
`map` starts the supplied >=90s source in the lobby, verifies both peers are playing,
then teleports each player to the supplied coordinates on the native readiness pad.
Check the pad position using `status`; no ready flag or map selection is changed.

On a map, the remote client's authoritative position is moved by exactly one built-in
`goto.position (x,y,z)` on its own `qc.request` mailbox, with a UUID-correlated direct
exec acknowledgement; an explicit `standat=x,y,z,yaw` then sets its yaw, because a local
client standat changes yaw but not its position (verified live). Before every `interact`
both actors are gated on a fresh `[reel:pose]` line read on the host (`reel.request
pose=<clientId>`), allowing a ground-settle distance and a small yaw tolerance. Mutations
are dispatched once and never retried; only read-only pose diagnostics are re-asked,
within a bound. The probe never aims at or repositions a booth.
"""
import argparse
from contextlib import ExitStack
from importlib import import_module
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

import session

focus = import_module('focus-logs')
SHORT = 'https://www.youtube.com/watch?v=R6DiFlAXrS0'
BOXES = ('qc.request', 'interact.request', 'reel.request', 'ui.request', 'music.request')
MAX_LOG = 32 * 1024 * 1024
POSE_SETTLE = 0.35          # metres: the host read may still settle onto the ground
YAW_TOL = 0.05              # horizontal body forward vs (sin,cos) of the requested yaw
POSE = re.compile(
    r'\[reel:pose\] actor=(\d+) source=([^ \r\n]+) '
    r'pos=\((-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)\) '
    r'fwd=\((-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)\)')


class Log:
    def __init__(self, path):
        self.path = path
        s = path.stat()
        self.identity = (s.st_dev, s.st_ino)
        self.start = self.last_size = s.st_size

    def mark(self):
        s = self.path.stat()
        if (s.st_dev, s.st_ino) != self.identity or s.st_size < self.last_size:
            raise RuntimeError(f'log replaced or truncated: {self.path}')
        self.last_size = s.st_size
        return s.st_size

    def since(self, offset):
        length = self.mark() - offset
        if not 0 <= length <= MAX_LOG:
            raise RuntimeError(f'log window out of bounds: {self.path}')
        with self.path.open('rb') as f:
            f.seek(offset)
            return f.read(length).decode('utf-8', 'replace')


def process_log(t):
    instance = t.process['instance']
    return Log(t.root / 'BepInEx' / (f'logs/{instance}.log' if instance else 'LogOutput.log'))


def owned(state):
    return any(p['owned'] and p['connected'] and p['id'] == state['clientId'] and
               p['scene'] == state['scene'] for p in state['players'])


def same_pair(states):
    t, p = states['target'], states['peer']
    return (p['server'] and not t['server'] and p['lobbyId'] == t['lobbyId'] != '0' and
            t['clientId'] != p['clientId'] and all(s['listening'] and s['connected'] and s['inLobby'] for s in (p, t)))


def settled_spin(host, client, actor, stake, remote):
    settled = re.findall(rf"\[reel:net\] settle spin=(\d+) actor={actor} type=", host)
    if len(settled) > 1:
        raise RuntimeError('more than one authoritative settlement')
    if not settled:
        return None
    spin = settled[0]
    takes = re.findall(rf"\[reel\] consumed held='{stake}' count=1\b", host)
    if len(takes) != 1:
        raise RuntimeError(f'expected one authoritative stake consumption, got {len(takes)}')
    if remote:
        accepted = re.findall(rf"\[reel:net\] ACCEPT id=(\d+) client={actor} .*held='{stake}'", host)
        if len(accepted) != 1:
            raise RuntimeError(f'expected exactly one accepted request, got {len(accepted)}')
        if f'request {accepted[0]} SETTLED spin={spin}' not in client:
            return None
    elif '[reel] result text=' not in host:
        return None
    if re.search(rf'\[reel:net\] settle spin={spin} actor={actor}(?: \(you\))? type=', client):
        return spin
    return None


def poses(text, actor):
    # Successful host reads for one actor, oldest first. A failure diagnostic has no
    # [reel:pose] line and a truncated or non-numeric one does not match, so neither can be
    # mistaken for a pose: an unreadable log yields no readings, never a guessed pose.
    readings = []
    for match in POSE.finditer(text):
        if match[1] != str(actor):
            continue
        values = [float(v) for v in match.groups()[2:]]
        if all(math.isfinite(v) for v in values):
            readings.append(dict(source=match[2], pos=tuple(values[:3]), fwd=tuple(values[3:])))
    return readings


def pose_problems(reading, expected, yaw=True):
    # expected is the explicit harness pose x,y,z,yaw. Only that pose is compared: nothing
    # here derives a position or heading from a booth.
    if reading is None:
        return ['no successful pose log']
    parts = [float(v) for v in expected.split(',')]
    distance = math.dist(reading['pos'], tuple(parts[:3]))
    problems = [] if distance <= POSE_SETTLE else [
        f'position {distance:.2f}m away via {reading["source"]} (limit {POSE_SETTLE:.2f}m)']
    if yaw:
        target = math.radians(parts[3])
        fx, _, fz = reading['fwd']
        flat = math.hypot(fx, fz)
        if flat < 0.5:
            problems.append(f'facing is not horizontal (fwd=({fx:.2f},{fz:.2f}))')
        else:
            # The body forward can carry pitch: compare its horizontal heading to (sin,cos).
            for axis, got, want in (('x', fx / flat, math.sin(target)), ('z', fz / flat, math.cos(target))):
                if abs(got - want) > YAW_TOL:
                    problems.append(f'yaw fwd.{axis}={got:.3f} vs {want:.3f} (tolerance {YAW_TOL:.2f})')
    return problems


def transport(text):
    values = re.findall(r'\[music:req\] status: transport playing=(True|False) channelOpen=(True|False) retiring=(\d+)', text)
    if not values:
        raise ValueError('missing transport status; deploy current diagnostic build')
    playing, opened, retiring = values[-1]
    return playing == 'True', opened == 'True', int(retiring)


def actively_playing(text):
    phases = re.findall(r'\[music:req\] status: phase=(\w+)', text)
    playing, opened, _ = transport(text)
    return playing and opened and phases[-1:] == ['Playing']


def youtube_duration(url):
    if not re.fullmatch(r'https://www\.youtube\.com/watch\?v=[A-Za-z0-9_-]{11}', url):
        raise ValueError('expected https://www.youtube.com/watch?v=VIDEO_ID')
    result = subprocess.run(['yt-dlp', '--ignore-config', '--no-playlist', '--no-warnings', '--skip-download',
                             '--socket-timeout', '6', '--retries', '0', '--extractor-retries', '0',
                             '-f', 'bestaudio/best', '--print', '%(duration)s %(acodec)s', url],
                            text=True, capture_output=True, check=True, timeout=16)
    duration, codec = result.stdout.split()
    if codec in ('none', 'NA'):
        raise ValueError('video has no audio stream')
    return float(duration)


class Probe:
    def __init__(self, peers, logs, deadline, args):
        self.peers, self.logs, self.deadline, self.args = peers, logs, deadline, args
        self.events = []
        self.spins = {}
        self.membership = None
        self.stage = 'preflight'

    def observe(self):
        states = {k: t.ask() for k, t in self.peers.items()}
        if self.membership is not None and (not same_pair(states) or any(
                (s['lobbyId'], s['clientId']) != self.membership[r] for r, s in states.items())):
            raise RuntimeError('paired lobby membership changed during the probe')
        return states

    def guard(self, scene):
        s = self.observe()
        if not same_pair(s) or any(x['scene'] != scene or not x['sceneLoaded'] or not owned(x) for x in s.values()):
            raise RuntimeError('pair no longer ready in expected scene: ' + scene)
        return s

    def until(self, label, predicate, seconds=8, delay=.15, mutation=True):
        self.stage = label
        end = time.monotonic() + min(seconds, self.deadline.remaining())
        while time.monotonic() < end:
            answer = predicate()
            if answer:
                return answer
            time.sleep(delay)
        raise TimeoutError(label + ': not observed before deadline' +
                           (' (no mutation retry)' if mutation else ' (read-only; bounded re-ask only)'))

    def request(self, role, box, command, expected, seconds=8, mutation=True):
        self.deadline.check()
        mailbox = self.peers[role].screens / box
        temp = mailbox.with_name('.probe-' + uuid.uuid4().hex)
        cursor = self.logs[role].mark()
        self.stage = f'{role}: {box} {command}'
        self.events.append(dict(stage=self.stage, offset=cursor, utc=session.diag.utc()))
        temp.write_text(command + '\n', encoding='utf-8')
        try:
            os.link(temp, mailbox)  # atomically publish; do not overwrite another request
            def evidence():
                text = self.logs[role].since(cursor)
                return text if not mailbox.exists() and re.search(expected, text) else None
            return self.until(self.stage, evidence, seconds=seconds, mutation=mutation)
        finally:
            try:
                if mailbox.samefile(temp):
                    mailbox.unlink()
            except FileNotFoundError:
                pass  # already taken; a timeout does not prove it failed to execute
            temp.unlink(missing_ok=True)

    def qc(self, role, command, seconds=8):
        """Exactly one direct console dispatch, correlated by a fresh request id.

        The acknowledgement proves dispatch, not that an asynchronous game action succeeded,
        so effects are observed separately. A missing acknowledgement leaves the outcome
        unknown and is never followed by a retry of this mutation.
        """
        self.deadline.check()
        mailbox = self.peers[role].screens / 'qc.request'
        request_id = uuid.uuid4().hex
        temp = mailbox.with_name('.probe-qc-' + request_id)
        cursor = self.logs[role].mark()
        self.stage = f'{role}: qc.request exec {command}'
        self.events.append(dict(stage=self.stage, offset=cursor, utc=session.diag.utc()))
        temp.write_text(f'exec {request_id} {command}\n', encoding='utf-8')
        try:
            os.link(temp, mailbox)  # complete publication; an existing request is never replaced
            terminal = re.compile(rf'\[qc:direct\] id={request_id} state=(dispatched|error)\b')
            def evidence():
                text = self.logs[role].since(cursor)
                match = terminal.search(text)
                return (match, text) if match else None
            match, text = self.until(self.stage, evidence, seconds=seconds)
            if match[1] == 'error':
                detail = re.search(rf'\[qc:direct\] id={request_id} state=error[^\r\n]*', text)
                raise RuntimeError(f'{self.stage}: {detail.group() if detail else "dispatch error"}')
            return text
        finally:
            try:
                if mailbox.samefile(temp):
                    mailbox.unlink()
            except FileNotFoundError:
                pass  # already taken; a timeout does not prove it failed to execute
            temp.unlink(missing_ok=True)

    def goto_pos(self, role, pose):
        """The one native position mutation for a remote client: goto.position (x,y,z).

        A local client standat changes yaw but not its authoritative position, so the map
        position comes from the game's own goto and the yaw follows in a separate standat.
        Exactly one dispatch: goto.position is asynchronous (~1-12s) and is proven by the
        host's fresh pose read, never by repeating the command.
        """
        x, y, z = pose.split(',')[:3]
        return self.qc(role, f'goto.position ({x},{y},{z})')

    def pose(self, actor, expected, yaw=True, seconds=14):
        """Gate one actor on the host's own read of that actor's pose.

        The request goes to the peer (the host) for the actor's clientId, so the same gate
        covers the host's own actor and the remote client actor. Every accepted reading is
        fresh (only lines after this request's log mark) and successful. Read-only, so a
        missed answer is re-asked until the bound; nothing here retries a mutation.
        """
        problems = ['no pose observation']
        end = time.monotonic() + min(seconds, self.deadline.remaining())
        while time.monotonic() < end:
            self.deadline.check()
            try:
                text = self.request('peer', 'reel.request', f'pose={actor}', rf'\[reel:pose\] actor={actor} ',
                                    seconds=min(6, max(.5, end - time.monotonic())), mutation=False)
            except TimeoutError:
                problems = ['no fresh successful pose log']
                continue
            readings = poses(text, actor)
            problems = pose_problems(readings[-1] if readings else None, expected, yaw)
            if not problems:
                return readings[-1]
            time.sleep(.2)
        raise TimeoutError(f'{actor}: host pose not confirmed: {"; ".join(problems)} (read-only; no mutation retry)')

    def music(self, role):
        return self.request(role, 'music.request', 'status', r'\[music:req\] status: transport .*retiring=\d+\r?\n')

    def status(self, states):
        for role in states:
            self.request(role, 'ui.request', 'scene', r'\[ui:scene\] player=')
            self.music(role)
            if states[role]['scene'] == 'Lobby':
                self.request(role, 'ui.request', 'lobby', r'\[ui:lobby\]')
            else:
                self.request(role, 'interact.request', 'booths', r'\[reel:act\] booths live=')

    def reel(self, states):
        scene = states['peer']['scene']
        if scene in ('Lobby', 'Menu', ''):
            raise ValueError('Reel stake comparison requires a loaded map')
        order = ('target', 'peer') if self.args.actor == 'both' else (self.args.actor,)
        stake, booth = self.args.item, self.args.booth
        for role in order:
            sid = self.guard(scene)[role]['clientId']
            remote = role == 'target'
            def inventory():
                text = self.request(role, 'reel.request', f'inv={stake}', r'\[reel:inv\].*count\(', mutation=False)
                match = re.search(rf"count\('{re.escape(stake)}'\)=(\d+)", text)
                if not match:
                    raise RuntimeError(f'{role}: item count unavailable')
                return int(match.group(1)), text
            if inventory()[0] == 0:
                command = f'giveremote={sid},{stake}' if remote else f'give={stake}'
                reply = self.request('peer', 'reel.request', command, r'\[reel:debug\] give(?:-player)? .*ok=(True|False)')
                if 'ok=False' in reply:
                    raise RuntimeError(f'{role}: grant refused; inventory may be full')
                self.until(f'{role}: confirmed stake grant', lambda: inventory()[0] > 0, seconds=12, mutation=False)
            reply = self.request(role, 'reel.request', f'hold={stake}', r'\[reel:debug\] hold.*(?:slotIndex=|found no slot|no local player|not in the runtime registry|no active-slot controller|threw:)')
            if 'slotIndex=' not in reply:
                raise RuntimeError(f'{role}: hold refused for {stake}')
            self.until(f'{role}: confirmed held {stake}', lambda: f"held='{stake}'" in inventory()[1], seconds=12, mutation=False)
            pos = getattr(self.args, role + '_booth_pos')
            if remote:
                # Position first, exactly once, then wait on the host's read of the remote body
                # for the asynchronous goto to land before the yaw mutation.
                self.goto_pos(role, pos)
                self.pose(sid, pos, yaw=False, seconds=16)
            self.request(role, 'interact.request', 'standat=' + pos, r'\[reel:act\] standat pos=.*via=')
            self.guard(scene)  # allow teleport and slot replication to reach the host
            self.pose(sid, pos)  # fresh authoritative host pose for this actor before interact
            marks = {r: log.mark() for r, log in self.logs.items()}
            action = 'complete' if self.args.callback_only else 'interact'
            self.request(role, 'interact.request', f'{action}@{booth}', rf'\[reel:act\] {action} test cheat:')
            def outcome():
                host, client = [self.logs[r].since(marks[r]) for r in ('peer', 'target')]
                rejection = re.search(rf'\[reel:net\] reject id=\d+ client={sid} reason=[^\r\n]*', host)
                if remote and rejection:
                    raise RuntimeError(rejection.group())
                actor_log = client if remote else host
                fault = re.search(r'\[reel:act\] (?:interact test threw |local booth interaction refused)[^\r\n]*', actor_log)
                if fault:
                    raise RuntimeError(fault.group())
                if ('NetworkBehaviour must be spawned' in actor_log and
                        'at InteractionSystem.Core.Interactable.Interact (' in actor_log):
                    raise RuntimeError('stock Interact threw an unspawned NetworkBehaviour RPC exception')
                return settled_spin(host, client, sid, stake, remote)
            spin = self.until(f'{role}: matched consume/spin/settle', outcome, seconds=13)
            self.spins[role] = spin
            boundary = 'callback-only' if self.args.callback_only else 'game Interact'
            print(f'{role}: {boundary} settled spin {spin}; physical hold dispatch not tested', flush=True)
        return self.spins

    def music_test(self, mode, url, duration):
        self.guard('Lobby')
        for role in self.peers:
            self.music(role)  # require the diagnostic schema before mutating
            if mode == 'map':
                self.request(role, 'ui.request', 'lobby', r'\[ui:lobby\]')
        marks = {r: log.mark() for r, log in self.logs.items()}
        self.request('peer', 'music.request', 'load=' + url, r'\[music:req\] load=')
        self.until('both peers playing', lambda: all(actively_playing(self.music(r)) for r in ('peer', 'target')), seconds=60)
        if mode == 'eof':
            self.until('authoritative natural EOF log', lambda: 'PLAYBACK ENDED' in self.logs['peer'].since(marks['peer']),
                       seconds=duration + 8)
        else:
            self.guard('Lobby')
            for role, pos in [('target', self.args.target_pad), ('peer', self.args.peer_pad)]:
                self.request(role, 'ui.request', 'standat=' + pos, r'\[reel:act\] standat pos=.*via=')
            def entered():
                # After a read-only timeout, the game may still be loading and have the
                # expired observe queued. Wait for consumption, never overwrite it.
                if any((t.screens / 'session.request').exists() for t in self.peers.values()):
                    return False
                try:
                    s = self.observe()
                except TimeoutError:
                    # A loading frame can miss one read-only mailbox deadline. Never repeat a teleport.
                    return False
                t, p = s['target'], s['peer']
                return (same_pair(s) and t['scene'] == p['scene'] not in ('Lobby', 'Menu', '') and
                        all(x['sceneLoaded'] and owned(x) for x in s.values()))
            self.until('native pad transition: both map players loaded', entered, seconds=70, delay=.4)
            if not re.search(r"\[music:host\] stop \('left Lobby scene'\) from phase=Playing\b",
                             self.logs['peer'].since(marks['peer'])):
                raise RuntimeError('host was not observed stopping a playing track on lobby exit')
        def stopped():
            return all(transport(self.music(r)) == (False, False, 0) for r in self.peers)
        self.until('channels closed and stream retire queue drained on both peers', stopped, seconds=12)
        end = time.monotonic() + 20
        cleanup_marks = {r: log.mark() for r, log in self.logs.items()}
        while time.monotonic() < end:
            if not stopped():
                raise RuntimeError('transport restarted or resources reopened during observation')
            time.sleep(min(1, max(0, end - time.monotonic())))
        for role in self.peers:
            fresh = self.logs[role].since(marks[role])
            spam = re.findall(r"\[music:(?:host|sync)\].*(?:finished track's channel stopped|fail-seek|fail-pause|fail-resume)", fresh)
            if spam:
                raise RuntimeError(f'{role}: transport warnings during the controlled run: {spam[:3]}')
            groups, _ = focus.summarize(self.logs[role].since(cleanup_marks[role]).splitlines())
            if groups:
                raise RuntimeError(f'{role}: errors or stopped-channel spam after playback: {groups.most_common(3)}')


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument('operation', choices=['status', 'reel', 'eof', 'map'])
    for role in ('target', 'peer'):
        p.add_argument(f'--{role}-root', required=True)
        p.add_argument(f'--{role}-instance')
        p.add_argument(f'--{role}-pid', type=int)
        p.add_argument(f'--{role}-pad', help='explicit native readiness pad pose x,y,z,yaw')
        p.add_argument(f'--{role}-booth-pos', help='explicit actor pose x,y,z,yaw (remote position via native goto, then yaw)')
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--timeout', type=int, default=160)
    p.add_argument('--actor', choices=['target', 'peer', 'both'], default='both')
    p.add_argument('--callback-only', action='store_true', help='bypass game Interact to isolate downstream Reel networking')
    p.add_argument('--booth', type=int, default=1)
    p.add_argument('--item', default='healthsyringe')
    p.add_argument('--url')
    p.add_argument('--ssh-host', default='windows')
    p.add_argument('--staging', default='/mnt/c/AlterumTools/diagnostic-worker')
    a = p.parse_args()
    if not 0 < a.booth <= 100 or not re.fullmatch(r'[a-z0-9_]+', a.item) or not 15 <= a.timeout <= 300:
        p.error('invalid booth/item/timeout')
    for role in ('target', 'peer'):
        root = session.diag.local_path(getattr(a, role + '_root'))
        instance, pid = getattr(a, role + '_instance'), getattr(a, role + '_pid')
        if not (root / 'Alterum.exe').is_file() or (instance and not re.fullmatch(r'[A-Za-z0-9_-]+', instance)) or (pid is not None and pid <= 0):
            p.error('invalid install/instance/PID for ' + role)
        setattr(a, role + '_root', str(root))
    if a.operation == 'reel':
        roles = ('target', 'peer') if a.actor == 'both' else (a.actor,)
        for role in roles:
            pose = getattr(a, role + '_booth_pos')
            if not pose or not re.fullmatch(r'-?\d+(?:\.\d+)?(?:,-?\d+(?:\.\d+)?){3}', pose) or not all(math.isfinite(float(x)) for x in pose.split(',')):
                p.error('reel requires an explicit booth pose for each actor: x,y,z,yaw')
    if a.operation == 'map':
        for pose in (a.target_pad, a.peer_pad):
            if not pose or not re.fullmatch(r'-?\d+(?:\.\d+)?(?:,-?\d+(?:\.\d+)?){3}', pose) or not all(math.isfinite(float(x)) for x in pose.split(',')):
                p.error('map requires both explicit pad poses: x,y,z,yaw')
        if not a.url:
            p.error('map requires a long --url')
    out = a.output.expanduser().resolve() / uuid.uuid4().hex
    out.mkdir(parents=True)
    print(out, flush=True)
    deadline = session.diag.Deadline(a.timeout)
    logs, probe = {}, None
    report = dict(operation=a.operation, status='failed')
    try:
        duration = None
        if a.operation in ('eof', 'map'):
            url = a.url or SHORT
            duration = youtube_duration(url)
            if not (8 <= duration <= 15 if a.operation == 'eof' else duration >= 90):
                raise ValueError('video duration does not match the requested test')
            report.update(url=url, verifiedDuration=duration)
        discovery = session.discover(a, out, deadline)
        entries = session.os_observe(discovery, 'probe-preflight', discovery.targets, deadline)
        peers = {role: session.Transport(getattr(a, role + '_root'), entries[key]['process'], out, role, deadline)
                 for role, key in (('target', 'secondary'), ('peer', 'main'))}
        if peers['target'].process['pid'] == peers['peer'].process['pid']:
            raise ValueError('selectors resolve to the same process')
        with ExitStack() as stack:
            session.lock_transports(stack, peers.values())
            for t in peers.values():
                if any((t.screens / box).exists() for box in BOXES):
                    raise RuntimeError('a repro mailbox is already busy')
            logs = {role: process_log(t) for role, t in peers.items()}
            probe = Probe(peers, logs, deadline, a)
            states = probe.observe()
            fields = ('pid', 'scene', 'lobbyId', 'clientId', 'mvid', 'generation')
            report['before'] = {r: {k: s[k] for k in fields} for r, s in states.items()}
            if not same_pair(states):
                raise RuntimeError('selected peers do not share the same active lobby')
            probe.membership = {r: (s['lobbyId'], s['clientId']) for r, s in states.items()}
            if a.operation == 'status':
                probe.status(states)
            elif a.operation == 'reel':
                # Matched spins land in the report before the final observe, so a post-observe
                # failure stays distinguishable from the interaction result.
                report['interaction'] = dict(status='matched', spins=probe.reel(states))
            else:
                probe.music_test(a.operation, url, duration)
            probe.stage = 'post-observe'
            after = probe.observe()  # validate the same hot process/generation after the action
            report['after'] = {r: {k: s[k] for k in fields} for r, s in after.items()}
        report['status'] = 'passed'
    except (OSError, TimeoutError, ValueError, RuntimeError, KeyError, subprocess.SubprocessError) as error:
        report.update(error=str(error), failedStage=probe.stage if probe else 'preflight')
        print(f"{report['failedStage']}: {error}", file=sys.stderr)
    finally:
        report['events'] = probe.events if probe else []
        if probe and probe.spins and 'interaction' not in report:
            report['interaction'] = dict(status='partial', spins=probe.spins)
        for role, log in logs.items():
            try:
                text = log.since(log.start)
                (out / f'{role}.log').write_text(text, encoding='utf-8')
                groups, events = focus.summarize(text.splitlines())
                (out / f'{role}-focused.txt').write_text(''.join(f'{n:7} {msg}\n' for msg, n in groups.most_common()) + '\n' + '\n'.join(events), encoding='utf-8')
            except (OSError, RuntimeError) as error:
                report.setdefault('evidenceErrors', {})[role] = str(error)
                report['status'] = 'failed'
        (out / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    return int(report['status'] != 'passed')


if __name__ == '__main__':
    sys.exit(main())
