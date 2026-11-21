# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import argparse
import datetime
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
    base = dict(n=20, valid=14, human=4, pr_set=list(range(8)), author_set=["x", "y"])

    def row(self, pr, author, bot=False, valid="valid"):
        return dict(pr=pr, author=author, bot=bot, valid=valid)

    def test_new_rows_lift_cluster_over_gates(self):
        st = rb.gate_cluster(self.base, [self.row(100, "a"), self.row(101, "b")])
        self.assertTrue(st["pass"], st["fails"])

    def test_bot_resolved_only_is_not_valid(self):
        st = rb.gate_cluster(dict(n=0, valid=0, human=0), [self.row(1, "claude[bot]", bot=True, valid="likely")])
        self.assertEqual(st["valid"], 0)

    def test_baseline_prs_and_commenters_are_not_counted_twice(self):
        base = dict(self.base, pr_set=list(range(7)) + [100], author_set=["a", "x"])
        st = rb.gate_cluster(base, [self.row(100, "a"), self.row(101, "a"), self.row(102, "b")])
        self.assertEqual((st["prs"], st["commenters"], st["n"]), (10, 3, 23))

    def test_two_people_in_both_periods_fail_the_commenter_gate(self):
        base = dict(self.base, author_set=["a", "b"])
        st = rb.gate_cluster(base, [self.row(100, "a"), self.row(101, "b")])
        self.assertIn("commenters", st["fails"])

    def test_missing_humans_fails(self):
        st = rb.gate_cluster(dict(self.base, pr_set=list(range(20)), author_set=list("abcde"), human=1), [])
        self.assertIn("human", st["fails"])


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


class Baseline(unittest.TestCase):
    def test_route_b_clusters_have_trusted_samples(self):
        baseline = rb.load(os.path.join(os.path.dirname(rb.__file__), "baseline.json"))
        routes = rb.load(os.path.join(os.path.dirname(rb.__file__), "routes.json"))
        for k, r in routes.items():
            if r["route"] == "B":
                samples = baseline["clusters"][k].get("samples", [])
                self.assertTrue(samples, k)
                self.assertTrue(all(s["body"] and not rb.is_bot(s["author"]) for s in samples), k)


class RouteB(unittest.TestCase):
    def test_next_id_skips_reserved(self):
        self.assertEqual(rb.next_neth_id({1, 2, 10, 11}, {9}), "NETH012")
        self.assertEqual(rb.next_neth_id({11}, {12}), "NETH013")

    def test_cooldown(self):
        today = datetime.date(2026, 11, 20)
        self.assertTrue(rb.recently_tried({"linq": "2026-11-01"}, "linq", today))
        self.assertFalse(rb.recently_tried({"linq": "2026-09-01"}, "linq", today))
        self.assertFalse(rb.recently_tried({}, "linq", today))

    def test_plan_queues_passing_clusters_outside_cooldown(self):
        with tempfile.TemporaryDirectory() as d:
            routes = rb.load(os.path.join(rb.HERE, "routes.json"))
            b = sorted(k for k, r in routes.items() if r["route"] == "B")[:5]
            gates = {k: dict(prs=50 - n, **{"pass": n != 1}) for n, k in enumerate(b)}
            rb.save(os.path.join(d, "gates.json"), gates)
            rb.save(os.path.join(d, "catalog.json"), {})
            rb.save(os.path.join(d, "route_b_history.json"), {b[2]: datetime.date.today().isoformat()})
            os.makedirs(os.path.join(d, os.path.dirname(UNSHIPPED)))
            open(os.path.join(d, UNSHIPPED), "w").write("NETH011 | Usage | Warning | x\n")
            rb.plan(argparse.Namespace(data=d, state=d, repo=d, offline=True, github_output=None))
            p = rb.load(os.path.join(d, "plan.json"))
            self.assertEqual([q["cluster"] for q in p["route_b_queue"]], [b[0], b[3], b[4]])
            self.assertEqual(p["route_b"], b[0])
            self.assertEqual(p["route_b_id"], "NETH012")

    def test_plan_attempts_no_analyzer_while_a_bot_pr_is_open(self):
        routes = rb.load(os.path.join(rb.HERE, "routes.json"))
        b = sorted(k for k, r in routes.items() if r["route"] == "B")[:2]
        for open_prs, expected in (('[{"number": 7}]', None), ("[]", b[0])):
            with tempfile.TemporaryDirectory() as d:
                rb.save(os.path.join(d, "gates.json"), {k: dict(prs=50, **{"pass": True}) for k in b})
                rb.save(os.path.join(d, "catalog.json"), {})
                os.makedirs(os.path.join(d, os.path.dirname(UNSHIPPED)))
                open(os.path.join(d, UNSHIPPED), "w").write("NETH011 | Usage | Warning | x\n")
                real = rb.gh
                rb.gh = lambda *a, **k: open_prs if "--label" in a else "[]"
                try:
                    rb.plan(argparse.Namespace(data=d, state=d, repo=d, offline=False, github_output=None))
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
