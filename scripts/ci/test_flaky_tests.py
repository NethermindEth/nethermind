# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import datetime
from pathlib import Path
import subprocess
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
        self.patch("claude_slots_used", return_value=0)
        self.patch("branch_exists", return_value=False)
        self.known = self.patch("is_known", return_value=False)

    def patch(self, name, **kwargs):
        patcher = mock.patch.object(flaky_tests, name, **kwargs)
        value = patcher.start()
        self.addCleanup(patcher.stop)
        return value

    def select(self):
        return flaky_tests.select(self.work / "logs", "o/r", 3, 14, datetime.date(2026, 10, 21))

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
        with mock.patch.object(flaky_tests, "claude_slots_used", return_value=3):
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

    def test_finds_the_declaring_file_of_the_traced_class(self):
        root = self.source.parent
        project = root / "X.Test"
        project.mkdir()
        (project / "A.cs").write_text("class Fixture\n{\n    void Go() { Run_twice(1); }\n}\n", encoding="utf-8")
        (project / "B.cs").write_text("class Other\n{\n    public void Run_twice(int x) { }\n}\n", encoding="utf-8")
        (project / "C.cs").write_text("class Fixture\n{\n    public async Task Run_twice(int x) { }\n}\n", encoding="utf-8")
        with mock.patch.object(flaky_tests, "SOURCE_ROOT", root):
            self.assertEqual(flaky_tests.find_source("X.Test", "Run_twice"), (project / "B.cs").as_posix())
            self.assertEqual(flaky_tests.find_source("X.Test", "Run_twice", "Ns.Fixture"), (project / "C.cs").as_posix())

    def test_falls_back_to_the_method_when_the_class_filter_matches_nothing(self):
        host = self.source.parent / "host.sh"
        host.write_text('#!/bin/sh\ncase "$2" in FullyQualifiedName*) exit 8;; esac\nexit 0\n', encoding="utf-8")
        host.chmod(0o755)
        (self.source.parent / "X.Test").mkdir()
        with mock.patch.object(flaky_tests, "SOURCE_ROOT", self.source.parent):
            result = flaky_tests.repeat("X.Test", "FullyQualifiedName~Ns.Base&Name=Run_twice", 3, 60, 0,
                                        self.source.parent / "out", host=host.as_posix(), load=0)
        self.assertEqual((result["runs"], result["failures"], result["filter"]), (3, 0, "Name=Run_twice"))


class FindSourceTests(unittest.TestCase):
    def test_returns_the_file_that_declares_the_method_not_a_call_site(self):
        with tempfile.TemporaryDirectory() as root:
            project = Path(root) / "Nethermind.Sample.Test"
            project.mkdir()
            (project / "A_Caller.cs").write_text("class Caller\n{\n    void Use() { helper.Run_twice(1); }\n}\n", encoding="utf-8")
            (project / "B_Fixture.cs").write_text("class Fixture\n{\n    public async Task<int> Run_twice(int x) { }\n}\n", encoding="utf-8")
            with mock.patch.object(flaky_tests, "SOURCE_ROOT", Path(root)):
                found = flaky_tests.find_source("Nethermind.Sample.Test", "Run_twice")
        self.assertEqual(Path(found).name, "B_Fixture.cs")


class GuardTests(unittest.TestCase):
    def test_blocks_ci_and_build_configuration(self):
        paths = [".github/workflows/x.yml", "scripts/ci/flaky_tests.py", "src/Directory.Build.props",
                 "src/Nethermind/X.targets", "src/Nethermind/X.Test/X.Test.csproj", "global.json", "AGENTS.md",
                 "src/Nethermind/CLAUDE.md", ".agents/rules/git.md", "src/Nethermind/NuGet.Config",
                 "src/Nethermind/Nethermind.Core/Foo.cs", "src/Nethermind/Nethermind.Core.Test/FooTests.cs"]
        self.assertEqual(flaky_tests.blocked_paths(paths), paths[:10])


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
            "comments": {"nodes": [comment("claude", "NONE", "Round 1", "Bot"), comment("m", "MEMBER", "Please rename"),
                                   comment("x", "CONTRIBUTOR", "evil"), comment("claude", "NONE", "Round 2", "Bot")]},
        }
        self.assertEqual(flaky_tests.trusted_feedback(pull_request),
                         "### a.cs:3\n\nHigh: race\n\n### PR comment\n\nPlease rename\n\n### PR comment\n\nRound 2")


class SearchTests(unittest.TestCase):
    def test_known_when_open_or_merged_since(self):
        def known(*items):
            with mock.patch.object(flaky_tests, "gh_search", return_value={"total_count": len(items), "items": list(items)}):
                return flaky_tests.is_known("o/r", "Run_twice", "2026-10-07")

        self.assertTrue(known({"state": "open"}))
        self.assertTrue(known({"state": "closed", "pull_request": {"merged_at": "2026-10-08T00:00:00Z"}}))
        self.assertFalse(known({"state": "closed", "pull_request": {"merged_at": "2026-10-01T00:00:00Z"}},
                               {"state": "closed", "pull_request": {"merged_at": None}}, {"state": "closed"}))

    def test_reads_every_page_and_treats_too_many_matches_as_known(self):
        old = {"state": "closed"}
        pages = {1: {"total_count": 150, "items": [old] * 100}, 2: {"total_count": 150, "items": [old] * 49 + [{"state": "open"}]}}
        with mock.patch.object(flaky_tests, "gh_search", side_effect=lambda query, page: pages[page]) as search:
            self.assertTrue(flaky_tests.is_known("o/r", "Run_twice", "2026-10-07"))
        self.assertEqual(search.call_count, 2)
        with mock.patch.object(flaky_tests, "gh_search", return_value={"total_count": 1001, "items": []}):
            self.assertTrue(flaky_tests.is_known("o/r", "Run_twice", "2026-10-07"))

    def test_fences_excerpt_and_breaks_mentions(self):
        self.assertEqual(flaky_tests.markdown_block("a ``` b @x"), "````\na ``` b @\u200bx\n````")
        self.assertEqual(flaky_tests.markdown_block("plain"), "```\nplain\n```")


class ClaudeSlotTests(unittest.TestCase):
    def test_takes_the_first_free_slot_and_stops_at_the_limit(self):
        taken = {"flaky-claude-2026-10-21-1"}

        def create(args, **kwargs):
            name = args[args.index("-f") + 1].removeprefix("name=")
            exists = name in taken
            taken.add(name)
            return subprocess.CompletedProcess(args, 1 if exists else 0, "already_exists" if exists else "", "")

        with mock.patch.object(flaky_tests.subprocess, "run", side_effect=create):
            self.assertEqual(flaky_tests.take_claude_slot("o/r", "2026-10-21", 2), "flaky-claude-2026-10-21-2")
            self.assertIsNone(flaky_tests.take_claude_slot("o/r", "2026-10-21", 2))


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
