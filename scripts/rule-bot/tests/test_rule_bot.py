# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import argparse
import contextlib
import datetime
import io
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import rule_bot as rb  # noqa: E402

UNSHIPPED = rb.UNSHIPPED


class Classify(unittest.TestCase):
    def thread(self, replies=(), resolved=False, outdated=False):
        cs = [dict(author=dict(login="reviewer"), body="unused field", createdAt="2026-11-20T00:00:00Z", url="u")]
        cs += [dict(author=dict(login="author"), body=b, createdAt="2026-11-20T01:00:00Z", url="u") for b in replies]
        return dict(isResolved=resolved, isOutdated=outdated, comments=dict(nodes=cs), path="a.cs", line=1, originalLine=1)

    def test_author_agreement_is_valid(self):
        self.assertEqual(rb.classify(self.thread(["Good catch, fixed"]), "author"), "valid")

    def test_rebuttal_is_invalid(self):
        self.assertEqual(rb.classify(self.thread(["This is intentional, by design"]), "author"), "invalid")

    def test_resolved_without_reply_is_likely(self):
        self.assertEqual(rb.classify(self.thread(resolved=True), "author"), "likely")

    def test_open_thread_is_unknown(self):
        self.assertEqual(rb.classify(self.thread(), "author"), "unknown")


class Gates(unittest.TestCase):
    def row(self, pr, author, bot=False, valid="valid"):
        return dict(pr=pr, author=author, bot=bot, valid=valid)

    def test_cluster_over_gates_passes(self):
        rows = [self.row(n, a) for n in range(10) for a in "ab"] + [self.row(10, "c")]
        st = rb.gate_cluster(rows)
        self.assertTrue(st["pass"], st["fails"])

    def test_bot_resolved_only_is_not_valid(self):
        st = rb.gate_cluster([self.row(1, "claude[bot]", bot=True, valid="likely")])
        self.assertEqual(st["valid"], 0)

    def test_prs_and_commenters_are_distinct(self):
        st = rb.gate_cluster([self.row(100, "a"), self.row(100, "a"), self.row(101, "a"), self.row(102, "b")])
        self.assertEqual((st["prs"], st["commenters"], st["n"]), (3, 2, 4))

    def test_two_commenters_fail_the_commenter_gate(self):
        st = rb.gate_cluster([self.row(n, "ab"[n % 2]) for n in range(12)])
        self.assertIn("commenters", st["fails"])

    def test_missing_humans_fails(self):
        st = rb.gate_cluster([self.row(n, a, bot=n > 0) for n, a in enumerate("abcdefghijklmnopqrst")])
        self.assertIn("human", st["fails"])

    def test_examples_come_from_trusted_human_rows(self):
        rows = [dict(self.row(1, "a"), association="MEMBER", url="u1", path="p", line=1, body="b"),
                dict(self.row(2, "claude[bot]", bot=True), association="NONE", url="u2", path="p", line=1, body="b"),
                dict(self.row(3, "c"), association="CONTRIBUTOR", url="u3", path="p", line=1, body="b"),
                dict(self.row(4, "d", valid="invalid"), association="MEMBER", url="u4", path="p", line=1, body="b")]
        self.assertEqual([e["url"] for e in rb.trusted_examples(rows)], ["u1"])
        self.assertEqual(rb.examples(dict(examples=rb.trusted_examples(rows))), [("u1", "a")])


def comment(id, pr, author="reviewer", created="2026-11-01T00:00:00Z", reply_to=None, body="unused field", bot=False):
    return dict(id=id, pull_request_url=f"https://api.github.com/repos/{rb.REPO}/pulls/{pr}", user=dict(login=author, type="Bot" if bot else "User"),
                author_association="MEMBER", in_reply_to_id=reply_to, path="a.cs", line=1, created_at=created, body=body,
                html_url=f"https://github.com/{rb.REPO}/pull/{pr}#discussion_r{id}")


