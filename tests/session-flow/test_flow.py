from contextlib import ExitStack
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('session', ROOT / 'win/session.py')
s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s)


def state(server=False, cid='8'):
    return dict(pid=1 if server else 2, startUtc='2026-11-18T01:00:00.0000000Z', exe='C:\\Game\\Alterum.exe',
        instance='', mvid='abcd', generation='generation1', utc=s.diag.utc(), frame=1, scene='Lobby', sceneLoaded=True,
        inLobby=True, lobbyId='123', lobbyCode='ABC123', listening=True, server=server, connected=True,
        clientId='0' if server else cid, players=[dict(id=i, owned=i == ('0' if server else cid), connected=True, scene='Lobby') for i in ('0', cid)],
        rosterReady=True, modelRow='native-row', modelDb=2, modelSlug='', modelReadiness='native', bodyState='native',
        uiBusy=False, exitReady=False, confirmReady=False, codeReady=False, connectReady=False, fieldReady=False, codeField='')


def disconnected(t, p):
    old = t['clientId']
    t.update(scene='Menu', inLobby=False, lobbyId='0', listening=False, connected=False,
             players=[], clientId='0', codeReady=True, exitReady=False, confirmReady=False)
    p['players'] = [x for x in p['players'] if x['id'] != old]


class Predicates(unittest.TestCase):
    def test_native_ready(self):
        self.assertEqual(s.connected_problems(state(), state(True), '123'), [])

    def test_body_failure_is_not_connection_failure(self):
        t, p = state(), state(True)
        t['bodyState'] = 'failed'
        self.assertEqual(s.connected_problems(t, p, '123'), [])

    def test_missing_host_view(self):
        t, p = state(), state(True)
        p['players'] = p['players'][:1]
        self.assertIn('host_sees_client', s.connected_problems(t, p, '123'))

    def test_wrong_lobby(self):
        t, p = state(), state(True)
        t['lobbyId'] = '999'
        self.assertIn('target_membership', s.connected_problems(t, p, '123'))

    def test_not_owned(self):
        t, p = state(), state(True)
        t['players'][1]['owned'] = False
        self.assertIn('local_player', s.connected_problems(t, p, '123'))

    def test_disconnection_requires_all_stages(self):
        t, p = state(), state(True)
        self.assertIn('network_stopped', s.disconnected_problems(t, p, '123', '8'))
        disconnected(t, p)
        self.assertEqual(s.disconnected_problems(t, p, '123', '8'), [])
        t['listening'] = True
        self.assertIn('network_stopped', s.disconnected_problems(t, p, '123', '8'))

    def test_join_from_menu_does_not_confuse_stale_zero_with_host(self):
        t, p = state(), state(True)
        disconnected(t, p)
        self.assertEqual(s.disconnected_problems(t, p, '123', None), [])

    def test_stability_needs_fresh_count_and_duration(self):
        stable = s.Stable()
        self.assertFalse(stable.accept([], 'x', 0))
        self.assertFalse(stable.accept([], 'x', .1))
        self.assertFalse(stable.accept([], 'x', .2))
        self.assertTrue(stable.accept([], 'x', 1.1))
        self.assertFalse(stable.accept(['bad'], 'x', 2))
        self.assertFalse(stable.accept([], 'x', 3))

    def test_row_change_resets_stability(self):
        stable = s.Stable()
        stable.accept([], 'x', 0)
        stable.accept([], 'x', .5)
        self.assertFalse(stable.accept([], 'y', 2))


class World:
    def __init__(self, menu=False, host_loss=None, refusal=None):
        self.t, self.p = state(), state(True)
        self.actions, self.host_loss, self.refusal = [], host_loss, refusal
        if menu:
            disconnected(self.t, self.p)

    def endpoint(self, peer=False):
        world = self
        class Endpoint:
            def ask(self, action='observe', *args):
                if peer:
                    if action != 'observe':
                        raise AssertionError('peer mutation!')
                    return copy.deepcopy(world.p)
                if action != 'observe':
                    world.actions.append(action)
                    if action == world.refusal:
                        raise RuntimeError('dispatch outcome unknown')
                    if action == 'pause': world.t['exitReady'] = True
                    if action == 'leave': world.t['confirmReady'] = True
                    if action == 'confirm': disconnected(world.t, world.p)
                    if action == 'open-code': world.t['fieldReady'] = True
                    if action == 'fill-code': world.t.update(codeField=args[1], connectReady=True)
                    if action == 'connect':
                        world.t, world.p = state(cid='19'), state(True, cid='19')
                    if action == world.host_loss:
                        world.p['lobbyId'] = '456'
                return copy.deepcopy(world.t)
        return Endpoint()


