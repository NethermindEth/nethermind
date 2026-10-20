#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

from argparse import Namespace
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import re
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


class CollectTests(unittest.TestCase):
    def setUp(self):
        self.originals = (st.gh, st.gh_text, st.subprocess.run, st.wait_for_budget)
        st.wait_for_budget = lambda: None
        self.addCleanup(self.restore)

    def restore(self):
        st.gh, st.gh_text, st.subprocess.run, st.wait_for_budget = self.originals

    @staticmethod
    def job(job_id, name):
        return {"id": job_id, "name": name}

    def fake_logs(self, logs):
        self.read = []

        def gh_text(path):
            job_id = int(path.split("/")[-2])
            self.read.append(job_id)
            return logs.get(job_id, "")

        st.gh_text = gh_text

    def test_eest_failures_survive_a_sample_of_infra_failures(self):
        checkout = "2026-10-19T16:52:47.9Z ##[error]The process '/usr/bin/git' failed with exit code 128\n"
        others = [self.job(i, f"Run Nethermind.P{i}.Test (ubuntu-latest)") for i in range(1, 21)]
        eest = self.job(99, "EEST nethtest / nethtest blockTest sequential 1of2")
        self.fake_logs({**{i: checkout for i in range(1, 21)}, 99: "2026-10-19T10:00:00Z FAIL test_add[fork_Cancun]\n"})
        failed = st.classify("o/r", others + [eest])
        self.assertEqual(failed["tests"], ["EEST|blockTest|sequential|test_add|Cancun"])
        self.assertEqual(len(self.read), st.SAMPLE_WHEN_WIDESPREAD + 1)
        self.assertIn("Nethermind.P20.Test", failed["infra"])

    def test_a_sample_of_infra_eest_jobs_marks_the_rest_infra(self):
        checkout = "2026-10-19T16:52:47.9Z ##[error]The process '/usr/bin/git' failed with exit code 128\n"
        eest = [self.job(i, f"EEST nethtest / nethtest blockTest sequential {i}of87") for i in range(1, 88)]
        self.fake_logs({i: checkout for i in range(1, 88)})
        failed = st.classify("o/r", eest)
        self.assertEqual((failed["tests"], failed["infra"]), ([], ["EEST"]))
        self.assertEqual(len(self.read), st.SAMPLE_WHEN_WIDESPREAD)

    def test_a_sample_with_test_failures_reads_every_job(self):
        failing = "2026-10-19T22:13:39.4Z failed Some_test (1ms)\n"
        jobs = [self.job(i, f"Run Nethermind.P{i}.Test (ubuntu-latest)") for i in range(1, 21)]
        self.fake_logs({i: failing for i in range(1, 21)})
        failed = st.classify("o/r", jobs)
        self.assertEqual(len(self.read), 20)
        self.assertEqual(len(failed["tests"]), 20)
        self.assertEqual(failed["infra"], [])

    def test_a_failed_download_raises_instead_of_returning_nothing(self):
        st.time.sleep, sleep = (lambda _: None), st.time.sleep
        self.addCleanup(setattr, st.time, "sleep", sleep)
        st.subprocess.run = lambda *a, **k: Namespace(returncode=1, stdout="", stderr="HTTP 502: Server Error")
        with self.assertRaises(RuntimeError):
            st.api("repos/o/r/actions/jobs/1/logs", raw=True)

    def test_a_capped_listing_is_split(self):
        calls = []

        def gh(path, paginate=True, attempts=4):
            window = path.split("created=")[1].split("&")[0]
            since, until = (datetime.fromisoformat(part) for part in window.split(".."))
            calls.append(window)
            count = st.LIST_RUNS_CAP if until - since > timedelta(hours=6) else 10
            return [{"workflow_runs": [{"id": f"{window}-{i}"} for i in range(count)]}]

        st.gh = gh
        runs = st.list_runs("o/r", AT, AT + timedelta(hours=12) - timedelta(seconds=1))
        self.assertEqual(len(calls), 3)
        self.assertEqual(len(runs), 20)


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


class ReplayReportTests(unittest.TestCase):
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
            st.replay(args)
            report = (work / "report.md").read_text()

        rows = [line for line in report.splitlines() if line.startswith("| 2 |")]
        self.assertEqual(len(rows), 1, report)
        # Run 2 learned test_add from run 1 and keeps it; run 3 changes JsonRpc, which never failed with test_mul.
        self.assertIn("| 2 (2 branches) | 1 |", rows[0])
        self.assertIn("[3](https://github.com/owner/repo/actions/runs/3)", report)