class Window(unittest.TestCase):
    def test_api_keeps_partial_graphql_data(self):
        real = rb.subprocess.run
        rb.subprocess.run = lambda *a, **k: argparse.Namespace(returncode=1, stdout='{"data":{"repository":{"p1":null}}}', stderr="gone")
        try:
            self.assertEqual(rb.api("graphql"), dict(data=dict(repository=dict(p1=None))))
        finally:
            rb.subprocess.run = real

    def test_in_window_drops_older_comments_and_dedupes(self):
        out = rb.in_window([comment(1, 5, created="2026-05-01T00:00:00Z"), comment(2, 5), comment(2, 5, body="edited")], "2026-05-25")
        self.assertEqual(list(out), [2])
        self.assertEqual(out[2]["body"], "unused field")

    def test_collect_pages_until_short_page_and_counts_each_comment_once(self):
        pages = {1: [comment(n, 7) for n in range(100)], 2: [comment(99, 7), comment(100, 8, body="never mind")]}
        calls = []

        def api(*a):
            calls.append(a)
            if a[0] == "graphql":
                return dict(data=dict(repository=dict(p7=dict(author=dict(login="author"), reviewThreads=dict(pageInfo=dict(hasNextPage=False, endCursor=None), nodes=[
                    dict(isResolved=True, isOutdated=False, comments=dict(nodes=[dict(databaseId=0)]))])))))
            return pages[int(next(x for x in a if x.startswith("page="))[5:])]

        real = rb.api
        rb.api = api
        try:
            with tempfile.TemporaryDirectory() as d:
                rb.collect(argparse.Namespace(data=d, days=180, today="2026-11-21"))
                c = rb.load(os.path.join(d, "comments.json"))
                prs = rb.load(os.path.join(d, "prs.json"))
        finally:
            rb.api = real
        self.assertEqual(c["since"], "2026-05-25")
        self.assertEqual(len(c["comments"]), 101)
        self.assertEqual(sum(a[0] != "graphql" for a in calls), 2)
        self.assertEqual(list(prs), ["7"])
        self.assertEqual(prs["7"]["threads"]["0"], [True, False])

    def test_collect_follows_review_thread_pages(self):
        def thread(id, resolved, outdated):
            return dict(isResolved=resolved, isOutdated=outdated, comments=dict(nodes=[dict(databaseId=id)]))

        def api(*a):
            if a[0] != "graphql":
                return [comment(1, 7), comment(2, 7)] if "page=1" in a else []
            if "after:" in a[-1]:
                self.assertIn('after:"c1"', a[-1])
                return dict(data=dict(repository=dict(pullRequest=dict(reviewThreads=dict(
                    pageInfo=dict(hasNextPage=False, endCursor=None), nodes=[thread(2, True, True)])))))
            return dict(data=dict(repository=dict(p7=dict(author=dict(login="author"), reviewThreads=dict(
                pageInfo=dict(hasNextPage=True, endCursor="c1"), nodes=[thread(1, False, False)])))))

        real = rb.api
        rb.api = api
        try:
            with tempfile.TemporaryDirectory() as d:
                rb.collect(argparse.Namespace(data=d, days=180, today="2026-11-21"))
                prs = rb.load(os.path.join(d, "prs.json"))
        finally:
            rb.api = real
        self.assertEqual(prs["7"]["threads"], {"1": [False, False], "2": [True, True]})

    def collect_with(self, api):
        real = rb.api
        rb.api = api
        try:
            with tempfile.TemporaryDirectory() as d, contextlib.redirect_stdout(io.StringIO()) as out:
                rb.collect(argparse.Namespace(data=d, days=180, today="2026-11-21"))
        finally:
            rb.api = real
        return out.getvalue()

    def test_collect_summary_prints_the_rest_page_count(self):
        def api(*a):
            if a[0] != "graphql":
                return [comment(1, 7)]
            return dict(data=dict(repository=dict(p7=dict(author=dict(login="author"), reviewThreads=dict(
                pageInfo=dict(hasNextPage=False, endCursor=None), nodes=[])))))

        self.assertIn(", 1 REST pages, ", self.collect_with(api))

    def test_collect_fails_clearly_when_a_follow_up_thread_page_is_missing(self):
        def api(*a):
            if a[0] != "graphql":
                return [comment(1, 7)] if "page=1" in a else []
            if "after:" in a[-1]:
                return dict(data=dict(repository=dict(pullRequest=None)))
            return dict(data=dict(repository=dict(p7=dict(author=dict(login="author"), reviewThreads=dict(
                pageInfo=dict(hasNextPage=True, endCursor="c1"), nodes=[])))))

        with self.assertRaises(SystemExit) as raised:
            self.collect_with(api)
        self.assertIn("PR 7", str(raised.exception))

    def test_api_reports_errors_of_a_partial_graphql_response(self):
        body = '{"data":{"repository":{"p1":null}},"errors":[{"message":"boom"}]}'
        real = rb.subprocess.run
        rb.subprocess.run = lambda *a, **k: argparse.Namespace(returncode=1, stdout=body, stderr="gone")
        try:
            with contextlib.redirect_stderr(io.StringIO()) as err:
                data = rb.api("graphql")
        finally:
            rb.subprocess.run = real
        self.assertEqual(data["data"]["repository"], dict(p1=None))
        self.assertIn("boom", err.getvalue())

    def test_findings_rebuild_threads_and_skip_the_pr_author(self):
        cs = [rb.slim(comment(1, 7)), rb.slim(comment(2, 7, author="author", reply_to=1, created="2026-11-02T00:00:00Z", body="Good catch, fixed")),
              rb.slim(comment(3, 7, author="author")), rb.slim(comment(4, 8)), rb.slim(comment(5, 7, author="claude[bot]", bot=True))]
        prs = {"7": dict(author="author", threads={"5": [True, False]})}
        rows = list(rb.findings_from(cs, prs))
        self.assertEqual([(r["url"][-2:], r["valid"], r["bot"]) for r in rows], [("r1", "valid", False), ("r5", "likely", True)])


