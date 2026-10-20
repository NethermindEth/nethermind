# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

from argparse import Namespace
from datetime import datetime, timedelta, timezone
from pathlib import Path
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).parent))
import master_failures as mf  # noqa: E402
import select_tests as st  # noqa: E402

GREEN_AT = datetime(2026, 10, 1, 12, tzinfo=timezone.utc)
EVM = f"{st.SRC}/Nethermind.Evm"
CORE = f"{st.SRC}/Nethermind.Core"
JSON_RPC = f"{st.SRC}/Nethermind.JsonRpc"
EVM_TEST = f"{st.SRC}/Nethermind.Evm.Test"
PROJECTS = st.Projects({CORE: set(), EVM: {CORE}, JSON_RPC: set(), EVM_TEST: {EVM}})
EVM_FAILURE = "Nethermind.Evm.Test|Add"


def merge(number: int, *files: str) -> mf.Merge:
    return mf.Merge(sha=f"{number:040x}", number=number, author=f"author{number}", title=f"PR {number}", files=files)


def failing(branch: str, hours: float, *tests: str) -> st.Entry:
    return st.Entry(run=hash((branch, hours)), at=GREEN_AT + timedelta(hours=hours), event="pull_request",
                    branch=branch, files=[], failed=st.Failures(tests=list(tests)))


class RegressionTests(unittest.TestCase):
    def regressions(self, *journal: st.Entry) -> list[str]:
        failed = st.Failures(tests=[EVM_FAILURE, "Hive|rpc-compat"], infra=["EEST"])
        return mf.regressions(failed, list(journal), GREEN_AT)

    def test_a_new_test_failure_is_a_regression_and_hive_and_infra_are_not(self):
        self.assertEqual(self.regressions(), [EVM_FAILURE])

    def test_a_test_that_two_branches_failed_in_the_day_before_the_merges_was_already_flaky(self):
        self.assertEqual(self.regressions(failing("a", -3, EVM_FAILURE), failing("b", -20, EVM_FAILURE)), [])

    def test_failures_after_the_merges_or_on_one_branch_or_long_before_do_not_make_it_flaky(self):
        journal = [failing("a", 1, EVM_FAILURE), failing("b", 2, EVM_FAILURE), failing("c", -3, EVM_FAILURE),
                   failing("d", -30, EVM_FAILURE), failing("master", -2, EVM_FAILURE)]
        self.assertEqual(self.regressions(*journal), [EVM_FAILURE], "a broken master breaks the branches after it")


class SuspectTests(unittest.TestCase):
    def suspects(self, merges: list[mf.Merge], tests: list[str], selector: st.Selector | None = None) -> list:
        return [(s.merge.number, s.reason) for s in mf.suspects(merges, tests, PROJECTS, selector or st.Selector())]

    def test_the_merge_that_changed_what_the_failing_test_references_wins(self):
        merges = [merge(1, f"{CORE}/A.cs"), merge(2, f"{EVM}/B.cs"), merge(3, "README.md")]
        self.assertEqual(self.suspects(merges, [EVM_FAILURE]), [(2, mf.REASONS[0])])

    def test_the_history_links_a_merge_to_the_failure(self):
        selector = st.Selector()
        selector.project_links[JSON_RPC] = {"Nethermind.Evm.Test"}
        merges = [merge(1, f"{CORE}/A.cs"), merge(2, f"{JSON_RPC}/B.cs")]
        self.assertEqual(self.suspects(merges, [EVM_FAILURE], selector), [(2, mf.REASONS[1])])

    def test_a_merge_that_changed_a_transitive_dependency_is_the_next_suspect(self):
        merges = [merge(1, f"{CORE}/A.cs"), merge(2, f"{JSON_RPC}/B.cs"), merge(3, "Directory.Packages.props")]
        self.assertEqual(self.suspects(merges, [EVM_FAILURE]), [(1, mf.REASONS[2])])

    def test_merges_with_the_same_link_are_all_suspects(self):
        merges = [merge(1, f"{EVM}/A.cs"), merge(2, f"{EVM_TEST}/B.cs")]
        self.assertEqual([number for number, _ in self.suspects(merges, [EVM_FAILURE])], [1, 2])

    def test_an_eest_failure_suspects_a_harness_change_first_and_any_project_change_next(self):
        eest = ["EEST|blockTest|sequential|test_add|Prague"]
        harness = merge(2, f"{st.SRC}/Ethereum.Test.Base/Runner.cs")
        self.assertEqual(self.suspects([merge(1, f"{JSON_RPC}/A.cs"), harness], eest), [(2, mf.REASONS[0])])
        self.assertEqual(self.suspects([merge(1, f"{JSON_RPC}/A.cs"), merge(3, "README.md")], eest),
                         [(1, mf.REASONS[2])])

    def test_a_build_wide_change_is_the_last_suspect(self):
        merges = [merge(1, "Directory.Packages.props"), merge(2, "README.md")]
        self.assertEqual(self.suspects(merges, [EVM_FAILURE]), [(1, mf.REASONS[3])])

    def test_a_single_merge_is_the_suspect_and_unlinked_merges_name_nobody(self):
        self.assertEqual(self.suspects([merge(1, "README.md")], [EVM_FAILURE]),
                         [(1, "the only merge since the last green run")])
        self.assertEqual(self.suspects([merge(1, "README.md"), merge(2, "docs/a.md")], [EVM_FAILURE]), [])