class Coordinator(unittest.TestCase):
    def run_world(self, world, operation='rejoin'):
        flow = s.Flow(world.endpoint(), world.endpoint(True), Path('.'), s.diag.Deadline(8), 3, lambda _: None)
        # Exercise the real coordinator/predicates, reduce only required hold duration for unit speed.
        with patch.object(s, 'Stable', lambda: OriginalStable(2, 0)), patch.object(s.time, 'sleep', lambda _: None):
            result = flow.run(operation)
        return result, flow

    def test_rejoin_new_client_id(self):
        world = World()
        code, flow = self.run_world(world)
        self.assertEqual(code, 0)
        self.assertEqual(world.actions, ['pause', 'leave', 'confirm', 'open-code', 'fill-code', 'connect'])
        self.assertEqual(flow.report['newClientId'], '19')

    def test_leave_stops_at_menu(self):
        world = World()
        code, flow = self.run_world(world, 'leave')
        self.assertEqual(world.actions, ['pause', 'leave', 'confirm'])
        self.assertEqual(flow.report['readiness'], 'disconnected-ready')

    def test_join_uses_menu_no_leave(self):
        world = World(menu=True)
        self.run_world(world, 'join')
        self.assertEqual(world.actions, ['open-code', 'fill-code', 'connect'])

    def test_status_never_mutates(self):
        world = World()
        self.run_world(world, 'status')
        self.assertEqual(world.actions, [])

    def test_host_refused_before_mutation(self):
        world = World()
        world.t['server'] = True
        with self.assertRaisesRegex(ValueError, 'non-host'):
            self.run_world(world)
        self.assertEqual(world.actions, [])

    def test_existing_confirmation_refused(self):
        world = World()
        world.t['confirmReady'] = True
        with self.assertRaisesRegex(ValueError, 'unrelated confirmation'):
            self.run_world(world)
        self.assertEqual(world.actions, [])

    def test_host_loss_stops_before_next_mutation(self):
        world = World(host_loss='leave')
        with self.assertRaisesRegex(RuntimeError, 'host changed'):
            self.run_world(world)
        self.assertEqual(world.actions, ['pause', 'leave'])

    def test_stage_deadline_bounds_nested_transport_and_is_restored(self):
        world = World()
        deadline = s.diag.Deadline(8)
        original_end = deadline.ends
        flow = s.Flow(world.endpoint(), world.endpoint(True), Path('.'), deadline, .02, lambda _: None)
        flow.lobby, flow.host_client = '123', '0'
        original_pair = flow.pair
        def slow_pair():
            self.assertLessEqual(deadline.remaining(), .02)
            time.sleep(.03)
            return original_pair()
        flow.pair = slow_pair
        with self.assertRaisesRegex(TimeoutError, 'short-stage'):
            flow.wait('short-stage', lambda t, p: [], False)
        self.assertEqual(deadline.ends, original_end)
        self.assertEqual(flow.report['stages'][-1]['status'], 'timeout')

    def test_no_retry_on_ambiguous_mutation(self):
        world = World(refusal='connect')
        with self.assertRaisesRegex(RuntimeError, 'outcome unknown'):
            self.run_world(world)
        self.assertEqual(world.actions.count('connect'), 1)


OriginalStable = s.Stable


