#!/usr/bin/env dotnet
#:include ../sync/SyncTests.Helpers.cs
#:include ../sync/Sync.Helpers.cs
#:include ../sync-metrics/Metrics.Helpers.cs
#:include ../../.github/actions/gcp-runner/Gcp.Helpers.cs
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string Line(int minute, string text, string prefix = "") => $"{prefix}2026-09-16T12:{minute:00}:00.123456789Z {text}";
foreach (var prefix in new[] { "", "job\tstep\t" })
    Cases.Test($"log markers counters duplicates {prefix}", () =>
    {
        var found = new Dictionary<string, DateTimeOffset>();
        var counters = new JsonObject();
        foreach (var (minute, text) in new[] { (0, "Starting snap sync."), (9, "Starting snap sync."), (10, "Snap sync completed."), (12, "STATE SYNC FINISHED"), (13, "Collecting trie stats"), (20, "Waiting for storage verification workers"), (23, "Verification complete. Accounts=400000000, Slots=1600000000") }) Metrics.ParseLine(Line(minute, text, prefix), found, counters);
        Cases.Equal(10d, Metrics.Minutes(found, "snap_start", "snap_end"));
        Cases.Equal(2d, Metrics.Minutes(found, "snap_end", "state_finished"));
        Cases.Equal(3d, Metrics.Minutes(found, "drain_start", "verify_end_flat"));
        Cases.Equal(400000000, counters["accounts"]!.GetValue<int>());
        Cases.Equal(1600000000, counters["slots"]!.GetValue<int>());
    });
Cases.Test("missing and reversed timestamps", () =>
{
    var found = new Dictionary<string, DateTimeOffset>();
    var counters = new JsonObject();
    foreach (var line in new[] { "untimestamped Starting snap sync.", Line(5, "Snap sync completed."), Line(10, "Starting snap sync."), "2026-09-16T12:00:00Z irrelevant" }) Metrics.ParseLine(line, found, counters);
    Cases.Equal<double?>(null, Metrics.Minutes(found, "snap_start", "snap_end"));
    Cases.Equal<double?>(null, Metrics.Minutes(found, "verify_start", "verify_end_flat"));
});
foreach (var (mode, ending, verify) in new[] { ("Flat", "Verification complete. Accounts=4, Slots=8", true), ("HalfPath", "Stats after finishing state", true), ("Flat", "node stopped before verification", false) })
    Cases.Test($"partial logs {mode} {ending}", () =>
    {
        var found = new Dictionary<string, DateTimeOffset>();
        var counters = new JsonObject();
        Metrics.ParseLine(Line(0, "Collecting trie stats"), found, counters);
        Metrics.ParseLine(Line(5, ending), found, counters);
        var record = Metrics.Record("gnosis", mode, "test-image", found, counters);
        Cases.Equal(mode, record["mode"]!.ToString());
        Cases.Equal("test-image", record["image"]!.ToString());
        Cases.Equal(verify, record.ContainsKey("verify_min"));
        Cases.True(!record.ContainsKey("drain_min"), "no drain without marker");
    });
foreach (var (network, expected) in new[] { ("base-mainnet", "sedge-execution-op-l2-client"), ("op-mainnet", "sedge-execution-op-l2-client"), ("world-sepolia", "sedge-execution-op-l2-client"), ("taiko-hoodi", "sedge-execution-taiko-client"), ("mainnet", "sedge-execution-client") })
    Cases.Test($"container mapping {network}", () => Cases.Equal(expected, Sync.Container(network)));
