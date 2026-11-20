#!/usr/bin/env python3
"""Confirmed normal-flow session automation for existing Alterum clients (WSL, stdlib).

  python3 win/session.py rejoin --target-root /mnt/c/AlterumB --target-instance b \
      --peer-root /mnt/c/AlterumPinned --peer-instance '' --output ~/session-evidence

The peer is the observed host of the intended lobby. Only target UI is mutated.
No launches, restarts, direct network joins, model changes or reservation bypasses.
Exit 0: operation ready; 1: flow/readiness failed; 2: invocation/preflight failed;
3: rejoined and ready, but optional post-cycle diagnostics reported findings/incomplete.
"""
import argparse
from contextlib import ExitStack
from datetime import datetime, timezone
import fcntl
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location('session_diagnostics', HERE / 'collect-diagnostics.py')
diag = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(diag)


def alive_players(state):
    return {p['id'] for p in state['players'] if p['connected'] and p['scene'] == 'Lobby'}


def owned_player(state):
    return [p for p in state['players'] if p['owned'] and p['connected'] and p['scene'] == 'Lobby'
            and p['id'] == state['clientId']]


def diagnostics_only(state):
    # Identity, not truthiness: only a real JSON true opts in, so a missing field from an older
    # runtime or a stale 'true' string can never relax a check.
    return state.get('diagnosticsOnly') is True


def native_model_observed(state):
    # The game's own row is applied and resolved: nothing custom, nothing pending.
    return (state['modelDb'] >= 0 and state['modelRow'] not in ('', '-', '?')
            and state['modelReadiness'] == 'native')


def roster_ok(state):
    # A diagnostics-only runtime disables gameplay registration, so it reports rosterReady=false.
    # Accept only that explicitly reported mode, and only while it still shows a native applied
    # model; every other roster failure stays a failure.
    return state['rosterReady'] or (diagnostics_only(state) and native_model_observed(state))


def host_problems(peer, lobby):
    checks = dict(host_role=peer['server'] and peer['listening'], host_membership=peer['inLobby'] and peer['lobbyId'] == lobby,
                  host_scene=peer['scene'] == 'Lobby' and peer['sceneLoaded'], host_player=len(owned_player(peer)) == 1)
    return [k for k, ok in checks.items() if not ok]


def connected_problems(target, peer, lobby):
    checks = dict(client_role=not target['server'] and target['connected'] and target['listening'],
                  target_membership=target['inLobby'] and target['lobbyId'] == lobby,
                  target_scene=target['scene'] == 'Lobby' and target['sceneLoaded'],
                  local_player=len(owned_player(target)) == 1,
                  host_sees_client=target['clientId'] in alive_players(peer),
                  client_sees_host=peer['clientId'] in alive_players(target),
                  distinct_clients=target['clientId'] != peer['clientId'],
                  roster_ready=roster_ok(target) and roster_ok(peer),
                  model_observed=target['modelDb'] >= 0 and target['modelRow'] not in ('', '-', '?')
                      and target['modelReadiness'] in ('native', 'ready'),
                  peer_model_observed=peer['modelDb'] >= 0 and peer['modelRow'] not in ('', '-', '?'),
                  target_ui_idle=not target['uiBusy'])
    return host_problems(peer, lobby) + [k for k, ok in checks.items() if not ok]


def disconnected_problems(target, peer, lobby, old_client):
    checks = dict(menu_loaded=target['scene'] == 'Menu' and target['sceneLoaded'],
                  membership_cleared=not target['inLobby'] and target['lobbyId'] == '0',
                  network_stopped=not target['listening'] and not target['connected'] and not target['server'],
                  player_despawned=not target['players'],
                  host_removed_client=old_client is None or old_client not in alive_players(peer),
                  menu_interactive=target['codeReady'], target_ui_idle=not target['uiBusy'])
    return host_problems(peer, lobby) + [k for k, ok in checks.items() if not ok]


def stable_signature(target, peer):
    # BodyState/material health deliberately excluded: a rendering regression is NOT a join timeout.
    return tuple((s['lobbyId'], s['clientId'], tuple(sorted(alive_players(s))), s['modelRow'], s['modelDb'], s['modelSlug'])
                 for s in (target, peer))


