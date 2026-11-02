#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from contextlib import ExitStack
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location('prepared', Path(__file__).with_name('prepared-snapshot.py'))
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


class PreparedTests(unittest.TestCase):
    def test_completion_requires_ordered_fresh_positive_markers(self):
        start = 'Adding transaction hashes to stored receipts, blocks 1 to 20.'
        done = 'Transaction hashes added to stored receipts of 20 blocks, 40 bytes added.'
        for text, expected in [('', False), (start, False), (done, False), (done + start, False), (start + done, True)]:
            with self.subTest(text=text):
                self.assertEqual(p.completion(text), expected)
        with self.assertRaises(RuntimeError):
            p.completion(start + done + 'Adding transaction hashes to stored receipts failed')

    def test_budget_includes_health_gate_and_reserve(self):
        p.budget(0, 15000)
        for remaining in (0, 10800, 14999):
            with self.subTest(remaining=remaining), self.assertRaises(RuntimeError):
                p.budget(0, remaining)

    def test_extended_finish_derives_latest_start_without_old_wall_clock_cutoff(self):
        finish = p.dt.datetime(2026, 10, 29, 11, tzinfo=p.dt.timezone.utc).timestamp()
        latest = p.dt.datetime(2026, 10, 29, 6, 50, tzinfo=p.dt.timezone.utc).timestamp()
        p.budget(latest, finish)
        with self.assertRaises(RuntimeError):
            p.budget(latest + 0.001, finish)
        args = SimpleNamespace(action='prepare', finish='2026-10-29T11:00:00Z')
        with patch.object(p.time, 'time', return_value=latest), patch.object(p, 'verify_persistence'), patch.object(Path, 'mkdir', side_effect=RuntimeError('allocation reached')):
            with self.assertRaisesRegex(RuntimeError, '^allocation reached$'):
                p.prepare(args)

    def test_host_namespace_and_host_are_required(self):
        for observer, host_ns, hostname, accepted in [
            ('mnt:[1]', 'mnt:[1]', 'core-ethereum-mainnet-snapshots-0', True),
            ('mnt:[2]', 'mnt:[1]', 'core-ethereum-mainnet-snapshots-0', False),
            ('mnt:[1]', 'mnt:[1]', 'another-host', False),
        ]:
            with self.subTest(observer=observer, hostname=hostname), patch.object(p.os, 'readlink', side_effect=[observer, host_ns]), patch.object(p.socket, 'gethostname', return_value=hostname), patch.object(Path, 'read_text', return_value='boot'):
                if accepted:
                    self.assertEqual(p.host_mount_identity()['boot_id'], 'boot')
                else:
                    with self.assertRaises(RuntimeError):
                        p.host_mount_identity()

    def test_cross_job_persistence_rejects_namespace_mount_upper_source_and_owner_drift(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, GITHUB_RUN_ID='123', GITHUB_SHA='sha', GITHUB_JOB='prepare-snapshot'):
            parent = Path(directory)
            root = parent / 'persistence-123'
            (root / 'base' / 'merged').mkdir(parents=True)
            (root / 'base' / 'upper').mkdir()
            marker = root / 'base' / 'upper' / 'marker'
            marker.write_text('retained')
            (root / 'base' / 'merged' / 'marker').write_text('retained')
            identity = {'host': 'host', 'boot_id': 'boot', 'mount_namespace': 'mnt:[1]'}
            mount = {'options': 'ro,lowerdir=owned'}
            record = {'identity': identity, 'run': '123', 'job': 'persistence-probe', 'source_sha': 'sha',
                      'source_tripwire': p.EXPECTED_SOURCE_TRIPWIRE, 'mount': mount}
            with patch.object(p, 'RETAINED_PARENT', parent), patch.object(p, 'host_mount_identity', return_value=identity), patch.object(p, 'mounted', return_value=mount), patch.object(p, 'fingerprint', return_value=p.EXPECTED_SOURCE_TRIPWIRE):
                p.atomic(root / 'proof.json', record)
                self.assertEqual(p.verify_persistence(), record)
                for key, bad in [('identity', {}), ('run', 'other'), ('job', 'prepare-snapshot'), ('source_sha', 'other'), ('mount', {}), ('source_tripwire', 'changed')]:
                    p.atomic(root / 'proof.json', dict(record, **{key: bad}))
                    with self.subTest(key=key), self.assertRaises(RuntimeError):
                        p.verify_persistence()
                p.atomic(root / 'proof.json', record)
                marker.unlink()
                with self.assertRaises(FileNotFoundError):
                    p.verify_persistence()
                marker.write_text('wrong')
                with self.assertRaises(RuntimeError):
                    p.verify_persistence()
                marker.write_text('retained')
                with patch.object(p, 'mounted', side_effect=RuntimeError('missing exact mount')), self.assertRaises(RuntimeError):
                    p.verify_persistence()

    def test_fresh_preparation_requires_cross_job_proof_before_allocation(self):
        args = SimpleNamespace(action='prepare', finish='2026-11-02T23:57:04Z')
        with patch.object(p.time, 'time', return_value=0), patch.object(p, 'verify_persistence', side_effect=RuntimeError('no proof')), patch.object(Path, 'mkdir') as mkdir:
            with self.assertRaisesRegex(RuntimeError, 'no proof'):
                p.prepare(args)
            mkdir.assert_not_called()
        with patch.object(p.time, 'time', return_value=p.dt.datetime(2026, 11, 2, 18, 31, tzinfo=p.dt.timezone.utc).timestamp()), patch.object(p, 'verify_persistence') as proof:
            with self.assertRaisesRegex(RuntimeError, 'latest start'):
                p.prepare(args)
            proof.assert_not_called()

    def test_new_migration_and_errors_rejected_on_reuse(self):
        for text in ('Adding transaction hashes to stored receipts, blocks 1 to 20.', 'Unhandled', 'Invalid Block', 'Exception'):
            with self.subTest(text=text), self.assertRaises(RuntimeError):
                p.check_logs(text, reused=True)
        p.check_logs('All DBs closed', reused=True)

    def test_atomic_publication_and_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'ready.json'
            p.atomic(path, {'status': 'ready'})
            self.assertEqual(json.loads(path.read_text()), {'status': 'ready'})
            self.assertFalse(path.with_suffix('.tmp').exists())
            self.assertEqual(len(p.digest(path.read_bytes())), 64)

    def test_seal_hashes_final_disk_sample_after_watchdog_stops(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, GITHUB_RUN_ID='123', GITHUB_SHA='sha'):
            parent = Path(directory).resolve()
            source = parent / 'source'
            source.mkdir()
            retained = parent / 'retained'
            retained.mkdir()
            output = parent / 'output'
            args = SimpleNamespace(action='prepare', finish='2026-11-02T23:57:04Z', output=str(output),
                                   oracle=Path(__file__).with_name('prepared-snapshot-oracle.json'))
            text = ('Adding transaction hashes to stored receipts, blocks 1 to 20.\n'
                    'Transaction hashes added to stored receipts of 20 blocks, 40 bytes added.\n'
                    'All DBs closed\nNethermind is shut down')
            def overlay(lower, root):
                for name in ('upper', 'work', 'merged'):
                    (root / name).mkdir()
            def command(*arguments, **kwargs):
                return '1 upper' if arguments[0] == 'du' else ''
            watcher = SimpleNamespace(join=lambda **kwargs: None)
            monitoring = SimpleNamespace(set=lambda: None)
            with ExitStack() as stack:
                for context in [
                    patch.object(p, 'SOURCE', source), patch.object(p, 'PARENT', parent / 'old'),
                    patch.object(p, 'RETAINED_PARENT', retained), patch.object(p.time, 'time', return_value=0),
                    patch.object(p, 'verify_persistence', return_value={'synthetic': True}),
                    patch.object(p, 'space', return_value={'available_bytes': 1024 * 2**30}),
                    patch.object(p.os, 'statvfs', return_value=SimpleNamespace(f_bavail=1024 * 2**30, f_frsize=1)),
                    patch.object(p, 'run', side_effect=command), patch.object(p, 'toy', return_value={}),
                    patch.object(p, 'overlay', side_effect=overlay),
                    patch.object(p, 'fingerprint', return_value=p.EXPECTED_SOURCE_TRIPWIRE),
                    patch.object(p, 'start_watchdog', return_value=(monitoring, watcher)),
                    patch.object(p, 'inspect', return_value={'Config': {'Image': p.BASELINE, 'Cmd': p.FLAGS, 'StopSignal': 'SIGINT'}, 'State': {'Running': True}}),
                    patch.object(p, 'logs', return_value=text), patch.object(p, 'rpc', return_value='0x1'),
                    patch.object(p, 'identity', return_value={'synthetic': True}), patch.object(p, 'validate_responses'),
                    patch.object(p, 'stop', return_value=text), patch.object(p, 'mounted', return_value={'options': 'ro'}),
                ]:
                    stack.enter_context(context)
                p.prepare(args)
            publication = json.loads((output / 'READY.json').read_text())
            manifest = publication['manifest']
            root = Path(manifest['root'])
            self.assertEqual(publication['manifest_sha256'], p.digest((root / 'ready.json').read_bytes()))
            self.assertEqual(len(json.loads((output / 'disk.json').read_text())), 2)
            for name, expected in manifest['evidence_sha256'].items():
                with self.subTest(name=name):
                    self.assertEqual(p.digest((output / name).read_bytes()), expected)
                    self.assertEqual(p.digest((root / 'evidence' / name).read_bytes()), expected)

    def test_guard_rejects_foreign_paths_and_symlink(self):
        for path in (Path('/'), Path('/mnt/sda/nethermind-flat-25490000'), p.PARENT / 'foreign', p.PARENT / '../getlogs-2'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                p.guard(path)

    def test_mount_rejects_wrong_type_writable_or_unsafe_options(self):
        for fstype, options in [('ext4', 'ro'), ('overlay', 'rw'), ('overlay', 'ro,volatile'), ('overlay', 'ro,metacopy=on'), ('overlay', 'ro,redirect_dir=on'), ('overlay', 'ro,index=on')]:
            payload = {'filesystems': [{'target': '/fixture', 'fstype': fstype, 'options': options}]}
            with self.subTest(options=options, fstype=fstype), patch.object(p, 'run', return_value=json.dumps(payload)), self.assertRaises(RuntimeError):
                p.mounted(Path('/fixture'), True)

    def test_overlay_omitted_off_options_require_verified_kernel_defaults(self):
        for options, default, accepted in [
            ('ro', 'N', True), ('ro', '0', True), ('ro', 'Y', False), ('ro', '', False),
            ('ro', 'unknown', False), ('ro,volatile', 'N', False), ('ro,index=on', 'N', False),
            ('ro,metacopy=on', 'N', False), ('ro,redirect_dir=on', 'N', False),
            ('ro,redirect_dir=follow,metacopy=off,index=off', 'N', False),
            ('ro,redirect_dir=follow,metacopy=off,index=off', 'Y', True),
            ('ro,redirect_dir=follow,metacopy=off,index=off', 'unknown', False),
            ('ro,redirect_dir=nofollow,metacopy=off,index=off', 'Y', True),
            ('ro,redirect_dir=off,metacopy=off,index=off', 'Y', True),
        ]:
            with self.subTest(options=options, default=default), patch.object(Path, 'read_text', return_value=default):
                if accepted:
                    p.overlay_off_options({'options': options})
                else:
                    with self.assertRaises(RuntimeError):
                        p.overlay_off_options({'options': options})
        with patch.object(Path, 'read_text', side_effect=FileNotFoundError), self.assertRaises(FileNotFoundError):
            p.overlay_off_options({'options': 'ro'})

    def test_verify_rejects_missing_partial_or_tampered_manifest(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(p, 'guard'):
            root = Path(directory)
            for data in (b'', b'{', b'{}', b'{"status":"incomplete"}'):
                (root / 'ready.json').write_bytes(data)
                with self.subTest(data=data), self.assertRaises((RuntimeError, KeyError, ValueError)):
                    p.verify(root, p.digest(data))
            with self.assertRaises(RuntimeError):
                p.verify(root, 'wrong-pin')

    def ready_manifest(self, root, recovering=False):
        evidence = root / ('recovery-evidence-123' if recovering else 'evidence')
        evidence.mkdir()
        p.atomic(evidence / 'disk.json', [{'sample': 1}])
        manifest = {'status': 'ready', 'host': p.socket.gethostname(), 'root': str(root), 'source': str(p.SOURCE),
                    'options': p.OPTIONS, 'flags': p.FLAGS, 'mount': {'options': 'ro'}, 'source_tripwire': 'source',
                    'prepared_tripwire': 'prepared', 'evidence_sha256': {'disk.json': p.digest((evidence / 'disk.json').read_bytes())}}
        if recovering:
            manifest['recovery'] = {'run': '123'}
        return manifest

    def test_verify_checks_retained_evidence_before_physical_probes(self):
        for recovering in (False, True):
            for change in ('none', 'changed', 'missing', 'missing-map', 'empty-map', 'traversal', 'absolute',
                           'invalid-hash', 'invalid-recovery-run', 'symlink-file', 'symlink-directory', 'non-file'):
                with self.subTest(recovering=recovering, change=change), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    manifest = self.ready_manifest(root, recovering)
                    evidence = root / ('recovery-evidence-123' if recovering else 'evidence')
                    disk = evidence / 'disk.json'
                    if change == 'changed':
                        p.atomic(disk, [{'sample': 1}, {'sample': 2}])
                    elif change in ('missing', 'symlink-file', 'non-file'):
                        disk.unlink()
                        if change == 'symlink-file':
                            (root / 'other.json').write_bytes(p.encoded([{'sample': 1}]))
                            disk.symlink_to(root / 'other.json')
                        elif change == 'non-file':
                            disk.mkdir()
                    elif change == 'missing-map':
                        del manifest['evidence_sha256']
                    elif change == 'empty-map':
                        manifest['evidence_sha256'] = {}
                    elif change in ('traversal', 'absolute'):
                        outside = root / 'other.json'
                        outside.write_bytes(disk.read_bytes())
                        manifest['evidence_sha256'] = {'../other.json' if change == 'traversal' else str(outside): p.digest(outside.read_bytes())}
                    elif change == 'invalid-hash':
                        manifest['evidence_sha256']['disk.json'] = 'not-a-sha256'
                    elif change == 'invalid-recovery-run':
                        manifest['recovery'] = {'run': '../../other'}
                    elif change == 'symlink-directory':
                        evidence.rename(root / 'other')
                        evidence.symlink_to(root / 'other', target_is_directory=True)
                    p.atomic(root / 'ready.json', manifest)
                    pin = p.digest((root / 'ready.json').read_bytes())
                    with patch.object(p, 'guard'), patch.object(p, 'mounted', return_value=manifest['mount']) as mounted, patch.object(p, 'fingerprint', side_effect=['source', 'prepared']) as fingerprint:
                        if change == 'none':
                            self.assertEqual(p.verify(root, pin), manifest)
                        else:
                            with self.assertRaisesRegex(RuntimeError, 'evidence'):
                                p.verify(root, pin)
                            mounted.assert_not_called()
                            fingerprint.assert_not_called()

    def test_verify_rejects_identity_option_mount_and_tripwire_drift(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(p, 'guard'):
            root = Path(directory)
            manifest = self.ready_manifest(root)
            for key, bad in [('status', 'incomplete'), ('host', 'other-host'), ('root', '/wrong'), ('source', '/wrong'), ('options', 'volatile'), ('flags', [])]:
                changed = dict(manifest, **{key: bad})
                p.atomic(root / 'ready.json', changed)
                with self.subTest(key=key), self.assertRaises(RuntimeError):
                    p.verify(root, p.digest((root / 'ready.json').read_bytes()))
            p.atomic(root / 'ready.json', manifest)
            pin = p.digest((root / 'ready.json').read_bytes())
            with patch.object(p, 'mounted', return_value={'options': 'different'}), self.assertRaises(RuntimeError):
                p.verify(root, pin)
            with patch.object(p, 'mounted', return_value=manifest['mount']), patch.object(p, 'fingerprint', return_value='drift'), self.assertRaises(RuntimeError):
                p.verify(root, pin)
            with patch.object(p, 'mounted', return_value=manifest['mount']), patch.object(p, 'fingerprint', side_effect=['source', 'prepared']):
                self.assertEqual(p.verify(root, pin), manifest)

    def test_shutdown_rejects_oom_kill_missing_close_and_bad_exit(self):
        for state, text in [({'Running': False, 'OOMKilled': True, 'ExitCode': 0}, 'All DBs closed'),
                            ({'Running': False, 'OOMKilled': False, 'ExitCode': 137}, 'All DBs closed'),
                            ({'Running': False, 'OOMKilled': False, 'ExitCode': 0}, 'stopping'),
                            ({'Running': True, 'OOMKilled': False, 'ExitCode': 0}, 'All DBs closed')]:
            with tempfile.TemporaryDirectory() as directory, self.subTest(state=state, text=text), patch.object(p, 'run'), patch.object(p, 'inspect', return_value={'State': state}), patch.object(p, 'logs', return_value=text), self.assertRaises(RuntimeError):
                p.stop('owned', Path(directory))

    def test_early_attempt_failure_preserves_request_and_subprocess_details(self):
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(action='recover', output=directory, root=Path('/missing'))
            error = p.subprocess.CalledProcessError(1, ['findmnt', '-M', '/missing'], output=b'query output', stderr=b'query error')
            def fail(_):
                self.assertTrue((Path(directory) / 'attempt.json').exists())
                raise error
            with patch.object(p, 'prepare', side_effect=fail), self.assertRaises(p.subprocess.CalledProcessError):
                p.execute_attempt(args)
            record = json.loads((Path(directory) / 'exception.json').read_text())
            self.assertEqual((record['status'], record['stdout'], record['stderr']), (1, 'query output', 'query error'))
            self.assertEqual(json.loads((Path(directory) / 'attempt.json').read_text())['request']['root'], '/missing')
        with self.assertRaises(ValueError):
            p.execute_attempt(SimpleNamespace(action='inventory', output=p.SOURCE / 'artifacts'))

    def test_inventory_probe_preserves_missing_mount_query_error_and_timeout(self):
        for status, stdout, stderr in [(1, b'', b''), (1, b'', b'query error'), (0, b'{"filesystems": []}', b'')]:
            with tempfile.TemporaryDirectory() as directory, self.subTest(status=status, stderr=stderr):
                with patch.object(p.subprocess, 'run', return_value=SimpleNamespace(returncode=status, stdout=stdout, stderr=stderr)):
                    result = p.inventory_probe(Path(directory), 'probe', 'findmnt', '-M', '/fixture')
                self.assertEqual((result['status'], result['stdout'], result['stderr']), (status, stdout.decode(), stderr.decode()))
        with tempfile.TemporaryDirectory() as directory, patch.object(p.subprocess, 'run', side_effect=p.subprocess.TimeoutExpired(['findmnt'], 30, output=b'partial', stderr=b'err')):
            result = p.inventory_probe(Path(directory), 'probe', 'findmnt')
            self.assertEqual((result['type'], result['stdout'], result['stderr']), ('TimeoutExpired', 'partial', 'err'))

    def test_inventory_is_read_only_and_missing_upper_is_observed_not_recreated(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            root = parent / 'quarantine'
            root.mkdir()
            evidence = parent / 'old'
            evidence.mkdir()
            (evidence / 'container-start.json').write_text('{"State":{"Pid":0}}')
            args = SimpleNamespace(action='inventory', output=str(parent / 'output'), root=root, finish='2026-10-30T00:06:04Z',
                                   recovery_evidence=evidence, oracle=parent / 'oracle')
            captured = []
            def probe(output, name, *command):
                captured.append((name, command))
                if name == 'mount-namespaces':
                    return {'status': 0, 'stdout': '{"namespaces":[{"pid":123,"ns":456,"nprocs":1}]}'}
                return {'status': 1, 'stdout': '', 'stderr': ''}
            with patch.object(p.time, 'time', return_value=0), patch.object(p, 'producer_provenance', return_value={'old_source_tripwire': 'same'}), patch.object(p, 'inventory_probe', side_effect=probe), patch.object(p, 'fingerprint', return_value='same'), patch.object(p, 'prepare') as prepare, patch.object(p, 'overlay') as overlay, patch.object(p, 'rpc') as rpc:
                p.execute_attempt(args)
                prepare.assert_not_called()
                overlay.assert_not_called()
                rpc.assert_not_called()
            self.assertEqual(list(root.iterdir()), [])
            result = json.loads((parent / 'output' / 'upper-shallow.json').read_text())
            self.assertEqual(result['unavailable']['type'], 'FileNotFoundError')
            self.assertTrue(json.loads((parent / 'output' / 'source-tripwire.json').read_text())['matches_old'])
            self.assertIn(('namespace-0-mountinfo', ('sudo', '-n', 'head', '-c', '1048577', '/proc/123/mountinfo')) if os.geteuid() != 0 else ('namespace-0-mountinfo', ('head', '-c', '1048577', '/proc/123/mountinfo')), captured)
            allowed = {'stat', 'df', 'findmnt', 'readlink', 'ps', 'sudo', 'head', 'lsns', 'docker'}
            self.assertTrue(all(command[0] in allowed for _, command in captured))
            self.assertEqual([command[1] for _, command in captured if command[0] == 'docker'], ['inspect', 'ps'])

    def test_recovery_requires_budget_and_never_falls_back_to_prepare(self):
        p.budget(0, 7800, gate=3600)
        with self.assertRaises(RuntimeError):
            p.budget(0, 7799, gate=3600)
        args = SimpleNamespace(action='recover', finish='2026-10-30T00:06:04Z', root=p.PARENT / 'getlogs-40815701914')
        with patch.object(p.time, 'time', return_value=0), patch.object(Path, 'mkdir'), patch.object(Path, 'is_dir', return_value=False):
            with self.assertRaisesRegex(RuntimeError, 'quarantine is missing'):
                p.prepare(args)

    def test_recovery_rejects_foreign_host_ready_or_unpinned_evidence(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(p, 'guard'):
            parent = Path(directory)
            root = parent / 'getlogs-40815701914'
            root.mkdir()
            evidence = parent / 'evidence'
            evidence.mkdir()
            with patch.object(p, 'PARENT', parent):
                with patch.object(p.socket, 'gethostname', return_value='wrong'), self.assertRaisesRegex(RuntimeError, 'root/host'):
                    p.recovery_preflight(root, evidence, parent / 'oracle')
                with patch.object(p.socket, 'gethostname', return_value='core-ethereum-mainnet-snapshots-0'):
                    with self.assertRaisesRegex(RuntimeError, 'artifact contents'):
                        p.recovery_preflight(root, evidence, parent / 'oracle')
                    (root / 'ready.json').write_text('{}')
                    with self.assertRaisesRegex(RuntimeError, 'overwrite'):
                        p.recovery_preflight(root, evidence, parent / 'oracle')

    def test_stop_and_stopped_gate_share_normal_shutdown_predicate(self):
        observed = {'State': {'Running': False, 'OOMKilled': False, 'Error': '', 'ExitCode': 130},
                    'Config': {'Image': p.BASELINE, 'Cmd': p.FLAGS, 'StopSignal': 'SIGINT'}}
        with tempfile.TemporaryDirectory() as directory, patch.object(p, 'inspect', return_value=observed), patch.object(p, 'logs', return_value='All DBs closed\nNethermind is shut down'), patch.object(p, 'run'), patch.object(p, 'normal_shutdown', wraps=p.normal_shutdown) as shutdown:
            p.stop('owned', Path(directory))
            args = SimpleNamespace(root=Path('/fixture'), manifest_sha256='pin', output=directory, container='owned', image=p.BASELINE, action='stopped')
            with patch.object(p, 'verify', return_value={'allowed_images': [p.BASELINE]}):
                p.gate(args)
            self.assertEqual(shutdown.call_count, 2)

    def test_normal_shutdown_accepts_only_closed_clean_exit_or_configured_sigint(self):
        # State and signal from preparation 40815701914, artifact 12881330426.
        observed = {'State': {'Running': False, 'OOMKilled': False, 'Error': '', 'ExitCode': 130},
                    'Config': {'StopSignal': 'SIGINT'}}
        text = 'All DBs closed\nNethermind is shut down'
        for code, signal, running, oom, error, log, accepted in [
            (130, 'SIGINT', False, False, '', text, True),
            (0, '', False, False, '', text, True),
            (137, 'SIGINT', False, False, '', text, False),
            (143, 'SIGINT', False, False, '', text, False),
            (130, 'SIGTERM', False, False, '', text, False),
            (130, '', False, False, '', text, False),
            (130, 'SIGINT', True, False, '', text, False),
            (130, 'SIGINT', False, True, '', text, False),
            (130, 'SIGINT', False, False, 'failure', text, False),
            (130, 'SIGINT', False, False, '', 'All DBs closed', False),
            (130, 'SIGINT', False, False, '', 'Nethermind is shut down', False),
            (130, 'SIGINT', False, False, '', '', False),
            *[(130, 'SIGINT', False, False, '', text + bad, False) for bad in ('Exception', 'Fatal', 'Invalid Block')],
        ]:
            observed['State'].update(ExitCode=code, Running=running, OOMKilled=oom, Error=error)
            observed['Config']['StopSignal'] = signal
            with self.subTest(code=code, signal=signal, running=running, oom=oom, error=error, log=log):
                if accepted:
                    p.normal_shutdown(observed, log)
                else:
                    with self.assertRaises(RuntimeError):
                        p.normal_shutdown(observed, log)

    def test_oracle_has_100_cases_and_92_positive(self):
        oracle = json.loads(Path(__file__).with_name('prepared-snapshot-oracle.json').read_text())
        self.assertEqual(len(oracle['cases']), 100)
        self.assertEqual(sum(case['expectation']['exact_count'] > 0 for case in oracle['cases']), 92)

    def test_response_validation_rejects_missing_cases_and_wrong_results(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'responses.json'
            with self.assertRaises(RuntimeError):
                p.validate_responses({'cases': []}, output)
            oracle = {'cases': [{'call': {'name': 'positive', 'method': 'eth_getLogs', 'params': []}, 'expectation': {'exact_count': 1, 'ordered_result_sha256': p.digest(p.encoded([1]))}}] * 100}
            with patch.object(p, 'rpc', return_value=[]), self.assertRaises(RuntimeError):
                p.validate_responses(oracle, output)
            with patch.object(p, 'rpc', return_value=[1]):
                p.validate_responses(oracle, output)
                self.assertEqual(len(json.loads(output.read_text())), 100)

    def test_running_gate_does_not_warm_measured_process_but_qualification_checks_100(self):
        for action in ('running', 'gate'):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory).resolve() / 'fixture'
                child = Path(directory).resolve() / 'child'
                args = SimpleNamespace(root=root, manifest_sha256='pin', output=directory, container='owned', image=p.BASELINE,
                                       action=action, child_source=str(child), oracle=str(Path(__file__).with_name('prepared-snapshot-oracle.json')))
                manifest = {'allowed_images': [p.BASELINE, p.CANDIDATE], 'oracle_sha256': p.digest(Path(args.oracle).read_bytes())}
                observed = {'Config': {'Image': p.BASELINE, 'Cmd': p.FLAGS}, 'State': {'Running': True},
                            'Mounts': [{'Destination': '/execution-data', 'Source': str(child), 'RW': True}]}
                with patch.object(p, 'verify', return_value=manifest), patch.object(p, 'inspect', return_value=observed), patch.object(p, 'logs', return_value='ready'), patch.object(p, 'mounted', return_value={'options': f'lowerdir={root}/merged'}), patch.object(p, 'identity', return_value={'chain_id': 1}), patch.object(p, 'validate_responses') as responses:
                    p.gate(args)
                    self.assertEqual(responses.call_count, int(action == 'gate'))
                for field in ('Image', 'Cmd'):
                    changed = dict(observed, Config=dict(observed['Config'], **{field: 'wrong'}))
                    with self.subTest(action=action, field=field), patch.object(p, 'verify', return_value=manifest), patch.object(p, 'inspect', return_value=changed), self.assertRaises(RuntimeError):
                        p.gate(args)
                with patch.object(p, 'verify', return_value=manifest), patch.object(p, 'inspect', return_value=observed), patch.object(p, 'logs', return_value='Transaction hashes added to stored receipts of 1 blocks, 2 bytes added.'), self.assertRaises(RuntimeError):
                    p.gate(args)
                with patch.object(p, 'verify', return_value=manifest), patch.object(p, 'inspect', return_value=observed), patch.object(p, 'logs', return_value='ready'), patch.object(p, 'mounted', return_value={'options': 'lowerdir=/wrong'}), self.assertRaises(RuntimeError):
                    p.gate(args)

    @unittest.skipUnless(os.environ.get('RUN_OVERLAY_TESTS') == '1', 'real kernel fixture enabled only on hosted Actions')
    def test_real_kernel_durable_overlay(self):
        with tempfile.TemporaryDirectory(prefix='prepared-kernel-') as directory:
            root = Path(directory) / 'toy'
            root.mkdir()
            result = p.toy(root)
            self.assertTrue(all(result[key] for key in ('whiteout', 'opaque_directory', 'replacement', 'remount', 'nested_isolation')))
            self.assertEqual(result['mount']['fstype'], 'overlay')


if __name__ == '__main__':
    unittest.main(verbosity=2)
