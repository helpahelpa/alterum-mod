#!/usr/bin/env python3
"""Observation-only A/B bundle collector for WSL (stdlib + Windows PowerShell via SSH).

Example: python3 win/collect-diagnostics.py --main-root /mnt/c/AlterumPinned \
 --secondary-root /mnt/c/AlterumB --output ~/assets/monorepo/alterum-data/validation/diagnostics
Exit 0: complete/healthy; 1: findings or incomplete; 2: invalid invocation/output failure.
No game launch/stop, deployment, config writes, UI input, POV change or model selection.
"""
import argparse
import base64
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import json
import ntpath
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
SCHEMA = 'alterum.diagnostics/1'


def utc():
    return datetime.now(timezone.utc).isoformat()


def observed(value, source, **extra):
    return dict(status='observed', value=value, source=source, **extra)


def unavailable(reason, **extra):
    return dict(status='unavailable', reason=reason, **extra)


def value(fact):
    return fact.get('value') if fact.get('status') == 'observed' else None


def windows_path(path):
    text = str(path)
    if re.match(r'^[A-Za-z]:[\\/]', text):
        return ntpath.normpath(text)
    resolved = str(Path(text).expanduser().resolve())
    match = re.fullmatch(r'/mnt/([a-zA-Z])(?:/(.*))?', resolved)
    if not match:
        raise ValueError('install/staging paths must be Windows drive paths or WSL /mnt/<drive>/ paths')
    return match[1].upper() + ':\\' + (match[2] or '').replace('/', '\\')


def local_path(path):
    text = str(path)
    if re.match(r'^[A-Za-z]:[\\/]', text):
        return Path('/mnt/' + text[0].lower() + '/' + text[3:].replace('\\', '/'))
    return Path(text).expanduser().resolve()


def path_key(path):
    return ntpath.normpath(windows_path(path)).casefold()


def instant(text):
    return datetime.fromisoformat(text.replace('Z', '+00:00')).timestamp()


def same_process(a, b, start_tolerance=0.00001):
    try:
        return (a['pid'] == b['pid'] and path_key(a['exe']) == path_key(b['exe']) and
                abs(instant(a['startUtc']) - instant(b['startUtc'])) < start_tolerance and
                a['session'] == b['session'] and a['instance'] == b['instance'])
    except (KeyError, TypeError, ValueError):
        return False


def select_process(candidates, root, instance=None, pid=None):
    matches = [p for p in candidates if path_key(p['exe']) == path_key(ntpath.join(windows_path(root), 'Alterum.exe'))
               and (instance is None or p['instance'] == instance) and (pid is None or p['pid'] == pid)]
    if not matches:
        raise ValueError('target absent or selector matched no process')
    if len(matches) != 1:
        raise ValueError('ambiguous process candidates; supply an instance or PID selector')
    return matches[0]


def parse_manifest(text):
    result, section = {}, ''
    for line in text.replace('\0', '').splitlines():
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('[') and line.endswith(']'):
            section = line[1:-1]
            continue
        if '=' not in line:
            raise ValueError('malformed manifest line')
        key, val = line.split('=', 1)
        key = (section + '.' if section else '') + key
        if key in result:
            raise ValueError('duplicate manifest key: ' + key)
        result[key] = val[1:-1] if val.startswith("'") and val.endswith("'") else val
    return result


def manifest_fact(data, key, source):
    raw = data.get(key)
    if raw is None or raw == 'unavailable':
        return unavailable(data.get(key + 'Why', 'runtime does not expose ' + key), source=source)
    return observed(raw, source)


def validate_manifest(data, expected, tag, earliest, latest):
    if data.get('schema') != 'alterum.snapshot/1' or data.get('tag') != tag:
        raise ValueError('wrong schema/tag; not this request artifact')
    process = dict(pid=int(data.get('process.pid', -1)), exe=data.get('process.exe'),
                   startUtc=data.get('process.startUtc'), session=expected['session'],
                   instance='' if data.get('process.instanceArg') == 'none' else data.get('process.instanceArg'))
    # Legacy snapshot/1 rounded start UTC to milliseconds. OS pre/post guards still require
    # the full precision; the runtime comparison permits ONLY that known representation loss.
    if not same_process(process, expected, start_tolerance=0.001):
        raise ValueError('runtime PID/start/exe/instance does not match discovered process')
    stamp = instant(data.get('utc', ''))
    if not earliest <= stamp <= latest:
        raise ValueError('artifact UTC outside request interval')
    if not data.get('commandId', '').startswith('snapshot-'):
        raise ValueError('missing snapshot command identity')
    for key in ('hot.generation', 'hot.mvid', 'hot.dllSha256'):
        if not data.get(key) or data[key] == 'unavailable':
            raise ValueError('runtime freshness lacks ' + key)


def generations_match(a, b):
    return all(a.get(k) == b.get(k) and a.get(k) not in (None, 'unavailable')
               for k in ('hot.generation', 'hot.mvid', 'hot.dllSha256'))


