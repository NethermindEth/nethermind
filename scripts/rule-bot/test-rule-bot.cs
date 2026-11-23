#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false
#:include ../ci/script-process.helpers.cs
#:include rule-bot.helpers.cs

using System.Text.Json;
using static RuleBot;
using static RuleBotTestSupport;

Home = ScriptDirectory();
var repositoryRoot = Path.GetFullPath(Path.Combine(Home, "..", ".."));
const string Hit = "src/Nethermind/Nethermind.Core/A.cs";
string[] row = ["+NETH012 | Reliability | Warning | Rent without Return."];
const string EditorConfigSample = "root = true\n[*.cs]\ndotnet_diagnostic.CA1510.severity = warning\n[src/**Test*/**.cs]\ndotnet_diagnostic.IDE0051.severity = warning\n";
const string OpenBotPr = """[{"number": 7, "state": "OPEN", "headRefName": "rule-bot/x-1", "createdAt": "2020-01-01T00:00:00Z"}]""";
var day = new DateOnly(2026, 11, 20);

List<string> RouteKeys(string route) => Routes().Where(item => item.Value.Route == route).Select(item => item.Key).Order(StringComparer.Ordinal).ToList();
string[] OverlayDiff(string id) => Lines(Overlay([id], true)).Select(line => "+" + line).ToArray();

void SeedPlan(string directory, IEnumerable<(string Key, long Prs, bool Pass)> gates, bool unshipped = true)
{
    var gateData = gates.ToDictionary(gate => gate.Key, gate => new Gate(0, gate.Prs, 0, 0, 0, 0, [], gate.Pass));
    Save(Path.Combine(directory, "gates.json"), gateData);
    Save(Path.Combine(directory, "catalog.json"), new Dictionary<string, CatalogEntry>());
    if (!unshipped) return;
    Directory.CreateDirectory(Path.Combine(directory, Path.GetDirectoryName(Unshipped)!));
    File.WriteAllText(Path.Combine(directory, Unshipped), "NETH011 | Usage | Warning | x\n");
}

