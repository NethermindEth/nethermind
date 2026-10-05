# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import json
import hashlib
import tempfile
import signal
import subprocess
from pathlib import Path
import unittest
from unittest.mock import patch

from native_capability import Capability


class SimulatedCapability(Capability):
    def __init__(self, root, failures):
        env = {"RPC_PRIVATE_STORAGE_ROOT": str(root), "SCRATCH_ROOT": str(root / "scratch"),
               "OUT_DIR": str(root / "out"), "STATE_DIR": str(root / "state/nativecap"),
               "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "1", "CONTAINER_NAME": "rpcbench-sweep-nativecap-123"}
        super().__init__(root / "repository", env)
        self.failures = failures
        self.calls = []

    def run(self, name, arguments, **options):
        self.calls.append(name)
        self.outcomes[name] = 1 if name in self.failures or name == "finalize" else 0
        root = Path(self.environment["RPC_PRIVATE_STORAGE_ROOT"])
        if name == "finalize":
            private = root / "spin-diagnostic-123-1"
            private.mkdir()
            (private / "audit-status.json").write_text('{"quality":"FAIL"}')
            if "archive" not in self.failures:
                archive = root / "rpc-private-public-123-1/encrypted"
                archive.mkdir(parents=True)
                (archive / "raw.tar.gz.cms").write_bytes(b"encrypted fixture")
                (archive / "archive.json").write_text(json.dumps({"status": "ENCRYPTED_NOT_DECRYPTION_VERIFIED",
                    "ciphertext": {"file": "raw.tar.gz.cms", "bytes": 17, "sha256": hashlib.sha256(b"encrypted fixture").hexdigest()}}))
        if self.outcomes[name]:
            raise ValueError(name)

    def restore(self):
        self.run("cpu-restore", [])

    def verify_cpu_cap(self):
        self.run("verify-cpu-cap", [])

    def verify_preload(self):
        self.run("verify-preload", [])


class LifecycleTests(unittest.TestCase):
    def execute(self, failures):
        with tempfile.TemporaryDirectory() as temporary:
            capability = SimulatedCapability(Path(temporary), failures)
            with patch("builtins.print"):
                result = capability.execute()
            record = json.loads((Path(temporary) / "native-capability.json").read_text())
            return result, capability.calls, record

    def test_one_arm_capability_pass_keeps_four_arm_quality_failed(self):
        code, calls, record = self.execute(set())
        self.assertEqual(code, 0)
        self.assertEqual(record["full_abba_quality"], "FAIL")
        self.assertFalse(record["timing_result"])
        self.assertTrue(record["raw_sources_retained"])
        self.assertEqual(calls[-3:], ["teardown", "cpu-restore", "finalize"])
        self.assertEqual(calls.count("main"), 1)
        self.assertNotIn("cleanup-sources", calls)

    def test_each_load_or_lifecycle_failure_preserves_failed_attempt_and_restores(self):
        for failure in ("cpu-attempt", "cpu-apply", "verify-cpu-cap", "start", "node-cid", "warm", "main", "stop", "node", "teardown", "cpu-restore", "archive"):
            with self.subTest(failure=failure):
                code, calls, record = self.execute({failure})
                self.assertEqual(code, 1)
                self.assertEqual(record["capability"], "FAIL")
                self.assertIn("teardown", calls)
                self.assertIn("cpu-restore", calls)
                self.assertIn("finalize", calls)
                if failure == "warm":
                    self.assertNotIn("main", calls)

    def test_foreign_preflight_failure_never_starts_or_cleans_resources(self):
        code, calls, record = self.execute({"preflight"})
        self.assertEqual(code, 1)
        self.assertEqual(calls, ["preflight"])
        self.assertFalse(record["archive_ready"])

    def test_prepare_failure_still_restores_but_cannot_finalize_unknown_context(self):
        code, calls, _ = self.execute({"prepare"})
        self.assertEqual(code, 1)
        self.assertIn("teardown", calls)
        self.assertIn("cpu-restore", calls)
        self.assertNotIn("finalize", calls)

    def test_unsupported_platform_cannot_mutate_native_resources(self):
        from native_capability import checked_environment
        with patch("native_capability.sys.platform", "win32"):
            with self.assertRaisesRegex(ValueError, "LINUX_REQUIRED"):
                checked_environment(Path("unused"))

    def test_timeout_waits_for_command_closure_without_signalling_and_closes_pidfd(self):
        with tempfile.TemporaryDirectory() as temporary:
            capability = Capability(Path(temporary), {"RPC_PRIVATE_STORAGE_ROOT": temporary})
            child = unittest.mock.Mock(pid=987654)
            child.wait.side_effect = [subprocess.TimeoutExpired("fixture", 1), 0]
            with patch("native_capability.subprocess.Popen", return_value=child), \
                 patch("native_capability.pidfd_compat.pidfd_open", return_value=91) as opened, \
                 patch("native_capability.pidfd_compat.pidfd_send_signal") as signalled, \
                 patch("native_capability.os.close") as closed:
                with self.assertRaises(ValueError):
                    capability.run("fixture", ["unused"], timeout=1)
            opened.assert_called_once_with(987654)
            signalled.assert_called_once_with(91, 0)
            closed.assert_called_once_with(91)
            self.assertEqual(capability.outcomes["fixture"], 124)
            self.assertFalse(capability.ownership_unknown)

    def test_unclosed_command_holds_resources_without_restore_or_archive(self):
        with tempfile.TemporaryDirectory() as temporary:
            capability = Capability(Path(temporary), {"RPC_PRIVATE_STORAGE_ROOT": temporary})
            child = unittest.mock.Mock(pid=987654)
            child.wait.side_effect = subprocess.TimeoutExpired("fixture", 1)
            with patch("native_capability.subprocess.Popen", return_value=child), \
                 patch("native_capability.pidfd_compat.pidfd_open", return_value=91), \
                 patch("native_capability.pidfd_compat.pidfd_send_signal") as signalled, \
                 patch("native_capability.os.close"):
                with self.assertRaises(ValueError):
                    capability.run("fixture", ["unused"], timeout=1)
            self.assertTrue(capability.ownership_unknown)
            signalled.assert_called_once_with(91, 0)
            simulated = SimulatedCapability(Path(temporary), {"warm"})
            simulated.ownership_unknown = True
            with patch("builtins.print"):
                self.assertEqual(simulated.execute(), 1)
            self.assertNotIn("teardown", simulated.calls)
            self.assertNotIn("cpu-restore", simulated.calls)
            self.assertNotIn("finalize", simulated.calls)

    def test_abnormal_completion_or_interrupted_spawn_persist_hold(self):
        for mode in ("negative", "nonzero", "spawn-interrupt", "pidfd-failure"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as temporary:
                capability = Capability(Path(temporary), {"RPC_PRIVATE_STORAGE_ROOT": temporary})
                child = unittest.mock.Mock(pid=987654)
                child.wait.return_value = -15 if mode == "negative" else 1
                with patch("native_capability.subprocess.Popen", side_effect=KeyboardInterrupt() if mode == "spawn-interrupt" else None,
                           return_value=child), \
                     patch("native_capability.pidfd_compat.pidfd_open", side_effect=OSError() if mode == "pidfd-failure" else None,
                           return_value=91), \
                     patch("native_capability.pidfd_compat.pidfd_send_signal") as signalled, \
                     patch("native_capability.os.close"):
                    with self.assertRaises((ValueError, KeyboardInterrupt)):
                        capability.run("fixture", ["unused"])
                self.assertTrue(capability.ownership_unknown)
                self.assertTrue((Path(temporary) / "native-ownership-hold.json").is_file())
                if mode == "spawn-interrupt":
                    signalled.assert_not_called()
                with self.assertRaisesRegex(ValueError, "OWNERSHIP_HOLD"):
                    capability.run("forbidden-next-cell", ["unused"])

    def test_keyboard_interrupt_cooperatively_closes_command_without_kill(self):
        with tempfile.TemporaryDirectory() as temporary:
            capability = Capability(Path(temporary), {"RPC_PRIVATE_STORAGE_ROOT": temporary})
            child = unittest.mock.Mock(pid=987654)
            child.wait.side_effect = [KeyboardInterrupt(), 0]
            with patch("native_capability.subprocess.Popen", return_value=child), \
                 patch("native_capability.pidfd_compat.pidfd_open", return_value=91), \
                 patch("native_capability.pidfd_compat.pidfd_send_signal") as signalled, \
                 patch("native_capability.os.close"):
                with self.assertRaises(ValueError):
                    capability.run("fixture", ["unused"])
            self.assertFalse(capability.ownership_unknown)
            signalled.assert_called_once_with(91, 0)

    def test_cpu_verification_refuses_best_effort_cap_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            state = root / "cpu-state"
            state.mkdir()
            governor = root / "scaling_governor"
            cap = root / "scaling_max_freq"
            governor.write_text("performance")
            cap.write_text("3800000")
            (state / "cpu-sysfs.orig").write_text(str(governor) + "\tpowersave\n" + str(cap) + "\t5000000\n")
            capability = Capability(root, {"SCRATCH_ROOT": temporary})
            capability.verify_cpu_cap()
            cap.write_text("5000000")
            with self.assertRaisesRegex(ValueError, "CPU_CAP_NOT_APPLIED"):
                capability.verify_cpu_cap()


if __name__ == "__main__":
    unittest.main()
