"""Fixture tests for diagnostic honesty/freshness; no Unity/process mutations."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('diagnostics', Path(__file__).parents[2]/'win/collect-diagnostics.py')
d = importlib.util.module_from_spec(spec)
spec.loader.exec_module(d)


def process(pid=101, instance=''):
    return dict(pid=pid, exe=r'C:\Fixture\Alterum.exe', startUtc='2026-01-01T00:00:00.1234567Z',
                session=1, instance=instance, hwnd=12345)


def snapshot():
    result = {'schema': 'alterum.snapshot/1', 'tag': 'fixture', 'commandId': 'snapshot-fixture',
              'utc': '2026-01-01T00:00:02Z', 'process.pid': '101', 'process.exe': r'C:\Fixture\Alterum.exe',
              'process.startUtc': '2026-01-01T00:00:00.123Z', 'process.instanceArg': 'none',
              'hot.generation': 'AlterumHot#2', 'hot.mvid': 'mvid', 'hot.dllSha256': 'a'*64,
              'hot.assembly': r'C:\Fixture\BepInEx\cache\hot\2\AlterumModelSwap.Hot.dll',
              'scene.name': 'Lobby', 'net.authority': 'server', 'local.clientId': '0',
              'local.rootPos': '(0,0,0)', 'local.rootEuler': '(0,0,0)',
              'local.posePos': '(1,2,3)', 'local.poseFwd': '(0,0,1)',
              'config.path': r'C:\Fixture\BepInEx\config\active-main.cfg',
              'config.showLocalPlayer': '1', 'config.source': 'in-memory-host-context',
              'view.pov': 'FirstPerson', 'view.renderers': '9', 'view.enabled': '8',
              'view.activeEnabled': '8', 'view.scope': 'all renderers under local actor; not visibility proof',
              'identity.hudSource': 'visible-sprite-observer/1',
              'identity.localSlug': 'tc2-spy', 'remotes.count': '0', 'assets.assert': 'PASS',
              'identity.local': 'actor=local wantedSlug=tc2-spy advertisedSlug=- canonicalSlug=tc2-spy canonicalLabel=TC2 Spy '
                'resolvedDb=22 appliedRow=TC2 Spy appliedDb=22 appliedSlug=tc2-spy bodySlug=tc2-spy '
                'hudRow=tc2-spy provenance=game:applied readiness=ready bodyState=live explicit=False'}
    for name in ('AlterumModelSwap', 'AlterumModelSwap.Contracts'):
        result.update({f'loaded.{name}.path': 'fixture/'+name+'.dll', f'loaded.{name}.sha256': 'a'*64,
                       f'loaded.{name}.mvid': 'mvid', f'loaded.{name}.hashSource': 'loaded-assembly-backing-file-not-memory'})
    return result


def state(role='main'):
    before, after = snapshot(), snapshot()
    before['utc'] = '2026-01-01T00:00:01Z'
    return dict(role=role, root=r'C:\Fixture', process=d.observed(process(), 'discover-worker.json'),
                runtime=d.observed('fresh', 'main-after.manifest.txt'),
                before=before, after=after, beforeEvidence='main-before.manifest.txt', afterEvidence='main-after.manifest.txt',
                disk={key: d.observed('a'*64, key+'.dll') for key in ('gameAssembly', 'unityPlayer', 'host', 'contracts', 'hot')},
                capture=dict(status='observed', evidence='main-window.png', utc='2026-01-01T00:00:01.5Z',
                             identity=process(), backend='fixture', sha256='b'*64), errors=[])


def report(*states):
    return dict(schema=d.SCHEMA, runId='fixture', findings=[], instances=list(states or [state()]), stages=[])


def codes(rep):
    return [f['code'] for f in rep['findings']]


class DiagnosticFixtures(unittest.TestCase):
    def test_complete_healthy_json_and_references(self):
        rep = report()
        d.diagnose(rep)
        self.assertTrue(rep['complete'])
        self.assertEqual(rep['health'], 'healthy')
        rep.update(exitCode=0, elapsedSeconds=.1)
        self.assertEqual(json.loads(json.dumps(rep))['schema'], 'alterum.diagnostics/1')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for ref in ('discover-worker.json', 'main-before.manifest.txt', 'main-after.manifest.txt', 'main-window.png'):
                (root/ref).touch()
            for instance in rep['instances']:
                self.assertTrue((root/instance['afterEvidence']).exists())
                self.assertTrue((root/instance['capture']['evidence']).exists())
            self.assertIn('Health: healthy; collection: complete; exit=0', d.summary(rep))

    def test_active_false_not_inactive_true(self):
        current = state()
        current['after']['config.showLocalPlayer'] = current['before']['config.showLocalPlayer'] = '0'
        # Inactive disk config must never override the host's effective snapshot.
        current['diskOnlyConfig'] = {'path': 'inactive-pinned.cfg', 'ShowLocalPlayer': True}
        rep = report(current)
        d.diagnose(rep)
        config = current['config']
        self.assertEqual(d.value(config['showLocalPlayer']), '0')
        self.assertTrue(d.value(config['path']).endswith('active-main.cfg'))
        self.assertIn('local-visibility-disabled', codes(rep))

    def test_game_mismatch_is_prominent_blocker_no_repair(self):
        a, b = state(), state('secondary')
        b['disk']['gameAssembly'] = d.observed('different', 'GameAssembly.dll')
        rep = report(a, b)
        d.diagnose(rep)
        self.assertEqual(rep['compatibility']['gameAssembly']['status'], 'mismatch')
        finding = next(f for f in rep['findings'] if f['code'] == 'game-binary-mismatch')
        self.assertIn('BLOCKER', finding['message'])
        self.assertIn('no repair performed', finding['message'])
        self.assertTrue(rep['complete'])  # Complete collection is NOT healthy.
        self.assertEqual(rep['health'], 'findings')

    def test_spy_applied_peter_body_and_advertised_canonical_hud_not_proof(self):
        current = state()
        current['after']['identity.local'] = current['after']['identity.local'].replace(
            'advertisedSlug=-', 'advertisedSlug=peter-griffin').replace(
            'canonicalSlug=tc2-spy', 'canonicalSlug=peter-griffin').replace(
            'bodySlug=tc2-spy', 'bodySlug=peter-griffin').replace('hudRow=tc2-spy', 'hudRow=peter-griffin')
        rep = report(current)
        d.diagnose(rep)
        self.assertIn('model-mismatch', codes(rep))
        self.assertIn('presentation-mismatch', codes(rep))

    def test_missing_hud_never_synthesized_and_legacy_claim_not_trusted(self):
        for source, hud in [(None, 'tc2-spy'), ('visible-sprite-observer/1', '<unobserved>')]:
            current = state()
            current['after']['identity.hudSource'] = source
            current['after']['identity.local'] = current['after']['identity.local'].replace('hudRow=tc2-spy', 'hudRow='+hud)
            rep = report(current)
            d.diagnose(rep)
            self.assertIsNone(d.value(current['actors'][0]['hud']))
            self.assertIn('hud-unavailable', codes(rep))
            self.assertFalse(rep['complete'])

    def test_old_host_config_path_and_missing_pov_are_not_disk_guesses(self):
        current = state()
        current['after'].pop('config.path')
        current['after'].pop('view.pov')
        rep = report(current)
        d.diagnose(rep)
        self.assertFalse(rep['complete'])
        self.assertIsNone(d.value(current['config']['path']))
        self.assertIn('pov-unavailable', codes(rep))

    def test_process_selectors_never_first_candidate(self):
        a, b = process(), process(pid=102, instance='b')
        with self.assertRaisesRegex(ValueError, 'ambiguous'):
            d.select_process([a, b], r'C:\Fixture')
        self.assertEqual(d.select_process([a, b], r'C:\Fixture', 'b')['pid'], 102)
        self.assertEqual(d.select_process([a, b], r'C:\Fixture', pid=101)['instance'], '')
        with self.assertRaisesRegex(ValueError, 'absent'):
            d.select_process([a, b], r'C:\Other')

    def test_runtime_freshness_rejects_wrong_pid_start_instance_old_tag_and_utc(self):
        good = snapshot()
        d.validate_manifest(good, process(), 'fixture', 1767225601, 1767225603)
        for key, bad in [('process.pid', '102'), ('process.startUtc', '2025-01-01T00:00:00Z'),
                         ('process.instanceArg', 'b'), ('tag', 'old-run'), ('utc', '2025-01-01T00:00:00Z')]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                data = dict(good, **{key: bad})
                d.validate_manifest(data, process(), 'fixture', 1767225601, 1767225603)
        exact = process()
        exact['startUtc'] = '2026-01-01T00:00:00.1239999Z'
        self.assertFalse(d.same_process(exact, process()))

    def test_generation_change_invalidates_models_and_capture(self):
        current = state()
        current['after']['hot.generation'] = 'AlterumHot#3'
        self.assertFalse(d.generations_match(current['before'], current['after']))
        current['runtime'] = d.unavailable('Hot generation/hash changed')
        rep = report(current)
        d.diagnose(rep)
        self.assertNotIn('actors', current)
        self.assertEqual(current['capture']['status'], 'unavailable')
        self.assertFalse(rep['complete'])

    def test_stale_or_wrong_identity_capture_rejected(self):
        for field, bad in [('utc', '2025-01-01T00:00:00Z'), ('identity', process(pid=999))]:
            current = state()
            current['capture'][field] = bad
            rep = report(current)
            d.diagnose(rep)
            self.assertEqual(current['capture']['status'], 'unavailable')
            self.assertIn('capture-unavailable', codes(rep))

    def test_log_rotation_truncation_and_truncate_regrow(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'log'
            for action in ('replace', 'truncate', 'regrow'):
                path.write_bytes(b'a'*300)
                guard = d.LogGuard(path)
                if action == 'replace':
                    path.rename(path.with_suffix('.old'))
                    path.write_bytes(b'a'*301)
                elif action == 'truncate':
                    path.write_bytes(b'abc')
                else:
                    path.write_bytes(b'b'*500)
                with self.subTest(action=action), self.assertRaises(ValueError):
                    guard.validate()

    def test_nul_localized_reservation_logs_and_secret_whitelist(self):
        text = '[Wardrobe] client 3 пытался занять недоступную модель 24 — отклонено.\0\n'
        text += 'password=never-copy-me\n[net:recv] secret=never-copy-me\n'
        lines = d.diagnostic_lines(text)
        self.assertEqual(len(lines), 1)
        self.assertIn('client 3', lines[0])
        self.assertIn('модель 24 — отклонено', lines[0])
        self.assertNotIn('\0', lines[0])
        self.assertNotIn('never-copy-me', ''.join(lines))

    def test_locked_capture_and_absent_secondary_preserve_text(self):
        current = state()
        current['capture'] = d.unavailable('desktop locked')
        absent = dict(role='secondary', root=r'C:\Absent', process=d.unavailable('absent'),
                      runtime=d.unavailable('target absent'), disk={}, capture=d.unavailable('not running'))
        rep = report(current, absent)
        d.diagnose(rep)
        self.assertIn('capture-unavailable', codes(rep))
        self.assertEqual(d.value(current['actors'][0]['applied']), 'tc2-spy')
        self.assertFalse(rep['complete'])
        # No B requested is supported, not an absence finding.
        rep = report()
        d.diagnose(rep)
        self.assertTrue(rep['complete'])

    def test_manifest_parser_and_unknown_values(self):
        data = d.parse_manifest("schema=alterum.snapshot/1\n[config]\npath='active.cfg'\nshowLocalPlayer=unavailable\nshowLocalPlayerWhy='not measured'\n")
        self.assertEqual(data['config.path'], 'active.cfg')
        self.assertEqual(d.manifest_fact(data, 'config.showLocalPlayer', 'fixture')['reason'], 'not measured')
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            d.parse_manifest('a=1\na=2\n')

    def test_deadline_is_shared_not_per_stage(self):
        deadline = d.Deadline(.05)
        time.sleep(.06)
        self.assertEqual(deadline.remaining(3), 0)
        with self.assertRaises(TimeoutError):
            deadline.check()


class PartialBundleTests(unittest.TestCase):
    def test_real_transport_busy_and_no_response_keep_partial_bundle(self):
        for busy in (True, False):
            with self.subTest(busy=busy), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)/'install'
                screens = root/'BepInEx/screens'
                screens.mkdir(parents=True)
                (root/'Alterum.exe').touch()
                log = root/'BepInEx/LogOutput.log'
                log.write_text('old log\n')
                request = screens/'qc.request'
                if busy:
                    request.write_text('do-not-overwrite\n')
                args = argparse.Namespace(main_root=r'C:\Fixture', secondary_root=None,
                    main_instance=None, secondary_instance=None, main_pid=None, secondary_pid=None,
                    output=directory, staging=directory+'/staged', timeout=1, ssh_host='unused', game_log=None)
                collector = d.Collector(args)
                collector.states['main']['root'] = str(root)
                collector.states['main']['process'] = d.observed(process(), 'fixture')
                collector.guards['main'] = d.LogGuard(log)
                with collector.stage('main:runtime-before'):
                    collector.runtime('main', 'before', budget=.25)
                self.assertEqual(collector.finish(), 1)
                if busy:
                    self.assertEqual(request.read_text(), 'do-not-overwrite\n')
                else:
                    # A timed-out request may execute later. It must not be blindly deleted/retried.
                    self.assertIn('alterum.test.snapshot d-', request.read_text())
                    tag = request.read_text().split()[-1]
                    self.assertLessEqual(len(tag), 48)
                parsed = json.loads((collector.out/'report.json').read_text())
                self.assertFalse(parsed['complete'])
                self.assertLess(parsed['elapsedSeconds'], .8)
                self.assertNotIn('before', parsed['instances'][0])
                self.assertTrue((collector.out/'summary.txt').exists())

    def test_expired_stage_context_keeps_all_independent_stages(self):
        with tempfile.TemporaryDirectory() as directory:
            args = argparse.Namespace(main_root=r'C:\Fixture', secondary_root=None,
                main_instance=None, secondary_instance=None, main_pid=None, secondary_pid=None,
                output=directory, staging=directory+'/staged', timeout=1, ssh_host='unused', game_log=None)
            collector = d.Collector(args)
            collector.deadline = d.Deadline(0)
            self.assertEqual(collector.collect(), 1)
            names = [stage['name'] for stage in collector.report['stages']]
            self.assertNotIn('fatal-collection', names)
            self.assertIn('shared-game-log', names)
            self.assertFalse(collector.report['complete'])

    def test_partial_worker_preserves_first_target_and_marks_missing_second(self):
        with tempfile.TemporaryDirectory() as directory:
            args = argparse.Namespace(main_root=r'C:\Fixture', secondary_root=r'C:\Other',
                main_instance=None, secondary_instance=None, main_pid=None, secondary_pid=None,
                output=directory, staging=directory+'/staged', timeout=1, ssh_host='unused', game_log=None)
            collector = d.Collector(args)
            def interrupted(*unused, **kwargs):
                partial = collector.stage_dir/'discover.result.json.partial'
                partial.write_text(json.dumps(dict(runId=collector.run_id, stage='discover', targets=[
                    dict(role='main', status='observed', process=process(), candidates=[process()])])) )
                raise d.subprocess.TimeoutExpired('fake worker', 1)
            with patch.object(d, 'windows_path', return_value=r'C:\staged'), patch.object(d.subprocess, 'run', interrupted):
                with collector.stage('discover'):
                    entries = collector.worker('discover', collector.targets)
            self.assertEqual(entries['main']['status'], 'observed')
            self.assertEqual(entries['secondary']['status'], 'unavailable')
            self.assertEqual(collector.report['stages'][0]['status'], 'incomplete')

    def test_mailbox_or_response_timeout_keeps_partial_report(self):
        # Exercise the real collector stage/finalization path while its OS adapter reports no
        # process. The real transport's busy-mailbox tests are in tests/direct-console.
        with tempfile.TemporaryDirectory() as directory:
            args = argparse.Namespace(main_root=r'C:\Fixture', secondary_root=None,
                main_instance=None, secondary_instance=None, main_pid=None, secondary_pid=None,
                output=directory, staging=directory+'/staged', timeout=1, ssh_host='unused', game_log=None)
            collector = d.Collector(args)
            with patch.object(collector, 'worker', side_effect=TimeoutError('mailbox busy / response deadline')):
                result = collector.collect()
            self.assertEqual(result, 1)
            parsed = json.loads((collector.out/'report.json').read_text())
            self.assertFalse(parsed['complete'])
            self.assertTrue((collector.out/'summary.txt').is_file())
            self.assertLess(parsed['elapsedSeconds'], 1.5)
            self.assertIn('stage-incomplete', codes(parsed))


if __name__ == '__main__':
    unittest.main()