var tests = new (string Name, Func<Task> Body)[]
{
    ("test_author_agreement_is_valid", Sync(() => Equal("valid", Classify(Thread(["Good catch, fixed"]), "author")))),
    ("test_rebuttal_is_invalid", Sync(() => Equal("invalid", Classify(Thread(["This is intentional, by design"]), "author")))),
    ("test_resolved_without_reply_is_likely", Sync(() => Equal("likely", Classify(Thread([], resolved: true), "author")))),
    ("test_open_thread_is_unknown", Sync(() => Equal("unknown", Classify(Thread([]), "author")))),

    ("test_cluster_over_gates_passes", Sync(() =>
    {
        var rows = Enumerable.Range(0, 10).SelectMany(n => new[] { GateRow(n, "a"), GateRow(n, "b") }).Append(GateRow(10, "c")).ToList();
        True(GateCluster(rows).Pass);
    })),
    ("test_bot_resolved_only_is_not_valid", Sync(() => Equal(0L, GateCluster([GateRow(1, "claude[bot]", bot: true, valid: "likely")]).Valid))),
    ("test_prs_and_commenters_are_distinct", Sync(() =>
    {
        var state = GateCluster([GateRow(100, "a"), GateRow(100, "a"), GateRow(101, "a"), GateRow(102, "b")]);
        Equal("3,2,4", $"{state.Prs},{state.Commenters},{state.N}");
    })),
    ("test_two_commenters_fail_the_commenter_gate", Sync(() =>
        True(GateCluster(Enumerable.Range(0, 12).Select(n => GateRow(n, "ab"[n % 2].ToString())).ToList()).Fails!.Contains("commenters")))),
    ("test_missing_humans_fails", Sync(() =>
        True(GateCluster("abcdefghijklmnopqrst".Select((author, n) => GateRow(n, author.ToString(), bot: n > 0)).ToList()).Fails!.Contains("human")))),
    ("test_examples_come_from_trusted_human_rows", Sync(() =>
    {
        List<Finding> rows = [GateRow(1, "a", association: "MEMBER", url: "u1"), GateRow(2, "claude[bot]", bot: true, association: "NONE", url: "u2"),
            GateRow(3, "c", association: "CONTRIBUTOR", url: "u3"), GateRow(4, "d", valid: "invalid", association: "MEMBER", url: "u4")];
        var examples = TrustedExamples(rows);
        Equal("u1", string.Join(',', examples.Select(example => example.Url)));
        Equal("u1:a", string.Join(',', Parse<Gate>(Dumps(GateCluster(rows) with { Examples = examples })).Examples!.Select(example => $"{example.Url}:{example.Author}")));
    })),

    ("test_api_keeps_partial_graphql_data", async () =>
    {
        using var scope = new Seams();
        Exec = (_, _, _) => Task.FromResult((1, """{"data":{"repository":{"p1":null}}}""", "gone"));
        Equal("""{"data":{"repository":{"p1":null}}}""", await RealApi(["graphql"], default));
    }),
    ("test_in_window_drops_older_comments_and_dedupes", Sync(() =>
    {
        var output = InWindow([ApiRow(1, 5, created: "2026-05-01T00:00:00Z"), ApiRow(2, 5), ApiRow(2, 5, body: "edited")], "2026-05-25");
        Equal("2", string.Join(',', output.Keys));
        Equal("unused field", output[2].Body);
    })),
    ("test_collect_pages_until_short_page_and_counts_each_comment_once", async () =>
    {
        using var scope = new Seams();
        using var directory = new TempDirectory();
        var calls = new List<string[]>();
        var pages = new Dictionary<int, string>
        {
            [1] = Dumps(Enumerable.Range(0, 100).Select(n => ApiRow(n, 7)).ToList()),
            [2] = Dumps(new List<ApiComment> { ApiRow(99, 7), ApiRow(100, 8, body: "never mind") }),
        };
        Api = (args, _) =>
        {
            calls.Add(args);
            if (args[0] == "graphql") return Task.FromResult("""{"data":{"repository":{"p7":{"author":{"login":"author"},"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[{"isResolved":true,"isOutdated":false,"comments":{"nodes":[{"databaseId":0}]}}]}}}}}""");
            return Task.FromResult(pages[int.Parse(args.First(arg => arg.StartsWith("page=", StringComparison.Ordinal))[5..])]);
        };
        await Quiet(() => Collect(directory.Path, 180, "2026-11-21", default));
        var comments = Load<CommentFile>(Path.Combine(directory.Path, "comments.json"));
        var prs = Load<Dictionary<string, PrThreads>>(Path.Combine(directory.Path, "prs.json"));
        Equal("2026-05-25", comments.Since);
        Equal(101, comments.Comments.Count);
        Equal(2, calls.Count(args => args[0] != "graphql"));
        Equal("7", string.Join(',', prs.Keys));
        Equal("True,False", string.Join(',', prs["7"].Threads["0"]));
    }),
    ("test_collect_follows_review_thread_pages", async () =>
    {
        using var scope = new Seams();
        using var directory = new TempDirectory();
        static string ThreadJson(int id, bool resolved, bool outdated) => $$$$"""{"isResolved":{{{{(resolved ? "true" : "false")}}}},"isOutdated":{{{{(outdated ? "true" : "false")}}}},"comments":{"nodes":[{"databaseId":{{{{id}}}}}]}}""";
        Api = (args, _) =>
        {
            if (args[0] != "graphql")
            {
                List<ApiComment> page = args.Contains("page=1") ? [ApiRow(1, 7), ApiRow(2, 7)] : [];
                return Task.FromResult(Dumps(page));
            }
            if (args[^1].Contains("after:"))
            {
                True(args[^1].Contains("after:\"c1\""));
                return Task.FromResult("""{"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[""" + ThreadJson(2, true, true) + "]}}}}}");
            }
            return Task.FromResult("""{"data":{"repository":{"p7":{"author":{"login":"author"},"reviewThreads":{"pageInfo":{"hasNextPage":true,"endCursor":"c1"},"nodes":[""" + ThreadJson(1, false, false) + "]}}}}}");
        };
        await Quiet(() => Collect(directory.Path, 180, "2026-11-21", default));
        Equal("""{"1":[false,false],"2":[true,true]}""", Dumps(Load<Dictionary<string, PrThreads>>(Path.Combine(directory.Path, "prs.json"))["7"].Threads));
    }),
    ("test_collect_summary_prints_the_rest_page_count", async () =>
    {
        using var scope = new Seams();
        using var directory = new TempDirectory();
        Api = (args, _) => Task.FromResult(args[0] != "graphql"
            ? Dumps(new List<ApiComment> { ApiRow(1, 7) })
            : """{"data":{"repository":{"p7":{"author":{"login":"author"},"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}""");
        True((await Quiet(() => Collect(directory.Path, 180, "2026-11-21", default))).Contains(", 1 REST pages, "));
    }),
    ("test_collect_fails_clearly_when_a_follow_up_thread_page_is_missing", async () =>
    {
        using var scope = new Seams();
        using var directory = new TempDirectory();
        Api = (args, _) =>
        {
            if (args[0] != "graphql")
            {
                List<ApiComment> page = args.Contains("page=1") ? [ApiRow(1, 7)] : [];
                return Task.FromResult(Dumps(page));
            }
            return Task.FromResult(args[^1].Contains("after:")
                ? """{"data":{"repository":{"pullRequest":null}}}"""
                : """{"data":{"repository":{"p7":{"author":{"login":"author"},"reviewThreads":{"pageInfo":{"hasNextPage":true,"endCursor":"c1"},"nodes":[]}}}}}""");
        };
        var failure = await Throws(() => Quiet(() => Collect(directory.Path, 180, "2026-11-21", default)));
        True(failure.Message.Contains("PR 7"));
    }),
    ("test_api_reports_errors_of_a_partial_graphql_response", async () =>
    {
        using var scope = new Seams();
        Exec = (_, _, _) => Task.FromResult((1, """{"data":{"repository":{"p1":null}},"errors":[{"message":"boom"}]}""", "gone"));
        var original = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        string data;
        try { data = await RealApi(["graphql"], default); }
        finally { Console.SetError(original); }
        True(data.Contains("""{"p1":null}"""));
        True(error.ToString().Contains("boom"));
    }),
    ("test_findings_rebuild_threads_and_skip_the_pr_author", Sync(() =>
    {
        List<Comment> comments = [Slim(ApiRow(1, 7)), Slim(ApiRow(2, 7, author: "author", replyTo: 1, created: "2026-11-02T00:00:00Z", body: "Good catch, fixed")),
            Slim(ApiRow(3, 7, author: "author")), Slim(ApiRow(4, 8)), Slim(ApiRow(5, 7, author: "claude[bot]", bot: true))];
        var prs = Parse<Dictionary<string, PrThreads>>("""{"7": {"author": "author", "threads": {"5": [true, false]}}}""");
        var rows = FindingsFrom(comments, prs).ToList();
        Equal("r1:valid:False,r5:likely:True", string.Join(',', rows.Select(r => $"{r.Url[^2..]}:{r.Valid}:{r.Bot}")));
    })),

    ("test_general_section_counts_as_enforced", Sync(() => True(EnforcedOnMaster("CA1510", EditorConfigRules(EditorConfigSample), new Dictionary<string, string>())))),
    ("test_scoped_section_does_not", Sync(() => True(!EnforcedOnMaster("IDE0051", EditorConfigRules(EditorConfigSample), new Dictionary<string, string>())))),
    ("test_sdk_default_warning_counts", Sync(() => True(EnforcedOnMaster("CA2200", [], new Dictionary<string, string> { ["CA2200"] = "warning" })))),

    ("test_build_lines_are_counted_once_and_scoped", Sync(() =>
    {
        string[] lines =
        [
            "/w/nethermind/src/Nethermind/Nethermind.Core/A.cs(3,5): warning IDE0051: Private member unused [/w/x.csproj]",
            "/w/nethermind/src/Nethermind/Nethermind.Core/A.cs(3,5): warning IDE0051: Private member unused [/w/y.csproj]",
            "/w/nethermind/src/Nethermind/Nethermind.Core.Test/B.cs(1,1): warning IDE0051: x [/w/z.csproj]",
            "/w/nethermind/src/Nethermind/Nethermind.Core/obj/Release/G.cs(1,1): warning IDE0051: x",
            "/w/nethermind/src/Nethermind/Nethermind.Core/A.cs(9,1): warning CA1510: other id",
            "/elsewhere/C.cs(1,1): warning IDE0051: outside the checkout",
        ];
        var found = ParseDiagnostics(lines, "/w/nethermind", new HashSet<string> { "IDE0051" });
        Equal("src/Nethermind/Nethermind.Core.Test/B.cs:test,src/Nethermind/Nethermind.Core/A.cs:prod,src/Nethermind/Nethermind.Core/obj/Release/G.cs:generated",
            string.Join(',', found.Select(hit => $"{hit.File}:{hit.Scope}")));
    })),
    ("test_benchmark_and_tools_tests_are_not_prod", Sync(() =>
    {
        Equal("test", Scope("src/Nethermind/Nethermind.Benchmark/X.cs"));
        Equal("test", Scope("tools/Kute/Kute.Test/X.cs"));
        Equal("prod", Scope("tools/Kute/Kute/X.cs"));
    })),
    ("test_scope_matches_the_editorconfig_exclusion", Sync(() =>
    {
        Equal("prod", Scope("src/Nethermind/Nethermind.Core/Tests/X.cs"));
        True(Overlay(["IDE0051"], excludeTests: true).Contains("src/Nethermind/{*Test*,*Benchmark*}/**.cs"));
    })),

    ("test_verdicts", Sync(() =>
    {
        Equal("skip: already enforced on master", Verdict(8, true));
        Equal("skip: 4 prod hits < 5", Verdict(4, false));
        Equal("defer: 61 prod hits > 60", Verdict(61, false));
        Equal("propose", Verdict(60, false));
    })),
    ("test_groups_respect_cap", Sync(() =>
        Equal("A,C|B", string.Join('|', GroupByCap(["A", "B", "C"], new Dictionary<string, long> { ["A"] = 11, ["B"] = 33, ["C"] = 23 }).Select(group => string.Join(',', group)))))),

    ("test_route_a_files_with_hits_pass", Sync(() => Empty(CheckPaths([("M", ".editorconfig"), ("M", Hit)], [], null, [Hit])))),
    ("test_route_a_file_without_hit_fails", Sync(() =>
    {
        Any(CheckPaths([("M", "src/Nethermind/Nethermind.Core/B.cs")], [], null, [Hit]));
        Any(CheckPaths([("A", Hit)], [], null, [Hit]));
    })),
    ("test_listed_hit_outside_the_fix_policy_fails", Sync(() =>
    {
        foreach (var path in new[] { FixDeniedPrefixes[0] + "EmptyCatchBlockAnalyzer.cs", "src/Nethermind/Nethermind.Core.Test/A.cs", "tools/Tool/Tool.Test/A.cs", "eng/A.cs", "src/Nethermind/Nethermind.Serialization.SszGenerator/G.cs" })
        {
            Any(CheckPaths([("M", path)], [], null, [path]));
            Any(CheckPaths([("M", path)], [], "NETH012", [path]));
        }
    })),
    ("test_deny_tools_cover_every_denied_prefix", Sync(() =>
    {
        var tools = FixDenyTools().Split(',');
        foreach (var prefix in FixDeniedPrefixes)
        {
            True(tools.Contains($"Edit({prefix}**)"));
            True(tools.Contains($"Write({prefix}**)"));
        }
        Equal(2 * FixDeniedPrefixes.Length, tools.Length);
    })),
    ("test_route_b_cannot_change_existing_analyzers", Sync(() =>
    {
        foreach (var status in new[] { "M", "D" }) Any(CheckPaths([(status, "src/Nethermind/Nethermind.Analyzers/EmptyCatchBlockAnalyzer.cs")], [], "NETH012"));
    })),
    ("test_editorconfig_overlay_append_passes", Sync(() => Empty(CheckPaths([("M", ".editorconfig")], [], null, [], OverlayDiff("NETH012"), ["NETH012"])))),
    ("test_editorconfig_overlay_for_unlisted_id_fails", Sync(() =>
    {
        Any(CheckPaths([("M", ".editorconfig")], [], null, [], OverlayDiff("NETH012")));
        Any(CheckPaths([("M", ".editorconfig")], [], null, [], OverlayDiff("NETH012"), ["NETH013"]));
    })),
    ("test_editorconfig_cannot_silence_an_enforced_rule_in_test_sections", Sync(() =>
        Any(CheckPaths([("M", ".editorconfig")], [], null, [], [.. OverlayDiff("NETH012"), "+[tools/*/{*Test*,*Benchmark*}/**.cs]", "+dotnet_diagnostic.CA2012.severity = none"], ["NETH012"])))),
    ("test_editorconfig_cannot_silence_a_rule", Sync(() =>
    {
        string[][] diffs =
        [
            ["-dotnet_diagnostic.CA1851.severity = warning", "+dotnet_diagnostic.CA1851.severity = none"],
            ["+[*.cs]", "+dotnet_diagnostic.CA1851.severity = none"],
            ["+[*.cs]", "+dotnet_diagnostic.CA1851.severity = suggestion"],
            ["+[src/Nethermind/Nethermind.Core/**.cs]", "+dotnet_diagnostic.NETH012.severity = none"],
            ["+dotnet_diagnostic.NETH012.severity = warning"],
            ["+[*.cs]", "+root = true"],
        ];
        foreach (var diff in diffs) Any(CheckPaths([("M", ".editorconfig")], [], null, [], diff));
    })),
    ("test_patch_changes_collects_editorconfig_lines", Sync(() =>
    {
        var (changes, _, edits) = PatchChanges(string.Join('\n', "diff --git a/.editorconfig b/.editorconfig", "index 1111111..2222222 100644", "--- a/.editorconfig", "+++ b/.editorconfig",
            "@@ -10 +10 @@", "-dotnet_diagnostic.CA1851.severity = warning", "+dotnet_diagnostic.CA1851.severity = none"));
        Any(CheckPaths(changes, [], null, [], edits));
    })),
    ("test_build_files_fail", Sync(() =>
    {
        foreach (var path in new[] { "src/Nethermind/Directory.Build.props", "src/Nethermind/Nethermind.Core/Nethermind.Core.csproj", "x.globalconfig" })
            Any(CheckPaths([("A", path)], [], "NETH012", [path]));
    })),
    ("test_appended_unshipped_row_passes", Sync(() => Empty(CheckPaths([("A", "src/Nethermind/Nethermind.Analyzers/RentAnalyzer.cs"), ("M", Unshipped)], row, "NETH012")))),
    ("test_analyzer_dirs_need_route_b", Sync(() => Any(CheckPaths([("A", "src/Nethermind/Nethermind.Analyzers/RentAnalyzer.cs")], [], null)))),
    ("test_edited_unshipped_row_fails", Sync(() => Any(CheckPaths([("M", Unshipped)], ["-NETH011 | Maintainability | Warning | old", "+NETH011 | Maintainability | Warning | new", .. row], "NETH012")))),
    ("test_two_rows_fail", Sync(() => Any(CheckPaths([("M", Unshipped)], [.. row, .. row], "NETH012")))),
    ("test_row_for_other_id_fails", Sync(() => Any(CheckPaths([("M", Unshipped)], row, "NETH013")))),
    ("test_unshipped_row_removal_fails", Sync(() => Any(CheckPaths([("M", Unshipped)], [.. row, "-NETH001 | Design | Warning | old"], "NETH012")))),
    ("test_new_file_outside_analyzer_dirs_fails", Sync(() => Any(CheckPaths([("A", "src/Nethermind/Nethermind.Core/New.cs")], [], "NETH012")))),
    ("test_guarded_paths_and_other_markdown_fail", Sync(() =>
    {
        foreach (var path in new[] { ".github/workflows/x.yml", ".agents/rules/robustness.md", "scripts/rule-bot/rule-bot.cs", "README.md", "src/Nethermind/Nethermind.Analyzers/AnalyzerReleases.Shipped.md", "scripts/rule-bot/x.cs", ".github/x.cs", ".agents/x.cs" })
            True(CheckPaths([("M", path)], [], "NETH012", [path]).Single().Contains("outside the paths", StringComparison.Ordinal));
    })),
    ("test_patch_changes", Sync(() =>
    {
        var patch = string.Join('\n',
            "diff --git a/src/Nethermind/Nethermind.Core/A.cs b/src/Nethermind/Nethermind.Core/A.cs",
            "index 1111111..2222222 100644", $"--- a/{Hit}", $"+++ b/{Hit}", "@@ -1 +1 @@", "--- a", "+++ b",
            "diff --git a/src/Nethermind/Nethermind.Analyzers/R.cs b/src/Nethermind/Nethermind.Analyzers/R.cs",
            "new file mode 100644", "index 0000000..3333333", "--- /dev/null", "+++ b/src/Nethermind/Nethermind.Analyzers/R.cs", "@@ -0,0 +1 @@", "+class R { }",
            $"diff --git a/{Unshipped} b/{Unshipped}", "index 4444444..5555555 100644", $"--- a/{Unshipped}", $"+++ b/{Unshipped}", "@@ -3,0 +4 @@",
            row[0],
            "diff --git a/scripts/rule-bot/run b/scripts/rule-bot/run", "new file mode 100755", "index 0000000..6666666",
            "diff --git a/src/a.cs b/src/b.cs", "similarity index 100%", "rename from src/a.cs", "rename to src/b.cs");
        var (changes, unshipped, _) = PatchChanges(patch);
        Equal($"M:{Hit},A:src/Nethermind/Nethermind.Analyzers/R.cs,M:{Unshipped},X:scripts/rule-bot/run,X:src/b.cs", string.Join(',', changes.Select(change => $"{change.Status}:{change.Path}")));
        Equal(row[0], string.Join('\n', unshipped));
    })),
    ("test_patch_changes_sees_files_without_git_header", Sync(() =>
    {
        var (changes, _, _) = PatchChanges(string.Join('\n', "--- /dev/null", "+++ b/.github/workflows/evil.yml", "@@ -0,0 +1 @@", "+on: push"));
        Equal("X:.github/workflows/evil.yml", string.Join(',', changes.Select(change => $"{change.Status}:{change.Path}")));
        Any(CheckPaths(changes, [], null, []));
    })),
    ("test_patch_changes_sees_second_file_inside_git_block", Sync(() =>
    {
        var (changes, _, _) = PatchChanges(string.Join('\n', $"diff --git a/{Hit} b/{Hit}", "index 1111111..2222222 100644", $"--- a/{Hit}", $"+++ b/{Hit}",
            "@@ -1 +1 @@", "-a", "+b", "--- /dev/null", "+++ b/src/Nethermind/Directory.Build.props", "@@ -0,0 +1 @@", "+<Project />"));
        True(changes.Contains(("X", "src/Nethermind/Directory.Build.props")));
    })),
    ("test_test_class_is_validated", async () =>
    {
        Equal("FullyQualifiedName~FooAnalyzerTests", TestArgv("FooAnalyzerTests")[^1]);
        foreach (var bad in new[] { "Foo; rm -rf /", "Foo'", "", null })
            await Throws(() => { TestArgv(bad); return Task.CompletedTask; });
    }),

    ("test_next_id_skips_reserved", Sync(() =>
    {
        Equal("NETH012", NextNethId([1, 2, 10, 11], [9]));
        Equal("NETH013", NextNethId([11], [12]));
    })),
    ("test_tried_comes_from_bot_pull_requests", Sync(() =>
    {
        List<BotPr> prs = [new(1, "CLOSED", "rule-bot/linq-neth012-1", "2026-11-01T00:00:00Z", null),
            new(2, "CLOSED", "rule-bot/sleep-neth013-2", "2026-09-01T00:00:00Z", null),
            new(3, "CLOSED", "rule-bot/nullforg-neth014-3", "2026-11-01T00:00:00Z", null)];
        Equal("linq,nullforg", string.Join(',', RecentlyTried(prs, ["linq", "sleep", "null", "nullforg"], day).Order(StringComparer.Ordinal)));
    })),
    ("test_rotation_moves_weekly", Sync(() =>
        Equal(3, Enumerable.Range(0, 3).Select(n => string.Join(',', Rotate(["a", "b", "c"], day.AddDays(7 * n)))).Distinct().Count()))),
    ("test_plan_queues_passing_clusters_outside_cooldown", async () =>
    {
        using var scope = new Seams();
        using var directory = new TempDirectory();
        var b = RouteKeys("B").Take(5).ToList();
        SeedPlan(directory.Path, b.Select((key, n) => (key, 50L - n, n != 1)));
        var today = Enumerable.Range(0, 21).Select(day.AddDays).First(candidate => Rotate(["1", "2", "3"], candidate)[0] == "1");
        var stamp = today.ToString("yyyy-MM-dd");
        var bot = BotPrJson(5, "CLOSED", $"rule-bot/{b[2]}-neth011-1", $"{stamp}T00:00:00Z", $"{stamp}T00:00:00Z");
        Exec = (argv, _, _) => Task.FromResult((0, argv.Contains("--label") ? bot : "[]", ""));
        await Quiet(() => Plan(directory.Path, directory.Path, stamp, false, null, default));
        var plan = Load<PlanFile>(Path.Combine(directory.Path, "plan.json"));
        Equal(b[0], string.Join(',', plan.RouteBQueue!.Select(item => item.Cluster)));
        Equal(b[0], plan.RouteB);
        Equal("NETH012", plan.RouteBId);
    }),
    ("test_plan_tries_one_cluster_per_week_and_each_once_per_cycle", async () =>
    {
        var b = RouteKeys("B").Take(3).ToList();
        var picked = new List<string>();
        for (var n = 0; n < 6; n++)
        {
            using var directory = new TempDirectory();
            SeedPlan(directory.Path, b.Select(key => (key, 50L, true)));
            await Quiet(() => Plan(directory.Path, directory.Path, day.AddDays(7 * n).ToString("yyyy-MM-dd"), true, null, default));
            var plan = Load<PlanFile>(Path.Combine(directory.Path, "plan.json"));
            Equal(1, plan.RouteBQueue!.Count);
            picked.Add(plan.RouteB!);
        }
        Equal(string.Join(',', b.Order(StringComparer.Ordinal)), string.Join(',', picked.Take(3).Order(StringComparer.Ordinal)));
        Equal(string.Join(',', picked.Take(3)), string.Join(',', picked.Skip(3)));
    }),
    ("test_rejected_route_a_ids_cool_down", Sync(() =>
    {
        var routes = Parse<Dictionary<string, RouteConfig>>("""{"unused": {"route": "A", "ids": ["IDE0051", "IDE0052"]}, "unusedx": {"route": "A", "ids": ["IDE0060"]}, "sleep": {"route": "B", "ids": []}}""");
        List<BotPr> prs =
        [
            new(1, "CLOSED", "rule-bot/unused-ide0051-99", null, "2026-11-01T00:00:00Z"),
            new(2, "MERGED", "rule-bot/unused-ide0052-98", null, "2026-11-01T00:00:00Z"),
            new(3, "CLOSED", "rule-bot/unusedx-ide0060-97", null, "2026-08-01T00:00:00Z"),
            new(4, "OPEN", "rule-bot/unused-ide0052-96", null, null),
            new(5, "CLOSED", "rule-bot/sleep-neth012-95", null, "2026-11-01T00:00:00Z"),
        ];
        Equal("""{"IDE0051":1}""", Dumps(RejectedIds(prs, routes, day)));
    })),
    ("test_plan_and_propose_skip_a_rejected_rule", async () =>
    {
        using var scope = new Seams();
        using var directory = new TempDirectory();
        var entry = Routes().First(item => item.Value.Route == "A" && item.Value.Ids.Count > 1);
        var ids = entry.Value.Ids;
        var first = ids[0];
        var bot = BotPrJson(42, "CLOSED", $"rule-bot/{entry.Key}-{first.ToLowerInvariant()}-1", "2026-10-01T00:00:00Z", "2026-11-10T00:00:00Z");
        SeedPlan(directory.Path, [(entry.Key, 50L, true)], unshipped: false);
        Exec = (argv, _, _) => Task.FromResult((0, argv.Contains("--label") && argv.Contains("all") ? bot : "[]", ""));
        await Quiet(() => Plan(directory.Path, directory.Path, "2026-11-20", false, null, default));
        var plan = Load<PlanFile>(Path.Combine(directory.Path, "plan.json"));
        Equal(string.Join(',', ids.Skip(1)), string.Join(',', plan.MeasureIds));
        Equal(42L, plan.RejectedIds![first]);
        Save(Path.Combine(directory.Path, "hits.json"), new HitsFile(ids.ToDictionary(id => id, _ => new HitCounts(10, 0, 0)), [], null));
        await Quiet(() => Propose(directory.Path, false, null, default));
        var candidates = Load<CandidatesFile>(Path.Combine(directory.Path, "candidates.json"));
        True(candidates.Decisions.First(decision => decision.Id == first).Verdict.StartsWith("skip: rule-bot PR #42 closed without merge", StringComparison.Ordinal));
        True(candidates.Packages.All(package => !package.Ids.Contains(first)));
    }),
    ("test_plan_skips_the_measure_matrix_when_there_is_nothing_to_measure", async () =>
    {
        var a = RouteKeys("A")[0];
        var b = RouteKeys("B")[0];
        (List<(string, long, bool)> Gates, string Open, string Expected)[] cases =
        [
            ([], "[]", "true"),
            ([(a, 50L, true)], "[]", "false"),
            ([(b, 50L, true)], "[]", "false"),
            ([(a, 50L, true), (b, 49L, true)], OpenBotPr, "true"),
        ];
        foreach (var (gates, open, expected) in cases)
        {
            using var scope = new Seams();
            using var directory = new TempDirectory();
            SeedPlan(directory.Path, gates);
            var output = Path.Combine(directory.Path, "github_output");
            Exec = (argv, _, _) => Task.FromResult((0, argv.Contains("--label") ? open : "[]", ""));
            await Quiet(() => Plan(directory.Path, directory.Path, "2026-11-12", false, output, default));
            True(File.ReadAllText(output).Contains($"skip_measure={expected}\n"));
        }
    }),
    ("test_plan_attempts_no_analyzer_while_a_bot_pr_is_open", async () =>
    {
        var b = RouteKeys("B").Take(2).ToList();
        foreach (var (open, expected) in new (string, string?)[] { (OpenBotPr, null), ("[]", b[0]) })
        {
            using var scope = new Seams();
            using var directory = new TempDirectory();
            SeedPlan(directory.Path, b.Select(key => (key, 50L, true)));
            Exec = (argv, _, _) => Task.FromResult((0, argv.Contains("--label") ? open : "[]", ""));
            await Quiet(() => Plan(directory.Path, directory.Path, "2026-11-12", false, null, default));
            var plan = Load<PlanFile>(Path.Combine(directory.Path, "plan.json"));
            Equal(expected, plan.RouteB);
            Equal(expected is not null, plan.RouteBQueue!.Count > 0);
        }
    }),
    ("test_strip_initialize_block", Sync(() =>
        Equal("class A { public override void Initialize(AnalysisContext context) { context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None); context.EnableConcurrentExecution(); } void B() { } }",
            StripInitialize("class A { public override void Initialize(AnalysisContext context) { context.Register(x => { Go(); }); } void B() { } }")))),
    ("test_strip_initialize_expression_body", Sync(() =>
        Equal("public override void Initialize(AnalysisContext ctx) { ctx.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None); ctx.EnableConcurrentExecution(); }",
            StripInitialize("public override void Initialize(AnalysisContext ctx) => ctx.Register(Go);")))),
    ("test_precision_gate_fails_without_hits", Sync(() => True(!Precision([], 0).Ok))),
    ("test_precision_gate", Sync(() =>
    {
        var labels = Labels(18, 2);
        Equal((18, 20, true), Precision(labels, 40));
        True(!Precision(Labels(16, 4), 40).Ok);
        True(!Precision(labels.Take(19).ToList(), 40).Ok);
        True(Precision(Labels(7, 0), 7).Ok);
        True(!Precision(Labels(6, 1), 7).Ok);
    })),
    ("test_unreadable_labels_clear_earlier_precision", async () =>
    {
        string[] rows =
        [
            """{"file":"f.cs","line":"5","label":"false_positive"}""",
            """{"file":"f.cs","line":true,"label":"false_positive"}""",
            """{"file":7,"line":5,"label":"false_positive"}""",
            """{"file":"f.cs","line":5,"label":1}""",
        ];
        foreach (var row in rows)
        {
            using var directory = new TempDirectory();
            string In(string name) => Path.Combine(directory.Path, name);
            var hit = """{"id":"NETH050","file":"f.cs","line":5,"col":1,"scope":"prod","msg":"m"}""";
            File.WriteAllText(In("hits.json"), """{"counts":{"NETH050":{"prod":5,"test":0,"generated":0}},"solutions":["a.slnx"],"hits":[""" + string.Join(',', Enumerable.Repeat(hit, 5)) + "]}");
            File.WriteAllText(In("gates.json"), "{}");
            File.WriteAllText(In("catalog.json"), "{}");
            File.WriteAllText(In("plan.json"), """{"measure_ids":[],"rejected_ids":{},"route_b":"unused","route_b_id":"NETH050","route_b_queue":[{"cluster":"unused","why":"w"}]}""");
            File.WriteAllText(In("route_b.json"), """{"id":"NETH050","cluster":"unused","attempts":[],"analyzer":"src/Nethermind/Nethermind.Analyzers/X.cs","why":"w","summary":"s","guard_ok":true,"tests_ok":true,"red_ok":true,"precision":{"true_positive":5,"labelled":5,"needed":5,"ok":true}}""");
            File.WriteAllText(In("labels.json"), $"[{string.Join(',', Enumerable.Repeat(row, 5))}]");
            var code = 0;
            await Quiet(async () => code = await Run(["precision", "--labels", In("labels.json"), "--hits", In("hits.json"), "--id", "NETH050", "--route-b", In("route_b.json")], default));
            Equal(1, code);
            await Quiet(() => Propose(directory.Path, true, null, default));
            using var candidates = JsonDocument.Parse(File.ReadAllText(In("candidates.json")));
            Equal("skip: precision sample failed", candidates.RootElement.GetProperty("decisions")[0].GetProperty("verdict").GetString());
        }
    }),

    ("test_ci_solutions_follow_build_workflows", Sync(() =>
    {
        var workflows = Path.Combine(repositoryRoot, ".github", "workflows");
        var solutions = CiSolutions(workflows);
        True(solutions.Contains("src/Nethermind/Nethermind.slnx"));
        True(solutions.Contains("src/Nethermind/EthereumTests.slnx"));
        True(solutions.Contains("tools/Kute/Kute.slnx"));
        True(!solutions.Contains("tools/SchemaGenerator/SchemaGenerator.slnx"));
        foreach (var solution in solutions) True(File.Exists(Path.Combine(repositoryRoot, solution)));
    })),
    ("test_log_names_round_trip", Sync(() => Equal("tools/Kute/Kute.slnx", SolutionOf(LogNameOf("tools/Kute/Kute.slnx"))))),
    ("test_rebuild_set_uses_prod_hits_only", Sync(() =>
    {
        var hits = Parse<HitsFile>("""{"hits": [{"id": "A", "scope": "prod", "solutions": ["x.slnx", "y.slnx"]}, {"id": "A", "scope": "test", "solutions": ["z.slnx"]}, {"id": "B", "scope": "prod", "solutions": ["w.slnx"]}]}""");
        Equal("x.slnx,y.slnx", string.Join(',', RebuildSet(hits, ["A"])));
    })),

    ("test_prod_only_overlay", Sync(() =>
    {
        var overlay = Overlay(["IDE0051"], excludeTests: true);
        True(overlay.Contains("[*.cs]\ndotnet_diagnostic.IDE0051.severity = warning"));
        True(overlay.Contains("[src/Nethermind/{*Test*,*Benchmark*}/**.cs]\ndotnet_diagnostic.IDE0051.severity = none"));
    })),

    ("test_prebuilt_guard_ignores_hostile_build_files", () => HostileBuildFiles(Home)),
    ("test_guard_patch_splits_on_carriage_return_like_git", () => HiddenBlockFails("\r")),
    ("test_guard_patch_splits_on_form_feed_like_git", () => HiddenBlockFails("\f")),
    ("test_guard_patch_splits_on_next_line_like_git", () => HiddenBlockFails("\u0085")),
    ("test_guard_patch_splits_on_line_separator_like_git", () => HiddenBlockFails("\u2028")),
    ("test_guard_patch_fails_on_path_only_git_parses", async () =>
    {
        var (code, output) = await GuardPatch("diff --git \"a/.github/e\\tvil.yml\" \"b/.github/e\\tvil.yml\"\nnew file mode 100644\n--- /dev/null\n+++ \"b/.github/e\\tvil.yml\"\n@@ -0,0 +1 @@\n+x\n");
        Equal(1, code);
        True(output.Contains("git applies it but the guard did not parse it"));
    }),
    ("test_numstat_paths_keep_tabs_and_both_rename_sides", Sync(() =>
        Equal("a\tb.cs|c.cs\t|old.cs|new.cs|bin.dat", string.Join('|', NumstatPaths("1\t0\ta\tb.cs\0002\t1\tc.cs\t\0003\t3\t\0old.cs\0new.cs\0-\t-\tbin.dat\0"))))),
    ("test_guard_patch_fails_on_hidden_rename_into_a_listed_path", async () =>
    {
        var (code, output) = await GuardPatch("diff --git a/src/Nethermind/Nethermind.Analyzers/N.cs b/src/Nethermind/Nethermind.Analyzers/N.cs\nnew file mode 100644\n--- /dev/null\n+++ b/src/Nethermind/Nethermind.Analyzers/N.cs\n@@ -0,0 +1 @@\n+x\nx\r@@ -1,1 +1,2 @@\ndiff --git a/.github/e.yml b/src/Nethermind/Nethermind.Analyzers/M.cs\nsimilarity index 100%\nrename from .github/e.yml\nrename to src/Nethermind/Nethermind.Analyzers/M.cs\n");
        Equal(1, code);
        True(output.Contains("Nethermind.Analyzers/M.cs: symlink, mode, rename"));
    }),
    ("test_guard_fails_on_applied_ignored_file", () => IgnoredFileFails(repositoryRoot)),
    ("test_guard_passes_overlay_appended_at_end_of_file", async () =>
    {
        var (patchCode, appliedCode) = await EditorConfigEdit(text => text + Overlay(["CA1510"], true));
        Equal(0, patchCode);
        Equal(0, appliedCode);
    }),
    ("test_guard_fails_on_mid_file_section_insert", async () =>
    {
        var (patchCode, appliedCode) = await EditorConfigEdit(text => text.Replace("[*.cs]\n", "[*.cs]\n[src/Nethermind/{*Test*,*Benchmark*}/**.cs]\ndotnet_diagnostic.CA1510.severity = none\n"));
        Equal(1, patchCode);
        Equal(1, appliedCode);
    }),
    ("test_guard_fails_on_earlier_edit_with_end_of_file_overlay", async () =>
    {
        var (patchCode, appliedCode) = await EditorConfigEdit(text => text.Replace("CA1822.severity = error", "CA1822.severity = none ") + Overlay(["CA1510"], true));
        Equal(1, patchCode);
        Equal(1, appliedCode);
    }),
    ("test_guard_patch_fails_on_symlink_mode_with_second_token", () => NewFileMode("120000 100644", 1)),
    ("test_guard_patch_fails_on_executable_mode_with_second_token", () => NewFileMode("100755 100644", 1)),
    ("test_guard_patch_passes_regular_file_mode", () => NewFileMode("100644", 0)),
    ("test_workflow_applies_patches_to_the_index", Sync(() =>
    {
        var applies = File.ReadAllLines(Path.Combine(repositoryRoot, ".github", "workflows", "rule-bot.yml")).Where(line => line.TrimStart().StartsWith("git apply", StringComparison.Ordinal)).ToList();
        Equal(3, applies.Count);
        True(applies.All(line => line.TrimStart().StartsWith("git apply --index ", StringComparison.Ordinal)));
    })),
    ("test_workflow_excludes_bot_files_before_git_add", Sync(() =>
    {
        var excluded = false;
        var intentToAddCount = 0;
        foreach (var line in File.ReadLines(Path.Combine(repositoryRoot, ".github", "workflows", "rule-bot.yml")))
        {
            if (line.StartsWith("  ", StringComparison.Ordinal) && line[2] != ' ' && line.EndsWith(':'))
                excluded = false;
            excluded |= line.Contains("echo /rule-bot/ >> .git/info/exclude", StringComparison.Ordinal);
            if (!line.TrimStart().StartsWith("git add -N", StringComparison.Ordinal))
                continue;
            True(excluded);
            intentToAddCount++;
        }
        Equal(4, intentToAddCount);
    })),

    ("test_generated_analyzer_logs_cannot_add_hits_for_other_rules", async () =>
    {
        using var directory = new TempDirectory();
        var clean = Path.Combine(directory.Path, "clean");
        var generated = Path.Combine(directory.Path, "generated");
        Directory.CreateDirectory(clean);
        Directory.CreateDirectory(generated);
        string Line(string file, string id) => $"{directory.Path}/src/Nethermind/Nethermind.Core/{file}.cs(1,2): warning {id}: message [{directory.Path}/x.csproj]\n";
        File.WriteAllText(Path.Combine(clean, "a.slnx.log"), Line("Real", "CA2012") + Line("CleanForged", "NETH099"));
        File.WriteAllText(Path.Combine(generated, "a.slnx.log"), Line("Forged", "CA2012") + Line("Own", "NETH099"));
        var output = Path.Combine(directory.Path, "hits.json");
        Equal(0, await Run(["hits", "--logs", clean, "--root", directory.Path, "--ids", "CA2012,NETH099", "--out", output, "--generated-logs", generated, "--generated-id", "NETH099"], CancellationToken.None));
        var found = Load<HitsFile>(output).Hits.Select(hit => $"{hit.Id}:{Path.GetFileName(hit.File)}").Order(StringComparer.Ordinal).ToList();
        Equal("CA2012:Real.cs,NETH099:Own.cs", string.Join(',', found));
    }),
    ("test_hits_requires_both_generated_options", async () =>
    {
        using var directory = new TempDirectory();
        Equal(2, await Run(["hits", "--logs", directory.Path, "--root", directory.Path, "--ids", "CA2012", "--out", Path.Combine(directory.Path, "hits.json"), "--generated-id", "CA2012"], CancellationToken.None));
    }),
    ("test_workflow_builds_route_a_before_generated_code", Sync(() =>
    {
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "rule-bot.yml"));
        var clean = workflow[workflow.IndexOf("\n  measure:\n", StringComparison.Ordinal)..workflow.IndexOf("\n  seal-logs:\n", StringComparison.Ordinal)];
        True(!clean.Contains("analyzer.patch", StringComparison.Ordinal) && !clean.Contains("rule-bot-route-b", StringComparison.Ordinal));
        True(workflow.IndexOf("\n  seal-logs:\n", StringComparison.Ordinal) < workflow.IndexOf("\n  route-b:\n", StringComparison.Ordinal));
        True(workflow.Contains("needs: [plan, measure, seal-logs]\n", StringComparison.Ordinal));
        True(workflow.Contains("name: rule-bot-clean-logs\n          path: rule-bot/logs/clean", StringComparison.Ordinal));
    })),

    ("test_route_b_stops_at_first_cluster_that_passes", async () =>
    {
        var (state, calls) = await RouteBRun(new() { ["a"] = """{"skip": "no shape"}""", ["b"] = RouteBResult, ["c"] = RouteBResult });
        Equal("b", state.Cluster);
        Equal(2, state.Attempts!.Count);
        True(state.GuardOk == true && state.TestsOk == true && state.RedOk == true);
        Equal(1, calls.Count(call => call[0] == "git" && call[1] == "reset"));
        True(calls.Any(call => call[0] == "git" && call[1] == "diff" && call[2].EndsWith("/analyzer.patch", StringComparison.Ordinal)));
        True(!calls.Any(call => call.Contains("CLUSTER=c")));
    }),
    ("test_route_b_tries_next_cluster_when_a_gate_fails", async () =>
    {
        foreach (var gate in new[] { "guard", "test", "red" })
        {
            var (state, calls) = await RouteBRun(new() { ["a"] = RouteBResult, ["b"] = RouteBResult }, gate);
            Equal("b", state.Cluster);
            Equal(2, calls.Count(call => call.Any(item => item.StartsWith("CLUSTER=", StringComparison.Ordinal))));
        }
    }),
    ("test_route_b_runs_generated_code_without_the_token", async () =>
    {
        var (_, calls) = await RouteBRun(new() { ["a"] = RouteBResult });
        var generated = calls.Where(call => call.Contains("--test-class")).ToList();
        Equal(2, generated.Count);
        foreach (var call in generated) True(string.Join(' ', call).Contains("env -u CLAUDE_CODE_OAUTH_TOKEN"));
    }),
    ("test_route_b_skips_analyzer_outside_the_analyzer_project", async () =>
    {
        var (state, calls) = await RouteBRun(new() { ["a"] = """{"test_class": "FooTests", "analyzer": "src/Nethermind/Nethermind.Core/OutsideAnalyzer.cs", "skip": "outside"}""" });
        Equal("a: skipped by the generator: outside", state.Skip);
        True(!calls.Any(call => call.Contains("guard")));
    }),
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try { await body(); }
    catch (Exception failure)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {failure.GetType().Name}: {failure.Message}");
    }
}
if (failures > 0)
{
    Console.Error.WriteLine($"{failures} of {tests.Length} tests failed.");
    return 1;
}
Console.WriteLine($"Passed {tests.Length} tests.");
return 0;