def identity_fields(text):
    matches = list(re.finditer(r'(?:^| )([A-Za-z][A-Za-z0-9]*)=', text or ''))
    return {m[1]: text[m.end():matches[i+1].start() if i+1 < len(matches) else len(text)].strip()
            for i, m in enumerate(matches)}


def actor_observations(text, source, remote=False, hud_measured=False):
    fields = identity_fields(text)
    actor = {'scope': 'client-replicated-view' if remote else 'local-applied-view'}
    for output, key in [('appliedRow', 'appliedRow'), ('appliedIndex', 'appliedDb'),
                        ('body', 'bodySlug'), ('bodyHealth', 'bodyState'),
                        ('advertised', 'advertisedSlug'), ('preference', 'wantedSlug'),
                        ('provenance', 'provenance')]:
        raw = fields.get(key)
        actor[output] = observed(raw, source) if raw and raw != '-' else unavailable('not observed for this actor', source=source)
    raw = fields.get('appliedSlug')
    if raw and raw != '-':
        actor['applied'] = observed(raw, source)
    elif value(actor['appliedRow']) is not None:
        actor['applied'] = observed('<native>', source)
    else:
        actor['applied'] = unavailable('no game-applied row observed', source=source)
    if fields.get('bodyState') == 'native' and fields.get('bodySlug') in ('-', '', None):
        actor['body'] = observed('<native>', source)
    hud = fields.get('hudRow')
    # Earlier runtimes echoed canonical identity. Accept only the declared observer capability in
    # this identity- and generation-validated manifest, never a legacy canonical HUD label.
    actor['hud'] = (observed(hud, source) if hud_measured and hud and not hud.startswith('<') and hud != '-'
                    else unavailable('HUD observer unsupported, hidden, absent, unknown sprite, or remote actor has no local HUD', source=source))
    actor['canonicalClaim'] = observed(fields.get('canonicalSlug', '-'), source + ' (derived; not display evidence)')
    actor['wardrobe'] = unavailable('runtime exposes applied/canonical row, not an independently observed visible wardrobe selection', source=source)
    actor['appliedRole'] = (observed('native-donor-row', source) if native_donor_applied(actor)
                            else observed('applied-identity', source) if value(actor['applied']) is not None
                            else unavailable('no applied identity observed', source=source))
    return actor


def native_donor_applied(actor):
    """`applied` is a native `PlayerModelData` row that a confirmed custom body replaces, so it is
    the donor row, not a competing applied identity. Every part of the custom claim must corroborate
    itself; any unconfirmed, unobserved, stale, or divergent field keeps the native row as a real
    second observation and leaves the mismatch reportable."""
    wanted = value(actor.get('preference', {}))
    return (value(actor.get('applied', {})) == '<native>' and
            value(actor.get('provenance', {})) == 'wanted:confirm' and
            value(actor.get('bodyHealth', {})) == 'live' and
            wanted not in (None, '<native>') and
            wanted == value(actor.get('body', {})) == value(actor.get('canonicalClaim', {})))


def hud_expectation(actor):
    """The model the local HUD portrait must present. A confirmed custom body replaces its native
    donor row, so the portrait is expected to show that body, not the row the game still applies;
    without that corroboration the HUD must show the applied identity itself."""
    if native_donor_applied(actor):
        return value(actor.get('body', {})), 'confirmed body'
    expected = (value(actor.get('appliedRow', {})) if value(actor.get('applied', {})) == '<native>'
                else value(actor.get('applied', {})))
    return expected, 'applied row'


def disagreements(actor):
    facts = {key: value(actor.get(key, {})) for key in ('applied', 'body', 'advertised')}
    if native_donor_applied(actor):
        facts.pop('applied')
    known = {k: v for k, v in facts.items() if v is not None}
    return known if len(set(known.values())) > 1 else None


def redact(text):
    text = text.replace('\0', '')
    text = re.sub(r'\b\d{17,}\b', '<redacted-id>', text)
    text = re.sub(r'\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b', '<redacted-address>', text)
    text = re.sub(r'(?i)\b(token|password|authorization|api[_-]?key|secret)\s*[:=]\s*\S+', r'\1=<redacted>', text)
    return text


def diagnostic_lines(text):
    # Whitelist, not a whole log/config dump. Keep localized reservation rejections and their
    # numeric actor/model fields; this does not assume that the shared Unity log belongs to A/B.
    patterns = (r'\[(?:qc:test|model:identity|model:hud|model:game-pool|pov|mirror:body[^\]]*|bodyview[^\]]*)\]',
                r'\[Wardrobe\].*(?:model|модел|недоступ|отклон|reject|unavailable)',
                r'\[qc:direct\].*state=(?:begin|dispatched|error)')
    return [redact(line)[:4000] for line in text.replace('\0', '').splitlines()
            if any(re.search(pattern, line, re.I) for pattern in patterns)][-100:]


