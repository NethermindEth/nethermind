# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import sequential_driver as driver


class CleanGuardTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        environment = patch.dict(os.environ, {'EXPB_DATA_DIR': str(self.root), 'DOCKER_BIN': 'docker-fixture'})
        environment.start()
        self.addCleanup(environment.stop)

    def test_empty_state_uses_only_read_only_inventory(self):
        with patch.object(driver.subprocess, 'check_output', return_value='') as execute, \
                patch.object(driver.Path, 'read_text', return_value=''):
            driver.verify_clean({'paths': {'work': 'work'}})
        self.assertEqual([call.args[0] for call in execute.call_args_list], [
            ['docker-fixture', 'container', 'ps', '-aq', '--filter', 'label=expb'],
            ['docker-fixture', 'network', 'ls', '-q', '--filter', 'label=expb'],
        ])

    def test_running_or_stopped_container_and_network_are_preserved(self):
        for responses, kind in [(['foreign-container'], 'containers'), (['', 'foreign-network'], 'networks')]:
            with self.subTest(kind=kind), \
                    patch.object(driver.subprocess, 'check_output', side_effect=responses) as execute, \
                    self.assertRaisesRegex(RuntimeError, 'benchmark ' + kind + ' remain'):
                driver.verify_clean({'paths': {'work': 'work'}})
            self.assertEqual(execute.call_count, len(responses))
            self.assertTrue(all(call.args[0][1:3] in (['container', 'ps'], ['network', 'ls'])
                                for call in execute.call_args_list))

    def test_inventory_or_mount_read_failure_is_not_clean(self):
        with patch.object(driver.subprocess, 'check_output', side_effect=subprocess.CalledProcessError(1, ['fixture'])), \
                self.assertRaises(subprocess.CalledProcessError):
            driver.verify_clean({})
        with patch.object(driver.subprocess, 'check_output', return_value=''), \
                patch.object(driver.Path, 'read_text', side_effect=PermissionError('fixture')), \
                self.assertRaises(PermissionError):
            driver.verify_clean({})

    def test_existing_overlay_and_scratch_evidence_are_preserved(self):
        evidence = self.root / 'work' / 'upper' / 'evidence'
        evidence.parent.mkdir(parents=True)
        evidence.write_bytes(b'preserve')
        mount = str(self.root / 'mounted').replace(' ', r'\040')
        for mounts, message in [(f'overlay {mount} overlay rw 0 0\n', 'overlay remains'), ('', 'scratch is not empty')]:
            with self.subTest(mounts=bool(mounts)), \
                    patch.object(driver.subprocess, 'check_output', return_value=''), \
                    patch.object(driver.Path, 'read_text', return_value=mounts), \
                    self.assertRaisesRegex(RuntimeError, message):
                driver.verify_clean({'paths': {'work': 'work'}})
            self.assertEqual(evidence.read_bytes(), b'preserve')


if __name__ == '__main__':
    unittest.main()