static class RuleBotTestSupport
{
    internal static string ScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "rule-bot");
            if (File.Exists(Path.Combine(candidate, "routes.json"))) return candidate;
        }

        throw new DirectoryNotFoundException("scripts/rule-bot not found above the current directory");
    }
    internal static Func<Task> Sync(Action body) => () => { body(); return Task.CompletedTask; };
    internal static void True(bool condition) { if (!condition) throw new InvalidOperationException("expected true"); }
    internal static void Equal<T>(T expected, object? actual) { if (!Equals(expected, actual)) throw new InvalidOperationException($"expected <{expected}>, got <{actual}>"); }
    internal static void Empty(List<string> items) { if (items.Count != 0) throw new InvalidOperationException($"expected none, got: {string.Join("; ", items)}"); }
    internal static void Any(List<string> items) => True(items.Count > 0);

    internal static async Task<BotExit> Throws(Func<Task> body)
    {
        try { await body(); }
        catch (BotExit exit) { return exit; }
        throw new InvalidOperationException("expected BotExit");
    }

    internal static async Task<string> Quiet(Func<Task> body)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try { await body(); }
        finally { Console.SetOut(original); }
        return output.ToString();
    }

    internal static ReviewThread Thread(string[] replies, bool resolved = false, bool outdated = false) =>
        new(resolved, outdated, [("reviewer", "unused field"), .. replies.Select(reply => ("author", reply))]);

    internal static Finding GateRow(long pr, string author, bool bot = false, string valid = "valid", string? association = "MEMBER", string url = "u") =>
        new(pr, author, bot, "p", 1, "2026-11-01T00:00:00Z", "b", url, association, valid);

    internal static ApiComment ApiRow(long id, long pr, string author = "reviewer", string created = "2026-11-01T00:00:00Z", long? replyTo = null, string body = "unused field", bool bot = false) =>
        new(id, $"https://api.github.com/repos/{Repo}/pulls/{pr}", new(author, bot ? "Bot" : "User"), "MEMBER", replyTo, "a.cs", 1, null, created, body, $"https://github.com/{Repo}/pull/{pr}#discussion_r{id}");

    internal static string BotPrJson(long number, string state, string head, string? created, string? closed) =>
        JsonSerializer.Serialize(new[] { new BotPr(number, state, head, created, closed) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static List<Label> Labels(int truePositive, int falsePositive) =>
        [.. Enumerable.Repeat("true_positive", truePositive).Concat(Enumerable.Repeat("false_positive", falsePositive)).Select(label => new Label(label, null, null, null))];

    internal const string RouteBResult = """{"test_class": "FooTests", "analyzer": "src/Nethermind/Nethermind.Analyzers/Foo.cs", "summary": "ok"}""";

    internal static async Task<(RouteBState State, List<string[]> Calls)> RouteBRun(Dictionary<string, string> results, string? failingGate = null)
    {
        using var directory = new TempDirectory();
        using var seams = new Seams();
        var previous = Environment.CurrentDirectory;
        var analyzers = Path.Combine(directory.Path, "src", "Nethermind", "Nethermind.Analyzers");
        Directory.CreateDirectory(analyzers);
        File.WriteAllText(Path.Combine(analyzers, "Foo.cs"), "");
        var outsideAnalyzers = Path.Combine(directory.Path, "src", "Nethermind", "Nethermind.Core");
        Directory.CreateDirectory(outsideAnalyzers);
        File.WriteAllText(Path.Combine(outsideAnalyzers, "OutsideAnalyzer.cs"), "");
        var data = Path.Combine(directory.Path, "data");
        var queue = results.Keys.Select(cluster => new QueuedCluster(cluster, "because")).ToList();
        Save(Path.Combine(data, "plan.json"), new PlanFile([], null, null, "NETH012", queue));
        Save(Path.Combine(data, "gates.json"), new Dictionary<string, Gate>());
        var calls = new List<string[]>();
        Exec = (argv, _, _) =>
        {
            calls.Add(argv);
            if (argv.FirstOrDefault(item => item.StartsWith("CLUSTER=", StringComparison.Ordinal)) is { } cluster)
                File.WriteAllText(Path.Combine(data, "analyzer-result.json"), results[cluster["CLUSTER=".Length..]]);
            else if (argv[0] == "bash") File.WriteAllText(argv[4], argv.Contains("red") ? "red: PASS\n" : "Passed! total: 1\n");
            bool RunsGate(string[] call) => failingGate is not null && call[0] != failingGate && call.Contains(failingGate);
            var gateFails = RunsGate(argv) && calls.Count(RunsGate) == 1;
            return Task.FromResult((gateFails ? 1 : 0, "", ""));
        };
        Environment.CurrentDirectory = directory.Path;
        try
        {
            await RouteB(data, default);
            return (Load<RouteBState>(Path.Combine(data, "route_b.json")), calls);
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    private static async Task<(int Code, string Output)> GuardIn(string directory, string? patch, string? ids = null)
    {
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
        try
        {
            var code = 0;
            var output = await Quiet(async () => code = await Guard("HEAD", patch, "NETH012", null, ids, default));
            return (code, output);
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    internal static async Task<(int Code, string Output)> GuardPatch(string patch)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "p.patch");
        File.WriteAllText(path, patch);
        return await GuardIn(directory.Path, path);
    }

    internal static async Task HiddenBlockFails(string separator)
    {
        var (code, output) = await GuardPatch($"x{separator}@@ -1,1 +1,2 @@\ndiff --git a/.github/workflows/evil.yml b/.github/workflows/evil.yml\nnew file mode 100644\n--- /dev/null\n+++ b/.github/workflows/evil.yml\n@@ -0,0 +1 @@\n+on: push\n");
        Equal(1, code);
        True(output.Contains("guard: FAIL .github/workflows/evil.yml"));
    }

    internal static async Task NewFileMode(string mode, int expected)
    {
        const string analyzer = "src/Nethermind/Nethermind.Analyzers/New.cs";
        var (code, _) = await GuardPatch($"diff --git a/{analyzer} b/{analyzer}\nnew file mode {mode}\n--- /dev/null\n+++ b/{analyzer}\n@@ -0,0 +1 @@\n+x\n");
        Equal(expected, code);
    }

    internal static async Task<(int PatchCode, int AppliedCode)> EditorConfigEdit(Func<string, string> edit)
    {
        using var directory = new TempDirectory();
        var tree = Path.Combine(directory.Path, "tree");
        var patch = Path.Combine(directory.Path, "editorconfig.patch");
        var file = Path.Combine(tree, ".editorconfig");
        const string original = "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1822.severity = error\ndotnet_diagnostic.CA1851.severity = warning\n";
        Directory.CreateDirectory(tree);
        await Checked("git", ["init", "-q"], tree);
        File.WriteAllText(file, original);
        await Checked("git", ["add", "."], tree);
        await Checked("git", ["-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "base"], tree);
        File.WriteAllText(file, edit(original));
        var diff = await ScriptProcess.Run("git", ["diff", "HEAD"], default, workingDirectory: tree);
        File.WriteAllText(patch, diff.Output);
        await Checked("git", ["checkout", "--", ".editorconfig"], tree);
        var (patchCode, _) = await GuardIn(tree, patch, "CA1510");
        await Checked("git", ["apply", "--index", patch], tree);
        await Checked("git", ["add", "-N", "."], tree);
        var (appliedCode, _) = await GuardIn(tree, null, "CA1510");
        return (patchCode, appliedCode);
    }

    internal static async Task IgnoredFileFails(string repositoryRoot)
    {
        using var directory = new TempDirectory();
        var tree = directory.Path;
        await Checked("git", ["init", "-q"], tree);
        File.WriteAllText(Path.Combine(tree, ".gitignore"), "*.user\n");
        await Checked("git", ["add", "."], tree);
        await Checked("git", ["-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "base"], tree);
        var patch = Path.Combine(tree, "..", $"{Path.GetFileName(tree)}.patch");
        File.WriteAllText(patch, "diff --git a/src/Demo/Demo.csproj.user b/src/Demo/Demo.csproj.user\nnew file mode 100644\n--- /dev/null\n+++ b/src/Demo/Demo.csproj.user\n@@ -0,0 +1 @@\n+<Project />\n");
        try
        {
            var apply = File.ReadAllLines(Path.Combine(repositoryRoot, ".github", "workflows", "rule-bot.yml")).First(line => line.TrimStart().StartsWith("git apply", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            await Checked("git", [.. apply[1..^1], patch], tree);
            await Checked("git", ["add", "-N", "."], tree);
            var (code, output) = await GuardIn(tree, null);
            Equal(1, code);
            True(output.Contains("guard: FAIL src/Demo/Demo.csproj.user"));
        }
        finally { File.Delete(patch); }
    }

    internal static async Task HostileBuildFiles(string home)
    {
        using var directory = new TempDirectory();
        var tool = Path.Combine(directory.Path, "tool");
        var tree = Path.Combine(directory.Path, "tree");
        var marker = Path.Combine(directory.Path, "pwned");
        Directory.CreateDirectory(tree);
        await Checked("dotnet", ["publish", Path.Combine(home, "rule-bot.cs"), "-o", tool, "--verbosity", "quiet"], home);
        foreach (var config in new[] { "clusters.json", "routes.json" }) File.Copy(Path.Combine(home, config), Path.Combine(tool, config));
        await Checked("git", ["init", "-q"], tree);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "a\n");
        await Checked("git", ["add", "."], tree);
        await Checked("git", ["-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "base"], tree);
        File.WriteAllText(Path.Combine(tree, "Directory.Build.props"),
            $"""<Project><Target Name="Hostile" BeforeTargets="Restore;CollectPackageReferences;Build"><Touch Files="{marker}" AlwaysCreate="true" /></Target></Project>""");
        File.WriteAllText(Path.Combine(tree, "control.cs"), "System.Console.WriteLine(1);\n");
        await Checked("git", ["add", "-N", "."], tree);

        var guard = await ScriptProcess.Run(Path.Combine(tool, "rule-bot"), ["guard", "--new-id", "NETH012"], default, workingDirectory: tree);
        Equal(1, guard.Code);
        True(guard.Output.Contains("guard: FAIL Directory.Build.props"));
        True(!File.Exists(marker));

        await ScriptProcess.Run("dotnet", ["build", Path.Combine(tree, "control.cs")], default, workingDirectory: tree);
        True(File.Exists(marker));
    }

    private static async Task Checked(string executable, string[] arguments, string workingDirectory)
    {
        var result = await ScriptProcess.Run(executable, arguments, default, workingDirectory: workingDirectory);
        if (result.Code != 0) throw new InvalidOperationException($"{executable} {arguments[0]} failed: {result.Output}{result.Error}");
    }
}

sealed class Seams : IDisposable
{
    private readonly Func<string[], bool, CancellationToken, Task<(int Code, string Output, string Error)>> _exec = RuleBot.Exec;
    private readonly Func<string[], CancellationToken, Task<string>> _api = RuleBot.Api;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = RuleBot.Delay;

    internal Seams() => RuleBot.Delay = (_, _) => Task.CompletedTask;

    public void Dispose()
    {
        RuleBot.Exec = _exec;
        RuleBot.Api = _api;
        RuleBot.Delay = _delay;
    }
}

sealed class TempDirectory : IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("rule-bot-test-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
