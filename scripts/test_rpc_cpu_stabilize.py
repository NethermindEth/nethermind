# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Run the actual CPU helper only against temporary fake sysfs."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import test_rpc_private_audit as audit_tests

SCRIPTS = audit_tests.SCRIPTS
module = audit_tests.module

FIXTURE_LIBRARY = r'''log() { :; }
die() { exit 1; }
mkdir() {
  [[ "$FIXTURE_MODE" != mkdir-fail ]] || return 1
  command mkdir "$@" || return
  [[ "$FIXTURE_MODE" != create-fail ]] || SAVED="$STATE_DIR/missing/journal"
}
cat() {
  if [[ "$1" == */scaling_governor ]]; then
    [[ "$FIXTURE_MODE" != read-fail ]] || return 1
    [[ "$FIXTURE_MODE" != empty-read ]] || return 0
  fi
  command cat "$@"
}
save_calls=0
printf() {
  if [[ "$1" == '%s\t%s\n' ]]; then
    save_calls=$((save_calls + 1))
    [[ "$FIXTURE_MODE" != save-fail ]] || return 1
    [[ "$FIXTURE_MODE" != late-save-fail || "$save_calls" -lt 3 ]] || return 1
    if [[ "$FIXTURE_MODE" == partial-save-fail && "$save_calls" == 3 ]]; then
      builtin printf '%s\t%s' "$2" "40000"
      return 1
    fi
  fi
  builtin printf "$@"
}
as_root() {
  "$@"
  local status=$?
  builtin printf 'write\n' >> "$FIXTURE_WRITES"
  if [[ "$FIXTURE_MODE" == cancel-after-first-write ]]; then kill -TERM "$$"; fi
  return "$status"
}
'''


class CpuHelperTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent)
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.script = self.root / "scripts/rpc-bench/cpu-stabilize.sh"
        self.script.parent.mkdir(parents=True)
        self.script.write_bytes((SCRIPTS / "cpu-stabilize.sh").read_bytes())
        self.script.chmod(0o755)
        self.script.with_name("lib.sh").write_text(FIXTURE_LIBRARY, encoding="utf-8", newline="\n")
        self.fake = self.root / "fake-sysfs"
        self.originals = {
            self.fake / "intel_pstate/no_turbo": "0",
            self.fake / "cpufreq/policy0/scaling_governor": "powersave",
            self.fake / "cpufreq/policy0/scaling_max_freq": "4000000",
        }
        for path, value in self.originals.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(value)
        self.state = self.root / "state"
        self.writes = self.root / "writes"
        self.env = dict(os.environ, CPU_SYSFS=self.fake.as_posix(), STATE_DIR=self.state.as_posix(),
                        CPU_MAX_FREQ_KHZ="2000000", FIXTURE_WRITES=self.writes.as_posix())

    def run_helper(self, command, mode="normal"):
        return subprocess.run([audit_tests.bash_executable(), "-c", 'export PATH=/usr/bin:/bin; exec bash "$1" "$2"',
                               "cpu-fixture", self.script.as_posix(), command],
                              env=self.env | {"FIXTURE_MODE": mode}, capture_output=True, timeout=20)

    def test_all_recording_failures_abort_before_any_sysfs_write(self):
        for mode in ("mkdir-fail", "create-fail", "read-fail", "empty-read", "save-fail", "late-save-fail"):
            with self.subTest(mode=mode):
                # Each failed apply starts with an independent journal, as a fresh private run does.
                self.state = self.root / mode
                self.env["STATE_DIR"] = self.state.as_posix()
                result = self.run_helper("apply", mode)
                self.assertNotEqual(result.returncode, 0, result.stderr)
                self.assertFalse(self.writes.exists())
                self.assertTrue(all(path.read_text() == value for path, value in self.originals.items()))

    def test_successful_apply_has_complete_originals_and_restores_all_values(self):
        result = self.run_helper("apply")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len((self.state / "cpu-sysfs.orig").read_text().splitlines()), 3)
        self.assertEqual([path.read_text() for path in self.originals], ["1", "performance", "2000000"])
        result = self.run_helper("restore")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(all(path.read_text() == value for path, value in self.originals.items()))
        self.assertFalse((self.state / "cpu-sysfs.orig").exists())

    def test_partial_final_journal_line_cannot_restore_a_truncated_frequency(self):
        result = self.run_helper("apply", "partial-save-fail")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.writes.exists())
        journal = (self.state / "cpu-sysfs.orig").read_bytes()
        self.assertTrue(journal.endswith(b"\t40000"))
        self.assertEqual(journal.count(b"\n"), 2)
        restored = self.run_helper("restore")
        self.assertEqual(restored.returncode, 0, restored.stderr)
        self.assertEqual(self.writes.read_text().splitlines(), ["write", "write"])
        self.assertTrue(all(path.read_text() == value for path, value in self.originals.items()))

    def workflow_block(self, name):
        workflow = (SCRIPTS.parent.parent / ".github/workflows/run-rpc-benchmarks.yml").read_text(encoding="utf-8")
        step = workflow.split("      - name: " + name + "\n", 1)[1].split("      - name:", 1)[0]
        block = "\n".join(line[10:] for line in step.split("        run: |\n", 1)[1].splitlines())
        return step, block.replace("${{ needs.resolve.outputs.cpu_stabilize }}", "true")

    def run_workflow_block(self, name, mode="normal", marker="valid"):
        _, block = self.workflow_block(name)
        prefix = 'set -e\nexport PATH=/usr/bin:/bin\nRPC_PRIVATE_AUDIT=true\n'
        prefix += 'python3() { '
        if marker == "fail":
            prefix += 'return 1;'
        elif marker == "missing":
            prefix += 'return 0;'
        else:
            prefix += 'builtin printf "%s\\n" "$FIXTURE_PRIVATE_STATE";'
        prefix += ' }\n'
        return subprocess.run([audit_tests.bash_executable(), "-c", prefix + block], cwd=self.root,
                              env=self.env | {"FIXTURE_MODE": mode, "FIXTURE_PRIVATE_STATE": self.state.as_posix()},
                              capture_output=True, timeout=20)

    def test_cancelled_apply_restores_through_actual_always_wrapper(self):
        applied = self.run_workflow_block("Stabilize CPU frequency", mode="cancel-after-first-write")
        self.assertNotEqual(applied.returncode, 0)
        self.assertEqual([path.read_text() for path in self.originals], ["1", "powersave", "4000000"])
        self.assertEqual(len((self.state / "cpu-sysfs.orig").read_text().splitlines()), 3)
        step, _ = self.workflow_block("Restore CPU frequency")
        condition = next(line.strip() for line in step.splitlines() if line.strip().startswith("if:"))
        self.assertIn("always()", condition)
        self.assertIn("steps.private-preflight.outcome == 'success'", condition)
        self.assertNotIn("steps.cpu-apply.outcome", condition)
        restored = self.run_workflow_block("Restore CPU frequency")
        self.assertEqual(restored.returncode, 0, restored.stderr)
        self.assertTrue(all(path.read_text() == value for path, value in self.originals.items()))

    def test_marker_failure_prevents_apply_and_missing_attempt_never_uses_fallback_state(self):
        result = self.run_workflow_block("Stabilize CPU frequency", marker="fail")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.writes.exists())
        self.state.mkdir()
        journal = self.state / "cpu-sysfs.orig"
        journal.write_text("FOREIGN_FALLBACK_SENTINEL")
        for marker, expected in (("missing", 0), ("fail", 1)):
            with self.subTest(marker=marker):
                result = self.run_workflow_block("Restore CPU frequency", marker=marker)
                self.assertEqual(result.returncode, expected, result.stderr)
                self.assertFalse(self.writes.exists())
                self.assertEqual(journal.read_text(), "FOREIGN_FALLBACK_SENTINEL")


class CpuAttemptTests(unittest.TestCase):
    setUp = audit_tests.AuditTests.setUp

    def test_checked_marker_restores_only_its_private_state_and_refuses_duplicate_apply(self):
        self.assertEqual(module.cpu_restore_state(), "")
        state = module.cpu_apply_attempt()
        self.assertEqual(state, str(self.storage / "scratch/cpu-state"))
        self.assertEqual(module.cpu_restore_state(), state)
        with self.assertRaises(FileExistsError):
            module.cpu_apply_attempt()

    def test_marker_flush_failure_is_reported_before_apply(self):
        with patch.object(module.os, "fsync", side_effect=OSError("fixture")):
            with self.assertRaises(OSError):
                module.cpu_apply_attempt()
        self.assertFalse((self.storage / "scratch/cpu-state").exists())

    def test_foreign_marker_owner_or_state_never_becomes_restore_target(self):
        module.cpu_apply_attempt()
        path = self.storage / "cpu-apply-attempt.json"
        original = json.loads(path.read_text())
        for replacement in ({"run": "foreign"}, {"state": str(self.base / "scratch/cpu-state")}, {"storage_identity": [0, 0, 0, 0]}):
            with self.subTest(field=next(iter(replacement))):
                path.write_text(json.dumps(original | replacement))
                with self.assertRaises(ValueError):
                    module.cpu_restore_state()


if __name__ == "__main__":
    unittest.main(verbosity=2)
