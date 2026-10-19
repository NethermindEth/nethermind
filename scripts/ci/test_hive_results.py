# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
LAUNCH = "client launch (nethermind)"
KNOWN_FAILING = "known failing (nethermind)"


@unittest.skipUnless(shutil.which("jq"), "hive-results.sh needs jq")
class HiveResultsTests(unittest.TestCase):
    """Runs scripts/hive-results.sh the way hive-tests.yml does: from the directory holding the
    `nethermind` checkout, against a glob of Hive result files in hive/workspace/logs."""

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.work = Path(directory.name)
        scripts = self.work / "nethermind/scripts"
        scripts.mkdir(parents=True)
        (scripts / "known-failing-hive-tests.txt").write_text(f"# suite\n\n{KNOWN_FAILING}\n")
        (scripts / "known-flaky-hive-tests.txt").write_text("# suite\n\n")
        self.logs = self.work / "hive/workspace/logs"
        self.logs.mkdir(parents=True)

    def write_suite(self, name, cases):
        test_cases = {str(i): {"name": case, "summaryResult": {"pass": passed}}
                      for i, (case, passed) in enumerate(cases.items(), start=1)}
        (self.logs / f"{name.replace('/', '-')}.json").write_text(json.dumps({"name": name, "testCases": test_cases}))

    def run_results(self):
        return subprocess.run(["bash", str(ROOT / "scripts/hive-results.sh"), "hive/workspace/logs/*.json"],
                              cwd=self.work, capture_output=True, text=True, timeout=60)

    def assert_no_test_cases(self, result):
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Hive ran no test cases.", result.stdout)

    def test_missing_result_files_fail(self):
        self.assert_no_test_cases(self.run_results())

    def test_suite_without_test_cases_fails(self):
        self.write_suite("eels/consume-engine", {})
        self.assert_no_test_cases(self.run_results())

    def test_launch_only_results_fail(self):
        # A simulator that stops after starting the client reports only the launch case.
        self.write_suite("eels/consume-engine", {LAUNCH: True})
        self.assert_no_test_cases(self.run_results())

    def test_passing_run_succeeds(self):
        self.write_suite("eels/consume-engine", {LAUNCH: True, "test_a (nethermind)": True,
                                                 KNOWN_FAILING: False})
        result = self.run_results()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Unexpected fails: 0", result.stdout)

    def test_unexpected_failure_fails(self):
        self.write_suite("eels/consume-engine", {LAUNCH: True, "test_a (nethermind)": False})
        result = self.run_results()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Unexpected fails: 1", result.stdout)

    def test_unexpected_pass_of_known_failure_fails(self):
        self.write_suite("eels/consume-engine", {LAUNCH: True, KNOWN_FAILING: True})
        result = self.run_results()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("Unexpected passes: 1", result.stdout)


if __name__ == "__main__":
    unittest.main()
