// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal sealed class SweepRunner(SweepConfiguration configuration)
{
    private readonly List<string> _summaries = [];
    private readonly List<string> _labels = [];
    private readonly Dictionary<string, (string State, string Label, bool Saved)> _baselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _records = new(StringComparer.Ordinal);
    private readonly List<(string Corpus, string Label, string Report)> _parityRows = [];
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private string _baselineLabel = "";
    private bool _usingSaved;
    private string[] _corpora = [];
    private string Get(string name) => configuration[name];
    private string ParityState => Path.Combine(Get("SCRATCH_ROOT"), "parity");
    private const string Rpc = "http://localhost:8545";

    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var scratch = await BenchmarkPaths.CanonicalAsync(Get("SCRATCH_ROOT"), false, cancellationToken);
        BenchmarkPaths.AssertSane(scratch, "SCRATCH_ROOT");
        foreach (var client in configuration.Arms.Select(arm => arm.Client).Distinct())
            await BenchmarkPaths.GuardAsync(configuration.Snapshot(client), scratch, cancellationToken);
        try { Directory.CreateDirectory(scratch); }
        catch (UnauthorizedAccessException) { await Root("mkdir", ["-p", "--", scratch], cancellationToken); }
        configuration.Values["SCRATCH_ROOT"] = await BenchmarkPaths.CanonicalAsync(scratch, true, cancellationToken);
        Directory.CreateDirectory(Get("OUT_DIR")); Directory.CreateDirectory(Get("STATE_ROOT"));
        foreach (var name in new[] { "METHOD", "TRACER", "TRACE_TYPES" }) Environment.SetEnvironmentVariable("RPC_BENCH_CORPUS_" + name, Get("CORPUS_" + name));
        if (configuration.Corpus)
        {
            _corpora = Directory.GetFiles(Get("CORPUS_DIR"), Get("CORPUS_GLOB")).Order(StringComparer.Ordinal).ToArray();
            if (_corpora.Length == 0) throw new IOException("no corpus files matched");
            var labels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var corpus in _corpora)
            {
                if (!labels.Add(SweepConfiguration.CorpusLabel(corpus))) throw new ArgumentException("corpus labels collide");
                var validation = await Script("CorpusParity.cs", ["validate", "--corpus", corpus], null, cancellationToken);
                var count = Regex.Match(validation.Output, @"corpus OK: ([0-9]+)");
                if (validation.Code != 0 || !count.Success || !long.TryParse(count.Groups[1].Value, out var records) || records <= 0) throw new IOException("corpus validation failed");
                _records[corpus] = records;
            }
            if (Directory.Exists(ParityState)) Directory.Delete(ParityState, true);
            Directory.CreateDirectory(ParityState);
            _usingSaved = Get("CORPUS_BASELINE") == "use" && _corpora.All(corpus => Nonempty(Path.Combine(Get("CORPUS_BASELINE_DIR"), SweepConfiguration.CorpusLabel(corpus) + ".json.gz")));
        }
        Console.WriteLine($"Sweep: head {Get("SNAPSHOT_BLOCK")}, layout {Get("STATE_LAYOUT")}, seed {Get("JB_SEED")}, rounds {Get("ROUNDS")}");
        LogProvenance();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var round = 0L; round < configuration.Number("ROUNDS"); round++)
        {
            foreach (var arm in round % 2 == 0 ? configuration.Arms : configuration.Arms.Reverse())
            {
                seen[arm.Label] = seen.GetValueOrDefault(arm.Label) + 1;
                var label = arm.Label + (seen[arm.Label] > 1 ? "_r" + seen[arm.Label] : "");
                await RunArm(arm, label, cancellationToken);
            }
        }
        await WriteSummary(cancellationToken);
        foreach (var (kind, count) in _failures) Console.Error.WriteLine($"::error::{count} {kind}");
        return _failures.Count == 0 ? 0 : 1;
    }

    private async Task RunArm(SweepArm arm, string label, CancellationToken cancellationToken)
    {
        var flags = arm.Flags;
        var armScratch = "";
        if (flags.Contains("{ARM_SCRATCH}", StringComparison.Ordinal))
        {
            var armRoot = Path.Combine(Get("SCRATCH_ROOT"), "arm");
            if (new DirectoryInfo(armRoot).LinkTarget is not null) throw new IOException("per-arm scratch root must not be a symlink");
            await Root("mkdir", ["-p", "--", armRoot], cancellationToken);
            armRoot = await BenchmarkPaths.CanonicalAsync(armRoot, true, cancellationToken);
            if (!BenchmarkPaths.Contains(Get("SCRATCH_ROOT"), armRoot)) throw new IOException("per-arm scratch root escaped SCRATCH_ROOT");
            armScratch = Path.Combine(armRoot, label);
            BenchmarkPaths.AssertSane(armScratch, "ARM_SCRATCH_DIR");
            if (BenchmarkPaths.MountsUnder(armScratch).Length > 0) throw new IOException("mounts remain under per-arm scratch");
            await Root("rm", ["-rf", "--", armScratch], cancellationToken);
            await Root("mkdir", ["-p", "--", armScratch], cancellationToken);
            await Root("chmod", ["0777", "--", armScratch], cancellationToken);
            if (!Directory.Exists(armScratch) || new DirectoryInfo(armScratch).LinkTarget is not null) throw new IOException("invalid per-arm scratch directory");
            flags = flags.Replace("{ARM_SCRATCH}", armScratch, StringComparison.Ordinal);
        }
        var pull = await BenchmarkProcess.RunAsync("docker", ["pull", arm.Image], cancellationToken);
        if (pull.Code != 0) Console.WriteLine($"pull failed — assuming {arm.Image} is local");
        var state = Path.Combine(Get("STATE_ROOT"), label);
        Directory.CreateDirectory(state);
        var container = $"rpcbench-sweep-{label}-{BenchmarkProcess.EnvironmentValue("GITHUB_RUN_ID", "local")}";
        Console.WriteLine($"::group::sweep {label} (type={arm.Client}, image={arm.Image}, isolation={configuration.Isolation(arm.Client)})");
        var environment = new Dictionary<string, string>
        {
            ["CLIENT"] = arm.Client, ["INSTANCE"] = "primary", ["NODE_IMAGE"] = arm.Image, ["DB_SOURCE"] = configuration.Snapshot(arm.Client),
            ["DB_ISOLATION"] = configuration.Isolation(arm.Client), ["SCRATCH_ROOT"] = Get("SCRATCH_ROOT"), ["STATE_DIR"] = state,
            ["NETWORK"] = Get("NETWORK"), ["JSONRPC_MODULES"] = Get("JSONRPC_MODULES"), ["LAYOUT_FLAGS"] = Get("STATE_LAYOUT") == "flat" ? "--FlatDb.Enabled=true" : "",
            ["ADDITIONAL_FLAGS"] = flags, ["ARM_SCRATCH_DIR"] = armScratch, ["HEALTH_TIMEOUT"] = Get("HEALTH_TIMEOUT"), ["DOTTRACE"] = "false",
            ["RPC_GAS_CAP"] = configuration.Corpus ? "1000000000000" : "", ["NODE_ENV_VARS"] = BenchmarkProcess.EnvironmentValue("NODE_ENV_VARS") + (arm.Environment.Length > 0 ? " " + arm.Environment : ""),
            ["DIAG_DIR"] = Get("DIAG_DIR"), ["CONTAINER_NAME"] = container, ["RPC_PORT"] = "8545"
        };
        var started = await Script("StartNode.cs", [], environment, cancellationToken);
        if (started.Code != 0)
        {
            Fail(configuration.Corpus && !_usingSaved && _baselineLabel.Length == 0 ? "configured parity baseline startup failures" : "sweep arms never started");
            Console.WriteLine("::endgroup::");
            return;
        }
        _labels.Add(label);
        try
        {
            if (configuration.Corpus)
            {
                foreach (var corpus in _corpora) await RunCorpus(arm, label, container, corpus, cancellationToken);
                if (_baselineLabel.Length == 0) _baselineLabel = label;
            }
            else
            {
                foreach (var rate in configuration.Rates)
                {
                    foreach (var config in SweepConfiguration.Words(Get("ISO_CONFIGS")))
                    {
                        var scenario = Path.GetFileNameWithoutExtension(config);
                        var cell = Path.Combine(Get("OUT_DIR"), "iso", label, rate.ToString(CultureInfo.InvariantCulture), scenario);
                        await MeasuredCell(config, rate, Get("ISO_DURATION"), cell, arm.Client, label, "", "", $"iso|{scenario}|{label}|{rate}", cancellationToken);
                    }
                    var mixed = Path.Combine(Get("OUT_DIR"), "mix", label, rate.ToString(CultureInfo.InvariantCulture));
                    await MeasuredCell(Get("JB_BENCHMARK_CONFIG"), rate, Get("JB_DURATION"), mixed, arm.Client, label, "", "", $"mix|{label}|{rate}", cancellationToken);
                }
            }
        }
        finally
        {
            // Teardown must finish even when the workflow cancels a cell.
            using var teardown = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var stopped = await Script("StopNode.cs", [], new() { ["STATE_DIR"] = state, ["CONTAINER_NAME"] = container, ["OUT_DIR"] = Get("OUT_DIR"), ["LOG_OUT"] = Path.Combine(state, "node.log") }, teardown.Token);
            if (stopped.Code != 0) Fail("stop-node DB-integrity/teardown failures");
            if (ScanNodeLog(Path.Combine(state, "node.log"), arm.Client, label, configuration.Corpus)) Fail("node health issues");
            Console.WriteLine("::endgroup::");
        }
    }

    private async Task MeasuredCell(string config, long rate, string duration, string cell, string client, string label, string corpus, string container, string manifest, CancellationToken cancellationToken)
    {
        if (await Cell(config, rate, duration, cell, client, label, corpus, container, false, cancellationToken) != 0) Fail("load-test cells failed — matrix incomplete");
        var summary = Path.Combine(cell, "jsonbench-summary.md");
        if (File.Exists(summary)) _summaries.Add(manifest + "=" + summary);
        if (corpus.Length > 0) ReportFailureRate(cell, label);
    }

    private async Task<int> Cell(string config, long rate, string duration, string cell, string client, string label, string corpus, string container, bool warmup, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cell);
        var sample = container.Length > 0 && Get("CORPUS_RESOURCE_SAMPLING") == "true";
        var environment = new Dictionary<string, string>
        {
            ["OUT_DIR"] = cell, ["RPC_URL"] = Rpc, ["CLIENT_TYPE"] = client, ["LABEL"] = label, ["SCRATCH_ROOT"] = Get("SCRATCH_ROOT"),
            ["JB_REF"] = Get("JB_REF"), ["JB_MODE"] = "benchmark", ["JB_BENCHMARK_CONFIG"] = config, ["JB_RPS"] = rate.ToString(CultureInfo.InvariantCulture),
            ["JB_DURATION"] = duration, ["JB_SEED"] = (configuration.Number("JB_SEED") + (warmup ? 1000 : 0)).ToString(CultureInfo.InvariantCulture),
            ["JB_HTML_REPORT"] = "false", ["JB_DEEP_CHECK"] = corpus.Length == 0 ? "true" : "false", ["JB_ETH_CALL_CORPUS"] = corpus.Length > 0 ? "true" : "false",
            ["JB_ETH_CALL_CORPUS_FILE"] = corpus, ["CORPUS_METHOD"] = Get("CORPUS_METHOD"), ["CORPUS_TRACER"] = Get("CORPUS_TRACER"), ["CORPUS_TRACE_TYPES"] = Get("CORPUS_TRACE_TYPES"),
            ["RESOURCE_SAMPLER_CONTAINER"] = sample ? container : "", ["RESOURCE_SAMPLER_OUT"] = sample ? Path.Combine(cell, "resources.json") : ""
        };
        if (warmup) environment["JB_MAX_FAIL_RATE_PCT"] = "100";
        return (await Script("RunJsonBench.cs", [], environment, cancellationToken)).Code;
    }

    private async Task<(double Seconds, double Rate)> Warmup(string corpus, string label, string client, CancellationToken cancellationToken)
    {
        var seconds = configuration.Number("WARMUP_SECONDS");
        if (seconds == 0) return (0, 0);
        var rate = Math.Max(configuration.Number("CORPUS_WARMUP_RPS"), configuration.Rates.DefaultIfEmpty().Max());
        if (Get("CORPUS_TIMINGS_PASSES").Length > 0) rate = Math.Max(rate, configuration.Number("CORPUS_TIMINGS_RPS"));
        var cap = configuration.Number("CORPUS_WARMUP_RPS_MAX");
        if (cap > 0) rate = Math.Min(rate, cap);
        var cell = Path.Combine(Get("SCRATCH_ROOT"), "warmup-cell", SweepConfiguration.CorpusLabel(corpus), label);
        if (configuration.Rates.Length > 0)
        {
            if (await Cell(Get("JB_BENCHMARK_CONFIG"), rate, seconds + "s", cell, client, label, corpus, "", true, cancellationToken) != 0)
            { Console.WriteLine($"::warning::warmup for {label} failed — measured cells may be cold"); return (0, 0); }
            var count = Number(Path.Combine(cell, "summary.json"), ["metrics", "http_reqs", "values", "count"], 0);
            if (count > 0 && count < rate * (double)seconds * .8) Console.WriteLine($"::warning::warmup for {label} delivered fewer than 80% of requested calls");
            ReportFailureRate(cell, label);
            return (seconds, count > 0 ? Math.Floor(count / seconds) : rate);
        }
        Directory.CreateDirectory(cell);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(Math.Max(300, seconds + 60)));
        var stopwatch = Stopwatch.StartNew();
        var status = 0;
        try
        {
            status = (await Script("CorpusParity.cs", ["timings", "--corpus", corpus, "--rpc-url", Rpc, "--out", Path.Combine(cell, "warmup-timings.csv"), "--passes", checked((rate * seconds + _records[corpus] - 1) / _records[corpus]).ToString(CultureInfo.InvariantCulture), "--rps", rate.ToString(CultureInfo.InvariantCulture), "--concurrency", Get("CORPUS_TIMINGS_CONCURRENCY")], null, bounded.Token)).Code;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { status = 124; }
        if (status is 0 or 124) return (Math.Floor(stopwatch.Elapsed.TotalSeconds), Number(Path.Combine(cell, "timings.meta.json"), ["achieved_rps"], rate));
        Console.WriteLine($"::warning::warmup replay for {label} failed — measured cells may be cold");
        return (0, 0);
    }

    private async Task RunCorpus(SweepArm arm, string label, string container, string corpus, CancellationToken cancellationToken)
    {
        var corpusLabel = SweepConfiguration.CorpusLabel(corpus);
        var warmed = await Warmup(corpus, label, arm.Client, cancellationToken);
        var seen = new Dictionary<long, int>();
        foreach (var rate in configuration.Rates)
        {
            seen[rate] = seen.GetValueOrDefault(rate) + 1;
            var slot = rate + (seen[rate] > 1 ? "_r" + seen[rate] : "");
            var target = Get("CORPUS_REQUESTS").Length > 0 ? configuration.Number("CORPUS_REQUESTS") : Get("CORPUS_PASSES").Length > 0 ? checked(_records[corpus] * configuration.Number("CORPUS_PASSES")) : 0;
            var duration = target > 0 ? checked((target + rate - 1) / rate) + "s" : Get("JB_DURATION");
            await MeasuredCell(Get("JB_BENCHMARK_CONFIG"), rate, duration, Path.Combine(Get("OUT_DIR"), "corpus", corpusLabel, label, slot), arm.Client, label, corpus, container, $"iso|{corpusLabel}|{label}|{slot}", cancellationToken);
        }
        var saved = Path.Combine(Get("CORPUS_BASELINE_DIR"), corpusLabel + ".json.gz");
        if (!_baselines.ContainsKey(corpusLabel) && Get("CORPUS_BASELINE") == "use" && Nonempty(saved))
        {
            var labelFile = Path.Combine(Get("CORPUS_BASELINE_DIR"), corpusLabel + ".label");
            _baselines[corpusLabel] = (saved, File.Exists(labelFile) ? File.ReadAllText(labelFile).TrimEnd('\r', '\n') : "master", true);
        }
        if (_baselines.TryGetValue(corpusLabel, out var baseline))
        {
            var directory = Path.Combine(Get("OUT_DIR"), "corpus", corpusLabel, label);
            Directory.CreateDirectory(directory);
            var report = Path.Combine(directory, "parity.json");
            var arguments = new List<string> { "compare", "--corpus", corpus, "--rpc-url", Rpc, "--state", baseline.State, "--report", report, "--baseline-client", baseline.Label, "--candidate-client", label };
            if (Get("CORPUS_PARITY_DIFFS") == "true") arguments.AddRange(["--diffs", Path.Combine(directory, "parity-diffs.json")]);
            var result = await Script("CorpusParity.cs", arguments, null, cancellationToken);
            if (result.Code == 2 && baseline.Saved) Fail("arm/corpus pairs unchecked against saved master baseline");
            else
            {
                if (result.Code != 0) Fail("corpus parity failures");
                if (File.Exists(report)) _parityRows.Add((corpusLabel, label, report));
            }
        }
        else
        {
            if (Get("CORPUS_BASELINE") == "use") Fail("arm/corpus pairs missing saved master baseline");
            var state = Path.Combine(ParityState, corpusLabel + ".json");
            var result = await Script("CorpusParity.cs", ["baseline", "--corpus", corpus, "--rpc-url", Rpc, "--state", state], null, cancellationToken);
            if (result.Code != 0) Fail("corpus parity baseline capture failures");
            else
            {
                _baselines[corpusLabel] = (state, label, false);
                if (Get("CORPUS_BASELINE") == "save")
                {
                    var labelFile = Path.Combine(Get("CORPUS_BASELINE_DIR"), corpusLabel + ".label");
                    try
                    {
                        Directory.CreateDirectory(Get("CORPUS_BASELINE_DIR")); File.Copy(state, saved + ".tmp", true); File.WriteAllText(labelFile + ".tmp", label + "\n");
                        File.Move(saved + ".tmp", saved, true); File.Move(labelFile + ".tmp", labelFile, true);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine("::warning::could not save parity baseline: " + exception.Message);
                        foreach (var temporary in new[] { saved + ".tmp", labelFile + ".tmp" })
                            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { Console.WriteLine("::warning::" + cleanup.Message); }
                    }
                }
            }
        }
        if (Get("CORPUS_TIMINGS_PASSES").Length > 0)
        {
            var directory = Path.Combine(Get("OUT_DIR"), "corpus", corpusLabel, label);
            Directory.CreateDirectory(directory);
            var result = await Script("CorpusParity.cs", ["timings", "--corpus", corpus, "--rpc-url", Rpc, "--out", Path.Combine(directory, "timings.csv"), "--passes", Get("CORPUS_TIMINGS_PASSES"), "--rps", Get("CORPUS_TIMINGS_RPS"), "--concurrency", Get("CORPUS_TIMINGS_CONCURRENCY"), "--warmup-seconds", warmed.Seconds.ToString(CultureInfo.InvariantCulture), "--warmup-rps", warmed.Rate.ToString(CultureInfo.InvariantCulture)], null, cancellationToken);
            if (result.Code != 0) Fail("timings replay cells failed");
        }
    }

    private async Task WriteSummary(CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder($"# Cross-client sweep — same head {Get("SNAPSHOT_BLOCK")}\n_{_summaries.Count} cells · {Get("ROUNDS")} round(s) · seed {Get("JB_SEED")} · isolated dur {Get("ISO_DURATION")}, mixed dur {Get("JB_DURATION")} · json-bench {Get("JB_REF")}_\n\n");
        if (_summaries.Count > 0)
        {
            var manifest = Path.Combine(Get("OUT_DIR"), "summaries.manifest"); File.WriteAllLines(manifest, _summaries);
            var aggregation = await Script("PerCategoryMatrix.cs", ["@" + manifest], null, cancellationToken, false);
            text.AppendLine(aggregation.Code == 0 ? aggregation.Output : "aggregation failed");
        }
        else if (configuration.Rates.Length == 0) text.AppendLine("No k6 cells requested (empty rps_list) — parity/timings only.");
        else { text.AppendLine("No cell summaries produced — every client failed to start."); Fail("no cell summaries produced"); }
        if (configuration.Corpus)
        {
            Directory.Delete(ParityState, true);
            var baseline = _usingSaved ? "saved " + (_baselines.GetValueOrDefault(SweepConfiguration.CorpusLabel(_corpora[0])).Label ?? "master") : _baselineLabel.Length > 0 ? _baselineLabel : "<none started>";
            text.AppendLine($"\n## Corpus parity (baseline = {baseline})\n\n| corpus | client | matched (+both-error)/total | nonzero defect counters |\n|---|---|---|---|");
            foreach (var row in _parityRows)
            {
                try
                {
                    var report = JsonNode.Parse(File.ReadAllText(row.Report))!.AsObject();
                    var defects = report.Where(pair => pair.Key is not ("total" or "matched" or "both_rpc_errors") && pair.Value is JsonValue value && value.TryGetValue<double>(out var number) && number > 0).Select(pair => pair.Key + "=" + pair.Value).ToArray();
                    text.AppendLine($"| {row.Corpus} | {row.Label} | {report["matched"]} (+{report["both_rpc_errors"]})/{report["total"]} | {(defects.Length > 0 ? string.Join(' ', defects) : "-")} |");
                }
                catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException) { text.AppendLine($"| {row.Corpus} | {row.Label} | report unreadable | - |"); }
            }
        }
        if (_labels.Count >= 2)
        {
            text.AppendLine("\n## Cross-client parity (mixed workload responses)");
            foreach (var rate in configuration.Rates)
            {
                var inputs = _labels.Select(label => (Label: label, Path: Path.Combine(Get("OUT_DIR"), "mix", label, rate.ToString(CultureInfo.InvariantCulture), "deep-check-" + label + ".jsonl"))).Where(entry => File.Exists(entry.Path)).Select(entry => entry.Label + "=" + entry.Path).ToArray();
                if (inputs.Length < 2) continue;
                var comparison = await Script("DeepCheckCompare.cs", inputs, null, cancellationToken, false);
                var lines = (comparison.Output + comparison.Error).Split('\n').Where(line => Regex.IsMatch(line, "requests compared|DIVERGENT|MALFORMED", RegexOptions.IgnoreCase));
                text.AppendLine($"- rps {rate}: {string.Join(' ', lines)}");
            }
        }
        foreach (var (kind, count) in _failures) text.AppendLine($"\n> **⚠️ {count} {kind}** — the job will fail.");
        var sink = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(sink)) Console.Write(text); else File.AppendAllText(sink, text.ToString());
    }

    internal static bool ScanNodeLog(string path, string client, string label, bool privateCorpus)
    {
        if (!File.Exists(path)) return false;
        var exceptions = 0; var invalid = false; var shutdown = false; var severe = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = Regex.Replace(raw, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
            if (line.Contains("Exception", StringComparison.OrdinalIgnoreCase) && !line.Contains("Incorrect JSON RPC parameters", StringComparison.Ordinal))
            { exceptions++; if (!privateCorpus && exceptions <= 20) Console.WriteLine(line); }
            invalid |= Regex.IsMatch(line, @"invalid[\s_-]*block", RegexOptions.IgnoreCase);
            shutdown |= line.Contains("Nethermind is shut down", StringComparison.Ordinal);
            if (Regex.IsMatch(line, "Unhandled|Fatal|ERROR", RegexOptions.IgnoreCase)) severe++;
        }
        if (exceptions > 0) Console.WriteLine($"::warning::{label}: {exceptions} Exception line(s) in node log");
        if (severe > 0) Console.WriteLine($"::warning::{label}: {severe} severe log line(s)");
        if (privateCorpus) File.Delete(path);
        return client == "nethermind" && (exceptions > 0 || invalid || !shutdown);
    }

    private static double Number(string path, string[] keys, double fallback)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            foreach (var key in keys) node = node?[key];
            return node is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException) { return fallback; }
    }
    private static void ReportFailureRate(string cell, string label)
    {
        var rate = Number(Path.Combine(cell, "summary.json"), ["metrics", "http_req_failed", "values", "rate"], double.NaN) * 100;
        if (double.IsNaN(rate)) return;
        var threshold = double.TryParse(BenchmarkProcess.EnvironmentValue("JB_MAX_FAIL_RATE_PCT", "1"), CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        Console.WriteLine(rate > threshold ? $"::warning::{label}: {rate}% of requests failed — percentiles at or above p{(int)(100 - rate)} describe failures, not latency" : $"{label}: fail rate {rate}%");
    }
    private void Fail(string kind) => _failures[kind] = _failures.GetValueOrDefault(kind) + 1;
    private static void LogProvenance()
    {
        static string Read(string path)
        {
            try { return File.ReadAllText(path).Trim(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return "unknown"; }
        }
        BenchmarkProcess.Log("=== host provenance ===");
        BenchmarkProcess.Log("kernel: " + Read("/proc/sys/kernel/osrelease"));
        BenchmarkProcess.Log("boot_id: " + Read("/proc/sys/kernel/random/boot_id"));
        BenchmarkProcess.Log("uptime_s: " + Read("/proc/uptime").Split(' ')[0]);
        BenchmarkProcess.Log("interrupts: " + Read("/proc/stat").Split('\n').FirstOrDefault(line => line.StartsWith("intr ", StringComparison.Ordinal))?.Split(' ').ElementAtOrDefault(1));
        foreach (var name in new[] { "scaling_governor", "scaling_max_freq" }) BenchmarkProcess.Log(name + ": " + Read("/sys/devices/system/cpu/cpu0/cpufreq/" + name));
        BenchmarkProcess.Log("thp: " + Read("/sys/kernel/mm/transparent_hugepage/enabled"));
        var vulnerabilities = "/sys/devices/system/cpu/vulnerabilities";
        if (Directory.Exists(vulnerabilities))
            foreach (var file in Directory.EnumerateFiles(vulnerabilities).Order(StringComparer.Ordinal))
            {
                var value = Read(file).Replace("\n", "", StringComparison.Ordinal);
                BenchmarkProcess.Log($"mitigation: {Path.GetFileName(file)}={value[..Math.Min(60, value.Length)]}");
            }
    }
    private static bool Nonempty(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
    private static async Task Root(string executable, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await BenchmarkProcess.RootAsync(executable, arguments, cancellationToken);
        if (result.Code != 0) throw new IOException(executable + ": " + result.Error);
    }

    private static async Task<(int Code, string Output, string Error)> Script(string name, IEnumerable<string> arguments, Dictionary<string, string>? environment, CancellationToken cancellationToken, bool display = true)
    {
        var directory = BenchmarkProcess.EnvironmentValue("RPC_BENCH_SCRIPT_ROOT");
        if (directory.Length == 0)
        {
            var root = new DirectoryInfo(Environment.CurrentDirectory);
            while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "scripts", "rpc-bench"))) root = root.Parent;
            directory = root is null ? throw new IOException("could not locate scripts/rpc-bench") : Path.Combine(root.FullName, "scripts", "rpc-bench");
        }
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "run", "--file", Path.Combine(directory, name), "--" }.Concat(arguments)) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new IOException("could not start " + name);
        async Task<string> Drain(StreamReader reader, TextWriter writer)
        {
            var captured = new System.Text.StringBuilder();
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (display) await writer.WriteLineAsync(line);
                // Validation and reports are small; benchmark progress must not accumulate without bound.
                if (captured.Length < 1024 * 1024) captured.AppendLine(line);
            }
            return captured.ToString();
        }
        var output = Drain(process.StandardOutput, Console.Out); var error = Drain(process.StandardError, Console.Error);
        try { await process.WaitForExitAsync(cancellationToken); return (process.ExitCode, await output, await error); }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