class Catalog(unittest.TestCase):
    cfg = "root = true\n[*.cs]\ndotnet_diagnostic.CA1510.severity = warning\n[src/**Test*/**.cs]\ndotnet_diagnostic.IDE0051.severity = warning\n"

    def test_general_section_counts_as_enforced(self):
        self.assertTrue(rb.enforced_on_master("CA1510", rb.editorconfig_rules(self.cfg), {}))

    def test_scoped_section_does_not(self):
        self.assertFalse(rb.enforced_on_master("IDE0051", rb.editorconfig_rules(self.cfg), {}))

    def test_sdk_default_warning_counts(self):
        self.assertTrue(rb.enforced_on_master("CA2200", [], {"CA2200": "warning"}))


class Hits(unittest.TestCase):
    root = "/w/nethermind"

    def test_build_lines_are_counted_once_and_scoped(self):
        lines = [
            "/w/nethermind/src/Nethermind/Nethermind.Core/A.cs(3,5): warning IDE0051: Private member unused [/w/x.csproj]",
            "/w/nethermind/src/Nethermind/Nethermind.Core/A.cs(3,5): warning IDE0051: Private member unused [/w/y.csproj]",
            "/w/nethermind/src/Nethermind/Nethermind.Core.Test/B.cs(1,1): warning IDE0051: x [/w/z.csproj]",
            "/w/nethermind/src/Nethermind/Nethermind.Core/obj/Release/G.cs(1,1): warning IDE0051: x",
            "/w/nethermind/src/Nethermind/Nethermind.Core/A.cs(9,1): warning CA1510: other id",
            "/elsewhere/C.cs(1,1): warning IDE0051: outside the checkout",
        ]
        found = rb.parse_diagnostics(lines, self.root, {"IDE0051"})
        self.assertEqual([(h["file"], h["scope"]) for h in found], [
            ("src/Nethermind/Nethermind.Core.Test/B.cs", "test"),
            ("src/Nethermind/Nethermind.Core/A.cs", "prod"),
            ("src/Nethermind/Nethermind.Core/obj/Release/G.cs", "generated"),
        ])

    def test_benchmark_and_tools_tests_are_not_prod(self):
        self.assertEqual(rb.scope("src/Nethermind/Nethermind.Benchmark/X.cs"), "test")
        self.assertEqual(rb.scope("tools/Kute/Kute.Test/X.cs"), "test")
        self.assertEqual(rb.scope("tools/Kute/Kute/X.cs"), "prod")

    def test_scope_matches_the_editorconfig_exclusion(self):
        self.assertEqual(rb.scope("src/Nethermind/Nethermind.Core/Tests/X.cs"), "prod")
        self.assertIn("src/Nethermind/{*Test*,*Benchmark*}/**.cs", rb.overlay(["IDE0051"], exclude_tests=True))


class Propose(unittest.TestCase):
    def test_verdicts(self):
        self.assertEqual(rb.verdict(8, True), "skip: already enforced on master")
        self.assertEqual(rb.verdict(4, False), "skip: 4 prod hits < 5")
        self.assertEqual(rb.verdict(61, False), "defer: 61 prod hits > 60")
        self.assertEqual(rb.verdict(60, False), "propose")

    def test_groups_respect_cap(self):
        self.assertEqual(rb.group_by_cap(["A", "B", "C"], {"A": 11, "B": 33, "C": 23}), [["A", "C"], ["B"]])