class IssueBodyTests(unittest.TestCase):
    master_run = {"html_url": "https://run", "head_sha": "b" * 40}

    def test_body_holds_the_marker_the_tests_and_the_suspects(self):
        tests = [f"Nethermind.Evm.Test|T{i}" for i in range(mf.MAX_LISTED_TESTS + 2)]
        body = mf.issue_body("Nethermind tests", self.master_run, "a" * 40, tests,
                             [mf.Suspect(merge(7, f"{EVM}/A.cs"), mf.REASONS[0])])
        self.assertIn(mf.marker("Nethermind tests"), body)
        self.assertIn("- #7 by @author7: " + mf.REASONS[0], body)
        self.assertIn("`Nethermind.Evm.Test|T0`", body)
        self.assertNotIn(f"T{mf.MAX_LISTED_TESTS}`", body)
        self.assertIn("and 2 more", body)
        self.assertIn("No merge since the last green run", mf.issue_body("W", self.master_run, "a" * 40, tests[:1], []))


class ReportTests(unittest.TestCase):
    def setUp(self):
        self.issues = mock.create_autospec(mf.Issues, instance=True)
        self.issues.open_for.return_value = None
        self.github = mock.create_autospec(st.GitHub, instance=True)
        patches = [
            mock.patch.object(mf, "Issues", return_value=self.issues),
            mock.patch.object(st, "GitHub", return_value=self.github),
            mock.patch.object(st.Projects, "of_repository", return_value=PROJECTS),
            mock.patch.object(mf, "last_green",
                              return_value={"head_sha": "a" * 40, "created_at": GREEN_AT.isoformat()}),
            mock.patch.object(mf, "merged_between", return_value=[merge(5, f"{EVM}/A.cs"), merge(6, "README.md")]),
        ]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def report(self, conclusion: str, failures: st.Failures | None = None) -> None:
        self.github.object.return_value = {
            "id": 9, "name": "Nethermind tests", "conclusion": conclusion, "html_url": "https://run",
            "head_sha": "b" * 40, "workflow_id": 1, "created_at": (GREEN_AT + timedelta(hours=1)).isoformat(),
        }
        with mock.patch.object(st, "read_failures", return_value=failures):
            mf.report(Namespace(repo="o/r", run=9, journal=Path("/missing"), model=Path("/missing")))

    def test_a_regression_opens_an_issue_assigned_to_the_suspect(self):
        self.report("failure", st.Failures(tests=[EVM_FAILURE]))
        title, body, authors = self.issues.create.call_args.args
        self.assertEqual(authors, ["author5"])
        self.assertIn("Nethermind.Evm.Test Add", title)
        self.assertIn("#5", body)

    def test_a_failure_without_a_regression_opens_nothing(self):
        self.report("failure", st.Failures(tests=["Hive|x"], infra=["Nethermind.Evm.Test"]))
        self.issues.create.assert_not_called()
        self.issues.comment.assert_not_called()

    def test_a_new_failure_while_an_issue_is_open_comments_on_it(self):
        self.issues.open_for.return_value = 3
        self.report("failure", st.Failures(tests=[EVM_FAILURE]))
        self.issues.create.assert_not_called()
        self.assertEqual(self.issues.comment.call_args.args[0], 3)

    def test_a_green_run_closes_the_open_issue(self):
        self.issues.open_for.return_value = 3
        self.report("success")
        self.assertEqual(self.issues.close.call_args.args[0], 3)
        self.issues.open_for.return_value = None
        self.issues.close.reset_mock()
        self.report("success")
        self.issues.close.assert_not_called()


class IssuesTests(unittest.TestCase):
    def test_an_author_that_cannot_be_assigned_still_gets_the_issue_opened(self):
        issues = mf.Issues("o/r")
        calls = []

        def gh(*arguments):
            calls.append(arguments)
            if any(argument.startswith("--assignee=") for argument in arguments):
                raise mf.subprocess.CalledProcessError(1, "gh")
            return ""

        with mock.patch.object(issues, "_gh", side_effect=gh):
            issues.create("title", "body", ["dependabot[bot]"])
        self.assertEqual(calls[-1], ("issue", "create", "--title", "title", "--body", "body", "--label", mf.LABEL))


if __name__ == "__main__":
    unittest.main()