class Stable:
    def __init__(self, count=3, seconds=1):
        self.required, self.seconds = count, seconds
        self.key, self.count, self.since = None, 0, 0

    def accept(self, problems, signature, now):
        if problems:
            self.key, self.count = None, 0
            return False
        if signature != self.key:
            self.key, self.count, self.since = signature, 1, now
        else:
            self.count += 1
        return self.count >= self.required and now - self.since >= self.seconds


class Transport:
    def __init__(self, root, process, out, role, deadline):
        self.root, self.process, self.out, self.role, self.deadline = Path(root), process, out, role, deadline
        self.screens = self.root / 'BepInEx/screens' / process['instance']
        self.baseline, self.last_frame, self.serial = None, -1, 0

    def ask(self, action='observe', lobby='0', code='-', client='-'):
        self.deadline.check()
        request_id = uuid.uuid4().hex
        reply = self.screens / ('session-' + request_id + '.json')
        request = self.screens / 'session.request'
        if request.exists():
            raise RuntimeError(self.role + ' session mailbox busy; existing request not overwritten')
        limit = min(8, self.deadline.remaining())
        expires = int((time.time() + limit) * 1000)
        start = self.baseline['startUtc'] if self.baseline else self.process['startUtc']
        mvid = self.baseline['mvid'] + ':' + self.baseline['generation'] if self.baseline else '-'
        text = f'v1 {request_id} {expires} {self.process["pid"]} {start} {mvid} {lobby} {action} {code} {client}\n'
        temporary = self.screens / ('.session-' + request_id + '.tmp')
        try:
            temporary.write_text(text, encoding='utf-8')
            os.link(temporary, request)  # complete publication, no overwrite
        finally:
            temporary.unlink(missing_ok=True)
        started, until = time.time(), time.monotonic() + limit
        while time.monotonic() < until:
            if reply.is_file():
                if reply.stat().st_size > 256 * 1024:
                    raise ValueError('session reply too large')
                text = reply.read_text(encoding='utf-8-sig')
                self.serial += 1
                (self.out / f'{self.role}-{self.serial:04}-{action}.json').write_text(text, encoding='utf-8')
                data = json.loads(text)
                if data.get('schema') != 'alterum.session/1' or data.get('requestId') != request_id or data.get('action') != action:
                    raise ValueError('session reply schema/request/action mismatch')
                if data.get('status') != ('observed' if action == 'observe' else 'dispatched'):
                    raise RuntimeError(self.role + ': ' + data.get('error', 'request refused'))
                state = data['state']
                if (state['pid'] != self.process['pid'] or diag.path_key(state['exe']) != diag.path_key(self.process['exe'])
                    or state['instance'] != self.process['instance']
                    or abs(diag.instant(state['startUtc']) - diag.instant(self.process['startUtc'])) >= .001):
                    raise ValueError('runtime process identity does not match OS discovery')
                if not started - .25 <= diag.instant(state['utc']) <= time.time() + .25:
                    raise ValueError('session reply timestamp is not fresh')
                if state['frame'] <= self.last_frame:
                    raise ValueError('session reply did not advance a game frame')
                if self.baseline and any(state[k] != self.baseline[k] for k in ('mvid', 'generation')):
                    raise RuntimeError('hot generation changed during flow; stopping')
                self.baseline = self.baseline or state
                self.last_frame = state['frame']
                return state
            time.sleep(.05)
        raise TimeoutError(f'{self.role} {action}: no current reply; request may still execute until its expiry. Do not blindly retry mutations.')


