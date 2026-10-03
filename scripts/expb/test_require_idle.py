# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import subprocess
import tempfile
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import require_idle as guard


class IdleGuardTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.evidence = self.root / 'preserve'
        self.evidence.write_bytes(b'unchanged')
        self.empty_mounts = f'rootfs {self.root.anchor} rootfs rw 0 0\n'

    def mount(self, target, filesystem='overlay'):
        target = str(target).replace('\\', r'\134').replace(' ', r'\040')
        return f'fixture {target} {filesystem} rw 0 0\n'

    def assert_read_only(self, calls):
        for call in calls:
            command = call.args[0]
            self.assertIn(command[1:-2], (['ps', '-aq'], ['network', 'ls', '-q']))
            self.assertEqual(command[-2], '--filter')
            self.assertIn(command[-1], guard.FILTERS)
        self.assertEqual(self.evidence.read_bytes(), b'unchanged')

    def test_idle_inventory_uses_only_read_only_commands(self):
        with patch.object(guard.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='')) as execute, \
                patch.object(guard.Path, 'read_text', return_value=self.empty_mounts):
            guard.require_idle(str(self.root))
        self.assertEqual(len(execute.call_args_list), 2 * len(guard.FILTERS))
        self.assert_read_only(execute.call_args_list)

    def test_every_container_and_network_filter_refuses_without_mutation(self):
        for blocked in range(2 * len(guard.FILTERS)):
            with self.subTest(blocked=blocked):
                responses = [SimpleNamespace(returncode=0, stdout='')] * blocked + [SimpleNamespace(returncode=0, stdout='foreign-resource')]
                with patch.object(guard.subprocess, 'run', side_effect=responses) as execute, \
                        patch.object(guard.Path, 'read_text') as mounts, \
                        self.assertRaises(guard.IdleCheckError):
                    guard.require_idle(str(self.root))
                mounts.assert_not_called()
                self.assertEqual(execute.call_count, blocked + 1)
                self.assert_read_only(execute.call_args_list)

    def test_inventory_failures_are_not_idle(self):
        for failure in (SimpleNamespace(returncode=1, stdout=''), OSError('fixture'), subprocess.TimeoutExpired('fixture', 30)):
            with self.subTest(failure=type(failure).__name__):
                settings = {'side_effect': failure} if isinstance(failure, Exception) else {'return_value': failure}
                with patch.object(guard.subprocess, 'run', **settings) as execute, self.assertRaises(guard.IdleCheckError):
                    guard.require_idle(str(self.root))
                self.assert_read_only(execute.call_args_list)

    def test_mount_read_failure_is_not_idle(self):
        with patch.object(guard.subprocess, 'run', return_value=SimpleNamespace(returncode=0, stdout='')) as execute, \
                patch.object(guard.Path, 'read_text', side_effect=PermissionError('fixture')), self.assertRaises(OSError):
            guard.require_idle(str(self.root))
        self.assert_read_only(execute.call_args_list)

    def test_overlay_at_root_or_any_descendant_is_preserved(self):
        for target in (self.root, self.root / 'arbitrary-leaf', self.root / 'with space' / 'nested'):
            with self.subTest(target=target), self.assertRaises(guard.IdleCheckError):
                guard.check_mounts(self.mount(target), self.root)
            self.assertEqual(self.evidence.read_bytes(), b'unchanged')

    def test_unrelated_overlay_and_non_overlay_mount_are_allowed(self):
        guard.check_mounts(self.mount(self.root.parent / (self.root.name + '-other')) + self.mount(self.root, 'ext4'), self.root)

    def test_empty_malformed_or_relative_mount_inventory_refuses(self):
        for text in ('', 'bad row', 'fixture relative overlay rw 0 0\n', 'fixture /bad\\999 overlay rw 0 0\n'):
            with self.subTest(text=text), self.assertRaises(guard.IdleCheckError):
                guard.check_mounts(text, self.root)

    def test_missing_relative_or_root_data_directory_refuses_before_docker(self):
        for value in ('', 'relative', str(self.root / 'missing'), self.root.anchor):
            with self.subTest(value=value), patch.object(guard.subprocess, 'run') as execute, self.assertRaises((guard.IdleCheckError, OSError)):
                guard.require_idle(value)
            execute.assert_not_called()


if __name__ == '__main__':
    unittest.main()
