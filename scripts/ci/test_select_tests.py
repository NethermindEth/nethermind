#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

from argparse import Namespace
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import select_tests as st  # noqa: E402


AT = datetime(2026, 10, 1, tzinfo=timezone.utc)
EVM = f"{st.SRC}/Nethermind.Evm"
JSON_RPC = f"{st.SRC}/Nethermind.JsonRpc"


class FailureParsingTests(unittest.TestCase):
    def test_unit_of_job_names(self):
        self.assertEqual(st.unit_of("EEST nethtest / nethtest blockTest flat_sequential 1of6"), "EEST")
        self.assertEqual(st.unit_of("Hive tests / Run Hive (engine-api-i)"), "Hive")
        self.assertEqual(st.unit_of("Run Nethermind.JsonRpc.Test (1of2) (ubuntu-latest)"), "Nethermind.JsonRpc.Test")
        self.assertIsNone(st.unit_of("Tests summary"))

    def test_nethtest_lane_with_and_without_the_caller_prefix(self):
        self.assertEqual(st.nethtest_lane("EEST nethtest / nethtest engineTest flat_sequential 1of9"), "engineTest|flat_sequential")
        self.assertEqual(st.nethtest_lane("nethtest blockTest sequential 1of2"), "blockTest|sequential")
        self.assertIsNone(st.nethtest_lane("EEST nethtest / Prepare nethtest matrix"))

    def test_eest_failures_carry_lane_test_and_fork(self):
        log = (
            "2026-10-19T10:00:00.1Z \x1b[31mFAIL\x1b[0m [12] test_add[fork_Cancun-blockchain_test-a] - mismatch\n"
            "2026-10-19T10:00:00.2Z \x1b[31mEXCEPTION\x1b[0m test_sstore[fork_Prague] - boom\n"
            "2026-10-19T10:00:00.3Z PROGRESS [100]\n"
        )
        self.assertEqual(
            st.job_failures(log, "EEST nethtest (checked) / nethtest blockTest sequential 1of2"),
            ["EEST|blockTest|sequential|test_add|Cancun", "EEST|blockTest|sequential|test_sstore|Prague"],
        )

    def test_nunit_failures_drop_arguments(self):
        log = "2026-10-19T22:13:39.4Z failed Deletes_hinted_updates(False) (175ms)\n"
        self.assertEqual(
            st.job_failures(log, "Run Nethermind.State.Flat.Test (ubuntu-latest)"),
            ["Nethermind.State.Flat.Test|Deletes_hinted_updates"],
        )

    def test_a_log_without_test_failures_has_none(self):
        log = "2026-10-19T16:52:47.9Z ##[error]The process '/usr/bin/git' failed with exit code 128\n"
        self.assertEqual(st.job_failures(log, "EEST nethtest / nethtest blockTest sequential 1of6"), [])


class ChangedProjectsTests(unittest.TestCase):
    projects = sorted([EVM, JSON_RPC, f"{st.SRC}/Nethermind.Evm.Test"], key=len, reverse=True)

    def test_files_map_to_their_project(self):
        files = [f"{EVM}/VirtualMachine.cs", f"{st.SRC}/Nethermind.Evm.Test/OpcodeTests.cs"]
        self.assertEqual(st.changed_projects(files, self.projects), {EVM, f"{st.SRC}/Nethermind.Evm.Test"})

    def test_inert_files_change_nothing(self):
        self.assertEqual(st.changed_projects(["README.md", "docs/a.md", "tools/DocGen/x.cs"], self.projects), set())

    def test_other_files_touch_everything(self):
        for name in [f"{st.SRC}/Directory.Build.props", ".github/workflows/run-nethtest.yml", st.ALL]:
            with self.subTest(name=name):
                self.assertEqual(st.changed_projects([name], self.projects), {st.ALL})