class Flow:
    def __init__(self, target, peer, out, deadline, stage_timeout=30, emit=print):
        self.target, self.peer, self.out, self.deadline = target, peer, out, deadline
        self.stage_timeout, self.emit = stage_timeout, emit
        self.report = dict(schema='alterum.session-flow/1', startedUtc=diag.utc(), operation=None,
                           stages=[], readiness='not-confirmed', health='not-requested', mutationAttempted=False,
                           scope='Sequential fresh peer samples; not an atomic cross-process snapshot.')
        self.lobby, self.code, self.old_client = None, None, None
        self.stage = 'preflight'

    def pair(self):
        # Host first: refuse further target mutations immediately if it changed lobby/authority.
        peer = self.peer.ask()
        if self.lobby:
            problems = host_problems(peer, self.lobby)
            if problems:
                raise RuntimeError('observed host changed: ' + ', '.join(problems))
            if peer['clientId'] != self.host_client:
                raise RuntimeError('host client identity changed')
        target = self.target.ask()
        self.report['lastObservation'] = dict(target=target, peer=peer)
        return target, peer

    def wait(self, name, predicate, stable=True):
        self.stage = name
        self.emit('session: ' + name)
        stage = dict(name=name, status='waiting', startedUtc=diag.utc(), samples=0)
        self.report['stages'].append(stage)
        clock, tracker = time.monotonic(), Stable()
        global_end = self.deadline.ends
        self.deadline.ends = min(global_end, clock + self.stage_timeout)
        try:
            while True:
                self.deadline.check()
                t, p = self.pair()
                self.deadline.check()
                problems = predicate(t, p)
                stage.update(samples=stage['samples'] + 1, missing=problems)
                if (tracker.accept(problems, stable_signature(t, p), time.monotonic()) if stable else not problems):
                    stage.update(status='ready', elapsedSeconds=round(time.monotonic() - clock, 3))
                    return t, p
                time.sleep(min(.2, self.deadline.remaining()))
        except TimeoutError as error:
            stage.update(status='timeout', elapsedSeconds=round(time.monotonic() - clock, 3))
            raise TimeoutError(name + ': missing ' + ', '.join(stage.get('missing') or ['stable fresh observations']) + '; ' + str(error)) from error
        except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
            stage.update(status='incomplete', reason=str(error), elapsedSeconds=round(time.monotonic() - clock, 3))
            raise
        finally:
            self.deadline.ends = global_end

    def act(self, action):
        self.stage = action
        t, p = self.pair()
        if t['server']:
            raise RuntimeError('refusing to cycle a host')
        self.report['mutationAttempted'] = True
        self.target.ask(action, self.lobby, self.code, self.old_client or '-')
        self.report['stages'].append(dict(name=action, status='dispatched', utc=diag.utc()))

    def run(self, operation):
        self.report['operation'] = operation
        t, p = self.pair()
        self.lobby, self.code, self.host_client = p['lobbyId'], p['lobbyCode'], p['clientId']
        if host_problems(p, self.lobby) or self.lobby == '0' or not re.fullmatch(r'[A-Z0-9]{4,16}', self.code):
            raise ValueError('peer must be the live host of an observable lobby in Lobby scene')
        if t['pid'] == p['pid'] or t['server']:
            raise ValueError('target must be a distinct non-host client')
        if t['uiBusy'] or t['confirmReady']:
            raise ValueError('target UI busy or already showing an unrelated confirmation')
        self.old_client = t['clientId'] if operation != 'join' else None
        self.report.update(lobbyId=self.lobby, targetBefore=t, peerBefore=p)
        if operation == 'status':
            self.report['readinessMissing'] = connected_problems(t, p, self.lobby)
            self.report['readiness'] = 'ready' if not self.report['readinessMissing'] else 'not-ready'
            return 0 if self.report['readiness'] == 'ready' else 1
        if operation in ('leave', 'rejoin'):
            self.wait('initial-membership', lambda t, p: connected_problems(t, p, self.lobby))
            self.act('pause')
            self.wait('pause-menu', lambda t, p: [] if t['exitReady'] else ['exit_button_interactive'], False)
            self.act('leave')
            self.wait('leave-confirmation', lambda t, p: [] if t['confirmReady'] else ['confirmation_button_interactive'], False)
            self.act('confirm')
            self.wait('disconnected-menu', lambda t, p: disconnected_problems(t, p, self.lobby, self.old_client))
        if operation == 'leave':
            self.report['readiness'] = 'disconnected-ready'
            return 0
        if operation == 'join':
            self.wait('disconnected-menu', lambda t, p: disconnected_problems(t, p, self.lobby, self.old_client))
        self.act('open-code')
        self.wait('code-panel', lambda t, p: [] if t['fieldReady'] else ['code_field_interactive'], False)
        self.act('fill-code')
        self.wait('code-confirmed', lambda t, p: [] if t['codeField'] == self.code and t['connectReady'] else ['matching_code_and_button'], False)
        self.act('connect')
        t, p = self.wait('joined-ready', lambda t, p: connected_problems(t, p, self.lobby))
        self.report.update(readiness='ready', targetAfter=t, peerAfter=p, newClientId=t['clientId'])
        return 0