class Deadline:
    def __init__(self, seconds):
        self.started = time.monotonic()
        self.ends = self.started + seconds

    def remaining(self, limit=None):
        left = max(0, self.ends - time.monotonic())
        return left if limit is None else min(left, limit)

    def check(self):
        if self.remaining() <= 0:
            raise TimeoutError('total collection deadline reached')


class LogGuard:
    """Detect replacement, truncation, and truncate/regrow over the starting tail boundary."""
    def __init__(self, path):
        self.path = Path(path)
        stat = self.path.stat()
        self.identity = (stat.st_dev, stat.st_ino)
        self.offset = stat.st_size
        self.anchor_offset = max(0, self.offset - 256)
        with self.path.open('rb') as stream:
            stream.seek(self.anchor_offset)
            self.anchor = stream.read(self.offset - self.anchor_offset)

    def validate(self):
        stat = self.path.stat()
        if (stat.st_dev, stat.st_ino) != self.identity or stat.st_size < self.offset:
            raise ValueError('log rotation/replacement/truncation invalidates freshness')
        with self.path.open('rb') as stream:
            stream.seek(self.anchor_offset)
            if stream.read(len(self.anchor)) != self.anchor:
                raise ValueError('log was truncated/regrown; starting boundary changed')

    def recent(self, size=128*1024):
        self.validate()
        with self.path.open('rb') as stream:
            stream.seek(max(0, self.path.stat().st_size - size))
            return stream.read(size).decode('utf-8', errors='replace')


def hash_file(path, deadline):
    try:
        start = path.stat()
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            while True:
                deadline.check()
                block = stream.read(1024*1024)
                if not block:
                    break
                digest.update(block)
        end = path.stat()
        if (start.st_size, start.st_mtime_ns, start.st_ino) != (end.st_size, end.st_mtime_ns, end.st_ino):
            return unavailable('file changed while hashing', source=str(path))
        return observed(digest.hexdigest(), str(path), bytes=end.st_size, mtimeNs=end.st_mtime_ns,
                        scope='current-disk-file-not-loaded-memory')
    except (OSError, TimeoutError) as error:
        return unavailable(str(error), source=str(path))