class Guard(unittest.TestCase):
    row = ["+NETH012 | Reliability | Warning | Rent without Return."]
    hit = "src/Nethermind/Nethermind.Core/A.cs"

    def test_route_a_files_with_hits_pass(self):
        self.assertEqual(rb.check_paths([("M", ".editorconfig"), ("M", self.hit)], [], None, {self.hit}), [])

    def test_route_a_file_without_hit_fails(self):
        self.assertTrue(rb.check_paths([("M", "src/Nethermind/Nethermind.Core/B.cs")], [], None, {self.hit}))
        self.assertTrue(rb.check_paths([("A", self.hit)], [], None, {self.hit}))

    def test_listed_hit_outside_the_fix_policy_fails(self):
        for path in (rb.FIX_DENIED_PREFIXES[0] + "EmptyCatchBlockAnalyzer.cs", "src/Nethermind/Nethermind.Core.Test/A.cs",
                     "tools/Tool/Tool.Test/A.cs", "eng/A.cs", "src/Nethermind/Nethermind.Serialization.SszGenerator/G.cs"):
            self.assertTrue(rb.check_paths([("M", path)], [], None, {path}), path)
            self.assertTrue(rb.check_paths([("M", path)], [], "NETH012", {path}), path)

    def test_deny_tools_cover_every_denied_prefix(self):
        tools = rb.fix_deny_tools().split(",")
        for prefix in rb.FIX_DENIED_PREFIXES:
            self.assertIn(f"Edit({prefix}**)", tools)
            self.assertIn(f"Write({prefix}**)", tools)
        self.assertEqual(len(tools), 2 * len(rb.FIX_DENIED_PREFIXES))

    def test_route_b_cannot_change_existing_analyzers(self):
        existing = "src/Nethermind/Nethermind.Analyzers/EmptyCatchBlockAnalyzer.cs"
        for st in ("M", "D"):
            self.assertTrue(rb.check_paths([(st, existing)], [], "NETH012"), st)

    def test_editorconfig_overlay_append_passes(self):
        overlay = rb.overlay(["NETH012"], True).splitlines()
        diff = ["+" + l for l in overlay]
        self.assertEqual(rb.check_paths([("M", ".editorconfig")], [], None, (), diff, {"NETH012"}), [])

    def test_editorconfig_overlay_for_unlisted_id_fails(self):
        diff = ["+" + l for l in rb.overlay(["NETH012"], True).splitlines()]
        self.assertTrue(rb.check_paths([("M", ".editorconfig")], [], None, (), diff))
        self.assertTrue(rb.check_paths([("M", ".editorconfig")], [], None, (), diff, {"NETH013"}))

    def test_editorconfig_cannot_silence_an_enforced_rule_in_test_sections(self):
        diff = ["+" + l for l in rb.overlay(["NETH012"], True).splitlines()] + ["+[tools/*/{*Test*,*Benchmark*}/**.cs]", "+dotnet_diagnostic.CA2012.severity = none"]
        self.assertTrue(rb.check_paths([("M", ".editorconfig")], [], None, (), diff, {"NETH012"}))

    def test_editorconfig_cannot_silence_a_rule(self):
        for diff in (["-dotnet_diagnostic.CA1851.severity = warning", "+dotnet_diagnostic.CA1851.severity = none"],
                     ["+[*.cs]", "+dotnet_diagnostic.CA1851.severity = none"],
                     ["+[*.cs]", "+dotnet_diagnostic.CA1851.severity = suggestion"],
                     ["+[src/Nethermind/Nethermind.Core/**.cs]", "+dotnet_diagnostic.NETH012.severity = none"],
                     ["+dotnet_diagnostic.NETH012.severity = warning"],
                     ["+[*.cs]", "+root = true"]):
            self.assertTrue(rb.check_paths([("M", ".editorconfig")], [], None, (), diff), diff)

    def test_patch_changes_collects_editorconfig_lines(self):
        patch = "\n".join(["diff --git a/.editorconfig b/.editorconfig", "index 1111111..2222222 100644", "--- a/.editorconfig", "+++ b/.editorconfig",
                           "@@ -10 +10 @@", "-dotnet_diagnostic.CA1851.severity = warning", "+dotnet_diagnostic.CA1851.severity = none"])
        changes, _, edits = rb.patch_changes(patch)
        self.assertTrue(rb.check_paths(changes, [], None, (), edits))

    def test_build_files_fail(self):
        for p in ("src/Nethermind/Directory.Build.props", "src/Nethermind/Nethermind.Core/Nethermind.Core.csproj", "x.globalconfig"):
            self.assertTrue(rb.check_paths([("A", p)], [], "NETH012", {p}), p)

    def test_appended_unshipped_row_passes(self):
        changes = [("A", "src/Nethermind/Nethermind.Analyzers/RentAnalyzer.cs"), ("M", UNSHIPPED)]
        self.assertEqual(rb.check_paths(changes, self.row, "NETH012"), [])

    def test_analyzer_dirs_need_route_b(self):
        self.assertTrue(rb.check_paths([("A", "src/Nethermind/Nethermind.Analyzers/RentAnalyzer.cs")], [], None))

    def test_edited_unshipped_row_fails(self):
        diff = ["-NETH011 | Maintainability | Warning | old", "+NETH011 | Maintainability | Warning | new"] + self.row
        self.assertTrue(rb.check_paths([("M", UNSHIPPED)], diff, "NETH012"))

    def test_two_rows_fail(self):
        self.assertTrue(rb.check_paths([("M", UNSHIPPED)], self.row * 2, "NETH012"))

    def test_row_for_other_id_fails(self):
        self.assertTrue(rb.check_paths([("M", UNSHIPPED)], self.row, "NETH013"))

    def test_guarded_paths_and_other_markdown_fail(self):
        for p in (".github/workflows/x.yml", ".agents/rules/robustness.md", "scripts/rule-bot/rule_bot.py", "README.md",
                  "src/Nethermind/Nethermind.Analyzers/AnalyzerReleases.Shipped.md", "scripts/rule-bot/x.cs"):
            self.assertTrue(rb.check_paths([("M", p)], [], "NETH012", {p}), p)

    def test_patch_changes(self):
        patch = "\n".join([
            "diff --git a/src/Nethermind/Nethermind.Core/A.cs b/src/Nethermind/Nethermind.Core/A.cs",
            "index 1111111..2222222 100644", f"--- a/{self.hit}", f"+++ b/{self.hit}", "@@ -1 +1 @@", "--- a", "+++ b",
            "diff --git a/src/Nethermind/Nethermind.Analyzers/R.cs b/src/Nethermind/Nethermind.Analyzers/R.cs",
            "new file mode 100644", "index 0000000..3333333", "--- /dev/null", "+++ b/src/Nethermind/Nethermind.Analyzers/R.cs", "@@ -0,0 +1 @@", "+class R { }",
            f"diff --git a/{UNSHIPPED} b/{UNSHIPPED}", "index 4444444..5555555 100644", f"--- a/{UNSHIPPED}", f"+++ b/{UNSHIPPED}", "@@ -3,0 +4 @@",
            self.row[0],
            "diff --git a/scripts/rule-bot/run b/scripts/rule-bot/run", "new file mode 100755", "index 0000000..6666666",
            "diff --git a/src/a.cs b/src/b.cs", "similarity index 100%", "rename from src/a.cs", "rename to src/b.cs",
        ])
        changes, udiff, _ = rb.patch_changes(patch)
        self.assertEqual(changes, [("M", self.hit), ("A", "src/Nethermind/Nethermind.Analyzers/R.cs"), ("M", UNSHIPPED),
                                   ("X", "scripts/rule-bot/run"), ("X", "src/b.cs")])
        self.assertEqual(udiff, self.row)

    def test_patch_changes_sees_files_without_git_header(self):
        patch = "\n".join(["--- /dev/null", "+++ b/.github/workflows/evil.yml", "@@ -0,0 +1 @@", "+on: push"])
        changes, *_ = rb.patch_changes(patch)
        self.assertEqual(changes, [("X", ".github/workflows/evil.yml")])
        self.assertTrue(rb.check_paths(changes, [], None, set()))

    def test_patch_changes_sees_second_file_inside_git_block(self):
        patch = "\n".join([
            f"diff --git a/{self.hit} b/{self.hit}", "index 1111111..2222222 100644", f"--- a/{self.hit}", f"+++ b/{self.hit}",
            "@@ -1 +1 @@", "-a", "+b", "--- /dev/null", "+++ b/src/Nethermind/Directory.Build.props", "@@ -0,0 +1 @@", "+<Project />"])
        changes, *_ = rb.patch_changes(patch)
        self.assertIn(("X", "src/Nethermind/Directory.Build.props"), changes)

    def test_test_class_is_validated(self):
        self.assertEqual(rb.test_argv("FooAnalyzerTests")[-1], "FullyQualifiedName~FooAnalyzerTests")
        for bad in ("Foo; rm -rf /", "Foo'", "", None):
            with self.assertRaises(SystemExit):
                rb.test_argv(bad)