def lock_transports(stack, transports):
    # Stable lock in each mailbox serializes this helper even if target/peer roles are reversed.
    # Never unlink a flock inode: doing so lets a concurrent caller lock a replacement file.
    for path in sorted({t.screens.resolve() for t in transports}):
        handle = stack.enter_context((path / 'session-flow.lock').open('a+'))
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError('another session flow owns ' + str(path))
        for name in ('session.request', 'ui.request', 'qc.request'):
            if (path / name).exists():
                raise RuntimeError('mailbox busy: ' + str(path / name))


def os_observe(discovery, stage, targets, deadline):
    # Collector.stage() intentionally swallows collection errors. A mutating flow must not: it
    # needs a fail-closed identity verdict, not a partial report followed by an unbound result.
    discovery.report['stages'].append(dict(name=stage, status='running'))
    entries = discovery.worker(stage, targets, budget=min(12, deadline.remaining()))
    if any(entry.get('status') != 'observed' or not entry.get('process') for entry in entries.values()):
        raise ValueError('OS discovery/revalidation incomplete: ' + json.dumps(entries))
    return entries


def discover(args, out, deadline):
    # Reuse the collector's bounded OS discovery worker, not its diagnosis/collection flow.
    cfg = argparse.Namespace(main_root=args.peer_root, secondary_root=args.target_root,
        main_instance=args.peer_instance, secondary_instance=args.target_instance,
        main_pid=args.peer_pid, secondary_pid=args.target_pid, output=out / 'discovery', timeout=deadline.remaining(),
        staging=args.staging, ssh_host=args.ssh_host)
    collector = diag.Collector(cfg)
    collector.deadline = deadline
    return collector


