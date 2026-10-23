#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include CorpusParity.Helpers.cs
#:include CorpusResults.Helpers.cs

using System.Globalization;
using System.Text.Json.Nodes;

var tests = 0;
var root = Directory.CreateTempSubdirectory("corpus results tests ");
try
{
    Check("sanitizer keeps only fixed aggregate schema", () =>
    {
        var raw = Write("raw.json", Raw()); var output = Path.Combine(root.FullName, "safe.json");
        CorpusResults.Sanitize(raw, output);
        var data = JsonNode.Parse(File.ReadAllText(output))!.AsObject();
        Equal(data.Count, 1); Equal(data["metrics"]!.AsObject().Count, 5); Equal(data.ToJsonString().Contains("SENTINEL"), false);
    });
    Check("selector classes are ranked and validated", () =>
    {
        var raw = Raw(); var metrics = raw["metrics"]!.AsObject();
        metrics["http_req_duration{req_name:class_1,scenario:nm}"] = metrics["http_req_duration"]!.DeepClone();
        metrics["http_reqs{req_name:class_1,scenario:nm}"] = new JsonObject { ["values"] = new JsonObject { ["count"] = 30 } };
        metrics["http_req_duration{req_name:'class_2'}"] = metrics["http_req_duration"]!.DeepClone();
        metrics["http_req_duration{req_name:SENTINEL}"] = metrics["http_req_duration"]!.DeepClone();
        var data = CorpusResults.SanitizeData(raw); var classes = data["metrics"]!["classes"]!.AsObject();
        Equal(string.Join(',', classes.Select(pair => pair.Key)), "class_1,class_2");
        Equal(classes["class_1"]!["values"]!["count"]!.ToString(), "30"); Equal(classes["class_2"]!["values"]!["count"]!.ToString(), "0");
        Equal(data.ToJsonString().Contains("SENTINEL"), false);
        Validate("summary.json", data);
        classes["SENTINEL"] = classes["class_2"]!.DeepClone(); classes.Remove("class_2");
        Reject(() => Validate("summary.json", data));
    });
    foreach (var layout in new[] { "{\"rate\":0.25}", "{\"value\":0.25}", "{\"values\":{\"rate\":0.25}}", "{\"values\":{\"value\":0.25}}" })
        Check($"rate layout {layout}", () => { var raw = Raw(); raw["metrics"]!["http_req_failed"] = JsonNode.Parse(layout); Equal(CorpusResults.SanitizeData(raw)["metrics"]!["http_req_failed"]!["values"]!["rate"]!.ToString(), "0.25"); });
    Check("optional metrics default to zero", () =>
    {
        var raw = Raw(); foreach (var name in new[] { "checks", "dropped_iterations", "http_req_failed" }) raw["metrics"]!.AsObject().Remove(name);
        var data = CorpusResults.SanitizeData(raw);
        foreach (var name in new[] { "checks", "dropped_iterations", "http_req_failed" }) Equal(data["metrics"]![name]!["values"]!.AsObject().All(pair => pair.Value!.ToString() == "0"), true);
    });
    foreach (var count in new JsonNode?[] { JsonValue.Create(double.PositiveInfinity), JsonValue.Create(-1), JsonValue.Create(true), JsonValue.Create(0), null })
        Check("reject invalid request count", () => { var raw = Raw(); raw["metrics"]!["http_reqs"]!["values"]!["count"] = count; Reject(() => CorpusResults.SanitizeData(raw)); });
    Check("reject absent mandatory metrics", () => { Reject(() => CorpusResults.SanitizeData(new JsonObject())); var raw = Raw(); raw["metrics"]!.AsObject().Remove("http_req_duration"); Reject(() => CorpusResults.SanitizeData(raw)); });
    Check("missing source diagnostic contains no private path", () =>
    {
        var previous = Console.Error; using var errors = new StringWriter();
        try { Console.SetError(errors); Equal(CorpusResults.Run(["sanitize", Path.Combine(root.FullName, "SENTINEL.json"), Path.Combine(root.FullName, "out.json")]), 1); }
        finally { Console.SetError(previous); }
        Equal(errors.ToString().Contains("SENTINEL"), false);
    });
    Check("stage allowlist excludes raw data and warmups", () =>
    {
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, Guid.NewGuid().ToString("N"))).FullName;
        foreach (var relative in new[] { "corpus/a/nm/100", "corpus/a/nm/warmup", "warmup-cell/a/nm", "corpus/warmup-heavy/nm/100" }) Write(Path.Combine(source, relative, "summary.json"), CorpusResults.SanitizeData(Raw()));
        Write(Path.Combine(source, "corpus/a/reth/parity.json"), Parity());
        File.WriteAllText(Path.Combine(source, "corpus/a/nm/100/jsonbench-summary.md"), "## ok\n");
        foreach (var filename in new[] { "raw-responses.jsonl", "jsonbench.log", "results.json" }) File.WriteAllText(Path.Combine(source, filename), "SENTINEL");
        var destination = Path.Combine(root.FullName, Guid.NewGuid().ToString("N")); CorpusResults.Stage(source, destination);
        var paths = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(destination, path).Replace('\\', '/')).Order().ToArray();
        Equal(string.Join(',', paths), "corpus/a/nm/100/jsonbench-summary.md,corpus/a/nm/100/summary.json,corpus/a/reth/parity.json,corpus/warmup-heavy/nm/100/summary.json");
        Equal(Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Any(path => File.ReadAllText(path).Contains("SENTINEL")), false);
    });
    foreach (var missing in new[] { "", "warmup_seconds", "warmup_rps", "negative" }) Check($"timings metadata {missing}", () =>
    {
        var meta = Meta(3, 49.9, 4); if (missing == "negative") meta["warmup_rps"] = -1; else if (missing != "") meta.Remove(missing);
        if (missing == "") Validate("timings.meta.json", meta); else Reject(() => Validate("timings.meta.json", meta));
    });
    foreach (var bad in new[] { "", "iso|a|nm|100=/etc/passwd", "not a manifest line", "iso|a|b|c|d|e=x/jsonbench-summary.md" }) Check($"manifest relativization {bad}", () =>
    {
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, Guid.NewGuid().ToString("N"))).FullName;
        Write(Path.Combine(source, "corpus/a/nm/100/summary.json"), CorpusResults.SanitizeData(Raw()));
        File.WriteAllText(Path.Combine(source, "summaries.manifest"), (bad.Length > 0 ? bad : "iso|a|nm|100=" + Path.Combine(source, "corpus/a/nm/100/jsonbench-summary.md")) + "\n");
        var destination = Path.Combine(root.FullName, Guid.NewGuid().ToString("N")); CorpusResults.Stage(source, destination);
        var manifest = Path.Combine(destination, "summaries.manifest"); Equal(File.Exists(manifest), bad.Length == 0);
        if (bad.Length == 0) Equal(File.ReadAllText(manifest), "iso|a|nm|100=corpus/a/nm/100/jsonbench-summary.md\n");
    });
    foreach (var mutation in new[] { "extra", "negative", "long label", "divergence kind", "divergence content", "raw summary" }) Check($"reject unsafe stage {mutation}", () =>
    {
        var report = Parity(); var filename = "parity.json";
        switch (mutation)
        {
            case "extra": report["extra"] = 1; break;
            case "negative": report["matched"] = -1; break;
            case "long label": report["baseline_client"] = new string('x', 200); break;
            case "divergence kind": report["divergences"]![0]!["index"] = 0; break;
            case "divergence content": report["divergences"]![0]!["data"] = "SENTINEL"; break;
            case "raw summary": report = Raw(); filename = "summary.json"; break;
        }
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, Guid.NewGuid().ToString("N"))).FullName;
        Write(Path.Combine(source, filename), report); Reject(() => CorpusResults.Stage(source, Path.Combine(root.FullName, Guid.NewGuid().ToString("N"))));
    });
    Check("empty and overlapping staging roots reject", () =>
    {
        var source = Directory.CreateDirectory(Path.Combine(root.FullName, Guid.NewGuid().ToString("N"))).FullName;
        Reject(() => CorpusResults.Stage(source, Path.Combine(root.FullName, Guid.NewGuid().ToString("N"))));
        Reject(() => CorpusResults.Stage(source, source)); Reject(() => CorpusResults.Stage(source, Path.Combine(source, "stage")));
    });
    Check("latency regression and improvement directions", () => { Cell("master", 20, 100); Cell("pr", 22, 90); Contains(Comment(), "+10.0%", "-10.0%", "🔴", "🟢"); });
    foreach (var clean in new[] { false, true }) Check($"response parity clean={clean}", () =>
    {
        Cell("master", 20, 100); Cell("pr", 20, 100);
        Write("corpus/corpus-a/pr/parity.json", new JsonObject { ["total"] = 497, ["matched"] = clean ? 497 : 490, ["both_rpc_errors"] = 0, ["content_mismatches"] = clean ? 0 : 7 });
        Contains(Comment(), clean ? "497/497 identical to master" : "DIVERGES from master", clean ? "identical" : "content_mismatches=7");
    });
    Check("repeats pool and show measured spread", () =>
    {
        Cell("master", 20, 100, cpu: 19); Cell("pr", 19, 90, cpu: 18); Cell("pr_r2", 19, 90, cpu: 18);
        Cell("master_r2", 22, 110, cpu: 21); Cell("master_r2", 21, 105, slot: "100_r2", cpu: 20);
        Contains(Comment(), "n=3/2 runs", "| avg | 21.00 ms | 19.00 ms |", "| CPU-ms/request | 20.00 | 18.00 |", "| 9.5% |");
        Equal(Comment().Contains("100_r2"), false);
    });
    Check("selector table and paired timings", () =>
    {
        Cell("master", 20, 100, classes: [10, 50]); Cell("pr", 20, 100, classes: [10, 40]);
        Timings("master", [.. Enumerable.Repeat(10d, 10), .. Enumerable.Repeat(100d, 10)], 800);
        Timings("pr", [.. Enumerable.Repeat(10d, 10), .. Enumerable.Repeat(80d, 10)], 880);
        Contains(Comment(), "| class_2 | 100 | 50.00 | 40.00 |", "-20.0%", "Paired per-record replay (20 records", "10 faster", "Closed-loop throughput (concurrency 16): master 800.0 req/s, PR 880.0 req/s");
    });
    foreach (var (cached, delta) in new[] { (true, 3d), (true, 6d), (false, 1.5), (false, 2d) }) Check($"timing floors cached={cached} delta={delta}", () =>
    {
        Timings("master", [100], 100); Timings("pr", [100 * (1 + delta / 100)], 100 * (1 - delta / 100));
        var arrow = cached && delta == 3 ? "⚪" : "🔴";
        Contains(Comment(cached), $"median delta {arrow}", $"{(cached ? 5 : 1)}% arrow floor", $", {arrow} -{delta.ToString("F1", CultureInfo.InvariantCulture)}%.");
    });
    Check("record shifts exceed their own repeat spread", () =>
    {
        Timings("master", [.. Enumerable.Repeat(10d, 10), .. Enumerable.Repeat(100d, 10)], 800);
        Timings("master_r2", [.. Enumerable.Repeat(10d, 10), .. Enumerable.Repeat(90d, 10)], 800);
        Timings("pr", [.. Enumerable.Repeat(10d, 10), .. Enumerable.Repeat(80d, 10)], 880);
        Contains(Comment(), "0 records slower and 0 faster", "median delta");
    });
    foreach (var unequal in new[] { false, true }) Check($"load mismatch {unequal}", () =>
    {
        Cell("master", 20, 100, requests: 20000); Cell("pr", 20, 100, requests: unequal ? 12000 : 19900, dropped: unequal ? 8000 : 0);
        Equal(Comment().Contains("Unequal load"), unequal);
        if (unequal) Contains(Comment(), "20000/12000 requests/run", "8000 iteration(s) dropped");
    });
    foreach (var repeat in new[] { false, true }) Check($"cached floor cannot use master control repeat={repeat}", () =>
    {
        Cell("master", 20, 100); if (repeat) Cell("master_r2", 20.1, 100); Cell("pr", 20.6, 103);
        var cached = Comment(true); var normal = Comment();
        Contains(cached, "⚪", "| n/a |", "not co-run", "is therefore 5%, not the in-run 2.5%", "No A/A control ran");
        Contains(normal, "🔴", "master against its own repeat"); Equal(cached.Contains("master against its own repeat"), false);
    });
    foreach (var cached in new[] { false, true }) Check($"comment CLI cached={cached}", () =>
    {
        Cell("master", 20, 100); Cell("pr", 20.6, 103);
        var previous = Console.Out; using var output = new StringWriter();
        try { Console.SetOut(output); Equal(CorpusResults.Run(["comment", root.FullName, "--baseline", "master", "--candidate", "pr", .. cached ? new[] { "--cached-baseline" } : []]), 0); }
        finally { Console.SetOut(previous); }
        Contains(output.ToString(), "corpus-a", "| metric | master | PR | delta |"); Equal(output.ToString().Contains("not co-run"), cached);
    });
    Check("mixed concurrency and failed replay are visible", () =>
    {
        Timings("master", [10, 10], 800, 8); Timings("pr", [10, 10], 880, 16);
        var path = Path.Combine(root.FullName, "corpus/corpus-a/pr/timings.meta.json"); var meta = JsonNode.Parse(File.ReadAllText(path))!; meta["outcomes"]!["transport_failure"] = 1; File.WriteAllText(path, meta.ToJsonString());
        Contains(Comment(), "mixed concurrency 8/16, not comparable", "1 replay request(s) did not return a result");
    });
    Check("missing client and empty comments", () => { Equal(Comment(), "No corpus cells were produced, so there is nothing to compare."); Cell("master", 20, 100); Contains(Comment(), "missing a client"); });
    Check("CLI rejects missing stage and unknown options", () => { Equal(CorpusResults.Run(["stage", Path.Combine(root.FullName, "absent"), Path.Combine(root.FullName, "stage")]), 1); Equal(CorpusResults.Run(["comment", root.FullName, "--baseline", "master", "--candidate", "pr", "--bad"]), 2); });
    Check("bootstrap generator preserves seeded samples", () =>
    {
        var random = new CorpusBootstrapRandom(20260825);
        var expected = new[] { 0.07013531231555714, 0.6376282484220942, 0.1831432641446603 };
        foreach (var value in expected) Equal(random.NextDouble(), value);
    });
    Console.WriteLine($"Passed {tests} tests.");
}
finally { root.Delete(true); }