class RouteB(unittest.TestCase):
    def test_next_id_skips_reserved(self):
        self.assertEqual(rb.next_neth_id({1, 2, 10, 11}, {9}), "NETH012")
        self.assertEqual(rb.next_neth_id({11}, {12}), "NETH013")

    def test_tried_comes_from_bot_pull_requests(self):
        today = datetime.date(2026, 11, 20)
        prs = [dict(headRefName="rule-bot/linq-neth012-1", createdAt="2026-11-01T00:00:00Z"),
               dict(headRefName="rule-bot/sleep-neth013-2", createdAt="2026-09-01T00:00:00Z"),
               dict(headRefName="rule-bot/nullforg-neth014-3", createdAt="2026-11-01T00:00:00Z")]
        self.assertEqual(rb.recently_tried(prs, ["linq", "sleep", "null", "nullforg"], today), {"linq", "nullforg"})

    def test_rotation_moves_weekly(self):
        q = ["a", "b", "c"]
        day = datetime.date(2026, 11, 20)
        weeks = {tuple(rb.rotate(q, day + datetime.timedelta(days=7 * n))) for n in range(3)}
        self.assertEqual(len(weeks), 3)

    def test_plan_queues_passing_clusters_outside_cooldown(self):
        with tempfile.TemporaryDirectory() as d:
            routes = rb.load(os.path.join(rb.HERE, "routes.json"))
            b = sorted(k for k, r in routes.items() if r["route"] == "B")[:5]
            gates = {k: dict(prs=50 - n, **{"pass": n != 1}) for n, k in enumerate(b)}
            rb.save(os.path.join(d, "gates.json"), gates)
            rb.save(os.path.join(d, "catalog.json"), {})
            os.makedirs(os.path.join(d, os.path.dirname(UNSHIPPED)))
            open(os.path.join(d, UNSHIPPED), "w").write("NETH011 | Usage | Warning | x\n")
            today = next(datetime.date(2026, 11, 20) + datetime.timedelta(days=n) for n in range(21)
                         if rb.rotate([1, 2, 3], datetime.date(2026, 11, 20) + datetime.timedelta(days=n))[0] == 1)
            bot = json.dumps([dict(number=5, state="CLOSED", headRefName=f"rule-bot/{b[2]}-neth011-1", createdAt=f"{today}T00:00:00Z", closedAt=f"{today}T00:00:00Z")])
            real = rb.gh
            rb.gh = lambda *a, **k: bot if "--label" in a else "[]"
            try:
                rb.plan(argparse.Namespace(data=d, repo=d, offline=False, github_output=None, today=today.isoformat()))
            finally:
                rb.gh = real
            p = rb.load(os.path.join(d, "plan.json"))
            self.assertEqual([q["cluster"] for q in p["route_b_queue"]], [b[0]])
            self.assertEqual(p["route_b"], b[0])
            self.assertEqual(p["route_b_id"], "NETH012")

    def test_plan_tries_one_cluster_per_week_and_each_once_per_cycle(self):
        routes = rb.load(os.path.join(rb.HERE, "routes.json"))
        b = sorted(k for k, r in routes.items() if r["route"] == "B")[:3]
        picked = []
        for n in range(6):
            with tempfile.TemporaryDirectory() as d:
                rb.save(os.path.join(d, "gates.json"), {k: dict(prs=50, **{"pass": True}) for k in b})
                rb.save(os.path.join(d, "catalog.json"), {})
                os.makedirs(os.path.join(d, os.path.dirname(UNSHIPPED)))
                open(os.path.join(d, UNSHIPPED), "w").write("NETH011 | Usage | Warning | x\n")
                today = datetime.date(2026, 11, 20) + datetime.timedelta(days=7 * n)
                rb.plan(argparse.Namespace(data=d, repo=d, offline=True, github_output=None, today=today.isoformat()))
                p = rb.load(os.path.join(d, "plan.json"))
                self.assertEqual(len(p["route_b_queue"]), 1)
                picked.append(p["route_b"])
        self.assertEqual(sorted(picked[:3]), sorted(b))
        self.assertEqual(picked[3:], picked[:3])

    def test_rejected_route_a_ids_cool_down(self):
        routes = {"unused": dict(route="A", ids=["IDE0051", "IDE0052"]), "unusedx": dict(route="A", ids=["IDE0060"]),
                  "sleep": dict(route="B", ids=[])}
        today = datetime.date(2026, 11, 20)
        prs = [dict(number=1, state="CLOSED", headRefName="rule-bot/unused-ide0051-99", closedAt="2026-11-01T00:00:00Z"),
               dict(number=2, state="MERGED", headRefName="rule-bot/unused-ide0052-98", closedAt="2026-11-01T00:00:00Z"),
               dict(number=3, state="CLOSED", headRefName="rule-bot/unusedx-ide0060-97", closedAt="2026-08-01T00:00:00Z"),
               dict(number=4, state="OPEN", headRefName="rule-bot/unused-ide0052-96", closedAt=None),
               dict(number=5, state="CLOSED", headRefName="rule-bot/sleep-neth012-95", closedAt="2026-11-01T00:00:00Z")]
        self.assertEqual(rb.rejected_ids(prs, routes, today), {"IDE0051": 1})

    def test_plan_and_propose_skip_a_rejected_rule(self):
        routes = rb.load(os.path.join(rb.HERE, "routes.json"))
        k = next(k for k, r in routes.items() if r["route"] == "A" and len(r["ids"]) > 1)
        first, rest = routes[k]["ids"][0], routes[k]["ids"][1:]
        bot = json.dumps([dict(number=42, state="CLOSED", headRefName=f"rule-bot/{k}-{first.lower()}-1",
                               createdAt="2026-10-01T00:00:00Z", closedAt="2026-11-10T00:00:00Z")])
        with tempfile.TemporaryDirectory() as d:
            rb.save(os.path.join(d, "gates.json"), {k: dict(prs=50, **{"pass": True})})
            rb.save(os.path.join(d, "catalog.json"), {})
            real = rb.gh
            rb.gh = lambda *a, **kw: bot if "--label" in a and "all" in a else "[]"
            try:
                rb.plan(argparse.Namespace(data=d, repo=d, offline=False, github_output=None, today="2026-11-20"))
                p = rb.load(os.path.join(d, "plan.json"))
                self.assertEqual(p["measure_ids"], rest)
                self.assertEqual(p["rejected_ids"], {first: 42})
                rb.save(os.path.join(d, "hits.json"), dict(counts={i: dict(prod=10, test=0) for i in routes[k]["ids"]}))
                rb.propose(argparse.Namespace(data=d, offline=False, github_output=None))
            finally:
                rb.gh = real
            c = rb.load(os.path.join(d, "candidates.json"))
            v = {x["id"]: x["verdict"] for x in c["decisions"]}
            self.assertTrue(v[first].startswith("skip: rule-bot PR #42 closed without merge"))
            self.assertTrue(all(first not in pk["ids"] for pk in c["packages"]))

    def test_plan_skips_the_measure_matrix_when_there_is_nothing_to_measure(self):
        routes = rb.load(os.path.join(rb.HERE, "routes.json"))
        a = next(k for k, r in routes.items() if r["route"] == "A")
        b = next(k for k, r in routes.items() if r["route"] == "B")
        open_pr = '[{"number": 7, "state": "OPEN", "headRefName": "rule-bot/x-1", "createdAt": "2020-01-01T00:00:00Z"}]'
        cases = (
            ("nothing", {}, "[]", "true"),
            ("route a only", {a: 50}, "[]", "false"),
            ("route b only", {b: 50}, "[]", "false"),
            ("bot pr open", {a: 50, b: 49}, open_pr, "true"),
        )
        for name, prs, open_prs, expected in cases:
            with self.subTest(name), tempfile.TemporaryDirectory() as d:
                rb.save(os.path.join(d, "gates.json"), {k: dict(prs=n, **{"pass": True}) for k, n in prs.items()})
                rb.save(os.path.join(d, "catalog.json"), {})
                os.makedirs(os.path.join(d, os.path.dirname(UNSHIPPED)))
                open(os.path.join(d, UNSHIPPED), "w").write("NETH011 | Usage | Warning | x\n")
                out = os.path.join(d, "github_output")
                real = rb.gh
                rb.gh = lambda *x, **k: open_prs if "--label" in x else "[]"
                try:
                    rb.plan(argparse.Namespace(data=d, repo=d, offline=False, github_output=out, today="2026-11-12"))
                finally:
                    rb.gh = real
                self.assertIn(f"skip_measure={expected}\n", open(out).read())

    def test_plan_attempts_no_analyzer_while_a_bot_pr_is_open(self):
        routes = rb.load(os.path.join(rb.HERE, "routes.json"))
        b = sorted(k for k, r in routes.items() if r["route"] == "B")[:2]
        for open_prs, expected in (('[{"number": 7, "state": "OPEN", "headRefName": "rule-bot/x-1", "createdAt": "2020-01-01T00:00:00Z"}]', None), ("[]", b[0])):
            with tempfile.TemporaryDirectory() as d:
                rb.save(os.path.join(d, "gates.json"), {k: dict(prs=50, **{"pass": True}) for k in b})
                rb.save(os.path.join(d, "catalog.json"), {})
                os.makedirs(os.path.join(d, os.path.dirname(UNSHIPPED)))
                open(os.path.join(d, UNSHIPPED), "w").write("NETH011 | Usage | Warning | x\n")
                real = rb.gh
                rb.gh = lambda *a, **k: open_prs if "--label" in a else "[]"
                try:
                    rb.plan(argparse.Namespace(data=d, repo=d, offline=False, github_output=None, today="2026-11-12"))
                finally:
                    rb.gh = real
                p = rb.load(os.path.join(d, "plan.json"))
                self.assertEqual(p["route_b"], expected)
                self.assertEqual(bool(p["route_b_queue"]), expected is not None)

    def test_strip_initialize_block(self):
        src = "class A { public override void Initialize(AnalysisContext context) { context.Register(x => { Go(); }); } void B() { } }"
        self.assertEqual(rb.strip_initialize(src), "class A { public override void Initialize(AnalysisContext context) { context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None); context.EnableConcurrentExecution(); } void B() { } }")

    def test_strip_initialize_expression_body(self):
        src = "public override void Initialize(AnalysisContext ctx) => ctx.Register(Go);"
        self.assertEqual(rb.strip_initialize(src), "public override void Initialize(AnalysisContext ctx) { ctx.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None); ctx.EnableConcurrentExecution(); }")

    def test_precision_gate_fails_without_hits(self):
        self.assertFalse(rb.precision([], 0)[2])

    def test_precision_gate(self):
        labels = [dict(label="true_positive")] * 18 + [dict(label="false_positive")] * 2
        self.assertEqual(rb.precision(labels, 40), (18, 20, True))
        self.assertFalse(rb.precision(labels[2:] + [dict(label="false_positive")] * 2, 40)[2])
        self.assertFalse(rb.precision(labels[:19], 40)[2])
        self.assertTrue(rb.precision([dict(label="true_positive")] * 7, 7)[2])
        self.assertFalse(rb.precision([dict(label="true_positive")] * 6 + [dict(label="false_positive")], 7)[2])