def diagnostics(args, out):
    command = [sys.executable, str(HERE / 'collect-diagnostics.py'), '--main-root', args.peer_root,
        '--secondary-root', args.target_root, '--output', str(out / 'diagnostics'), '--timeout', str(args.diagnostic_timeout),
        '--ssh-host', args.ssh_host, '--staging', args.staging]
    for name, role in (('peer', 'main'), ('target', 'secondary')):
        for field in ('instance', 'pid'):
            value = getattr(args, name + '_' + field)
            if value is not None:
                command += ['--' + role + '-' + field, str(value)]
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=args.diagnostic_timeout + 3)
        (out / 'diagnostics-dispatch.txt').write_text(result.stdout + result.stderr, encoding='utf-8')
        return dict(status='healthy' if result.returncode == 0 else 'findings-or-incomplete', exitCode=result.returncode,
                    output=result.stdout.strip())
    except (OSError, subprocess.TimeoutExpired) as e:
        return dict(status='unavailable', reason=str(e))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('operation', choices=['status', 'leave', 'join', 'rejoin'])
    for role in ('target', 'peer'):
        parser.add_argument('--' + role + '-root', required=True)
        parser.add_argument('--' + role + '-instance', default=None, help='exact actual instance argument; empty selects unnamed')
        parser.add_argument('--' + role + '-pid', type=int)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--timeout', type=float, default=150, help='total flow deadline, excluding bounded diagnostics')
    parser.add_argument('--stage-timeout', type=float, default=30)
    parser.add_argument('--diagnostics', action='store_true', help='also collect post-success health evidence; failures always collect')
    parser.add_argument('--diagnostic-timeout', type=float, default=30)
    parser.add_argument('--ssh-host', default='windows')
    parser.add_argument('--staging', default='/mnt/c/AlterumTools/diagnostic-worker')
    args = parser.parse_args()
    for name, low, high in [('timeout', 10, 600), ('stage_timeout', 2, 120), ('diagnostic_timeout', 1, 120)]:
        if not math.isfinite(getattr(args, name)) or not low <= getattr(args, name) <= high:
            parser.error(name + ' out of range')
    for role in ('target', 'peer'):
        root = diag.local_path(getattr(args, role + '_root'))
        if not (root / 'Alterum.exe').is_file():
            parser.error(role + ' root must contain Alterum.exe')
        setattr(args, role + '_root', str(root))
        instance, pid = getattr(args, role + '_instance'), getattr(args, role + '_pid')
        if instance and not re.fullmatch(r'[A-Za-z0-9_-]+', instance):
            parser.error('unsafe instance selector')
        if pid is not None and pid <= 0:
            parser.error('PID selector must be positive')
    out = args.output.expanduser().resolve() / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + uuid.uuid4().hex)
    out.mkdir(parents=True, exist_ok=False)
    deadline, flow = diag.Deadline(args.timeout), None
    report = dict(schema='alterum.session-flow/1', readiness='not-confirmed', mutationAttempted=False)
    code, locked = 2, False
    with ExitStack() as stack:
        try:
            discovery = discover(args, out, deadline)
            found = os_observe(discovery, 'session-preflight', discovery.targets, deadline)
            processes = {}
            for role, name in [('main', 'peer'), ('secondary', 'target')]:
                entry = found[role]
                if entry['status'] != 'observed' or not entry['process']:
                    raise ValueError(name + ': ' + str(entry.get('reason', 'process not uniquely discovered')))
                processes[name] = entry['process']
                setattr(args, name + '_instance', entry['process']['instance'])
                setattr(args, name + '_pid', entry['process']['pid'])
            if processes['target']['pid'] == processes['peer']['pid']:
                raise ValueError('target and peer selectors resolve to same process')
            transports = {role: Transport(getattr(args, role + '_root'), processes[role], out, role, deadline) for role in processes}
            lock_transports(stack, transports.values())
            locked = True
            flow = Flow(transports['target'], transports['peer'], out, deadline, args.stage_timeout)
            report = flow.report
            report['processes'] = processes
            code = flow.run(args.operation)
            # OS identity rechecked after runtime readiness; neither process may be replaced.
            targets = [dict(t, expected=processes['peer' if t['role'] == 'main' else 'target']) for t in discovery.targets]
            flow.stage = 'OS-postflight'
            os_observe(discovery, 'session-postflight', targets, deadline)
            report['processPreservation'] = 'confirmed-by-OS-pre-post-and-runtime-guards'
        except (OSError, ValueError, RuntimeError, TimeoutError, KeyError, TypeError) as error:
            code = 1 if report['mutationAttempted'] else 2
            report.update(error=str(error), failedStage=flow.stage if flow else 'OS-preflight')
            if report['failedStage'] == 'OS-postflight':
                report['readiness'] = 'invalidated-by-process-revalidation'
            print('session: ' + str(error), file=sys.stderr)
        # Locks remain held through diagnostics so another coordinator cannot mutate these peers.
        if (code != 0 or args.diagnostics) and locked:
            report['diagnostics'] = diagnostics(args, out)
            report['health'] = report['diagnostics']['status']
            if code == 0 and report['health'] != 'healthy':
                code = 3
        elif code != 0:
            report['diagnostics'] = dict(status='not-run', reason='exclusive mailboxes not acquired; refusing diagnostic contention')
        report.update(finishedUtc=diag.utc(), exitCode=code)
        (out / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
        summary = 'operation=' + args.operation + '\nreadiness=' + report['readiness'] + '\nhealth=' + report.get('health', 'not-requested')
        summary += '\nexitCode=' + str(code) + '\n'
        if 'error' in report:
            summary += 'failedStage=' + report['failedStage'] + '\nerror=' + report['error'] + '\n'
        (out / 'summary.txt').write_text(summary, encoding='utf-8')
        print(summary + str(out / 'report.json'))
    return code


if __name__ == '__main__':
    sys.exit(main())
