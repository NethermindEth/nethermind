# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Offline regression coverage for foreign benchmark refusal and owned unmount."""
import subprocess
import unittest
from unittest.mock import patch

import test_rpc_private_audit as audit_tests

module = audit_tests.module


class OwnershipCorrectionsTests(unittest.TestCase):
    setUp = audit_tests.AuditTests.setUp
    node_fixture = audit_tests.OwnershipTests.node_fixture

    def test_preflight_refuses_expb_name_or_label_before_owner_registration(self):
        (self.storage / "owner.json").unlink()
        sentinel = self.base / "foreign-expb-evidence"
        sentinel.write_text("FOREIGN")
        for outputs in (["expb-foreign"], ["unrelated-name", "foreign-labelled-id"]):
            with self.subTest(outputs=outputs), patch.object(module, "command_output", side_effect=outputs) as command, \
                 patch.object(module, "mount_records", return_value={}), patch.object(module.subprocess, "run") as child:
                with self.assertRaisesRegex(ValueError, "FOREIGN"):
                    module.preflight()
                self.assertEqual(command.call_args_list[0].args[0], ["docker", "ps", "-a", "--format", "{{.Names}}"])
                if len(outputs) == 2:
                    self.assertEqual(command.call_args_list[1].args[0], ["docker", "ps", "-aq", "--filter", "label=expb"])
                child.assert_not_called()
                self.assertFalse((self.storage / "owner.json").exists())
                self.assertFalse((self.storage / "cpu-apply-attempt.json").exists())
                self.assertEqual(sentinel.read_text(), "FOREIGN")

    def test_expb_label_lookup_failure_is_not_assumed_empty(self):
        (self.storage / "owner.json").unlink()
        error = subprocess.CalledProcessError(1, ["docker", "ps"])
        with patch.object(module, "command_output", side_effect=["unrelated", error]) as command:
            with self.assertRaises(subprocess.CalledProcessError):
                module.preflight()
            self.assertEqual(command.call_count, 2)
        self.assertFalse((self.storage / "owner.json").exists())

    def mount_fixture(self):
        directory, scratch, _ = self.node_fixture()
        target = scratch / "merged"
        target.mkdir(parents=True)
        checkpoint = {str(target): "101 10 0:11 / " + str(target) + " rw - overlay overlay rw"}
        with patch.object(module, "mount_records", return_value=checkpoint):
            module.node_mounted(directory, target)
        return directory, target, checkpoint

    def test_owned_unmount_uses_direct_root_or_sudo_and_checks_absence(self):
        directory, target, checkpoint = self.mount_fixture()
        for uid, expected in ((0, ["umount", "--", str(target)]), (1001, ["sudo", "umount", "--", str(target)])):
            with self.subTest(uid=uid), patch.object(module.os, "geteuid", return_value=uid, create=True), \
                 patch.object(module, "owned_cid", return_value=None), patch.object(module, "name_available"), \
                 patch.object(module, "mount_records", side_effect=[checkpoint, {}]) as mounts, \
                 patch.object(module, "command_output") as command:
                module.node_unmount(directory)
                command.assert_called_once_with(expected)
                self.assertEqual(mounts.call_count, 2)

    def test_failed_sudo_unmount_keeps_checkpoint_without_retry_or_deletion(self):
        directory, target, checkpoint = self.mount_fixture()
        marker = directory / "node-mount.json"
        original = marker.read_bytes()
        error = subprocess.CalledProcessError(1, ["sudo", "umount"])
        with patch.object(module.os, "geteuid", return_value=1001, create=True), \
             patch.object(module, "owned_cid", return_value=None), patch.object(module, "name_available"), \
             patch.object(module, "mount_records", return_value=checkpoint), \
             patch.object(module, "command_output", side_effect=error) as command:
            with self.assertRaises(subprocess.CalledProcessError):
                module.node_unmount(directory)
            command.assert_called_once_with(["sudo", "umount", "--", str(target)])
        self.assertEqual(marker.read_bytes(), original)
        self.assertTrue(target.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
