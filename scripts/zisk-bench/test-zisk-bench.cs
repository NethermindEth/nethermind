#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include zisk-bench.helpers.cs
#:include ../ci/script-process.helpers.cs
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "AGENTS.md"))) root = Directory.GetParent(root)?.FullName ?? throw new InvalidOperationException("repository not found");
var temporary = Directory.CreateTempSubdirectory("zisk-tests-").FullName;
var passed = 0;
try
{
    var workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/stateless-benchmarks.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
    const string guest = "src/Nethermind/Nethermind.Stateless.ZiskGuest";
    const string log = "STEPS 437561208\nMAIN 29754162144 59.88%\nOPCODES 6287492347 12.65%\nPRECOMPILES 8023714093 16.15%\nMEMORY 5334595573 10.74%\nTOTAL 49687273981 100.00%\n";
    // cspell:ignore FROPS
    const string grouped = "STEPS 406,243,606\nMAIN 27,624,565,208 61.34%\nOPCODES 6,223,986,843 13.82%\nPRECOMPILES 6,875,247,706 15.27%\nMEMORY 4,027,415,979 8.94%\nTOTAL 45,038,525,560 100.00%\nFROPS 4,877,143,744 10.90%\nOP keccak 165,632 0.04% 6,369,212,928 14.23%\n";
    JsonObject Row(string name = "a.ssz", long steps = 100, long total = 1000, long main = 1, long opcodes = 1, long precompiles = 1, long memory = 1) => new()
    { ["input"] = name, ["steps"] = steps, ["total"] = total, ["main"] = main, ["opcodes"] = opcodes, ["precompiles"] = precompiles, ["memory"] = memory };
    Dictionary<string, JsonObject> Rows(params JsonObject[] rows) => rows.ToDictionary(row => row["input"]!.GetValue<string>());
    (string Body, bool Regressed, bool Stale) Render(JsonObject[] current, JsonObject[]? baseline = null, string baselineCommit = "", string baseCommit = "", string kind = "commit") =>
        ZiskBench.Render(Rows(current), baseline is null ? null : Rows(baseline), "c0ffee", baselineCommit, baseCommit, kind);
    string Write(string name, string content) { var path = Path.Combine(temporary, name); File.WriteAllText(path, content); return path; }
    void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    void Contains(string actual, params string[] expected) { foreach (var value in expected) Require(actual.Contains(value, StringComparison.Ordinal), "missing " + value + " in " + actual); }
    void Case(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    (int Code, string Output, string Error) Run(string executable, string[] arguments, string? directory = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return ScriptProcess.Run(executable, arguments, timeout.Token, workingDirectory: directory ?? root).GetAwaiter().GetResult();
    }
    (int Code, string Output, string Error) App(string name, params string[] arguments) => Run("dotnet", ["run", "--file", Path.Combine(root, "scripts/zisk-bench", name + ".cs"), "--", .. arguments]);
    string Step(string name, string? yaml = null)
    {
        var match = StepRegex().Matches(yaml ?? workflow).FirstOrDefault(match => match.Groups[1].Value == name);
        Require(match is not null, "step not found: " + name);
        return StepIndentRegex().Replace(match!.Groups[2].Value, "");
    }
    (int Code, string Output, string Error) Bash(string script, Dictionary<string, string> environment, string? directory = null) =>
        Run("env", [.. environment.Select(pair => pair.Key + "=" + pair.Value), "bash", "-c", script], directory);
    void Executable(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/bash\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    foreach (var (text, name, expected) in new[] { (log, "25532382.ssz", Row("25532382.ssz", 437561208, 49687273981, 29754162144, 6287492347, 8023714093, 5334595573)),
        (grouped, "25526356.ssz", Row("25526356.ssz", 406243606, 45038525560, 27624565208, 6223986843, 6875247706, 4027415979)) })
        Case("parse all buckets " + name, () => Require(JsonNode.DeepEquals(ZiskBench.Parse(text, name), expected), "parsed figures differ"));
    Case("additional memory sections", () => Require(ZiskBench.Integer(ZiskBench.Parse(log + "TOTAL ALIGNED 129709872 79.22%\nTOTAL UNALIGNED 34029073 20.78%\nTOTAL RAM 159208872 97.23%\n", "1.ssz"), "total") == 49687273981, "later subtotal selected"));
    foreach (var (text, name) in new[] { ("nothing useful", "1.ssz"), ("STEPS 437561208\n", "1.ssz"), (log, "../1.ssz"), (log, "1|2.ssz"), (log, "1.ssz\n"), (log, "1.txt") })
        Case("reject incomplete log or input " + name, () => { try { ZiskBench.Parse(text, name); } catch (ArgumentException) { return; } throw new InvalidOperationException("invalid measurement accepted"); });
    foreach (var name in new[] { "25526356.ssz", "25532382.ssz" })
        Case("append or replace " + name, () =>
        {
            var path = Write("rows.json", new JsonArray(Row(name, 999, 999)).ToJsonString());
            var result = App("parse-stats", "--input", "25532382.ssz", "--log", Write("stats.log", log), "--into", path);
            Require(result.Code == 0, result.Error);
            var rows = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            Require(rows.Count == (name == "25532382.ssz" ? 1 : 2) && rows[0]!["input"]!.ToString() == name && rows[^1]!["steps"]!.GetValue<long>() == 437561208, "append/replacement changed");
        });
    Case("signed percentages and step-only regression", () =>
    {
        var result = Render([Row(steps: 110, total: 900)], [Row()]);
        Contains(result.Body, "| a | 110 | +10.000% | 900 | -10.000% |");
        Require(!result.Regressed && !result.Body.Contains('⚠'), "step-only regression flagged");
    });
    foreach (var (now, before, expected) in new[] { (1100L, 1000L, true), (1000000L, 999999L, false), (1000400L, 1000000L, false), (1000600L, 1000000L, true) })
        Case($"cost threshold {now}/{before}", () => { var result = Render([Row(total: now)], [Row(total: before)]); Require(result.Regressed == expected && result.Body.Contains("Prover cost is up", StringComparison.Ordinal) == expected, "threshold/body differs"); });
    Case("shared totals and changed coverage", () =>
    {
        var result = Render([Row(steps: 110, total: 900), Row("new.ssz", 500, 5000)], [Row(), Row("gone.ssz", 700, 7000)]);
        Contains(result.Body, "| **all 1 blocks** | **110** | **+10.000%** | **900** | **-10.000%** |", "| new | 500 | new | 5,000 | new |", "this run did not: gone", "Blocks absent from the baseline: new");
        Require(result.Regressed, "coverage loss not flagged");
    });
    Case("per-block threshold covers weighted totals", () =>
    {
        var result = Render([Row("big.ssz", 100, 1000000), Row("small.ssz", 100, 1100)], [Row("big.ssz", 100, 1000000), Row("small.ssz", 100, 1000)]);
        Require(result.Regressed, "individual increase hidden by aggregate"); Contains(result.Body, "**+0.010%**");
    });
    Case("variable bucket columns", () =>
    {
        var result = Render([Row("a.ssz", 10, 100, 20, 30, 40, 50), Row("new.ssz")], [Row("a.ssz", 10, 100, 10, 20, 30, 40), Row("gone.ssz")]);
        Contains(result.Body, "Variable cost", "excludes BASE", "| MAIN | 10 | 20 | +100.000% |", "| OPCODES | 20 | 30 | +50.000% |", "| PRECOMPILES | 30 | 40 | +33.333% |", "| MEMORY | 40 | 50 | +25.000% |");
    });
    foreach (var (baseline, baseCommit, stale, comparison) in new[] { ("ba5e1111aaaa", "ba5e1111aaaa", false, "`ba5e1111aaaa`"), ("", "", false, "an unrecorded master commit"), ("0lde5t000000", "ba5e1111aaaa", true, "") })
        Case("baseline provenance " + baseline, () =>
        {
            var result = Render([Row()], [Row()], baseline, baseCommit);
            Require(result.Stale == stale && !result.Regressed, "stale/regression state changed");
            if (stale) { Contains(result.Body, "not this pull request's base (`ba5e1111aaaa`)"); Require(!result.Body.Contains('Δ') && !result.Body.Contains("+0.000%", StringComparison.Ordinal), "stale deltas shown"); }
            else Contains(result.Body, "Compared against " + comparison + ".");
        });
    foreach (var baseline in new JsonObject[]?[] { null, [] })
        Case("absolute report " + (baseline is null ? "missing" : "empty"), () =>
        {
            var result = Render([Row("25532382.ssz")], baseline, "base", "base", "merge commit");
            Contains(result.Body, "No baseline was restored", "| 25532382 |", "`c0ffee` (merge commit)");
            Require(result.Body.StartsWith(ZiskBench.Marker, StringComparison.Ordinal) && !result.Body.Contains('Δ') && !result.Body.Contains("Compared against", StringComparison.Ordinal) && !result.Body.Contains("25532382.ssz", StringComparison.Ordinal) && !result.Regressed && !result.Stale, "absolute report contract");
        });
    foreach (var wrapped in new[] { false, true })
        Case("load " + (wrapped ? "wrapped" : "array"), () =>
        {
            var rows = new JsonArray(Row());
            var result = ZiskBench.Load(Write("load.json", (wrapped ? (JsonNode)new JsonObject { ["rows"] = rows, ["commit"] = "ba5e1111aaaa" } : rows).ToJsonString()));
            Require(result is not null && result.Value.Rows.Keys.SequenceEqual(["a.ssz"]) && result.Value.Commit == (wrapped ? "ba5e1111aaaa" : ""), "baseline load differs");
        });
    foreach (var document in new[] { "{\"commit\":\"base\"}", "{\"rows\":[{\"input\":\"a.ssz\"}]}", "\"not rows\"" })
        Case("invalid baseline " + document, () => Require(ZiskBench.Load(Write("load.json", document)) is null, "invalid baseline accepted"));
    foreach (var state in new[] { "current", "stale", "invalid" })
        Case("report CLI outputs " + state, () =>
        {
            var current = Write("current.json", new JsonArray(Row("1.ssz", 110, 1100)).ToJsonString());
            var baseline = Write("base.json", state == "invalid" ? "{\"rows\":[{\"input\":\"1.ssz\"}]}" : new JsonObject { ["commit"] = state == "stale" ? "older" : "base", ["rows"] = new JsonArray(Row("1.ssz")) }.ToJsonString());
            var output = Write("output", ""); var summary = Write("summary", "");
            var result = App("report", "--current", current, "--baseline", baseline, "--commit", "measured-merge", "--base-commit", "base", "--summary", summary, "--github-output", output);
            Require(result.Code == 0, result.Error);
            var lines = File.ReadAllLines(output);
            Require(lines[0] == "regressed=" + (state == "current" ? "true" : "false") && lines[1] == "stale=" + (state == "stale" ? "true" : "false") && lines[^1] == lines[2][8..], "action output flags/delimiter");
            Contains(File.ReadAllText(output), File.ReadAllText(summary)); Contains(File.ReadAllText(summary), "`measured-mer`");
        });
    Case("workflow SDK and fetch contract", () =>
    {
        Contains(workflow, "global-json-file: global.json", "fetch-depth: 2", "filter: blob:none", "git fetch --depth=1 --filter=blob:none origin \"$BASE_SHA\"");
        Require(!workflow.Contains("DOTNET_VERSION", StringComparison.Ordinal), "SDK override added");
    });
    Case("fixture workers use one prebuilt parser", () =>
    {
        var measure = Step("Measure", File.ReadAllText(Path.Combine(root, ".github/workflows/stateless-benchmark-fixtures.yml")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Contains(measure, "dotnet build", "dotnet \"$RUNNER_TEMP/parse-stats/parse-stats.dll\"");
        Require(measure.IndexOf("dotnet build", StringComparison.Ordinal) < measure.IndexOf("xargs", StringComparison.Ordinal) && !measure.Contains("dotnet run", StringComparison.Ordinal), "workers can rebuild the parser");
        var directory = Path.Combine(temporary, "parser");
        var build = Run("dotnet", ["build", Path.Combine(root, "scripts/zisk-bench/parse-stats.cs"), "--output", directory]);
        Require(build.Code == 0, build.Error);
        var input = Write("parallel.log", log);
        Parallel.For(0, 4, index =>
        {
            var result = Run("dotnet", [Path.Combine(directory, "parse-stats.dll"), "--input", $"{index}.ssz", "--log", input, "--into", Path.Combine(temporary, $"parallel-{index}.json")]);
            Require(result.Code == 0, result.Error);
        });
    });
    var correctness = File.ReadAllText(Path.Combine(root, ".github/workflows/stateless-tests.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
    Case("matrix comes from pinned file", () => { Contains(correctness, guest + "/inputs.json", "block: ${{ fromJSON(needs.blocks.outputs.blocks) }}"); Require(!correctness.Contains("- input:", StringComparison.Ordinal), "matrix duplicates pins"); });
    Case("pinned input shape", () =>
    {
        var inputs = JsonNode.Parse(File.ReadAllText(Path.Combine(root, guest, "inputs.json")))!.AsArray();
        Require(inputs.Count >= 9, "pinned blocks dropped");
        foreach (var row in inputs)
            foreach (var (key, pattern) in new[] { ("input", InputNameRegex()), ("hash", HashRegex()), ("output", OutputRegex()), ("digest", HashRegex()) })
                Require(pattern.IsMatch(row![key]!.ToString()), "invalid pin " + key);
    });

    if (OperatingSystem.IsLinux())
    {
        var expected = new string('a', 64) + "0101000000000000000100";
        var start = workflow.IndexOf("            grep -Fxq \"${output}\"", StringComparison.Ordinal);
        var gate = workflow[start..workflow.IndexOf("            dotnet run --file \"$BENCH_DEFINITIONS", start, StringComparison.Ordinal)];
        foreach (var (text, success) in new[] { (expected, true), ("wrong", false), (expected + "00", false), (expected + "\nException", false), (expected + "\nInvalid Blocks", false) })
            Case("measured log gate " + text, () => { Write("stats.log", text + "\n"); var result = Bash(gate, new() { ["RUNNER_TEMP"] = temporary, ["input"] = "1.ssz", ["output"] = expected }); Require((result.Code == 0) == success, result.Output + result.Error); });
        foreach (var valid in new[] { true, false })
            Case("download checksum " + valid, () =>
            {
                var directory = Directory.CreateDirectory(Path.Combine(temporary, "download"));
                Directory.CreateDirectory(Path.Combine(directory.FullName, "bin"));
                var tools = Directory.CreateDirectory(Path.Combine(directory.FullName, "tools"));
                Executable(Path.Combine(tools.FullName, "curl"), "printf fixture > \"${@: -1}\"\n");
                var digest = valid ? Convert.ToHexStringLower(SHA256.HashData("fixture"u8)) : new string('0', 64);
                File.WriteAllText(Path.Combine(directory.FullName, "inputs.json"), new JsonArray(new JsonObject { ["input"] = "1.ssz", ["hash"] = digest }).ToJsonString());
                var result = Bash(Step("Download inputs"), new() { ["PATH"] = tools.FullName + ":" + Environment.GetEnvironmentVariable("PATH"), ["BENCH_DEFINITIONS"] = directory.FullName, ["GUEST_DIR"] = "." }, directory.FullName);
                Require((result.Code == 0) == valid, result.Output + result.Error);
            });
        Case("staged baseline provenance", () =>
        {
            var expression = CommitExpressionRegex().Match(workflow).Groups[1].Value;
            var path = Write("baseline.json", new JsonArray(Row("1.ssz", 10, 100)).ToJsonString());
            var result = Run("jq", ["--arg", "commit", "base-sha", expression, path]);
            Require(result.Code == 0, result.Error);
            var loaded = ZiskBench.Load(Write("baseline.json", result.Output));
            Require(loaded?.Commit == "base-sha" && ZiskBench.Integer(loaded.Value.Rows["1.ssz"], "total") == 100, "staged provenance differs");
        });
        Case("matrix publishes pinned set", () =>
        {
            var output = Write("outputs", "");
            var result = Bash(Step("Read the pinned block set", correctness), new() { ["GITHUB_OUTPUT"] = output });
            Require(result.Code == 0 && JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(output)[5..]), JsonNode.Parse(File.ReadAllText(Path.Combine(root, guest, "inputs.json")))), result.Error);
        });
        Case("resolve SDK before disabling roll-forward", () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(temporary, "sdk"));
            var tools = Directory.CreateDirectory(Path.Combine(directory.FullName, "tools"));
            var temp = Directory.CreateDirectory(Path.Combine(directory.FullName, "temp"));
            var path = Path.Combine(directory.FullName, "global.json");
            File.WriteAllText(path, "{\"sdk\":{\"version\":\"10.0.300\",\"rollForward\":\"latestFeature\"},\"other\":\"preserved\"}");
            Executable(Path.Combine(tools.FullName, "dotnet"), "set -euo pipefail\n[[ $1 == --version ]]\njq -e '.sdk.version == \"10.0.300\" and .sdk.rollForward == \"latestFeature\"' global.json >/dev/null\necho 10.0.301\n");
            var output = Write("outputs", "");
            var result = Bash(Step("Pin resolved SDK"), new() { ["PATH"] = tools.FullName + ":" + Environment.GetEnvironmentVariable("PATH"), ["RUNNER_TEMP"] = temp.FullName, ["GITHUB_OUTPUT"] = output }, directory.FullName);
            Require(result.Code == 0 && File.ReadAllText(output) == "version=10.0.301\n", result.Error);
            Require(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse("{\"sdk\":{\"version\":\"10.0.301\",\"rollForward\":\"disable\"},\"other\":\"preserved\"}")), "SDK pin changed other properties");
        });
        foreach (var shallow in new[] { false, true })
            foreach (var change in new[] { "none", "add", "remove", "hash", "output" })
                SelectDefinitions(shallow, change);
        foreach (var change in new[] { "duplicate", "malformed", "empty", "invalid-base", "no-base", "python-base" }) SelectDefinitions(false, change);
        void SelectDefinitions(bool shallow, string change) => Case($"select definitions shallow={shallow} change={change}", () =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(temporary, Guid.NewGuid().ToString("N")));
            var source = Directory.CreateDirectory(Path.Combine(directory.FullName, "source")).FullName;
            Directory.CreateDirectory(Path.Combine(source, guest));
            Directory.CreateDirectory(Path.Combine(source, "scripts/zisk-bench"));
            var definitions = new JsonArray(Enumerable.Range(0, 9).Select(index => (JsonNode)new JsonObject { ["input"] = index + ".ssz", ["hash"] = new string('0', 64), ["output"] = new string('1', 86) }).ToArray());
            var manifest = Path.Combine(source, guest, "inputs.json");
            File.WriteAllText(manifest, change == "invalid-base" ? "[42]" : definitions.ToJsonString());
            var instrument = Path.Combine(source, "scripts/zisk-bench/report.cs");
            File.WriteAllText(change == "python-base" ? Path.Combine(source, "scripts/zisk-bench/report.py") : instrument, "base instrument");
            string Git(params string[] arguments)
            {
                var result = Run("git", ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgSign=false", .. arguments], source);
                Require(result.Code == 0, result.Error);
                return result.Output.Trim();
            }
            Git("init"); Git("add", "."); Git("commit", "-m", "base");
            var baseSha = Git("rev-parse", "HEAD");
            File.WriteAllText(instrument, "changed instrument");
            var candidate = definitions.DeepClone().AsArray();
            if (change is "hash" or "output") candidate[0]![change] = new string('2', change == "hash" ? 64 : 86);
            else if (change == "add") candidate.Add((JsonNode)new JsonObject { ["input"] = "9.ssz", ["hash"] = new string('0', 64), ["output"] = new string('1', 86) });
            else if (change == "remove") candidate.RemoveAt(candidate.Count - 1);
            else if (change == "duplicate") candidate.Add(candidate[0]!.DeepClone());
            else if (change == "malformed") candidate = new JsonArray(42);
            else if (change == "empty") candidate.Clear();
            File.WriteAllText(manifest, candidate.ToJsonString());
            Git("add", "."); Git("commit", "-m", "candidate");
            var checkout = source;
            if (shallow)
            {
                var remote = Path.Combine(directory.FullName, "remote.git");
                Git("clone", "--bare", ".", remote);
                checkout = Path.Combine(directory.FullName, "checkout");
                Git("clone", "--depth=1", "file://" + remote, checkout);
                Require(Run("git", ["cat-file", "-e", baseSha + "^{commit}"], checkout).Code != 0, "base unexpectedly present in shallow fixture");
            }
            var outputs = Path.Combine(directory.FullName, "outputs");
            var result = Bash(Step("Select measurement definitions"), new() { ["BASE_SHA"] = change == "no-base" ? "" : baseSha, ["GUEST_DIR"] = guest,
                ["RUNNER_TEMP"] = directory.FullName, ["GITHUB_ENV"] = Path.Combine(directory.FullName, "env"), ["GITHUB_OUTPUT"] = outputs }, checkout);
            if (change is "duplicate" or "malformed" or "empty") { Require(result.Code != 0, "invalid head accepted"); Contains(result.Error, "invalid pinned block set"); return; }
            Require(result.Code == 0, result.Error);
            var fallback = change is "hash" or "output" or "invalid-base" or "no-base" or "python-base";
            var selected = Path.Combine(directory.FullName, "zisk-bench-definitions");
            Require(File.ReadAllText(Path.Combine(selected, "scripts/zisk-bench/report.cs")) == (fallback ? "changed instrument" : "base instrument"), "wrong instrument selected");
            Require(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(selected, guest, "inputs.json"))), fallback ? candidate : definitions), "wrong definitions selected");
            Contains(File.ReadAllText(outputs), "trusted=" + (!fallback).ToString().ToLowerInvariant());
            Require(result.Output.Contains("::notice::Head changed pinned definitions", StringComparison.Ordinal) == (change is "hash" or "output"), "re-pinning notice differs");
            if (change == "invalid-base") Contains(result.Output, "::notice::Base pinned definitions are unusable");
            if (change == "python-base") Contains(result.Output, "No base-owned measurement definitions");
            if (change == "no-base") Require(!result.Output.Contains("No base-owned measurement definitions", StringComparison.Ordinal), "no-base notice changed");
        });

        var script = ScriptIndentRegex().Replace(workflow.Split("          script: |\n", 2)[1], "");
        var node = Write("comment.cjs", """
            const {script,comments,result,report} = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));
            const calls = [];
            const github = {paginate: async () => comments, rest: {issues: {
              listComments: {}, updateComment: async x => calls.push(['update',x.comment_id,x.body]),
              createComment: async x => calls.push(['create',x.body])}}};
            const context = {repo:{owner:'o',repo:'r'},payload:{pull_request:{number:1}}};
            const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
            new AsyncFunction('github','context','process',script)(github,context,{env:{
              COMMENT_BODY:report,MEASURE_RESULT:result,MEASURE_COMMIT:'measured-sha',RUN_URL:'https://example.com/run'}})
              .then(() => console.log(JSON.stringify(calls))).catch(e => {console.error(e);process.exit(1)});
            """);
        foreach (var hasBot in new[] { false, true })
            foreach (var (status, report) in new[] { ("success", "report"), ("failure", ""), ("failure", "partial report"), ("success", "") })
                Case($"comment ownership bot={hasBot} status={status} report={report}", () =>
                {
                    var comments = new JsonArray(new JsonObject { ["id"] = 101, ["user"] = new JsonObject { ["type"] = "User", ["login"] = "reviewer" }, ["body"] = ZiskBench.Marker });
                    if (hasBot) comments.Add((JsonNode)new JsonObject { ["id"] = 202, ["user"] = new JsonObject { ["type"] = "Bot", ["login"] = "github-actions[bot]" }, ["body"] = ZiskBench.Marker });
                    var path = Write("comment.json", new JsonObject { ["script"] = script, ["comments"] = comments, ["result"] = status, ["report"] = report }.ToJsonString());
                    var result = Run("node", [node, path]);
                    Require(result.Code == 0, result.Error);
                    var calls = JsonNode.Parse(result.Output)!.AsArray();
                    Require(calls.Count == 1 && calls[0]![0]!.ToString() == (hasBot ? "update" : "create") && (!hasBot || calls[0]![1]!.GetValue<int>() == 202), "comment ownership changed");
                    var body = calls[0]!.AsArray()[^1]!.ToString();
                    if (status == "success" && report.Length > 0) Require(body == report, "successful report altered");
                    else { Require(body.StartsWith(ZiskBench.Marker, StringComparison.Ordinal), "missing report marker"); Contains(body, "previous measurements are stale", "measured-sha", "https://example.com/run"); }
                    Contains(workflow.Split("\n  comment:\n", 2)[1], "!cancelled()");
                });
    }
    Console.WriteLine($"Passed {passed} tests.");
}
finally { Directory.Delete(temporary, true); }

partial class Program
{
    [GeneratedRegex(@"      - name: ([^\n]*)\n(?:(?!      - name:).)*?        run: \|\n((?:          [^\n]*\n|\n)+)", RegexOptions.Singleline)]
    private static partial Regex StepRegex();
    [GeneratedRegex(@"\A[0-9]+\.ssz\z")]
    private static partial Regex InputNameRegex();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashRegex();
    [GeneratedRegex(@"\A[0-9a-f]{86}\z")]
    private static partial Regex OutputRegex();

    [GeneratedRegex("^            ", RegexOptions.Multiline)]
    private static partial Regex ScriptIndentRegex();
    [GeneratedRegex("jq --arg commit \"\\$GITHUB_SHA\" '(.*?)'")]
    private static partial Regex CommitExpressionRegex();
    [GeneratedRegex("^          ", RegexOptions.Multiline)]
    private static partial Regex StepIndentRegex();
}