class SelectorTests(unittest.TestCase):
    def selector(self):
        return st.Selector(["Cancun", "Prague", "Osaka"])

    def test_harness_and_build_wide_changes_run_everything(self):
        selector = self.selector()
        self.assertIsNone(selector.decide(1, AT, {f"{st.SRC}/Ethereum.Test.Base"}, []))
        self.assertIsNone(selector.decide(1, AT, {st.ALL}, []))

    def test_a_change_runs_tests_that_failed_with_its_project(self):
        selector = self.selector()
        selector.observe(AT - timedelta(days=30), {EVM}, {"test_add"})
        keep = selector.decide(1, AT, {EVM}, [f"{EVM}/A.cs"])
        self.assertTrue(keep("test_add", "Cancun"))
        unrelated = selector.decide(1, AT, {JSON_RPC}, [f"{JSON_RPC}/A.cs"])
        self.assertEqual(unrelated("test_add", "Cancun"), st.sampled(1, "test_add", "Cancun", st.Selector.SAMPLE_RATE))

    def test_mass_failures_teach_no_links(self):
        selector = self.selector()
        names = {f"test_{i}" for i in range(st.Selector.MASS_FAILURES + 1)}
        selector.observe(AT - timedelta(days=1), {EVM}, names)
        self.assertEqual(selector.learned[EVM], set())
        self.assertEqual(dict(selector.failures), {})

    def test_tests_that_failed_twice_recently_run_for_any_change(self):
        selector = self.selector()
        selector.observe(AT - timedelta(days=3), {EVM}, {"test_flaky"})
        selector.observe(AT - timedelta(days=2), {EVM}, {"test_flaky"})
        self.assertTrue(selector.decide(1, AT, {JSON_RPC}, [])("test_flaky", "Cancun"))
        later = AT + st.Selector.UNSTABLE_WINDOW
        self.assertEqual(
            selector.decide(1, later, {JSON_RPC}, [])("test_flaky", "Cancun"),
            st.sampled(1, "test_flaky", "Cancun", st.Selector.SAMPLE_RATE),
        )

    def test_a_fork_named_in_the_changed_files_runs_its_tests(self):
        keep = self.selector().decide(1, AT, {EVM}, [f"{EVM}/Specs/OsakaSpec.cs"])
        self.assertTrue(keep("test_anything", "Osaka"))

    def test_the_sample_is_deterministic_and_near_its_rate(self):
        hits = sum(st.sampled(7, f"test_{i}", "Cancun", 0.02) for i in range(20_000))
        self.assertEqual(hits, sum(st.sampled(7, f"test_{i}", "Cancun", 0.02) for i in range(20_000)))
        self.assertTrue(300 < hits < 500, hits)


class ShadowReportTests(unittest.TestCase):
    def test_report_counts_selected_cost_and_missed_runs(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            inventory = {
                "EEST|blockTest|sequential|test_add|Cancun": 10.0,
                "EEST|blockTest|sequential|test_mul|Cancun": 90.0,
            }
            (work / "inventory.json").write_text(json.dumps(inventory))

            def entry(run, days, event, files, failed=()):
                tests = [f"EEST|blockTest|sequential|{name}|Cancun" for name in failed]
                return {
                    "run": run, "at": (AT + timedelta(days=days)).isoformat(), "event": event, "branch": f"b{run}",
                    "files": files, "failed": {"tests": tests, "infra": []} if failed else None,
                }

            evm_file = f"{EVM}/VirtualMachine.cs"
            journal = [
                entry(1, 0, "push", [evm_file], ["test_add"]),
                entry(2, 8, "pull_request", [evm_file], ["test_add"]),
                entry(3, 9, "pull_request", [f"{JSON_RPC}/Eth.cs"], ["test_mul"]),
            ]
            (work / "journal.jsonl").write_text("".join(json.dumps(e) + "\n" for e in journal))
            args = Namespace(
                journal=str(work / "journal.jsonl"), inventory=str(work / "inventory.json"),
                warmup_days=7, report=str(work / "report.md"), repo="owner/repo",
            )
            # The missed-run assertion below needs run 3 to leave test_mul out of its random sample.
            self.assertFalse(st.sampled(3, "test_mul", "Cancun", st.Selector.SAMPLE_RATE))
            st.shadow(args)
            report = (work / "report.md").read_text()

        rows = [line for line in report.splitlines() if line.startswith("| 2 |")]
        self.assertEqual(len(rows), 1, report)
        # Run 2 learned test_add from run 1 and keeps it; run 3 changes JsonRpc, which never failed with test_mul.
        self.assertIn("| 2 (2 branches) | 1 |", rows[0])
        self.assertIn("[3](https://github.com/owner/repo/actions/runs/3)", report)


if __name__ == "__main__":
    unittest.main()
