#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

from argparse import Namespace
from datetime import datetime, timedelta, timezone
from pathlib import Path
import json
import re
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import select_tests as st  # noqa: E402


NOW = datetime(2026, 10, 1, tzinfo=timezone.utc)
EVM = f"{st.SRC}/Nethermind.Evm"
JSON_RPC = f"{st.SRC}/Nethermind.JsonRpc"
CHECKOUT_ERROR = "2026-10-19T16:52:47.9Z ##[error]The process '/usr/bin/git' failed with exit code 128\n"


def run_without_holdout(start: int = 1) -> int:
    """A run id that the selector does not hold out, so decide() applies the selection rules."""
    return next(run for run in range(start, start + 1000) if not is_holdout(run))


def is_holdout(run: int) -> bool:
    return st.sampled(f"holdout|{run}", st.Selector.HOLDOUT_RATE)


def unsampled(run: int, *key: str) -> bool:
    return not st.sampled("|".join([str(run), *key]))


class JobLogTests(unittest.TestCase):
    def test_unit_of_job_names_maps_each_to_its_unit(self):
        cases = {
            "EEST nethtest / nethtest blockTest flat_sequential 1of6": "EEST",
            "Hive tests / Run Hive (engine-api-i)": "Hive",
            "Run Nethermind.JsonRpc.Test (1of2) (ubuntu-latest)": "Nethermind.JsonRpc.Test",
            "Tests summary": None,
        }
        for name, unit in cases.items():
            with self.subTest(name=name):
                self.assertEqual(st.unit_of(name), unit)

    def test_nethtest_lane_with_or_without_caller_prefix_returns_type_and_setup(self):
        self.assertEqual(
            st.nethtest_lane("EEST nethtest / nethtest engineTest flat_sequential 1of9"), "engineTest|flat_sequential")
        self.assertEqual(st.nethtest_lane("nethtest blockTest sequential 1of2"), "blockTest|sequential")
        self.assertIsNone(st.nethtest_lane("EEST nethtest / Prepare nethtest matrix"), "not a test lane")

    def test_test_failures_in_nethtest_log_returns_lane_test_and_fork(self):
        log = (
            "2026-10-19T10:00:00.1Z \x1b[31mFAIL\x1b[0m [12] test_add[fork_Cancun-blockchain_test-a] - mismatch\n"
            "2026-10-19T10:00:00.2Z \x1b[31mEXCEPTION\x1b[0m test_sstore[fork_Prague] - boom\n"
            "2026-10-19T10:00:00.3Z PROGRESS [100]\n"
        )
        self.assertEqual(
            st.test_failures(log, "EEST nethtest (checked) / nethtest blockTest sequential 1of2"),
            {"EEST|blockTest|sequential|test_add|Cancun", "EEST|blockTest|sequential|test_sstore|Prague"},
        )

    def test_test_failures_in_mtp_log_returns_project_and_test_without_arguments(self):
        log = "2026-10-19T22:13:39.4Z failed Deletes_hinted_updates(False) (175ms)\n"
        self.assertEqual(
            st.test_failures(log, "Run Nethermind.State.Flat.Test (ubuntu-latest)"),
            {"Nethermind.State.Flat.Test|Deletes_hinted_updates"},
        )

    def test_test_failures_in_checkout_failure_returns_nothing(self):
        self.assertEqual(st.test_failures(CHECKOUT_ERROR, "EEST nethtest / nethtest blockTest sequential 1of6"), set())