class Solutions(unittest.TestCase):
    def test_ci_solutions_follow_build_workflows(self):
        wf = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", ".github", "workflows")
        s = rb.ci_solutions(wf)
        self.assertIn("src/Nethermind/Nethermind.slnx", s)
        self.assertIn("src/Nethermind/EthereumTests.slnx", s)
        self.assertIn("tools/Kute/Kute.slnx", s)
        self.assertNotIn("tools/SchemaGenerator/SchemaGenerator.slnx", s)
        for x in s:
            self.assertTrue(os.path.exists(os.path.join(wf, "..", "..", x)), x)

    def test_log_names_round_trip(self):
        self.assertEqual(rb.solution_of(rb.log_name_of("tools/Kute/Kute.slnx")), "tools/Kute/Kute.slnx")

    def test_rebuild_set_uses_prod_hits_only(self):
        h = dict(hits=[dict(id="A", scope="prod", solutions=["x.slnx", "y.slnx"]), dict(id="A", scope="test", solutions=["z.slnx"]),
                       dict(id="B", scope="prod", solutions=["w.slnx"])])
        self.assertEqual(rb.rebuild_set(h, ["A"]), ["x.slnx", "y.slnx"])


class Overlay(unittest.TestCase):
    def test_prod_only_overlay(self):
        s = rb.overlay(["IDE0051"], exclude_tests=True)
        self.assertIn("[*.cs]\ndotnet_diagnostic.IDE0051.severity = warning", s)
        self.assertIn("[src/Nethermind/{*Test*,*Benchmark*}/**.cs]\ndotnet_diagnostic.IDE0051.severity = none", s)


if __name__ == "__main__":
    unittest.main()
