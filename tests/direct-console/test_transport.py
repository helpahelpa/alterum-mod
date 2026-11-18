"""Transport tests; live verification must also exercise QC's real processor and gate."""
import importlib.util
from pathlib import Path
import tempfile
import threading
import time
import unittest

spec = importlib.util.spec_from_file_location('console', Path(__file__).parents[2] / 'win' / 'console.py')
console = importlib.util.module_from_spec(spec)
spec.loader.exec_module(console)


class ConsoleTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'Alterum.exe').touch()
        self.screens = self.root / 'BepInEx' / 'screens' / 'b'
        self.screens.mkdir(parents=True)
        self.log = self.root / 'BepInEx' / 'logs' / 'b.log'
        self.log.parent.mkdir()
        self.log.write_text('old output\n')
        self.errors = []

    def consumer(self, state='dispatched', output='command output', existing=None):
        def consume():
            try:
                request = self.screens / 'qc.request'
                deadline = time.monotonic() + 3
                if existing:
                    time.sleep(0.1)
                    self.assertEqual(request.read_text(), existing)
                    request.unlink()
                while not request.exists() and time.monotonic() < deadline:
                    time.sleep(0.01)
                text = request.read_text()
                request.unlink()
                verb, request_id, command = text.rstrip('\n').split(' ', 2)
                self.assertEqual(verb, 'exec')
                self.assertEqual(command, 'echo "quoted argument"')
                with self.log.open('a') as stream:
                    stream.write(f'[qc:direct] id={request_id} state=begin\n{output}\n')
                    stream.flush()
                    # Exercise fragmented log writes and terminal-line buffering.
                    stream.write(f'[qc:direct] id={request_id} state={state}')
                    stream.flush()
                    time.sleep(0.03)
                    stream.write(' playerAccess=state=locked\n')
            except Exception as error:
                self.errors.append(error)
        thread = threading.Thread(target=consume)
        thread.start()
        self.addCleanup(thread.join)
        return thread

    def run_command(self):
        lines = []
        result = console.execute(self.root, 'b', 'echo "quoted argument"', 2, emit=lines.append)
        return result, lines

    def test_success_and_quotes(self):
        thread = self.consumer()
        result, lines = self.run_command()
        thread.join()
        self.assertFalse(self.errors)
        self.assertEqual(result, 0)
        self.assertIn('command output', lines)
        self.assertIn('state=locked', lines[-1])
        self.assertNotIn('old output', lines)

    def test_processor_error(self):
        self.consumer(state='error')
        self.assertEqual(self.run_command()[0], 1)

    def test_failed_assertion_is_failure(self):
        self.consumer(output='[qc:test] assert-model result=FAIL')
        self.assertEqual(self.run_command()[0], 1)

    def test_native_trampoline_error_is_failure(self):
        self.consumer(output='[Error  :Il2CppInterop] During invoking native->managed trampoline')
        self.assertEqual(self.run_command()[0], 1)

    def test_does_not_overwrite_existing_request(self):
        request = self.screens / 'qc.request'
        request.write_text('status\n')
        thread = self.consumer(existing='status\n')
        self.assertEqual(self.run_command()[0], 0)
        thread.join()
        self.assertFalse(self.errors)

    def test_busy_timeout_preserves_request(self):
        request = self.screens / 'qc.request'
        request.write_text('status\n')
        with self.assertRaisesRegex(TimeoutError, 'mailbox busy'):
            console.execute(self.root, 'b', 'help', 0.05)
        self.assertEqual(request.read_text(), 'status\n')
        self.assertFalse(list(self.screens.glob('*.tmp')))

    def test_rejects_injection_and_slot_traversal(self):
        for instance, command in [('b', 'help\ninvoke exit'), ('../b', 'help'), ('b', ''), ('b', 'help\0')]:
            with self.subTest(instance=instance, command=command), self.assertRaises(ValueError):
                console.execute(self.root, instance, command)
        self.assertFalse((self.screens / 'qc.request').exists())


if __name__ == '__main__':
    unittest.main()