class ClassifyTests(unittest.TestCase):
    def setUp(self):
        self.read = []

    def classify(self, jobs: list[dict], logs: dict[int, str]) -> st.Failures:
        def read_log(job: dict) -> str:
            self.read.append(job["id"])
            return logs[job["id"]]

        return st.classify(jobs, read_log)

    def test_classify_when_many_unit_jobs_fail_at_checkout_still_reads_eest_failures(self):
        unit_jobs = [job(i, f"Run Nethermind.P{i}.Test (ubuntu-latest)") for i in range(1, 21)]
        eest_job = job(99, "EEST nethtest / nethtest blockTest sequential 1of2")
        logs = {**{i: CHECKOUT_ERROR for i in range(1, 21)}, 99: "2026-10-19T10:00:00Z FAIL test_add[fork_Cancun]\n"}
        failures = self.classify(unit_jobs + [eest_job], logs)
        self.assertEqual(failures.tests, ["EEST|blockTest|sequential|test_add|Cancun"], "the EEST job is judged apart")
        self.assertEqual(len(self.read), st.INFRA_SAMPLE + 1, "only a sample of the infra group is read")
        self.assertIn("Nethermind.P20.Test", failures.infra, "unread jobs of an infra group count as infra")

    def test_classify_when_many_eest_jobs_fail_at_checkout_reads_only_a_sample(self):
        eest_jobs = [job(i, f"EEST nethtest / nethtest blockTest sequential {i}of87") for i in range(1, 88)]
        failures = self.classify(eest_jobs, {i: CHECKOUT_ERROR for i in range(1, 88)})
        self.assertEqual((failures.tests, failures.infra), ([], ["EEST"]))
        self.assertEqual(len(self.read), st.INFRA_SAMPLE, "a sample without test failures decides for the group")

    def test_classify_when_sample_has_test_failures_reads_every_job(self):
        jobs = [job(i, f"Run Nethermind.P{i}.Test (ubuntu-latest)") for i in range(1, 21)]
        failures = self.classify(jobs, {i: "2026-10-19T22:13:39.4Z failed Some_test (1ms)\n" for i in range(1, 21)})
        self.assertEqual(len(self.read), 20, "real failures in the sample make every log worth reading")
        self.assertEqual((len(failures.tests), failures.infra), (20, []))


class GitHubTests(unittest.TestCase):
    def test_call_when_the_error_persists_raises_instead_of_returning_nothing(self):
        failed = Namespace(returncode=1, stdout="", stderr="HTTP 502: Server Error")
        github = st.GitHub("owner/repo")
        with mock.patch.object(st.subprocess, "run", return_value=failed), mock.patch.object(st.time, "sleep"), \
                mock.patch.object(st.GitHub, "_wait_for_budget"):
            with self.assertRaises(st.GitHubError, msg="an empty log would record real failures as infra"):
                github.text("repos/owner/repo/actions/jobs/1/logs")

    def test_runs_when_a_window_hits_the_cap_splits_it(self):
        windows = []

        def pages(path: str) -> list[dict]:
            window = path.split("created=")[1].split("&")[0]
            since, until = (datetime.fromisoformat(part) for part in window.split(".."))
            windows.append(window)
            count = st.GitHub.LIST_RUNS_CAP if until - since > timedelta(hours=6) else 10
            return [{"workflow_runs": [{"id": f"{window}-{i}"} for i in range(count)]}]

        github = st.GitHub("owner/repo")
        with mock.patch.object(github, "pages", side_effect=pages), mock.patch.object(st, "WORKFLOWS", ("a.yml",)):
            runs = github.runs(NOW, NOW + timedelta(hours=12) - timedelta(seconds=1))
        self.assertEqual(len(windows), 3, "the capped 12 h window is split into two 6 h windows")
        self.assertEqual(len(runs), 20, "the split windows return every run")


EVM_TEST = f"{st.SRC}/Nethermind.Evm.Test"
JSON_RPC_TEST = f"{st.SRC}/Nethermind.JsonRpc.Test"
PROJECTS = st.Projects({EVM: set(), JSON_RPC: {EVM}, EVM_TEST: {EVM}, JSON_RPC_TEST: {JSON_RPC}})


