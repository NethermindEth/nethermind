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

    @unittest.skipUnless(os.environ.get('RUN_OVERLAY_TESTS') == '1', 'real kernel fixture enabled only on hosted Actions')
    def test_real_kernel_durable_overlay(self):
        with tempfile.TemporaryDirectory(prefix='prepared-kernel-') as directory:
            root = Path(directory) / 'toy'
            root.mkdir()
            self.assertTrue(all(p.toy(root).values()))


if __name__ == '__main__':
    unittest.main(verbosity=2)