class SelectTests(unittest.TestCase):
    inventory = {
        "EEST|blockTest|sequential|test_add|Cancun": 10.0,
        "EEST|blockTest|sequential|test_addmod|Cancun": 10.0,
        "EEST|blockTest|sequential|test_add|Prague": 10.0,
        "EEST|blockTest|sequential|test_mul|Cancun": 70.0,
        "EEST|stateTest|sequential|test_mul|Cancun": 100.0,
    }
    matrix = {"include": [
        *({"test_type": "blockTest", "test_setup": "sequential", "chunk": f"{i}of4"} for i in range(1, 5)),
        *({"test_type": "stateTest", "test_setup": "sequential", "chunk": f"{i}of2"} for i in range(1, 3)),
        {"test_type": "txTest", "test_setup": "sequential", "chunk": "1of1"},
    ]}

    def run_select(self, files, built_at=None, history_from=None, learned=None, run="7"):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            now = datetime.now(timezone.utc)
            selector = st.Selector(["Cancun", "Prague"])
            for project, names in (learned or {}).items():
                selector.learned[project] = set(names)
            data = {
                "built_at": (built_at or now).isoformat(),
                "history_from": (history_from or now - timedelta(days=40)).isoformat(),
                "history_to": now.isoformat(),
                **selector.to_json(),
            }
            (work / "model.json").write_text(json.dumps(data))
            (work / "inventory.json").write_text(json.dumps(self.inventory))
            (work / "changed.txt").write_text("\n".join(files) + "\n")
            (work / "matrix.json").write_text(json.dumps(self.matrix))
            args = Namespace(
                model=str(work / "model.json"), inventory=str(work / "inventory.json"),
                changed_files=str(work / "changed.txt"), run=run, matrix=str(work / "matrix.json"),
                out_matrix=str(work / "out.json"), filters=str(work / "filters"),
            )
            st.select(args)
            filters = {f.name: f.read_text() for f in (work / "filters").glob("*.txt")} if (work / "filters").exists() else {}
            return json.loads((work / "out.json").read_text()), filters

    def test_a_stale_or_young_model_runs_everything(self):
        now = datetime.now(timezone.utc)
        for kwargs in ({"built_at": now - timedelta(days=2)}, {"history_from": now - timedelta(days=10)}):
            with self.subTest(**{k: str(v) for k, v in kwargs.items()}):
                matrix, filters = self.run_select([f"{EVM}/A.cs"], **kwargs)
                self.assertEqual((matrix, filters), (self.matrix, {}))

    def test_a_harness_change_runs_everything(self):
        matrix, filters = self.run_select([f"{st.SRC}/Ethereum.Test.Base/BlockchainTestBase.cs"])
        self.assertEqual((matrix, filters), (self.matrix, {}))

    def test_selection_writes_filters_and_shrinks_the_matrix(self):
        # Run 7 samples none of these units, so only the learned link selects.
        for unit in self.inventory:
            _, _, _, name, fork = unit.split("|")
            self.assertFalse(st.sampled("7", name, fork, st.Selector.SAMPLE_RATE), unit)
        matrix, filters = self.run_select([f"{EVM}/A.cs"], learned={EVM: ["test_add"]})

        lanes = [(e["test_type"], e["chunk"]) for e in matrix["include"]]
        # blockTest keeps 20% of its cost, so 1 of 4 chunks; stateTest has nothing selected; txTest is not EEST cost.
        self.assertEqual(lanes, [("blockTest", "1of1"), ("txTest", "1of1")])
        self.assertEqual(set(filters), {"blockTest-sequential.txt"})
        pattern = re.compile("^(?:" + "|".join(filters["blockTest-sequential.txt"].split()) + ")")
        self.assertTrue(pattern.match("test_add[fork_Cancun-blockchain_test-a]"))
        self.assertTrue(pattern.match("test_add[fork_Prague]"))
        self.assertFalse(pattern.match("test_addmod[fork_Cancun-blockchain_test-a]"))
        self.assertFalse(pattern.match("test_mul[fork_Cancun-blockchain_test-a]"))

    def test_the_model_round_trips(self):
        selector = st.Selector(["Cancun"])
        selector.observe(AT, {EVM}, {"test_add"})
        restored = st.Selector.from_json(json.loads(json.dumps(selector.to_json())))
        self.assertEqual(restored.learned[EVM], {"test_add"})
        self.assertEqual(restored.failures["test_add"], [AT])


if __name__ == "__main__":
    unittest.main()