class Transport(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.screens = self.root / 'BepInEx/screens'
        self.screens.mkdir(parents=True)
        self.fact = state()
        self.process = dict(pid=2, startUtc=self.fact['startUtc'], exe=self.fact['exe'], instance='')
        self.transport = s.Transport(self.root, self.process, self.root, 'target', s.diag.Deadline(3))

    def responder(self, mutate=lambda data: None):
        def worker():
            path = self.screens / 'session.request'
            until = time.monotonic() + 2
            while not path.exists() and time.monotonic() < until:
                time.sleep(.005)
            if not path.exists(): return
            parts = path.read_text().split()
            path.unlink()
            fact = copy.deepcopy(self.fact)
            fact['utc'] = s.diag.utc()
            fact['frame'] = self.transport.last_frame + 2
            data = dict(schema='alterum.session/1', requestId=parts[1], action=parts[7],
                        status='observed' if parts[7] == 'observe' else 'dispatched', state=fact)
            mutate(data)
            (self.screens / ('session-' + parts[1] + '.json')).write_text(json.dumps(data))
        thread = threading.Thread(target=worker)
        thread.start()
        self.addCleanup(thread.join)

    def test_correlated_reply(self):
        self.responder()
        self.assertEqual(self.transport.ask()['clientId'], '8')

    def test_busy_does_not_overwrite(self):
        request = self.screens / 'session.request'
        request.write_text('existing')
        with self.assertRaisesRegex(RuntimeError, 'busy'):
            self.transport.ask()
        self.assertEqual(request.read_text(), 'existing')

    def test_wrong_request_rejected(self):
        self.responder(lambda d: d.update(requestId='wrong'))
        with self.assertRaisesRegex(ValueError, 'mismatch'): self.transport.ask()

    def test_wrong_process_rejected(self):
        self.responder(lambda d: d['state'].update(pid=99))
        with self.assertRaisesRegex(ValueError, 'identity'): self.transport.ask()

    def test_stale_reply_rejected(self):
        self.responder(lambda d: d['state'].update(utc='2020-01-01T00:00:00Z'))
        with self.assertRaisesRegex(ValueError, 'not fresh'): self.transport.ask()

    def test_hot_reload_stops_flow(self):
        self.responder()
        self.transport.ask()
        self.responder(lambda d: d['state'].update(mvid='reloaded'))
        with self.assertRaisesRegex(RuntimeError, 'generation'): self.transport.ask()

    def test_same_bytes_hot_reload_stops_flow(self):
        self.responder()
        self.transport.ask()
        self.responder(lambda d: d['state'].update(generation='generation2'))
        with self.assertRaisesRegex(RuntimeError, 'generation'): self.transport.ask()

    def test_timeout_keeps_unknown_request_no_retry(self):
        self.transport.deadline = s.diag.Deadline(.03)
        with self.assertRaisesRegex(TimeoutError, 'may still execute'):
            self.transport.ask('connect')
        request = self.screens / 'session.request'
        contents = request.read_text()
        self.transport.deadline = s.diag.Deadline(1)
        with self.assertRaisesRegex(RuntimeError, 'busy'):
            self.transport.ask('connect')
        self.assertEqual(request.read_text(), contents)

    def test_refusal_not_success(self):
        self.responder(lambda d: d.update(status='error', error='expired'))
        with self.assertRaisesRegex(RuntimeError, 'expired'): self.transport.ask('connect')

    def test_old_frame_rejected(self):
        self.transport.last_frame = 50
        self.responder(lambda d: d['state'].update(frame=49))
        with self.assertRaisesRegex(ValueError, 'frame'): self.transport.ask()


class OsAndLockGuards(unittest.TestCase):
    def test_os_worker_error_propagates(self):
        class Discovery:
            report = {'stages': []}
            def worker(self, *args, **kwargs): raise TimeoutError('SSH deadline')
        with self.assertRaisesRegex(TimeoutError, 'SSH deadline'):
            s.os_observe(Discovery(), 'pre', [], s.diag.Deadline(1))

    def test_partial_os_discovery_is_not_pass(self):
        class Discovery:
            report = {'stages': []}
            def worker(self, *args, **kwargs):
                return {'target': {'status': 'unavailable', 'process': None}}
        with self.assertRaisesRegex(ValueError, 'incomplete'):
            s.os_observe(Discovery(), 'pre', [], s.diag.Deadline(1))

    def test_lock_conflict_and_release(self):
        with tempfile.TemporaryDirectory() as td:
            class Endpoint: screens = Path(td)
            with ExitStack() as first:
                s.lock_transports(first, [Endpoint()])
                with ExitStack() as second:
                    with self.assertRaisesRegex(RuntimeError, 'another session flow'):
                        s.lock_transports(second, [Endpoint()])
            with ExitStack() as third:
                s.lock_transports(third, [Endpoint()])
            self.assertTrue((Path(td) / 'session-flow.lock').exists())

    def test_busy_ui_is_not_consumed(self):
        with tempfile.TemporaryDirectory() as td:
            class Endpoint: screens = Path(td)
            path = Path(td) / 'ui.request'
            path.write_text('existing')
            with ExitStack() as stack:
                with self.assertRaisesRegex(RuntimeError, 'mailbox busy'):
                    s.lock_transports(stack, [Endpoint()])
            self.assertEqual(path.read_text(), 'existing')


if __name__ == '__main__':
    unittest.main()