class Collector:
    def __init__(self, args):
        self.args = args
        self.deadline = Deadline(args.timeout)
        self.run_id = uuid.uuid4().hex
        self.out = Path(args.output).expanduser().resolve() / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + self.run_id)
        self.out.mkdir(parents=True, exist_ok=False)
        self.stage_dir = local_path(args.staging) / self.run_id
        self.stage_dir.mkdir(parents=True, exist_ok=False)
        for name in ('diagnostic-bridge.ps1', 'diagnostic-worker.ps1', 'instance-label-common.ps1'):
            shutil.copyfile(HERE / name, self.stage_dir / name)
        self.report = dict(schema=SCHEMA, runId=self.run_id, startedUtc=utc(),
                           observationScope='sequential samples; not an atomic cross-process snapshot',
                           stages=[], instances=[], findings=[], sharedGameLog=unavailable('not collected'))
        self.targets = []
        for role in ('main', 'secondary'):
            root = getattr(args, role + '_root')
            if root:
                self.targets.append(dict(role=role, root=windows_path(root),
                                         instance=getattr(args, role + '_instance'), pid=getattr(args, role + '_pid')))
        self.states = {t['role']: dict(role=t['role'], root=t['root'], process=unavailable('not discovered'),
                       runtime=unavailable('not collected'), capture=unavailable('not collected'),
                       buildId=unavailable('no authoritative install build manifest exposed'),
                       loadedGame={name: unavailable('loaded game-memory/module-file evidence not exposed; disk hashes are separate')
                                   for name in ('GameAssembly', 'UnityPlayer')},
                       config={key: unavailable('no fresh runtime') for key in ('path', 'showLocalPlayer', 'source')},
                       pov=unavailable('no fresh runtime'),
                       visibility={key: unavailable('no fresh runtime') for key in ('renderers', 'enabled', 'activeEnabled', 'scope')},
                       pose={key: unavailable('no fresh runtime') for key in ('posePos', 'poseFwd', 'rootPos', 'rootEuler')},
                       loadedHot={key: unavailable('no fresh runtime') for key in ('generation', 'mvid', 'dllSha256', 'assembly')},
                       loadedAssemblies={},
                       disk={key: unavailable('not collected') for key in ('gameAssembly', 'unityPlayer', 'host', 'contracts', 'hot')},
                       errors=[]) for t in self.targets}
        self.guards = {}

    @contextmanager
    def stage(self, name):
        started = time.monotonic()
        record = dict(name=name, utc=utc(), status='complete')
        self.report['stages'].append(record)
        try:
            yield
            self.deadline.check()
        except Exception as error:
            record.update(status='incomplete', reason=str(error))
        finally:
            record['elapsedSeconds'] = round(time.monotonic() - started, 4)

    def worker(self, stage, targets, budget=6):
        limit = self.deadline.remaining(budget)
        if limit < 0.3:
            raise TimeoutError('no time remaining for interactive worker')
        request = self.stage_dir / (stage + '.request.json')
        result = self.stage_dir / (stage + '.result.json')
        request.write_text(json.dumps(dict(runId=self.run_id, stage=stage, targets=targets,
                           budgetSeconds=max(0.1, limit-0.5), result=windows_path(result))), encoding='utf-8')
        bridge = windows_path(self.stage_dir / 'diagnostic-bridge.ps1')
        script = "& '" + bridge.replace("'", "''") + "' -Request '" + windows_path(request).replace("'", "''") + "'"
        encoded = base64.b64encode(script.encode('utf-16-le')).decode()
        failure = None
        try:
            proc = subprocess.run(['ssh', '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=3', self.args.ssh_host,
                                   'powershell', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                                   '-EncodedCommand', encoded], capture_output=True, timeout=limit)
            if proc.returncode:
                failure = 'interactive worker/SSH exit=' + str(proc.returncode)
        except subprocess.TimeoutExpired:
            failure = 'interactive worker exceeded stage deadline'
        if not result.is_file():
            failure = failure or 'interactive worker produced no current completion'
            partial = Path(str(result)+'.partial')
            if not partial.is_file():
                raise RuntimeError(failure)
            result = partial
        data = json.loads(result.read_text(encoding='utf-8-sig'))
        if data.get('runId') != self.run_id or data.get('stage') != stage:
            raise ValueError('stale worker result')
        (self.out / (stage + '-worker.json')).write_text(json.dumps(data, indent=2), encoding='utf-8')
        if failure or data.get('error'):
            self.report['stages'][-1].update(status='incomplete', reason=failure or data['error'])
        entries = {entry['role']: entry for entry in data['targets']}
        for target in targets:
            entries.setdefault(target['role'], dict(role=target['role'], status='unavailable', reason='worker did not reach this target', candidates=[], process=None, capture=None))
        return entries

    def runtime(self, role, phase, budget=3):
        state = self.states[role]
        process = value(state['process'])
        if not process:
            raise ValueError('no unique process; refusing mailbox request')
        guard = self.guards[role]
        guard.validate()
        root = local_path(state['root'])
        screens = root / 'BepInEx' / 'screens'
        if process['instance']:
            screens /= process['instance']
        tag = 'd-' + self.run_id + '-' + role[0] + '-' + phase[0]
        artifact = screens / ('alterum-snapshot-' + tag + '.manifest.txt')
        if artifact.exists():
            raise ValueError('artifact already exists before request')
        started = time.time()
        # Reuse the existing atomic mailbox and real QC command path. This child is bounded by
        # the stage/global deadline even if a filesystem or parser call gets stuck.
        limit = self.deadline.remaining(budget)
        if limit <= 0:
            raise TimeoutError('total collection deadline reached')
        command = [sys.executable, str(HERE / 'console.py'), '--root', str(root), '--timeout', str(max(.01, limit-.1))]
        if process['instance']:
            command += ['--instance', process['instance']]
        proc = subprocess.run(command + ['alterum.test.snapshot ' + tag], capture_output=True, timeout=limit)
        guard.validate()
        lines = diagnostic_lines(proc.stdout.decode('utf-8', errors='replace'))
        (self.out / (role + '-' + phase + '-dispatch.txt')).write_text('\n'.join(lines) + '\n', encoding='utf-8')
        if proc.returncode != 0:
            error = proc.stderr.decode('utf-8', errors='replace').strip()
            raise RuntimeError('snapshot dispatch failed: ' + redact(error or 'QC command error'))
        # The snapshot verb writes synchronously today, but ack alone is not the artifact contract.
        # A unique tag, result=ok, correct identity/hash/generation and artifact UTC are all required.
        if not any('snapshot result=ok' in line and "tag='" + tag + "'" in line for line in lines):
            raise ValueError('no matching snapshot completion, despite dispatch acknowledgement')
        if not artifact.is_file() or artifact.stat().st_size > 256*1024:
            raise ValueError('fresh manifest absent or exceeds bounded size')
        text = artifact.read_text(encoding='utf-8-sig')
        data = parse_manifest(text)
        validate_manifest(data, process, tag, started, time.time())
        expected_path = windows_path(artifact)
        if path_key(data['artifact']) != path_key(expected_path):
            raise ValueError('manifest artifact path does not match request target')
        dest = role + '-' + phase + '.manifest.txt'
        (self.out / dest).write_text(text, encoding='utf-8')
        state[phase] = data
        state[phase + 'Evidence'] = dest
        return data

    def collect(self):
        with self.stage('discover'):
            inventory = self.worker('discover', self.targets)
            for target in self.targets:
                role = target['role']
                entry = inventory[role]
                state = self.states[role]
                if entry['status'] != 'observed':
                    state['process'] = unavailable(entry['reason'], candidates=entry['candidates'])
                    continue
                process = select_process(entry['candidates'], target['root'], target['instance'], target['pid'])
                if not same_process(process, entry['process']):
                    raise ValueError('worker selected a different process than its candidates')
                state['process'] = observed(process, 'discover-worker.json', utc=utc())
                root = local_path(target['root'])
                log = root / 'BepInEx' / ('logs/' + process['instance'] + '.log' if process['instance'] else 'LogOutput.log')
                try:
                    self.guards[role] = LogGuard(log)
                    state['logSource'] = str(log)
                except OSError as error:
                    state['errors'].append(str(error))
        for target in self.targets:
            role = target['role']
            with self.stage(role + ':runtime-before'):
                self.runtime(role, 'before')
        guarded = [dict(t, expected=value(self.states[t['role']]['process'])) for t in self.targets
                   if value(self.states[t['role']]['process'])]
        with self.stage('window-captures'):
            results = self.worker('capture', guarded) if guarded else {}
            for target in guarded:
                state = self.states[target['role']]
                entry = results[target['role']]
                cap = entry.get('capture')
                if entry['status'] != 'observed' or not cap:
                    state['capture'] = unavailable(entry['reason'] or 'no capture result')
                    continue
                state['capture'] = cap
                image = self.stage_dir / (target['role'] + '.png')
                if cap['status'] in ('observed', 'suspect'):
                    if not same_process(cap['identity'], target['expected']) or image.exists() is False:
                        state['capture'] = unavailable('capture identity mismatch or missing image')
                    elif hashlib.sha256(image.read_bytes()).hexdigest() != cap['sha256']:
                        state['capture'] = unavailable('capture hash mismatch')
                    else:
                        name = target['role'] + '-window.png'
                        shutil.copyfile(image, self.out / name)
                        cap.update(evidence=name, sourcePath=cap.pop('path'))
        for target in self.targets:
            role = target['role']
            with self.stage(role + ':runtime-after'):
                self.runtime(role, 'after')
        with self.stage('process-revalidation'):
            results = self.worker('revalidate', guarded, budget=4) if guarded else {}
            for target in guarded:
                state = self.states[target['role']]
                entry = results[target['role']]
                state['processAfter'] = entry
                before, after = state.get('before'), state.get('after')
                try:
                    self.guards[target['role']].validate()
                    if entry['status'] != 'observed' or not same_process(entry['process'], target['expected']):
                        raise ValueError('process identity changed during collection')
                    if entry['process'].get('hwnd') != target['expected'].get('hwnd'):
                        raise ValueError('game window changed during collection')
                    if not before or not after or not generations_match(before, after):
                        raise ValueError('missing snapshot or Hot generation/hash changed during collection')
                    state['runtime'] = observed('fresh', state['beforeEvidence'] + ', ' + state['afterEvidence'])
                except (ValueError, OSError, KeyError) as error:
                    state['runtime'] = unavailable(str(error))
                    state['capture']['status'] = 'unavailable'
                    state['capture']['reason'] = 'not accepted as current proof: ' + str(error)
        for target in self.targets:
            role = target['role']
            state = self.states[role]
            with self.stage(role + ':disk-and-logs'):
                root = local_path(target['root'])
                process = value(state['process'])
                slot = process['instance'] if process else target['instance']
                paths = {'gameAssembly': root/'GameAssembly.dll', 'unityPlayer': root/'UnityPlayer.dll',
                         'host': root/'BepInEx/plugins/AlterumModelSwap.dll',
                         'contracts': root/'BepInEx/plugins/AlterumModelSwap.Contracts.dll'}
                if slot is not None:
                    paths['hot'] = root/'BepInEx/hot'/slot/'AlterumModelSwap.Hot.dll'
                for key, path in paths.items():
                    state['disk'][key] = hash_file(path, self.deadline)
                state['buildId'] = unavailable('no authoritative build-ID manifest exposed by this install; not inferred from hashes')
                if role in self.guards:
                    excerpt = diagnostic_lines(self.guards[role].recent())
                    name = role + '-recent-log.txt'
                    (self.out/name).write_text('\n'.join(excerpt)+'\n', encoding='utf-8')
                    state['recentLog'] = dict(evidence=name, source=state['logSource'], scope='bounded recent context, NOT fresh runtime proof')
                    state['reservationRejections'] = observed([line for line in excerpt if '[Wardrobe]' in line], name,
                        scope='recent context; not necessarily this membership or current state')
        with self.stage('shared-game-log'):
            # Explicit opt-in path avoids guessing whose Windows profile/log is active.
            if self.args.game_log:
                path = local_path(self.args.game_log)
                with path.open('rb') as stream:
                    stream.seek(max(0, path.stat().st_size - 128*1024))
                    lines = diagnostic_lines(stream.read(128*1024).decode('utf-8', errors='replace'))
                (self.out/'shared-game-log.txt').write_text('\n'.join(lines)+'\n', encoding='utf-8')
                self.report['sharedGameLog'] = observed(lines, str(path), evidence='shared-game-log.txt',
                    attribution='shared Windows-profile log; target attribution uncertain; bounded recent context, not current-state proof')
            else:
                self.report['sharedGameLog'] = unavailable('not supplied; pass --game-log explicitly to avoid profile guessing')
        return self.finish()

    def finish(self):
        self.report['instances'] = list(self.states.values())
        diagnose(self.report)
        self.report['finishedUtc'] = utc()
        self.report['elapsedSeconds'] = round(time.monotonic()-self.deadline.started, 4)
        code = 0 if self.report['complete'] and self.report['health'] == 'healthy' else 1
        self.report['exitCode'] = code
        (self.out/'report.json').write_text(json.dumps(self.report, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')
        (self.out/'summary.txt').write_text(summary(self.report), encoding='utf-8')
        print(str(self.out/'report.json'))
        print(str(self.out/'summary.txt'))
        # Staged source/result files remain on deadline failure for troubleshooting. No process or
        # task-name cleanup guessing; the bridge owns and always removes its unique worker task.
        return code


def diagnose(report):
    findings = report['findings']
    complete = True
    def add(code, role, message, severity='warning', evidence=None):
        findings.append(dict(code=code, role=role, severity=severity, message=message, evidence=evidence))
    for stage in report.get('stages', []):
        if stage['status'] != 'complete':
            complete = False
            add('stage-incomplete', None, stage['name'] + ': ' + stage.get('reason', 'unknown'))
    for state in report['instances']:
        role = state['role']
        if value(state['runtime']) != 'fresh':
            complete = False
            add('runtime-unavailable', role, state['runtime'].get('reason', 'freshness not verified'))
            if state['capture'].get('status') in ('observed', 'suspect'):
                state['capture'].update(status='unavailable', reason='capture not accepted as current proof without stable process and generation')
            # Raw manifests remain evidence but their stale identities are not accepted as current.
            continue
        data = state['after']
        source = state['afterEvidence']
        state['authority'] = manifest_fact(data, 'net.authority', source)
        state['scene'] = manifest_fact(data, 'scene.name', source)
        state['loadedHot'] = {key: manifest_fact(data, 'hot.' + key, source) for key in ('generation', 'mvid', 'dllSha256', 'assembly')}
        state['config'] = {key: manifest_fact(data, 'config.' + native, source) for key, native in [('path', 'path'), ('showLocalPlayer', 'showLocalPlayer'), ('source', 'source')]}
        state['pov'] = manifest_fact(data, 'view.pov', source)
        state['visibility'] = {key: manifest_fact(data, 'view.' + key, source) for key in ('renderers', 'enabled', 'activeEnabled', 'scope')}
        state['pose'] = {key: manifest_fact(data, 'local.' + key, source) for key in ('posePos', 'poseFwd', 'rootPos', 'rootEuler')}
        state['loadedAssemblies'] = {name: {key: manifest_fact(data, 'loaded.' + name + '.' + key, source) for key in ('path', 'mvid', 'sha256', 'hashSource')}
                                     for name in ('AlterumModelSwap', 'AlterumModelSwap.Contracts')}
        state['actors'] = []
        for name, assembly in state['loadedAssemblies'].items():
            if value(assembly['sha256']) is None:
                complete = False
                add('loaded-assembly-unavailable', role, name+' loaded backing-file hash unavailable', evidence=source)
        local = actor_observations(data.get('identity.local', ''), source,
                                   hud_measured=data.get('identity.hudSource') == 'visible-sprite-observer/1')
        local['clientId'] = manifest_fact(data, 'local.clientId', source)
        state['actors'].append(local)
        count = data.get('remotes.count', 'unavailable')
        if not count.isdigit():
            complete = False
            add('remote-roster-unavailable', role, 'remote roster count not observed', evidence=source)
        for index in range(int(count) if count.isdigit() else 0):
            actor = actor_observations(data.get(f'remotes.remote[{index}].identity', ''), source, remote=True,
                                       hud_measured=False)
            actor['clientId'] = manifest_fact(data, f'remotes.remote[{index}].clientId', source)
            state['actors'].append(actor)
        for actor in state['actors']:
            mismatch = disagreements(actor)
            if mismatch:
                add('model-mismatch', role, 'independent model observations disagree: '+json.dumps(mismatch), 'error', source)
            hud = value(actor['hud'])
            expected_hud, expectation = hud_expectation(actor)
            if hud is not None and expected_hud is not None and hud != expected_hud:
                add('presentation-mismatch', role, 'observed HUD '+hud+' disagrees with '+expectation+' '+expected_hud, 'error', source)
            if value(actor['hud']) is None and actor['scope'] == 'local-applied-view':
                complete = False
                add('hud-unavailable', role, actor['hud']['reason'], evidence=source)
            if value(actor['applied']) is None:
                complete = False
                add('applied-unavailable', role, 'remote/client applied row cannot be independently observed', evidence=source)
            if value(actor['body']) is None or value(actor['bodyHealth']) not in ('native', 'live'):
                complete = False
                add('body-unavailable', role, 'effective body identity or health not observed live', evidence=source)
        if value(state['scene']) is None or value(state['authority']) is None:
            complete = False
            add('scene-authority-unavailable', role, 'scene or network authority not observed', evidence=source)
        if any(value(fact) is None for fact in state['pose'].values()):
            complete = False
            add('pose-unavailable', role, 'local root or body pose observations incomplete', evidence=source)
        if value(state['config']['showLocalPlayer']) is None or value(state['config']['path']) is None:
            complete = False
            add('effective-config-unavailable', role, 'runtime lacks active in-memory config/path observations; disk configs cannot substitute', evidence=source)
        elif value(state['config']['showLocalPlayer']) == '0':
            add('local-visibility-disabled', role, 'effective [Mirror] ReflectLocalPlayer=false (self-reflection opted out) at '+str(value(state['config']['path'])), evidence=source)
        if value(state['pov']) is None:
            complete = False
            add('pov-unavailable', role, state['pov']['reason'], evidence=source)
        for key in ('gameAssembly', 'unityPlayer', 'host', 'contracts', 'hot'):
            if value(state.get('disk', {}).get(key, {})) is None:
                complete = False
                add('disk-fact-unavailable', role, key+' current file hash unavailable')
        if value(state['visibility']['renderers']) is None:
            complete = False
            add('visibility-unavailable', role, 'no live renderer observations', evidence=source)
        if state['capture'].get('status') == 'observed':
            try:
                cap = state['capture']
                if not same_process(cap['identity'], value(state['process'])):
                    raise ValueError('capture process identity mismatch')
                if cap['identity'].get('hwnd') != value(state['process']).get('hwnd'):
                    raise ValueError('capture window handle changed')
                if not instant(state['before']['utc']) <= instant(cap['utc']) <= instant(data['utc']):
                    raise ValueError('capture UTC not bracketed by fresh runtime snapshots')
                cap['generationAssociation'] = observed(data['hot.generation'], source,
                    scope='bracketed by matching pre/post generation, not sampled inside image')
            except (KeyError, TypeError, ValueError) as error:
                state['capture'].update(status='unavailable', reason=str(error))
        if state['capture'].get('status') != 'observed':
            complete = False
            add('capture-unavailable', role, state['capture'].get('reason', 'capture not collected'))
        if state['capture'].get('focusChanged'):
            add('foreground-changed', role, 'foreground changed during capture; worker did not request a focus change')
        keys = ('identity.localSlug', 'local.posePos', 'local.poseFwd', 'view.pov', 'config.path', 'config.showLocalPlayer', 'scene.name', 'report.qc')
        state['unchanged'] = {key: (observed(state['before'][key] == data[key], source) if key in state['before'] and key in data and data[key] != 'unavailable'
                                    else unavailable('missing before/after observation')) for key in keys}
        for key, fact in state['unchanged'].items():
            if value(fact) is False:
                add('state-changed', role, key+' changed between sequential observations', evidence=source)
        if data.get('assets.assert') == 'FAIL':
            add('asset-health-failure', role, data.get('assets.assertFailedFields', 'asset assertion failed'), 'error', source)
        if 'hot' in state['disk'] and value(state['disk']['hot']) and value(state['disk']['hot']) != data['hot.dllSha256']:
            add('loaded-disk-drift', role, 'loaded Hot backing-file hash differs from watched disk Hot', evidence=source)
    report['compatibility'] = {}
    if len(report['instances']) == 2:
        a, b = report['instances']
        for key in ('gameAssembly', 'unityPlayer', 'host', 'contracts', 'hot'):
            av, bv = value(a.get('disk', {}).get(key, {})), value(b.get('disk', {}).get(key, {}))
            verdict = 'unknown' if not av or not bv else 'match' if av == bv else 'mismatch'
            report['compatibility'][key] = dict(status=verdict, scope='disk content parity only, not a compatibility guarantee')
            if verdict == 'mismatch':
                add('game-binary-mismatch' if key in ('gameAssembly', 'unityPlayer') else 'mod-binary-mismatch', 'A/B',
                    key+' hashes differ; '+('BLOCKER for multiplayer diagnosis; no repair performed' if key in ('gameAssembly', 'unityPlayer') else 'review version compatibility; no repair performed'), 'error')
            elif verdict == 'unknown':
                complete = False
                add('compatibility-unknown', 'A/B', key+' parity unavailable')
        for key, assembly_name in (('loadedHost', 'AlterumModelSwap'), ('loadedContracts', 'AlterumModelSwap.Contracts')):
            av = value(a.get('loadedAssemblies', {}).get(assembly_name, {}).get('sha256', {}))
            bv = value(b.get('loadedAssemblies', {}).get(assembly_name, {}).get('sha256', {}))
            verdict = 'unknown' if not av or not bv else 'match' if av == bv else 'mismatch'
            report['compatibility'][key] = dict(status=verdict, scope='loaded assembly backing-file parity, not memory hash')
            if verdict == 'mismatch':
                add('loaded-binary-mismatch', 'A/B', key+' differs', 'error')
        av = value(a.get('loadedHot', {}).get('dllSha256', {}))
        bv = value(b.get('loadedHot', {}).get('dllSha256', {}))
        report['compatibility']['loadedHot'] = dict(status='unknown' if not av or not bv else 'match' if av == bv else 'mismatch', scope='loaded shadow-copy file parity')
        if av and bv and av != bv:
            add('loaded-binary-mismatch', 'A/B', 'loaded Hot hashes differ', 'error')
    findings.sort(key=lambda f: (0 if f['code'] == 'game-binary-mismatch' else 1 if f['severity'] == 'error' else 2))
    report['complete'] = complete
    report['health'] = 'findings' if findings else 'healthy' if complete else 'unknown'


def summary(report):
    lines = [f"Alterum diagnostics {report['runId']}", f"Health: {report['health']}; collection: {'complete' if report['complete'] else 'incomplete'}; exit={report['exitCode']}", 'Findings:']
    lines += [f"{f['severity'].upper()} {f['role'] or 'collector'} {f['code']}: {f['message']}" for f in report['findings']] or ['none']
    for state in report['instances']:
        lines += ['', state['role']+' '+state['root'], 'Process: '+json.dumps(state['process'], ensure_ascii=False),
                  'Runtime: '+json.dumps(state['runtime']), 'Config: '+json.dumps(state.get('config', {})),
                  'Build files: '+json.dumps({key: value(fact) for key, fact in state['disk'].items()}),
                  'POV: '+json.dumps(state.get('pov', unavailable('runtime not accepted'))),
                  'Models: '+json.dumps([{k: value(a[k]) for k in ('clientId', 'applied', 'body', 'advertised', 'hud')} for a in state.get('actors', [])]),
                  'Capture: '+state['capture'].get('status', 'unavailable')+' '+str(state['capture'].get('evidence', state['capture'].get('reason', '')))]
    lines += ['', 'Compatibility: '+json.dumps(report['compatibility']),
              'Timings: '+', '.join(s['name']+'='+str(s['elapsedSeconds'])+'s' for s in report['stages']),
              f"Total: {report['elapsedSeconds']}s. Sequential samples, not atomic. Window pixels do not prove body visibility."]
    return '\n'.join(lines)+'\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--main-root', required=True)
    parser.add_argument('--secondary-root')
    parser.add_argument('--main-instance', default=None, help='optional exact actual instance argument (empty = unnamed)')
    parser.add_argument('--secondary-instance', default=None)
    parser.add_argument('--main-pid', type=int)
    parser.add_argument('--secondary-pid', type=int)
    parser.add_argument('--output', required=True, help='parent directory; a unique run directory is created')
    parser.add_argument('--timeout', type=float, default=30)
    parser.add_argument('--ssh-host', default='windows')
    parser.add_argument('--staging', default='/mnt/c/AlterumTools/diagnostic-worker')
    parser.add_argument('--game-log', help='optional explicit shared Player.log path; attribution remains uncertain')
    args = parser.parse_args()
    collector = None
    try:
        if not 1 <= args.timeout <= 300:
            raise ValueError('timeout must be between 1 and 300 seconds')
        for role in ('main', 'secondary'):
            root = getattr(args, role+'_root')
            if root:
                windows_path(root)
                if not (local_path(root)/'Alterum.exe').is_file():
                    raise ValueError(role+' root has no Alterum.exe')
                instance = getattr(args, role+'_instance')
                pid = getattr(args, role+'_pid')
                if pid is not None and pid <= 0:
                    raise ValueError('PID selector must be positive')
                if instance and not re.fullmatch(r'[A-Za-z0-9_-]+', instance):
                    raise ValueError('unsafe instance selector')
        if args.secondary_root and path_key(args.main_root) == path_key(args.secondary_root) and args.main_instance == args.secondary_instance and args.main_pid == args.secondary_pid:
            raise ValueError('main/secondary selectors are identical')
        collector = Collector(args)
        return collector.collect()
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        if collector:
            collector.report['stages'].append(dict(name='fatal-collection', utc=utc(), status='incomplete', reason=str(error), elapsedSeconds=0))
            try:
                return collector.finish()
            except OSError as output_error:
                print('diagnostics output failed: '+str(output_error), file=sys.stderr)
                return 2
        print('diagnostics invocation failed: '+str(error), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