class ProjectsTests(unittest.TestCase):
    projects = PROJECTS

    def test_changed_maps_files_to_their_most_specific_project(self):
        files = [f"{EVM}/VirtualMachine.cs", f"{EVM_TEST}/OpcodeTests.cs"]
        self.assertEqual(self.projects.changed(files), {EVM, EVM_TEST})

    def test_changed_with_inert_files_changes_nothing(self):
        self.assertEqual(self.projects.changed(["README.md", "docs/a.md", "tools/DocGen/x.cs"]), set())

    def test_direct_tests_are_changed_test_projects_and_those_referencing_a_changed_project(self):
        self.assertEqual(self.projects.direct_tests({EVM}), {"Nethermind.Evm.Test"},
                         "JsonRpc.Test refers to JsonRpc only")
        self.assertEqual(self.projects.direct_tests({JSON_RPC_TEST}), {"Nethermind.JsonRpc.Test"})

    def test_changed_with_build_wide_files_touches_everything(self):
        for name in [f"{st.SRC}/Directory.Build.props", ".github/workflows/run-nethtest.yml", st.ALL]:
            with self.subTest(name=name):
                self.assertEqual(self.projects.changed([name]), {st.ALL})


class SelectorTests(unittest.TestCase):
    def setUp(self):
        self.selector = st.Selector()
        self.run = run_without_holdout()

    def decide(self, changed: set[str], at: datetime = NOW, selector: st.Selector | None = None) -> st.Selection | None:
        return (selector or self.selector).decide(self.run, at, changed, PROJECTS.direct_tests(changed))

    def test_decide_on_harness_or_build_wide_change_runs_everything(self):
        for changed in ({f"{st.SRC}/Ethereum.Test.Base"}, {st.ALL}):
            with self.subTest(changed=changed):
                self.assertIsNone(self.decide(changed))

    def test_decide_on_a_holdout_run_runs_everything(self):
        holdout = next(run for run in range(1000) if is_holdout(run))
        self.assertIsNone(self.selector.decide(holdout, NOW, {EVM}, set()), "holdout runs keep the history unbiased")

    def test_decide_runs_eest_tests_that_failed_with_a_changed_project(self):
        self.assertTrue(unsampled(self.run, "test_add", "Cancun"), "precondition: the sample skips the test")
        self.selector.observe(NOW - timedelta(days=30), {EVM}, {("test_add", "Cancun")}, set())
        self.assertTrue(self.decide({EVM}).keeps_test("test_add", "Cancun"), "it failed with Evm")
        self.assertFalse(self.decide({JSON_RPC}).keeps_test("test_add", "Cancun"), "never with JsonRpc")

    def test_decide_runs_test_projects_that_reference_or_failed_with_a_changed_project(self):
        project = "Nethermind.Merge.Plugin.Test"
        self.assertTrue(unsampled(self.run, project), "precondition: the sample skips the project")
        self.selector.observe(NOW - timedelta(days=30), {JSON_RPC}, set(), {project})
        selection = self.decide({JSON_RPC})
        self.assertTrue(selection.keeps_project(project), "it failed with JsonRpc")
        self.assertTrue(selection.keeps_project("Nethermind.JsonRpc.Test"), "it references JsonRpc")
        self.assertFalse(self.decide({EVM}).keeps_project(project), "it never failed with Evm")

    def test_decide_leaves_the_model_unchanged(self):
        self.decide({EVM, JSON_RPC})
        self.assertEqual(self.selector.to_json(), st.Selector().to_json(), "a decision is a query, not a command")

    def test_observe_with_mass_failures_learns_nothing(self):
        tests = {(f"test_{i}", "Cancun") for i in range(st.Selector.MASS_TESTS + 1)}
        projects = {f"Nethermind.P{i}.Test" for i in range(st.Selector.MASS_PROJECTS + 1)}
        self.selector.observe(NOW, {EVM}, tests, projects)
        self.assertEqual(self.selector.to_json(), st.Selector().to_json(), "a mass breakage links Evm to everything")

    def test_decide_when_a_project_recently_broke_enough_tests_of_a_fork_runs_the_whole_fork(self):
        self.assertTrue(unsampled(self.run, "test_never_failed", "Prague"), "precondition: the sample skips the test")
        cases = [
            (st.Selector.FORK_LINK_MIN_TESTS, 1, True),
            (st.Selector.FORK_LINK_MIN_TESTS - 1, 1, False),
            (st.Selector.FORK_LINK_MIN_TESTS, st.Selector.FORK_LINK_WINDOW.days + 1, False),
        ]
        for count, days_ago, expected in cases:
            with self.subTest(count=count, days_ago=days_ago):
                selector = st.Selector()
                failures = {(f"test_{i}", "Prague") for i in range(count)}
                selector.observe(NOW - timedelta(days=days_ago), {EVM}, failures, set())
                decided = self.decide({EVM}, selector=selector)
                self.assertEqual(decided.keeps_test("test_never_failed", "Prague"), expected)

    def test_sampled_is_deterministic_and_close_to_its_rate(self):
        draws = [st.sampled(f"7|test_{i}|Cancun") for i in range(20_000)]
        self.assertEqual(draws, [st.sampled(f"7|test_{i}|Cancun") for i in range(20_000)])
        self.assertTrue(300 < sum(draws) < 500, "about 2% are drawn")

    def test_model_round_trips_through_json(self):
        self.selector.observe(NOW, {EVM}, {("test_add", "Cancun")}, {"Nethermind.Evm.Test"})
        restored = st.Selector.from_json(json.loads(json.dumps(self.selector.to_json())))
        self.assertEqual(restored.to_json(), self.selector.to_json())


class JournalTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.work = Path(directory.name)

    def test_journal_round_trips_plain_and_gzip(self):
        entries = [entry(2, 1, "pull_request", [f"{EVM}/A.cs"], ["test_add"], selected=True), entry(1, 0, "push", [])]
        for name in ("journal.jsonl", "journal.jsonl.gz"):
            with self.subTest(name=name):
                st.write_journal(self.work / name, entries)
                self.assertEqual(st.read_journal(self.work / name), sorted(entries, key=lambda e: e.at))
                self.assertFalse((self.work / f"{name}.tmp").exists(), "the temporary file is renamed into place")

    def test_journal_written_before_the_selected_field_reads_as_not_selected(self):
        line = {"run": 1, "at": NOW.isoformat(), "event": "push", "branch": "master", "files": [], "failed": None}
        (self.work / "old.jsonl").write_text(json.dumps(line) + "\n")
        self.assertFalse(st.read_journal(self.work / "old.jsonl")[0].selected, "old journals stay readable")


def selection(tests: set[str] = frozenset(), projects: set[str] = frozenset(), run: int | None = None) -> st.Selection:
    return st.Selection(run=str(run or run_without_holdout()), tests=frozenset(tests), forks=frozenset(),
                        projects=frozenset(projects))


class InventoryTests(unittest.TestCase):
    inventory = st.Inventory(
        eest={
            "blockTest|sequential": {
                "test_add": {"Cancun": 10.0, "Prague": 10.0},
                "test_addmod": {"Cancun": 10.0},
                "test_mul": {"Cancun": 70.0},
            },
            "stateTest|sequential": {"test_mul": {"Cancun": 100.0}},
        },
        projects={"Nethermind.Evm.Test": 30.0, "Nethermind.JsonRpc.Test": 70.0},
    )
    matrix = {"include": [
        *({"test_type": "blockTest", "test_setup": "sequential", "chunk": f"{i}of4"} for i in range(1, 5)),
        *({"test_type": "stateTest", "test_setup": "sequential", "chunk": f"{i}of2"} for i in range(1, 3)),
        {"test_type": "txTest", "test_setup": "sequential", "chunk": "1of1"},
    ]}

    def setUp(self):
        self.run = run_without_holdout()
        for lane, tests in self.inventory.eest.items():
            for name, forks in tests.items():
                self.assertTrue(all(unsampled(self.run, name, fork) for fork in forks),
                                f"precondition: no sample in {lane}")
        for project in self.inventory.projects:
            self.assertTrue(unsampled(self.run, project), f"precondition: the sample skips {project}")
        self.add_only = selection({"test_add"}, run=self.run)

    def test_filter_lines_match_the_selected_tests_and_nothing_else(self):
        lines = self.inventory.lanes(self.add_only)["blockTest|sequential"].filter_lines()
        pattern = re.compile("^(?:" + "|".join(lines) + ")")
        for name, expected in [
            ("test_add[fork_Cancun-blockchain_test-a]", True),
            ("test_add[fork_Prague]", True),
            ("test_addmod[fork_Cancun-blockchain_test-a]", False),
            ("test_mul[fork_Cancun-blockchain_test-a]", False),
        ]:
            with self.subTest(name=name):
                self.assertEqual(bool(pattern.match(name)), expected)

    def test_shrink_nethtest_matrix_keeps_the_selected_share_of_chunks_and_drops_empty_lanes(self):
        reduced = st.shrink_nethtest_matrix(self.matrix, self.inventory.lanes(self.add_only))
        lanes = [(entry["test_type"], entry["chunk"]) for entry in reduced["include"]]
        # blockTest keeps 20% of its cost, so 1 of 4 chunks; stateTest keeps nothing; txTest is outside the inventory.
        self.assertEqual(lanes, [("blockTest", "1of1"), ("txTest", "1of1")])

    def test_shrink_nethtest_matrix_when_nothing_is_selected_keeps_the_full_matrix(self):
        matrix = {"include": [entry for entry in self.matrix["include"] if entry["test_type"] != "txTest"]}
        lanes = self.inventory.lanes(selection(run=self.run))
        self.assertEqual(st.shrink_nethtest_matrix(matrix, lanes), matrix, "GitHub rejects an empty matrix")

    def test_shares_weigh_by_cost(self):
        self.assertAlmostEqual(self.inventory.expected_eest_share(self.add_only), (20.0 + 0.02 * 180.0) / 200.0)
        prague = st.Selection(run=self.add_only.run, tests=frozenset(), forks=frozenset({"Prague"}),
                              projects=frozenset())
        self.assertAlmostEqual(self.inventory.expected_eest_share(prague), (10.0 + 0.02 * 190.0) / 200.0)
        evm_only = selection(projects={"Nethermind.Evm.Test"}, run=self.run)
        self.assertAlmostEqual(self.inventory.project_share(evm_only), 0.3)

    def test_write_filters_refuses_a_lane_that_is_not_a_plain_name(self):
        lanes = self.inventory.lanes(self.add_only)
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ValueError):
                st.write_filters(Path(directory), {"../../escape|x": lanes["blockTest|sequential"]})

    def test_inventory_round_trips_through_gzip(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "inventory.json.gz"
            self.inventory.save(path)
            restored = st.Inventory.load(path)
        self.assertEqual((restored.eest, restored.projects), (self.inventory.eest, self.inventory.projects))


class TestMatrixTests(unittest.TestCase):
    matrix = {
        "runner": ["ubuntu-latest", "ubuntu-24.04-arm"],
        "project": ["Nethermind.Evm.Test", "Nethermind.JsonRpc.Test"],
        "exclude": [{"runner": "ubuntu-latest", "project": "Nethermind.JsonRpc.Test"}],
        "include": [
            {"runner": "ubuntu-latest", "project": "Nethermind.JsonRpc.Test", "chunk": "1of2"},
            {"runner": "ubuntu-24.04-arm", "timeout": 60},
        ],
    }

    def test_filter_keeps_other_dimensions_and_drops_unselected_projects_everywhere(self):
        filtered = st.filter_test_matrix(self.matrix, lambda project: project == "Nethermind.Evm.Test")
        self.assertEqual(filtered, {
            "runner": ["ubuntu-latest", "ubuntu-24.04-arm"], "project": ["Nethermind.Evm.Test"], "exclude": [],
            "include": [{"runner": "ubuntu-24.04-arm", "timeout": 60}],
        }, "an include entry without a project belongs to every project")

    def test_filter_with_only_include_entries_left_keeps_them_alone(self):
        filtered = st.filter_test_matrix(self.matrix, lambda project: project == "Nethermind.JsonRpc.Test")
        self.assertEqual(filtered["project"], ["Nethermind.JsonRpc.Test"], "the project dimension is still filled")
        only_include = {key: value for key, value in self.matrix.items() if key != "project"} | {"project": []}
        self.assertEqual(st.filter_test_matrix(only_include, lambda project: True),
                         {"include": self.matrix["include"][:1]}, "an entry without a project cannot run alone")

    def test_filter_with_test_objects_keeps_the_selected_ones(self):
        matrix = {"runner": ["ubuntu-latest"],
                  "test": [{"project": "Ethereum.Legacy.Blockchain.Test", "chunk": "1of8"}]}
        self.assertIsNone(st.filter_test_matrix(matrix, lambda project: False), "a job with nothing to run is skipped")
        self.assertEqual(st.filter_test_matrix(matrix, lambda project: True), matrix)

    def test_filter_keeping_every_project_returns_every_repository_matrix_unchanged(self):
        matrices = st.read_json(st.ROOT / "scripts/ci/test-matrices.json")
        for workflow, jobs in matrices.items():
            for job, matrix in jobs.items():
                with self.subTest(workflow=workflow, job=job):
                    self.assertEqual(st.filter_test_matrix(matrix, lambda project: True), matrix)


class SelectCommandTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.work = Path(directory.name)
        self.run = run_without_holdout()
        InventoryTests.inventory.save(self.work / "inventory.json")
        st.write_json(self.work / "nethtest.json", InventoryTests.matrix)
        st.write_json(self.work / "matrices.json", {"workflow": {"tests": TestMatrixTests.matrix}})
        (self.work / "changed.txt").write_text(f"{EVM}/VirtualMachine.cs\n")

    def write_model(self, built_at: datetime, history_from: datetime) -> None:
        selector = st.Selector()
        selector.test_links[EVM] = {"test_add"}
        st.write_json(self.work / "model.json", {
            "built_at": built_at.isoformat(), "history_from": history_from.isoformat(), **selector.to_json(),
        })

    def arguments(self, **extra) -> Namespace:
        return Namespace(
            model=self.work / "model.json", changed_files=self.work / "changed.txt", run=str(self.run),
            min_history_days=28, max_model_age_hours=24, **extra,
        )

    def select_eest(self) -> tuple[dict, set[str]]:
        st.select_eest(self.arguments(
            inventory=self.work / "inventory.json", matrix=self.work / "nethtest.json",
            out_matrix=self.work / "out.json", filters=self.work / "filters",
        ))
        return st.read_json(self.work / "out.json"), {path.name for path in (self.work / "filters").glob("*.txt")}

    def select_projects(self, event: str) -> dict:
        output = self.work / "output"
        st.select_projects(self.arguments(matrices=self.work / "matrices.json", workflow="workflow", event=event,
                                          output=output))
        return dict(line.split("=", 1) for line in output.read_text().splitlines())

    def test_select_eest_with_a_stale_or_young_model_runs_everything(self):
        now = datetime.now(timezone.utc)
        stale, young = (now - timedelta(days=2), now - timedelta(days=40)), (now, now - timedelta(days=10))
        for built_at, history_from in (stale, young):
            with self.subTest(built_at=built_at, history_from=history_from):
                self.write_model(built_at, history_from)
                self.assertEqual(self.select_eest(), (InventoryTests.matrix, set()),
                                 "an untrusted model runs everything")

    def test_select_eest_with_a_trusted_model_writes_filters_and_the_reduced_matrix(self):
        self.assertTrue(unsampled(self.run, "test_mul", "Cancun"), "precondition: the sample skips test_mul")
        now = datetime.now(timezone.utc)
        self.write_model(now, now - timedelta(days=40))
        matrix, filters = self.select_eest()
        self.assertEqual(filters, {"blockTest-sequential.txt"}, "only the lane with a selected test gets a filter")
        self.assertEqual(len(matrix["include"]), 2, "one blockTest chunk and the txTest lane remain")

    def test_select_projects_on_a_pull_request_keeps_the_projects_the_change_affects(self):
        now = datetime.now(timezone.utc)
        self.write_model(now, now - timedelta(days=40))
        self.assertTrue(unsampled(self.run, "Nethermind.JsonRpc.Test"), "precondition: the sample skips JsonRpc.Test")
        outputs = self.select_projects("pull_request")
        tests = json.loads(outputs["matrices"])["tests"]
        self.assertEqual(tests["project"], ["Nethermind.Evm.Test"], "Evm.Test references the changed Evm")
        self.assertEqual(outputs["any"], "true")

    def test_select_projects_with_no_affected_project_skips_every_job(self):
        now = datetime.now(timezone.utc)
        self.write_model(now, now - timedelta(days=40))
        (self.work / "changed.txt").write_text("README.md\n")
        self.assertEqual(self.select_projects("pull_request"), {"matrices": '{"tests": null}', "any": "false"})

    def test_select_projects_outside_pull_requests_keeps_every_matrix_whole(self):
        now = datetime.now(timezone.utc)
        self.write_model(now, now - timedelta(days=40))
        for event in ("push", "merge_group", "ci-full"):
            with self.subTest(event=event):
                (self.work / "output").unlink(missing_ok=True)
                outputs = self.select_projects(event)
                self.assertEqual(json.loads(outputs["matrices"]), {"tests": TestMatrixTests.matrix})
                self.assertEqual(outputs["any"], "true")


class ReplayTests(unittest.TestCase):
    def test_replay_reports_hidden_failures_and_leaves_out_selected_runs(self):
        run = run_without_holdout(start=3)
        self.assertTrue(unsampled(run, "test_mul", "Cancun"), "precondition: the sample skips test_mul")
        evm_file = f"{EVM}/VirtualMachine.cs"
        journal = [
            entry(1, 0, "push", [evm_file], ["test_add"]),
            entry(2, 8, "pull_request", [evm_file], ["test_add"]),
            entry(run, 9, "pull_request", [f"{JSON_RPC}/Eth.cs"], ["test_mul"]),
            entry(run + 1, 9, "pull_request", [f"{JSON_RPC}/Eth.cs"], ["test_mul"], selected=True),
        ]
        report = st.Replay(InventoryTests.inventory, PROJECTS).run(journal, NOW + timedelta(days=7))
        self.assertEqual(report.measured, 2, "the selected run cannot show what it skipped")
        self.assertEqual([hidden[0].run for hidden in report.eest.hidden], [run], "JsonRpc never failed with test_mul")


def job(job_id: int, name: str) -> dict:
    return {"id": job_id, "name": name}


def entry(
    run: int, days: int, event: str, files: list[str], failed: tuple[str, ...] | list[str] = (), selected: bool = False
) -> st.Entry:
    tests = [f"EEST|blockTest|sequential|{name}|Cancun" for name in failed]
    return st.Entry(
        run=run, at=NOW + timedelta(days=days), event=event, branch=f"b{run}", files=files,
        failed=st.Failures(tests=tests) if failed else None, selected=selected,
    )


if __name__ == "__main__":
    unittest.main()
