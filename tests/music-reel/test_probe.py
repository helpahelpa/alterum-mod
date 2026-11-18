import importlib.util
from pathlib import Path
import sys
import tempfile
import threading
import time
import types
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'win'))
spec = importlib.util.spec_from_file_location('music_reel_probe', ROOT / 'win/music-reel-probe.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)

POSE_LINE = ('[reel:pose] actor=13 source=ThirdViewPlayerModel '
              'pos=(-34.35,0.578,-4.35) fwd=(0.00,0.00,-1.00)\n')


class Deadline:
    def __init__(self, seconds=20): self.ends = time.monotonic() + seconds
    def check(self):
        if time.monotonic() > self.ends: raise TimeoutError('deadline')
    def remaining(self): return max(0, self.ends - time.monotonic())


def responder(screens, name, reply, seen, stop):
    # Stand-in for the game's twice-a-second mailbox poll: consume the complete request,
    # then answer it (if at all) on the instance log.
    while not stop.is_set():
        box = screens / name
        try:
            if box.exists():
                text = box.read_text()
                seen.append((name, text))
                box.unlink()
                reply(name, text)
        except FileNotFoundError:
            pass
        time.sleep(.005)


class Fixture:
    def __init__(self, d, reply, mailbox='qc.request'):
        self.screens = Path(d)
        (self.screens / 'instance.log').touch()
        self.log = p.Log(self.screens / 'instance.log')
        self.reply, self.mailbox = reply, mailbox
        self.seen, self.stop = [], threading.Event()
        self.thread = threading.Thread(target=responder, args=(self.screens, mailbox, self.call, self.seen, self.stop),
                                       daemon=True)
        self.probe = p.Probe({'target': types.SimpleNamespace(screens=self.screens),
                              'peer': types.SimpleNamespace(screens=self.screens)},
                             {'target': self.log, 'peer': self.log}, Deadline(), None)

    def call(self, name, text):
        self.reply(name, text)

    def start(self):
        self.thread.start()
        return self

    def close(self):
        self.stop.set()
        self.thread.join(2)

    def append(self, text):
        with self.log.path.open('a') as out: out.write(text)


class TestProbe(unittest.TestCase):
    def test_pose_log_requires_a_complete_matching_success_line(self):
        readings = p.poses(POSE_LINE, '13')
        self.assertEqual(readings[-1], dict(source='ThirdViewPlayerModel', pos=(-34.35, 0.578, -4.35), fwd=(0.0, 0.0, -1.0)))
        self.assertEqual(p.poses(POSE_LINE, '14'), [])
        self.assertEqual(p.poses('[reel:act] pose: no player for client 13\n', '13'), [])
        self.assertEqual(p.poses(POSE_LINE.replace(') fwd=', ')'), '13'), [])
        self.assertEqual(p.poses(POSE_LINE.replace('0.578', 'NaN'), '13'), [])
        self.assertEqual(p.poses(POSE_LINE.replace('pos=(', 'pos=['), '13'), [])
        self.assertEqual(p.pose_problems(None, '-34.35,0.578,-4.35,180'), ['no successful pose log'])

    def test_pose_problems_allow_ground_settle_and_small_yaw_drift(self):
        expected = '-34.35,0.578,-4.35,90'
        reading = dict(source='ThirdViewPlayerModel', pos=(-34.35, 0.778, -4.35), fwd=(1.0, 0.0, 0.0))
        self.assertEqual(p.pose_problems(reading, expected), [])
        self.assertTrue(p.pose_problems(dict(reading, pos=(-34.35, 1.0, -4.35)), expected))
        # 2 degrees off is inside the small yaw tolerance, 5 degrees is not.
        self.assertEqual(p.pose_problems(dict(reading, fwd=(0.9994, 0.0, 0.0349)), expected), [])
        self.assertTrue(p.pose_problems(dict(reading, fwd=(0.9962, 0.0, 0.0872)), expected))
        self.assertTrue(p.pose_problems(dict(reading, fwd=(0.0, 1.0, 0.0)), expected))

    def test_preflight_covers_the_direct_console_mailbox(self):
        self.assertIn('qc.request', p.BOXES)

    def test_goto_is_one_uuid_correlated_direct_exec(self):
        with tempfile.TemporaryDirectory() as d:
            fx = Fixture(d, lambda name, text: fx.append(
                f'[qc:direct] id={text.split()[1]} state=dispatched playerAccess=locked\n'))
            fx.start()
            try:
                fx.probe.goto_pos('target', '-34.35,0.578,-4.35,90')
            finally:
                fx.close()
            self.assertEqual(len(fx.seen), 1)
            mailbox, text = fx.seen[0]
            self.assertEqual(mailbox, 'qc.request')
            request_id, command = text.split(' ', 2)[1:]
            self.assertRegex(request_id, r'^[0-9a-f]{32}$')
            self.assertEqual(command.rstrip('\n'), 'goto.position (-34.35,0.578,-4.35)')
            self.assertFalse((fx.screens / 'qc.request').exists())

    def test_missing_direct_exec_ack_fails_once_and_never_retries_the_mutation(self):
        with tempfile.TemporaryDirectory() as d:
            fx = Fixture(d, lambda name, text: None)  # consumed, never acknowledged
            fx.start()
            try:
                with self.assertRaises(TimeoutError) as caught:
                    fx.probe.qc('target', 'goto.position (1.00,0.00,2.00)', seconds=.3)
            finally:
                fx.close()
            self.assertIn('no mutation retry', str(caught.exception))
            self.assertEqual(len(fx.seen), 1)

    def test_direct_exec_error_acknowledgement_is_reported(self):
        with tempfile.TemporaryDirectory() as d:
            fx = Fixture(d, lambda name, text: fx.append(
                f'[qc:direct] id={text.split()[1]} state=error error=QC table is not ready\n'))
            fx.start()
            try:
                with self.assertRaises(RuntimeError) as caught:
                    fx.probe.goto_pos('target', '-34.35,0.578,-4.35,90')
            finally:
                fx.close()
            self.assertIn('state=error error=QC table is not ready', str(caught.exception))
            self.assertEqual(len(fx.seen), 1)

    def test_pose_gate_accepts_only_a_fresh_successful_host_read(self):
        with tempfile.TemporaryDirectory() as d:
            # A valid but stale line: it must not satisfy the gate for a later request.
            (Path(d) / 'instance.log').write_text(POSE_LINE)
            fx = Fixture(d, lambda name, text: fx.append(
                '[reel:pose] actor=13 source=ThirdViewPlayerModel pos=(-34.35,0.778,-4.35) fwd=(1.00,0.00,0.00)\n'),
                mailbox='reel.request')
            fx.start()
            try:
                reading = fx.probe.pose('13', '-34.35,0.578,-4.35,90', seconds=.6)
            finally:
                fx.close()
            self.assertEqual(reading['pos'], (-34.35, 0.778, -4.35))
            self.assertTrue(fx.seen)
            self.assertTrue(all(name == 'reel.request' and text == 'pose=13\n' for name, text in fx.seen))

    def test_pose_gate_times_out_on_no_answer_or_a_malformed_fresh_line(self):
        for answer in (None, '[reel:pose] actor=13 source=ThirdViewPlayerModel pos=(x,y,z) fwd=(1.00,0.00,0.00)\n'):
            with tempfile.TemporaryDirectory() as d:
                (Path(d) / 'instance.log').write_text(POSE_LINE)
                fx = Fixture(d, (lambda name, text: None) if answer is None else (lambda name, text: fx.append(answer)),
                             mailbox='reel.request')
                fx.start()
                try:
                    with self.assertRaises(TimeoutError):
                        fx.probe.pose('13', '-34.35,0.578,-4.35,180', seconds=.6)
                finally:
                    fx.close()
                self.assertTrue(fx.seen)

    def test_transport_uses_last_complete_status(self):
        line = '[music:req] status: transport playing=True channelOpen=True retiring=2\n'
        self.assertEqual(p.transport(line + '[music:req] status: transport playing=False channelOpen=False retiring=0\n'), (False, False, 0))
        with self.assertRaises(ValueError):
            p.transport('[music:req] status: phase=Idle')

    def test_paused_fmod_channel_does_not_count_as_active_playback(self):
        channel = '[music:req] status: transport playing=True channelOpen=True retiring=0\n'
        self.assertFalse(p.actively_playing('[music:req] status: phase=Paused\n' + channel))
        self.assertTrue(p.actively_playing('[music:req] status: phase=Playing\n' + channel))

    def test_correlates_remote_accept_consumption_and_settlement(self):
        host = "[reel] consumed held='healthsyringe' count=1 slot=12\n[reel:net] ACCEPT id=9 client=13 machine='lair-1' held='healthsyringe'\n[reel:net] settle spin=7 actor=13 type=item\n"
        client = '[reel:net] request 9 SETTLED spin=7\n[reel:net] settle spin=7 actor=13 (you) type=item\n'
        self.assertEqual(p.settled_spin(host, client, '13', 'healthsyringe', True), '7')
        self.assertIsNone(p.settled_spin(host, client.replace('request 9', 'request 8'), '13', 'healthsyringe', True))
        with self.assertRaises(RuntimeError):
            p.settled_spin(host + "[reel] consumed held='healthsyringe' count=1 slot=12\n", client, '13', 'healthsyringe', True)

    def test_log_rotation_and_truncation_fails_closed(self):
        with tempfile.TemporaryDirectory() as d:
            f = Path(d) / 'x.log'; f.write_text('before')
            log = p.Log(f); mark = log.mark()
            with f.open('a') as out: out.write('after')
            self.assertEqual(log.since(mark), 'after')
            f.write_text('x')
            with self.assertRaises(RuntimeError): log.mark()

    def test_same_pair_requires_network_membership(self):
        peer = dict(server=True, lobbyId='1', clientId='0', listening=True, connected=True, inLobby=True)
        target = dict(peer, server=False, clientId='13')
        self.assertTrue(p.same_pair(dict(peer=peer, target=target)))
        target['connected'] = False
        self.assertFalse(p.same_pair(dict(peer=peer, target=target)))


if __name__ == '__main__': unittest.main()
