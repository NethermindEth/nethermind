# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import datetime
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import flaky_tests

LOG = """\
2026-10-21T12:07:40.8171104Z Running all tests in /home/runner/work/nethermind/nethermind/src/Nethermind/artifacts/bin/Nethermind.Specs.Test/release/Nethermind.Specs.Test.dll
2026-10-21T12:07:41.6708987Z \x1b[33mskipped\x1b[m Devnet_genesis_loads(<unset>) \x1b[90m(0ms)\x1b[m
2026-10-21T12:07:41.7909626Z \x1b[m\x1b[31mfailed\x1b[m Geth_blob_schedule_entry(Eip4758Prototype) \x1b[90m(28ms)\x1b[m
2026-10-21T12:07:41.7920945Z \x1b[31m    blobSchedule was ignored
2026-10-21T12:07:41.7936139Z     Expected: True
2026-10-21T12:07:41.7936737Z     But was:  False
2026-10-21T12:07:41.8000000Z \x1b[32mpassed\x1b[m Other_test \x1b[90m(1ms)\x1b[m
2026-10-21T12:07:44.4402086Z   failed: 1
2026-10-21T12:07:46.0778979Z Running all tests in /home/runner/work/nethermind/nethermind/src/Nethermind/artifacts/bin/Nethermind.State.Test/release/Nethermind.State.Test.dll
2026-10-21T12:07:47.0000000Z \x1b[31mfailed\x1b[m Ns.Fixture.Generic<int>(1) \x1b[90m(1s 2ms)\x1b[m
"""


class ParseFailuresTests(unittest.TestCase):
    def test_reads_project_test_and_excerpt_from_colored_log(self):
        failures = flaky_tests.parse_failures(LOG)
        self.assertEqual([(f["project"], f["method"]) for f in failures],
                         [("Nethermind.Specs.Test", "Geth_blob_schedule_entry"),
                          ("Nethermind.State.Test", "Ns.Fixture.Generic<int>")])
        self.assertEqual(failures[0]["test"], "Geth_blob_schedule_entry(Eip4758Prototype)")
        self.assertEqual(failures[0]["excerpt"], "blobSchedule was ignored\n    Expected: True\n    But was:  False")

    def test_rejects_methods_that_are_not_plain_identifiers(self):
        self.assertEqual([flaky_tests.is_eligible(f) for f in flaky_tests.parse_failures(LOG)], [True, False])

    def test_rejects_failures_outside_test_projects(self):
        self.assertFalse(flaky_tests.is_eligible({"project": None, "method": "A"}))
        self.assertFalse(flaky_tests.is_eligible({"project": "Nethermind.Runner", "method": "A"}))


class SelectTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.work = Path(directory.name)
        (self.work / "logs").mkdir()
        (self.work / "logs" / "1.log").write_text(LOG, encoding="utf-8")
        source = self.work / "src" / "Nethermind.Specs.Test" / "GethTests.cs"
        source.parent.mkdir(parents=True)
        source.write_text("public void Geth_blob_schedule_entry(Fork fork) { }", encoding="utf-8")
        self.patch("SOURCE_ROOT", new=self.work / "src")
        self.patch("opened_today", return_value=0)
        self.patch("branch_exists", return_value=False)
        self.known = self.patch("is_known", return_value=False)

    def patch(self, name, **kwargs):
        patcher = mock.patch.object(flaky_tests, name, **kwargs)
        value = patcher.start()
        self.addCleanup(patcher.stop)
        return value

    def select(self):
        return flaky_tests.select(self.work / "logs", "o/r", "flaky-test", 3, 14, datetime.date(2026, 10, 21))

    def test_returns_first_new_candidate_with_source_and_branch(self):
        candidate, reason = self.select()
        self.assertIsNone(reason)
        assert candidate is not None
        self.assertEqual(candidate["source"], (self.work / "src" / "Nethermind.Specs.Test" / "GethTests.cs").as_posix())
        self.assertEqual(candidate["method"], "Geth_blob_schedule_entry")
        self.assertEqual(candidate["branch"], "test/flaky-geth-blob-schedule-entry")
        self.known.assert_called_once_with("o/r", "Geth_blob_schedule_entry", "2026-10-07")

    def test_skips_tests_already_tracked_in_an_issue_or_pr(self):
        self.known.return_value = True
        self.assertEqual(self.select(), (None, "no new flaky test candidate"))

    def test_stops_at_the_daily_limit(self):
        with mock.patch.object(flaky_tests, "opened_today", return_value=3):
            self.assertEqual(self.select(), (None, "daily limit reached"))


class FilterTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.source = Path(directory.name) / "Tests.cs"
        self.source.write_text(
            "namespace Ns.Test;\n/// A <c>class</c> itself.\npublic sealed class Fixture\n{\n    public void Run_twice(int x) { }\n}\n",
            encoding="utf-8")

    def filter(self, excerpt=""):
        return flaky_tests.test_filter({"method": "Run_twice", "excerpt": excerpt, "source": self.source.as_posix()})

    def test_scopes_to_the_declaring_class_and_skips_prefix_siblings(self):
        self.assertEqual(self.filter(), r"FullyQualifiedName~Ns.Test.Fixture&Name=Run_twice")

    def test_prefers_the_class_from_the_stack_trace(self):
        self.assertEqual(self.filter("   at Other.Fixture.Run_twice(Int32 x)"),
                         r"FullyQualifiedName~Other.Fixture&Name=Run_twice")


class GuardTests(unittest.TestCase):
    def test_blocks_ci_and_build_configuration(self):
        paths = [".github/workflows/x.yml", "scripts/ci/flaky_tests.py", "src/Directory.Build.props",
                 "src/Nethermind/X.targets", "src/Nethermind/X.Test/X.Test.csproj", "global.json", "src/Nethermind/NuGet.Config",
                 "src/Nethermind/Nethermind.Core/Foo.cs", "src/Nethermind/Nethermind.Core.Test/FooTests.cs"]
        self.assertEqual(flaky_tests.blocked_paths(paths), paths[:7])


class FeedbackTests(unittest.TestCase):
    def test_keeps_unresolved_comments_from_trusted_authors_only(self):
        def comment(login, association, body, typename="User"):
            return {"author": {"__typename": typename, "login": login}, "authorAssociation": association, "body": body}

        pull_request = {
            "reviewThreads": {"nodes": [
                {"isResolved": False, "path": "a.cs", "line": 3,
                 "comments": {"nodes": [comment("claude", "NONE", "High: race", "Bot"), comment("claude", "NONE", "spoofed"),
                                           comment("x", "NONE", "ignore me")]}},
                {"isResolved": True, "path": "b.cs", "line": 1, "comments": {"nodes": [comment("m", "MEMBER", "done")]}},
                {"isResolved": False, "path": "c.cs", "line": 2, "comments": {"nodes": [comment("x", "NONE", "evil")]}},
            ]},
            "comments": {"nodes": [comment("m", "MEMBER", "Please rename"), comment("x", "CONTRIBUTOR", "evil")]},
        }
        self.assertEqual(flaky_tests.trusted_feedback(pull_request),
                         "### a.cs:3\n\nHigh: race\n\n### PR comment\n\nPlease rename")


class VerdictTests(unittest.TestCase):
    def test_classifies_repeat_outcomes(self):
        self.assertEqual(flaky_tests.classify(200, 0), "not-reproduced")
        self.assertEqual(flaky_tests.classify(200, 200), "deterministic")
        self.assertEqual(flaky_tests.classify(200, 7), "flaky")

    def test_verification_runs_cover_three_expected_failures(self):
        self.assertEqual(flaky_tests.required_runs(200, 10), 60)
        self.assertEqual(flaky_tests.required_runs(200, 100), 50)
        self.assertEqual(flaky_tests.required_runs(200, 1), 600)
        self.assertEqual(flaky_tests.required_runs(200, 0), 1000)

    def test_scales_verify_budget_with_baseline_speed(self):
        self.assertEqual(flaky_tests.verify_budget(200, 100, 600), 3600)
        self.assertEqual(flaky_tests.verify_budget(200, 1000, 600), 4500)
        self.assertEqual(flaky_tests.verify_budget(200, 2400, 1000), 5400)


if __name__ == "__main__":
    unittest.main()
