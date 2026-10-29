#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
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
        with patch.object(p.time, 'time', return_value=latest), patch.object(Path, 'mkdir', side_effect=RuntimeError('allocation reached')):
            with self.assertRaisesRegex(RuntimeError, '^allocation reached$'):
                p.prepare(args)

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

    def test_verify_rejects_identity_option_mount_and_tripwire_drift(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(p, 'guard'):
            root = Path(directory)
            manifest = {'status': 'ready', 'host': p.socket.gethostname(), 'root': str(root), 'source': str(p.SOURCE),
                        'options': p.OPTIONS, 'flags': p.FLAGS, 'mount': {'options': 'ro'}, 'source_tripwire': 'source', 'prepared_tripwire': 'prepared'}
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