foreach (var scenario in new[] { "legacy", "reusable", "trigger", "paginate", "failure", "incomplete", "reversed", "retry", "empty", "invalid" })
    await Cases.Test($"publish {scenario}", async () =>
    {
        var temporary = Directory.CreateTempSubdirectory("sync-metrics-");
        try
        {
            var directory = Path.Combine(temporary.FullName, "metrics");
            Directory.CreateDirectory(directory);
            var history = Path.Combine(temporary.FullName, "history.jsonl");
            var source = Path.Combine(directory, "mainnet.json");
            await File.WriteAllTextAsync(source, """{"network":"mainnet","mode":"Flat","snap_min":10}""");
            var run = new JsonObject { ["created_at"] = "2026-09-16T12:00:00Z", ["event"] = "workflow_run", ["head_sha"] = new string('a', 40), ["run_attempt"] = 2 };
            var job = new JsonObject { ["name"] = scenario == "legacy" ? "Sync mainnet (Flat)" : "Sync mainnet (Flat) / sync", ["conclusion"] = scenario == "failure" ? "failure" : "success", ["started_at"] = "2026-09-16T12:00:00Z", ["completed_at"] = scenario == "incomplete" ? null : scenario == "reversed" ? "2026-09-16T11:00:00Z" : "2026-09-16T12:20:00Z" };
            var lifecycle = (JsonObject)job.DeepClone();
            lifecycle["name"] = "Sync mainnet (Flat) / destroy_runner";
            lifecycle["completed_at"] = "2026-09-16T12:59:00Z";
            var calls = new List<string>();
            Task<JsonNode> Api(string path, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                calls.Add(path);
                if (!path.Contains("/jobs?", StringComparison.Ordinal)) return Task.FromResult<JsonNode>(run);
                var jobs = new JsonArray();
                if (scenario == "paginate" && path.EndsWith("page=1", StringComparison.Ordinal))
                    for (var i = 0; i < 100; i++) { var other = job.DeepClone(); other["name"] = "irrelevant"; jobs.Add(other); }
                else { jobs.Add(job.DeepClone()); jobs.Add(lifecycle.DeepClone()); }
                return Task.FromResult<JsonNode>(new JsonObject { ["jobs"] = jobs });
            }
            if (scenario == "retry") await File.WriteAllTextAsync(history, """
                {"run_id":122,"network":"mainnet","mode":"Flat","snap_min":10}
                {"run_id":123,"network":"mainnet","mode":"HalfPath","snap_min":10}
                {"run_id":123,"network":"mainnet","mode":"Flat","snap_min":99}
                """ + "\n");
            if (scenario is "empty" or "invalid")
            {
                await File.WriteAllTextAsync(history, "{\"run_id\":1}\n");
                if (scenario == "empty") File.Delete(source); else await File.WriteAllTextAsync(source, "invalid json");
            }
            async Task Execute() => await Metrics.Publish(directory, history, "owner/repo", 123, "2", scenario == "trigger" ? new string('b', 40) : "", Api, CancellationToken.None);
            try { await Execute(); Cases.True(scenario != "invalid", "invalid JSON must fail"); }
            catch (System.Text.Json.JsonException) when (scenario == "invalid") { }
            if (scenario is "empty" or "invalid") { Cases.Equal("{\"run_id\":1}\n", await File.ReadAllTextAsync(history)); return; }
            var lines = await File.ReadAllLinesAsync(history);
            var record = JsonNode.Parse(lines[^1])!;
            Cases.Equal(new string(scenario == "trigger" ? 'b' : 'a', 10), record["commit"]!.ToString());
            Cases.Equal(2, record["run_attempt"]!.GetValue<int>());
            if (scenario is "failure" or "incomplete" or "reversed") Cases.True(record["job_min"] is null, "unsuccessful job omitted");
            else Cases.Equal(20d, record["job_min"]!.GetValue<double>());
            if (scenario == "paginate")
            {
                Cases.True(calls[1].EndsWith("/attempts/2/jobs?per_page=100&page=1", StringComparison.Ordinal), "attempt scoped");
                Cases.True(calls[2].EndsWith("page=2", StringComparison.Ordinal), "pagination");
            }
            if (scenario == "retry")
            {
                Cases.Equal(3, lines.Length);
                Cases.Equal(122, JsonNode.Parse(lines[0])!["run_id"]!.GetValue<int>());
                Cases.Equal("HalfPath", JsonNode.Parse(lines[1])!["mode"]!.ToString());
                Cases.Equal(10, record["snap_min"]!.GetValue<int>());
                var once = await File.ReadAllTextAsync(history);
                await Execute();
                Cases.Equal(once, await File.ReadAllTextAsync(history));
            }
        }
        finally { temporary.Delete(recursive: true); }
    });
await Cases.Test("dashboard pinned master images", async () =>
{
    var html = File.ReadAllText(Path.Combine(Cases.Root(), "scripts/sync-metrics/index.html"));
    var script = Regex.Match(html, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value.Replace("init();", "", StringComparison.Ordinal);
    script += """
        for (const [row, expected] of [
            [{event:'workflow_run',image:'nethermindeth/nethermind:master'},false],
            [{event:'workflow_run',image:'nethermindeth/nethermind:master-abcdef1'},false],
            [{event:'workflow_run',image:'nethermindeth/nethermind:master-'+ 'a'.repeat(40)},false],
            [{},false],[{event:'workflow_dispatch'},true],[{image:'nethermindeth/nethermind:experiment'},true]
        ]) { if (isExperimental(row)!==expected) throw new Error(JSON.stringify(row)); }
        """;
    var result = await Gcp.Run("node", ["-e", script], null, CancellationToken.None);
    Cases.Equal(0, result.Code);
});
Cases.Finish();