void Check(string name, Action action)
{
    foreach (var directory in root.GetDirectories()) directory.Delete(true);
    foreach (var file in root.GetFiles()) file.Delete();
    try { action(); Console.WriteLine($"PASS {name}"); tests++; }
    catch (Exception error) { throw new Exception($"FAIL {name}: {error.Message}", error); }
}
void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
void Contains(string actual, params string[] expected) { foreach (var value in expected) if (!actual.Contains(value, StringComparison.Ordinal)) throw new Exception($"Missing {value} in {actual}"); }
void Reject(Action action) { try { action(); } catch (CorpusResultsError) { return; } throw new Exception("Expected validation error"); }
string Write(string path, JsonNode data) { path = Path.Combine(root.FullName, path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, data.ToJsonString()); return path; }
void Validate(string filename, JsonObject data) => CorpusResults.Validate(Write(Path.Combine(Guid.NewGuid().ToString("N"), filename), data));
JsonObject Raw() => JsonNode.Parse("""{"metrics":{"http_req_duration":{"values":{"avg":1.0,"med":1.0,"p(90)":2.0,"p(95)":3.0,"p(99)":4.0,"max":5.0}},"http_reqs":{"values":{"count":90,"rate":3.0}},"http_req_failed":{"values":{"rate":0.0}},"checks":{"values":{"passes":180,"fails":0}},"dropped_iterations":{"values":{"count":0}},"http_req_duration{url:SENTINEL}":{"values":{"avg":9}}},"root_group":{"name":"SENTINEL"}}""")!.AsObject();
JsonObject Parity()
{
    var report = new JsonObject(); foreach (var field in CorpusParity.CounterFields) report[field] = 0;
    report["total"] = 90; report["matched"] = 89; report["content_mismatches"] = 1;
    report["divergences"] = new JsonArray(new JsonObject { ["index"] = 7, ["kind"] = "content_mismatch" });
    report["baseline_client"] = "nethermind"; report["candidate_client"] = "reth"; return report;
}
void Cell(string label, double average, double p99, string slot = "100", double? cpu = null, double[]? classes = null, int requests = 12000, int dropped = 0)
{
    var data = CorpusResults.SanitizeData(Raw()); var metrics = data["metrics"]!;
    foreach (var (key, value) in new[] { ("avg", average), ("med", average), ("p(90)", average * 2), ("p(95)", average * 2.2), ("p(99)", p99), ("max", p99 * 3) }) metrics["http_req_duration"]!["values"]![key] = value;
    metrics["http_reqs"]!["values"]!["count"] = requests; metrics["dropped_iterations"]!["values"]!["count"] = dropped;
    if (classes is not null)
    {
        metrics["classes"] = new JsonObject();
        for (var index = 0; index < classes.Length; index++)
        {
            var values = new JsonObject(); foreach (var key in CorpusResults.MetricFields["http_req_duration"]) values[key] = classes[index] * (key == "p(99)" ? 2 : key == "max" ? 3 : 1);
            values["count"] = 100; metrics["classes"]![$"class_{index + 1}"] = new JsonObject { ["values"] = values };
        }
    }
    var path = $"corpus/corpus-a/{label}/{slot}"; Write(path + "/summary.json", data);
    if (cpu.HasValue) Write(path + "/resources.json", new JsonObject { ["cpu_ms_per_request"] = cpu.Value, ["requests"] = requests });
}
JsonObject Meta(int records, double achieved, int concurrency) => new()
{
    ["head"] = 1, ["chain_id"] = 1, ["block_hash"] = "0x" + new string('a', 64), ["records"] = records, ["passes"] = 2, ["requests"] = 2 * records,
    ["target_rps"] = 0, ["achieved_rps"] = achieved, ["concurrency"] = concurrency, ["warmup_seconds"] = 60, ["warmup_rps"] = 400, ["outcomes"] = new JsonObject { ["ok"] = 2 * records }
};
void Timings(string label, double[] latencies, double achieved, int concurrency = 16)
{
    var path = Path.Combine(root.FullName, "corpus/corpus-a", label); Directory.CreateDirectory(path);
    var rows = new[] { "record_index,pass_1_ms,pass_1_status,pass_2_ms,pass_2_status" }.Concat(latencies.Select((ms, index) => string.Create(CultureInfo.InvariantCulture, $"{index + 1},{ms:F3},ok,{ms * 1.02:F3},ok")));
    File.WriteAllLines(Path.Combine(path, "timings.csv"), rows); Write(Path.Combine(path, "timings.meta.json"), Meta(latencies.Length, achieved, concurrency));
}
string Comment(bool cached = false) => CorpusResults.Comment(root.FullName, "master", "pr", cached);
