#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include benchmark-process.helpers.cs
#:include benchmark-paths.helpers.cs
#:include benchmark-files.helpers.cs
#:include benchmark-rpc.helpers.cs
#:include node-configuration.helpers.cs
#:include rpc-json.helpers.cs
#:include resource-sampling.helpers.cs
#:include transaction-lookup-workload.helpers.cs

using System.Globalization;
#if !SCRIPT_TEST
using System.Runtime.InteropServices;
#endif
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

#if !SCRIPT_TEST
using var cancellation = new CancellationTokenSource();
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
var container = Get("JB_CONTAINER_NAME", "jsonbench-bench");
var containerOwned = false;
try
{
    var output = Get("OUT_DIR"); var scratch = Get("SCRATCH_ROOT");
    if (output.Length == 0 || scratch.Length == 0) throw new ArgumentException("OUT_DIR and SCRATCH_ROOT are required");
    var repositoryRoot = new DirectoryInfo(Environment.CurrentDirectory);
    while (repositoryRoot is not null && !Directory.Exists(Path.Combine(repositoryRoot.FullName, "scripts", "rpc-bench"))) repositoryRoot = repositoryRoot.Parent;
    var scripts = Get("RPC_BENCH_SCRIPT_ROOT", repositoryRoot is null ? "" : Path.Combine(repositoryRoot.FullName, "scripts", "rpc-bench"));
    if (scripts.Length == 0) throw new IOException("Run from the repository or set RPC_BENCH_SCRIPT_ROOT");
    var url = Get("RPC_URL", "http://localhost:8545"); var reference = Get("REFERENCE_RPC_URL");
    var label = Get("LABEL", Get("CLIENT_TYPE", "nethermind")); var clientType = Get("CLIENT_TYPE", label);
    var referenceLabel = Get("REFERENCE_LABEL", Get("REFERENCE_CLIENT_TYPE", "reference")); var referenceType = Get("REFERENCE_CLIENT_TYPE", referenceLabel);
    if (reference.Length > 0 && referenceLabel == label) referenceLabel += "_ref";
    var mode = Get("JB_MODE", reference.Length > 0 ? "compare" : "benchmark");
    if (mode is not ("benchmark" or "compare")) throw new ArgumentException($"unknown JB_MODE '{mode}' (expected benchmark | compare)");
    if (mode == "compare" && reference.Length == 0) throw new ArgumentException("JB_MODE=compare needs a reference node");
    var lookup = Get("JB_BENCHMARK_CONFIG") == TransactionLookupWorkload.Name;
    var lookupPreset = Get("LOOKUP_METHOD", "mixed");
    var lookupTargets = TransactionLookupWorkload.TargetCount(Get("LOOKUP_TARGETS", "256"));
    var lookupSamples = lookup && Get("LOOKUP_SAMPLES", "false") == "true";
    if (lookup) _ = TransactionLookupWorkload.SelectedMethods(lookupPreset);
    var corpus = Get("JB_ETH_CALL_CORPUS", "false");
    if (lookup && (corpus == "true" || mode != "benchmark")) throw new ArgumentException("Snapshot lookups require benchmark mode without an eth_call corpus");
    if (corpus is not ("true" or "false")) throw new ArgumentException("JB_ETH_CALL_CORPUS must be true or false");
    var seed = Get("JB_SEED", "1"); if (!SeedRegex().IsMatch(seed)) throw new ArgumentException($"JB_SEED must be a non-negative integer, got '{seed}'");
    var method = Get("CORPUS_METHOD", "eth_call");
    if (method is not ("eth_call" or "debug_traceCall" or "trace_call")) throw new ArgumentException("CORPUS_METHOD must be eth_call, debug_traceCall or trace_call");
    if (method != "eth_call" && corpus != "true") throw new ArgumentException($"CORPUS_METHOD={method} requires the eth_call corpus (JB_ETH_CALL_CORPUS=true)");
    var tracer = Environment.GetEnvironmentVariable("CORPUS_TRACER") ?? "callTracer";
    var types = Environment.GetEnvironmentVariable("CORPUS_TRACE_TYPES") ?? "trace";
    Environment.SetEnvironmentVariable("RPC_BENCH_CORPUS_METHOD", method); Environment.SetEnvironmentVariable("RPC_BENCH_CORPUS_TRACER", tracer); Environment.SetEnvironmentVariable("RPC_BENCH_CORPUS_TRACE_TYPES", types);
    var corpusPath = Get("JB_ETH_CALL_CORPUS_FILE", Path.Combine(Get("CORPUS_DIR", "/data/expb-data/rpc-bench"), "eth-call-corpus.jsonl.gz"));
    var deep = Get("JB_DEEP_CHECK", "false") == "true" && corpus != "true";
    var html = Get("JB_HTML_REPORT", "true") == "true" && corpus != "true";
    if (corpus == "true")
    {
        if (mode != "benchmark") throw new ArgumentException("eth_call corpus is supported only in benchmark mode");
        if (!File.Exists(corpusPath)) throw new IOException($"eth_call corpus file not found: {corpusPath}");
        await Script("corpus-parity.cs", ["validate", "--corpus", corpusPath]);
        if (method != "eth_call")
            foreach (var endpoint in new[] { url, reference }.Where(value => value.Length > 0)) await Script("corpus-parity.cs", ["probe", "--corpus", corpusPath, "--rpc-url", endpoint]);
    }
    Directory.CreateDirectory(output);
    scratch = await BenchmarkPaths.CanonicalAsync(scratch, false, cancellation.Token); BenchmarkPaths.AssertSane(scratch, "SCRATCH_ROOT");
    var work = lookup ? Path.Combine(scratch, "jsonbench", TransactionLookupWorkload.Version) : Path.Combine(scratch, "jsonbench"); var source = Path.Combine(work, "src"); var io = Path.Combine(work, "io");
    var repository = Get("JB_REPO", "https://github.com/NethermindEth/json-bench.git"); var revision = Get("JB_REF", "de1bcfadea47258ccacae2f420141032a82a9ded");
    var tag = UnsafeTagRegex().Replace(revision, "-"); tag = "jsonbench-runner:" + tag[..Math.Min(24, tag.Length)];
    if (lookup) tag += "-" + TransactionLookupWorkload.Version;
    var fixtures = Path.Combine(source, "rpc-calls", "corpus"); var marker = Path.Combine(work, lookup ? "prepared-" + TransactionLookupWorkload.Version : "prepared");
    var reuse = Get("JB_REUSE_PREPARED", "false") == "true"; var prepared = false; var preparationId = "";
    Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
    if (reuse)
    {
        var resolved = await Required("git", ["ls-remote", repository, revision]);
        var commit = resolved.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? revision;
        preparationId = repository + "@" + commit + (lookup ? " " + TransactionLookupWorkload.Version : "");
        if (corpus == "true")
        {
            var canonical = await BenchmarkPaths.CanonicalAsync(corpusPath, true, cancellation.Token);
            var stat = (await Required("stat", ["-c", "%s %Y", "--", corpusPath])).Trim();
            preparationId += $" corpus={canonical} {stat} method={method}/{tracer}/{types}";
        }
        prepared = File.Exists(marker) && File.ReadAllText(marker).TrimEnd('\r', '\n') == preparationId && (await BenchmarkProcess.RunAsync("docker", ["image", "inspect", tag], cancellation.Token)).Code == 0 && (corpus != "true" || Nonempty(Path.Combine(fixtures, "classes.json")));
        if (prepared) BenchmarkProcess.Log($"Reusing the json-bench checkout, runner image and fixture prepared by the previous invocation ({preparationId})");
    }
    await Remove(io); Directory.CreateDirectory(Path.Combine(io, "out"));
    if (!prepared)
    {
        File.Delete(marker);
        var parked = await BenchmarkProcess.RunAsync("git", ["-C", source, "rev-parse", "HEAD"], cancellation.Token);
        if (parked.Code == 0 && parked.Output.Trim() == revision) await Required("git", ["-C", source, "clean", "-qfdx"]);
        else
        {
            await Remove(source); await Required("git", ["init", "-q", source]); await Required("git", ["-C", source, "remote", "add", "origin", repository]);
            var fetch = new List<string>();
            if (Get("GH_TOKEN").Length > 0) fetch.AddRange(["-c", "http.extraheader=AUTHORIZATION: basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + Get("GH_TOKEN")))]);
            fetch.AddRange(["-C", source, "fetch", "-q", "--depth", "1", "origin", revision]);
            var fetched = false;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if ((await BenchmarkProcess.RunAsync("git", fetch, cancellation.Token)).Code == 0) { fetched = true; break; }
                BenchmarkProcess.Log($"fetch attempt {attempt} of {revision} failed; retrying in {attempt * 5}s"); await Task.Delay(attempt * 5000, cancellation.Token);
            }
            if (!fetched) throw new IOException($"failed to fetch {revision} from {repository}");
            await Required("git", ["-C", source, "checkout", "-q", "FETCH_HEAD"]);
        }
        var dockerfile = Path.Combine(source, "runner", "Dockerfile");
        if (!File.Exists(dockerfile)) throw new IOException($"json-bench runner Dockerfile not found at {dockerfile}");
        if (lookup) PatchLookupRunner();
        await Required("docker", ["build", "-q", "-f", dockerfile, "-t", tag, source]);
    }
    if (lookup) PatchLookupRunner();
    using var lookupHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var lookupPath = Get("RPC_BENCH_TRANSACTION_LOOKUP_MANIFEST", Path.Combine(output, "transaction-lookup-workload.json"));
    JsonObject? lookupManifest = null;
    var lookupHash = "";
    if (lookup)
    {
        if (Get("LOOKUP_REQUIRE_MANIFEST") == "true" && !File.Exists(lookupPath)) throw new IOException("Measured lookup node requires an existing manifest");
        var existingManifest = File.Exists(lookupPath) ? JsonNode.Parse(await File.ReadAllTextAsync(lookupPath, cancellation.Token)) as JsonObject ?? throw new IOException("Invalid lookup manifest") : null;
        var expectedHead = existingManifest is null ? long.Parse(Get("SNAPSHOT_BLOCK"), NumberStyles.None, CultureInfo.InvariantCulture) : Convert.ToInt64(existingManifest["headNumber"]!.GetValue<string>(), 16);
        var expectedHash = existingManifest is null ? null : existingManifest["headHash"]!.GetValue<string>();
        var stableHead = await TransactionLookupWorkload.WaitForHeadAsync(expectedHead, expectedHash, (method, parameters) => TransactionLookupWorkload.RpcAsync(lookupHttp, url, method, parameters, cancellation.Token), cancellation.Token);
        if (reference.Length > 0) await TransactionLookupWorkload.WaitForHeadAsync(expectedHead, stableHead["hash"]!.GetValue<string>(), (method, parameters) => TransactionLookupWorkload.RpcAsync(lookupHttp, reference, method, parameters, cancellation.Token), cancellation.Token);
        lookupManifest = await TransactionLookupWorkload.PrepareAsync(lookupPath, (method, parameters) => TransactionLookupWorkload.RpcAsync(lookupHttp, url, method, parameters, cancellation.Token), cancellation.Token, stableHead, lookupTargets);
        lookupHash = TransactionLookupWorkload.Hash(lookupPath);
        var exported = Path.Combine(output, "transaction-lookup-workload.json");
        if (Get("LOOKUP_EXPORT_MANIFEST", "true") == "true" && Path.GetFullPath(exported) != Path.GetFullPath(lookupPath)) File.Copy(lookupPath, exported, overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(output, "transaction-lookup-workload.sha256"), lookupHash + "\n", cancellation.Token);
    }
    var clients = new YamlSequenceNode { new YamlMappingNode { { "name", label }, { "type", clientType }, { "url", url }, { "timeout", "60s" }, { "max_retries", "3" } } };
    if (reference.Length > 0) clients.Add(new YamlMappingNode { { "name", referenceLabel }, { "type", referenceType }, { "url", reference }, { "timeout", "60s" }, { "max_retries", "3" } });
    var clientsPath = Path.Combine(io, "clients.yaml"); Save(clientsPath, new YamlMappingNode { { "clients", clients } });
    var benchmarkName = Get("JB_BENCHMARK_CONFIG"); YamlMappingNode? benchmark = null;
    if (mode == "benchmark")
    {
        var configPath = lookup || benchmarkName.Length == 0 ? "" : Path.IsPathRooted(benchmarkName) ? benchmarkName : benchmarkName.Contains('/') ? Path.Combine(source, benchmarkName) : Path.Combine(source, "config", "benchmark", benchmarkName + ".yaml");
        if (configPath.Length > 0 && !File.Exists(configPath)) throw new IOException($"benchmark_config '{benchmarkName}' not found (looked at {configPath})");
        if (corpus == "true" && !prepared) { Directory.CreateDirectory(fixtures); await Script("prepare-eth-call-corpus.cs", [corpusPath, fixtures]); }
        benchmark = Render(configPath, label, reference.Length > 0 ? referenceLabel : "", corpus == "true" ? Path.Combine(fixtures, "classes.json") : "", method, name => Get(name));
        if (lookup) TransactionLookupWorkload.Fixtures(lookupManifest!, source, benchmark, lookupPreset);
        Save(Path.Combine(io, "benchmark.yaml"), benchmark);
    }
    if (reuse) await File.WriteAllTextAsync(marker, preparationId + "\n", cancellation.Token);
    await Required("chmod", ["-R", "a+rwX", io]);
    await BenchmarkProcess.RunAsync("docker", ["rm", "-fv", container], cancellation.Token);
    var docker = new List<string> { "run", "--rm", "--name", container, "--network", "host", "-w", "/jb", "-v", source + ":/jb:ro", "-v", io + ":/io" };
    if (lookupSamples)
    {
        TransactionLookupWorkload.CheckSampleBudget(Get("JB_RPS"), Get("JB_DURATION"), Get("JB_VUS"));
        docker.AddRange(["-e", "K6_OUT=json=/io/out/latency-samples.json.gz", "-e", "K6_SYSTEM_TAGS=scenario,status,check,error,error_code", "-e", "K6_METRIC_SAMPLES_BUFFER_SIZE=1000"]);
    }
    docker.AddRange([tag, mode]);
    if (mode == "compare")
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var a = await BenchmarkRpc.HeadAsync(http, url, cancellation.Token); var b = await BenchmarkRpc.HeadAsync(http, reference, cancellation.Token);
        if (a is null || b is null || Convert.ToInt64(a[2..], 16) != Convert.ToInt64(b[2..], 16)) throw new IOException("head mismatch or could not read eth_blockNumber from both nodes");
        var config = Get("JB_COMPARE_CONFIG", "config/compare/defaults.yaml");
        if (Path.IsPathRooted(config)) { File.Copy(config, Path.Combine(source, "rpc-bench-custom.yaml"), overwrite: true); config = "rpc-bench-custom.yaml"; }
        if (!File.Exists(Path.Combine(source, config))) throw new IOException($"config '{config}' not found in the json-bench checkout");
        docker.AddRange(["--config", config, "--clients", "/io/clients.yaml", "--client-refs", label + "," + referenceLabel, "--concurrency", Get("JB_CONCURRENCY", "5"), "--timeout", Get("JB_TIMEOUT", "30"), "--output", "/io/out"]);
        if (Get("JB_VALIDATE_SCHEMA", "false") == "true") docker.Add("--validate-schema");
    }
    else
    {
        docker.AddRange(["--config", "/io/benchmark.yaml", "--clients", "/io/clients.yaml", "--output", "/io/out"]);
        if (html) docker.Add("--html-report");
    }
    docker.AddRange(NodeConfiguration.SplitFlags(Get("JB_EXTRA_ARGS")));
    var toolLog = corpus == "true" ? Path.Combine(work, "jsonbench-tool.log") : Path.Combine(output, "jsonbench.log");
    int toolCode;
    if (lookup) await ValidateLookups("before");
    if (lookup && Get("LOOKUP_PREPARE_ONLY") == "true")
    {
        await TransactionLookupWorkload.QualifyReceiptEncodingAsync(lookupManifest!, (method, parameters) => TransactionLookupWorkload.RpcAsync(lookupHttp, url, method, parameters, cancellation.Token), Path.Combine(output, "receipt-encoding.json"), cancellation.Token);
        var historical = await TransactionLookupWorkload.HistoricalProbeManifestAsync(lookupManifest!, (method, parameters) => TransactionLookupWorkload.RpcAsync(lookupHttp, url, method, parameters, cancellation.Token), cancellation.Token);
        await File.WriteAllTextAsync(Path.Combine(output, "historical-receipt-probe-manifest.json"), historical.ToJsonString(), cancellation.Token);
        await TransactionLookupWorkload.QualifyReceiptEncodingAsync(historical, (method, parameters) => TransactionLookupWorkload.RpcAsync(lookupHttp, url, method, parameters, cancellation.Token), Path.Combine(output, "historical-receipt-encoding.json"), cancellation.Token);
        return 0;
    }
    await using (var sampler = new ResourceSamplerSession(Get("RESOURCE_SAMPLER_CONTAINER"), Get("RESOURCE_SAMPLER_OUT")))
    {
        containerOwned = true;
        toolCode = await BenchmarkFiles.RunCombinedToFileAsync("docker", docker, toolLog, cancellation.Token, echo: corpus != "true");
        containerOwned = false;
    }
    if (lookup) await ValidateLookups("after");
    if (corpus == "true")
    {
        if (!reuse) Directory.Delete(fixtures, recursive: true);
        if (toolCode != 0) throw new IOException($"json-bench exited non-zero — tool log retained on the runner at {toolLog}");
    }
    if (deep && benchmark is not null)
        try { await JsonBenchReporting.CaptureAsync(benchmark, source, url, Path.Combine(output, "deep-check-" + label + ".jsonl"), cancellation.Token); }
        catch (Exception exception) when (exception is IOException or JsonException or HttpRequestException) { BenchmarkProcess.Log($"::warning::deep-check capture failed (continuing): {exception.Message}"); }
    var uid = (await Required("id", ["-u"])).Trim(); var gid = (await Required("id", ["-g"])).Trim();
    await BenchmarkProcess.RootAsync("chown", ["-R", uid + ":" + gid, io], cancellation.Token);
    if (corpus == "true") { File.Delete(Path.Combine(output, "summary.json")); await Script("corpus-results.cs", ["sanitize", Path.Combine(io, "out", "summary.json"), Path.Combine(output, "summary.json")]); }
    else
    {
        await BenchmarkProcess.RunAsync("cp", ["-r", Path.Combine(io, "out") + "/.", output + "/"], cancellation.Token);
        if (!lookupSamples) File.Delete(Path.Combine(output, "requests.csv"));
    }
    File.Copy(clientsPath, Path.Combine(output, "clients.yaml"), overwrite: true);
    var summary = new StringBuilder(); double? failPercent = null; int? differences = null; var parseFailed = false;
    if (mode == "compare")
    {
        summary.Append($"## RPC Comparison — json-bench compare\n\n`{label}` = `{url}` vs `{referenceLabel}` = `{reference}` | config: `{Get("JB_COMPARE_CONFIG", "config/compare/defaults.yaml")}`\n\n");
        var path = Path.Combine(output, "comparison-results.json");
        if (Nonempty(path))
        {
            try { var rendered = JsonBenchReporting.Comparison(JsonNode.Parse(File.ReadAllText(path))); summary.Append(rendered.Markdown); differences = rendered.Differences; }
            catch (JsonException) { parseFailed = true; }
        }
        else summary.Append("**NO RESULTS** — json-bench did not write `comparison-results.json` (see `jsonbench.log` in the artifact).\n");
    }
    else
    {
        string Display(string key) => benchmark is not null && benchmark.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : "?";
        summary.Append($"## RPC Benchmark — json-bench (k6)\n\nNode(s): `{label}` = `{url}`{(reference.Length > 0 ? $", `{referenceLabel}` = `{reference}`" : "")} | config: `{(benchmarkName.Length > 0 ? benchmarkName : "<generated default>")}` | duration: `{Display("duration")}` | rps: `{Display("rps")}` | vus: `{Display("vus")}` | seed: `{seed}`\n\n");
        var path = Path.Combine(output, "summary.json");
        if (Nonempty(path))
        {
            try
            {
                var parsed = JsonNode.Parse(File.ReadAllText(path));
                var document = parsed as JsonObject ?? (parsed is null ? [] : throw new JsonException("summary must be an object"));
                var rendered = JsonBenchReporting.Performance(document); summary.Append(rendered.Markdown).Append('\n'); failPercent = rendered.FailPercent;
                var sample = Get("RESOURCE_SAMPLER_OUT");
                if (sample.Length > 0 && Nonempty(sample) && document["metrics"]?["http_reqs"]?["values"]?["count"] is JsonValue count && count.TryGetValue<long>(out var delivered) && delivered > 0)
                    try { ResourceSampling.Normalize(sample, delivered); }
                    catch (IOException exception) { BenchmarkProcess.Log($"resource sampling unavailable: {exception.Message}"); }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { parseFailed = true; }
        }
        var csv = Path.Combine(output, "results.csv");
        if (Nonempty(csv)) summary.Append("<details><summary>results.csv (first 60 lines)</summary>\n\n```csv\n").AppendJoin('\n', File.ReadLines(csv).Take(60)).Append("\n```\n\n</details>\n\n");
        summary.Append(corpus == "true" ? "Private corpus cell: aggregate-only `summary.json` in the artifact; raw tool output stays on the runner.\n" : Nonempty(path) || Nonempty(csv) ? "Full results: `summary.json` / `results.json` / `results.csv` in the artifact.\n" : "**NO RESULTS** — json-bench wrote neither `summary.json` nor `results.csv` (see `jsonbench.log` in the artifact).\n");
    }
    var summaryPath = Path.Combine(output, "jsonbench-summary.md"); await File.WriteAllTextAsync(summaryPath, summary.Append('\n').ToString(), cancellation.Token); BenchmarkProcess.Log($"json-bench summary written to {summaryPath}");
    if (toolCode != 0) throw new IOException("json-bench exited non-zero (see jsonbench.log)");
    if (mode == "benchmark")
    {
        if (!Nonempty(Path.Combine(output, "summary.json")) && !Nonempty(Path.Combine(output, "results.csv"))) throw new IOException("json-bench benchmark produced no summary.json or results.csv");
        if (parseFailed) throw new IOException("summary.json exists but could not be parsed");
        if (lookup)
        {
            var lookupSummary = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "summary.json"))) as JsonObject ?? throw new IOException("Missing lookup summary");
            TransactionLookupWorkload.CheckSummary(lookupSummary, lookupPreset);
            if (lookupSamples) TransactionLookupWorkload.CheckSamples(Path.Combine(output, "latency-samples.json.gz"), lookupSummary, lookupPreset);
        }
        if (failPercent > double.Parse(Get("JB_MAX_FAIL_RATE_PCT", "1"), CultureInfo.InvariantCulture)) throw new IOException($"http fail rate {failPercent:F4}% exceeds max_fail_rate_pct={Get("JB_MAX_FAIL_RATE_PCT", "1")}%");
    }
    else if (differences is null || parseFailed) throw new IOException("json-bench compare produced no parseable results");
    else if (Get("JB_FAIL_ON_DIFF", "false") == "true" && differences > 0) throw new IOException($"json-bench compare found {differences} response difference(s) and fail_on_diff is enabled");
    return 0;

    void PatchLookupRunner()
    {
        var path = Path.Combine(source, "runner", "generator", "scripts", "k6-script.js");
        File.WriteAllText(path, TransactionLookupWorkload.Patch(File.ReadAllText(path), requestIdentity: true));
    }
    async Task ValidateLookups(string phase)
    {
        foreach (var (endpoint, name) in new[] { (url, "primary"), (reference, "reference") }.Where(pair => pair.Item1.Length > 0))
            await TransactionLookupWorkload.ValidateAsync(lookupManifest!, (method, parameters) => TransactionLookupWorkload.RpcAsync(lookupHttp, endpoint, method, parameters, cancellation.Token), Path.Combine(output, $"transaction-lookup-validation-{phase}-{name}.json"), lookupHash, cancellation.Token);
    }
    async Task<string> Required(string executable, IEnumerable<string> arguments)
    {
        var result = await BenchmarkProcess.RunAsync(executable, arguments, cancellation.Token);
        if (result.Code != 0) throw new IOException($"{executable} failed with exit code {result.Code}: {result.Error.Trim()}");
        return result.Output;
    }
    async Task Script(string name, IEnumerable<string> arguments)
    {
        var result = await BenchmarkProcess.RunAsync("dotnet", new[] { "run", "--file", Path.Combine(scripts, name), "--" }.Concat(arguments), cancellation.Token);
        Console.Write(result.Output); Console.Error.Write(result.Error);
        if (result.Code != 0) throw new IOException($"{name} failed");
    }
    async Task Remove(string path)
    {
        if (BenchmarkPaths.MountsUnder(await BenchmarkPaths.CanonicalAsync(path, false, cancellation.Token)).Length > 0) throw new IOException($"refusing to delete '{path}' — still mounted");
        var result = await BenchmarkProcess.RootAsync("rm", ["-rf", "--", path], cancellation.Token);
        if (result.Code != 0) throw new IOException($"could not remove '{path}'");
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("json-bench cancelled."); return 1; }
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException or HttpRequestException or YamlDotNet.Core.YamlException or System.ComponentModel.Win32Exception)
{ Console.Error.WriteLine($"ERROR: {exception.Message}"); return 1; }
finally
{
    Console.CancelKeyPress -= cancel;
    if (containerOwned)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await BenchmarkProcess.RunAsync("docker", ["rm", "-fv", container], cleanup.Token); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { Console.Error.WriteLine($"json-bench cleanup failed: {exception.Message}"); }
    }
}
static string Get(string name, string fallback = "") => BenchmarkProcess.EnvironmentValue(name, fallback);
static bool Nonempty(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
static YamlMappingNode Render(string source, string primary, string reference, string classesPath, string method, Func<string, string> environment)
{
    YamlMappingNode config;
    if (source.Length > 0)
    {
        using var input = File.OpenText(source); var yaml = new YamlStream(); yaml.Load(input);
        config = yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is YamlScalarNode { Value: "" or null or "null" } ? [] : yaml.Documents[0].RootNode as YamlMappingNode ?? throw new IOException("benchmark config must be a mapping");
    }
    else config = (YamlMappingNode)ToYaml(JsonNode.Parse("""
        {"test_name":"RPC read benchmark","description":"Snapshot-backed read-path benchmark","duration":"60s","rps":100,"vus":10,"calls":[
          {"name":"WETH balance eth_call","method":"eth_call","weight":40,"params":[{"to":"0xc02aaa39b223fe8d0a0e5c4f27ead9083c756cc2","data":"0x70a08231000000000000000000000000000000000000000000000000000000000000000a"}]},
          {"name":"eth_getBalance","method":"eth_getBalance","weight":20,"params":["0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045","latest"]},
          {"name":"eth_blockNumber","method":"eth_blockNumber","weight":20,"params":[]},
          {"name":"eth_getTransactionCount","method":"eth_getTransactionCount","weight":10,"params":["0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045","latest"]},
          {"name":"eth_getBlockByNumber","method":"eth_getBlockByNumber","weight":10,"params":["latest",false]}
        ]}
        """));
    var clients = new YamlSequenceNode { primary }; if (reference.Length > 0) clients.Add(reference); config.Children[new YamlScalarNode("clients")] = clients;
    foreach (var key in new[] { "rps", "vus", "seed" })
    {
        var value = environment("JB_" + key.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(value)) config.Children[new YamlScalarNode(key)] = new YamlScalarNode(long.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
    }
    var duration = environment("JB_DURATION"); if (!string.IsNullOrWhiteSpace(duration)) config.Children[new YamlScalarNode("duration")] = new YamlScalarNode(duration);
    if (classesPath.Length > 0)
    {
        var classes = JsonNode.Parse(File.ReadAllText(classesPath))!.AsObject();
        var calls = new YamlSequenceNode();
        foreach (var (name, count) in classes)
            calls.Add(new YamlMappingNode { { "name", (method == "eth_call" ? "" : method + " ") + name }, { "file", "./rpc-calls/corpus/" + name + ".json" }, { "file_type", "json" }, { "weight", count!.ToJsonString() } });
        config.Children[new YamlScalarNode("calls")] = calls;
    }
    if (config.Children.TryGetValue(new YamlScalarNode("calls"), out var entries) && entries is YamlSequenceNode sequence)
        foreach (var call in sequence.Children.OfType<YamlMappingNode>())
            if (!call.Children.ContainsKey(new YamlScalarNode("thresholds"))) call.Add("thresholds", new YamlSequenceNode { "p(99)<600000" });
    return config;
}

static void Save(string path, YamlMappingNode config)
{
    using var output = File.CreateText(path); new YamlStream(new YamlDocument(config)).Save(output, assignAnchors: false);
}

static YamlNode ToYaml(JsonNode? node) => node switch
{
    JsonObject obj => new YamlMappingNode(obj.Select(pair => KeyValuePair.Create<YamlNode, YamlNode>(new YamlScalarNode(pair.Key), ToYaml(pair.Value)))),
    JsonArray array => new YamlSequenceNode(array.Select(ToYaml)),
    JsonValue value when value.TryGetValue<string>(out var text) => new YamlScalarNode(text) { Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted },
    null => new YamlScalarNode("null"),
    _ => new YamlScalarNode(node.ToJsonString())
};
#endif

internal static partial class JsonBenchReporting
{
    internal static (string Markdown, double FailPercent) Performance(JsonObject root)
    {
        var metrics = root["metrics"] as JsonObject ?? (root["metrics"] is null ? [] : throw new JsonException("metrics must be an object"));
        var duration = metrics["http_req_duration"]; var requests = metrics["http_reqs"]; var failures = metrics["http_req_failed"]; var checks = metrics["checks"];
        var rate = Number(failures, "rate"); if (rate == 0) rate = Number(failures, "value");
        var passed = Number(checks, "passes"); var failed = Number(checks, "fails");
        var text = new StringBuilder("### Overall\n\n| metric | value |\n|---|---:|\n");
        text.Append(CultureInfo.InvariantCulture, $"| requests | {(long)Number(requests, "count")} |\n| throughput (req/s) | {Number(requests, "rate"):F2} |\n| http fail rate | {rate * 100:F2}% |\n");
        if (passed + failed > 0) text.Append(CultureInfo.InvariantCulture, $"| checks passed | {passed / (passed + failed) * 100:F2}% |\n");
        var fields = new[] { ("avg", "avg"), ("p50", "med"), ("p90", "p(90)"), ("p95", "p(95)"), ("p99", "p(99)"), ("max", "max") };
        foreach (var (label, key) in fields) text.Append(CultureInfo.InvariantCulture, $"| latency {label} (ms) | {Number(duration, key):F2} |\n");
        var methods = new List<(string Name, JsonNode? Values)>();
        foreach (var (key, value) in metrics)
        {
            if (!key.StartsWith("http_req_duration{", StringComparison.Ordinal)) continue;
            var match = RequestNameRegex().Match(key);
            if (match.Success) methods.Add((match.Groups[1].Value.Trim().Trim('\'', '"'), value));
        }
        if (metrics["classes"] is JsonObject classes) methods.AddRange(classes.Select(pair => (pair.Key, pair.Value)));
        if (methods.Count > 0)
        {
            text.Append("\n### Per method (http_req_duration, ms)\n\n| method | avg | p50 | p90 | p95 | p99 | max |\n|---|---:|---:|---:|---:|---:|---:|\n");
            foreach (var (name, values) in methods.OrderBy(row => row.Name, StringComparer.Ordinal)) text.Append("| ").Append(name).Append(" | ").AppendJoin(" | ", fields.Select(field => Number(values, field.Item2).ToString("F2", CultureInfo.InvariantCulture))).Append(" |\n");
        }
        return (text.ToString(), rate * 100);
    }

    internal static double Number(JsonNode? node, string field)
    {
        if (node is not JsonObject obj) return 0;
        if (obj[field] is JsonValue value && value.TryGetValue<double>(out var number)) return number;
        return obj["values"] is JsonObject values && values[field] is JsonValue nested && nested.TryGetValue<double>(out number) ? number : 0;
    }

    internal static (string Markdown, int Differences) Comparison(JsonNode? root)
    {
        if (root is not (JsonArray or JsonObject)) throw new JsonException("comparison results must be an object or array");
        var rows = root as JsonArray ?? (root as JsonObject)?["results"] as JsonArray ?? (root as JsonObject)?["comparisons"] as JsonArray ?? [];
        var different = rows.OfType<JsonObject>().Where(row => row["differences"] is JsonObject { Count: > 0 } or JsonArray { Count: > 0 }).ToArray();
        var text = new StringBuilder($"**{rows.Count} calls compared, {different.Length} with response differences.**\n\n");
        if (different.Length > 0)
        {
            text.Append("| method | params | differences |\n|---|---|---|\n");
            foreach (var row in different.Take(50))
            {
                var parameters = (row["params"] ?? new JsonArray()).ToJsonString(); parameters = parameters[..Math.Min(80, parameters.Length)];
                var keys = row["differences"] is JsonObject fields ? string.Join(", ", fields.Select(pair => pair.Key).Order(StringComparer.Ordinal)) : string.Join(", ", Enumerable.Range(0, row["differences"]!.AsArray().Count));
                text.Append($"| {row["method"]} | {parameters} | {keys} |\n");
            }
            text.Append("\nFull diff detail: `comparison-results.json` / `comparison-report.html` in the artifact.\n");
        }
        return (text.ToString(), different.Length);
    }

    internal static async Task CaptureAsync(YamlMappingNode config, string source, string url, string output, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        await using var writer = File.CreateText(output);
        var sequence = 0;
        if (config.Children.TryGetValue(new YamlScalarNode("calls"), out var calls) && calls is YamlSequenceNode entries)
            foreach (var call in entries.Children.Cast<YamlMappingNode>())
            {
                if (call.Children.TryGetValue(new YamlScalarNode("file"), out var file))
                {
                    foreach (var line in File.ReadLines(Path.Combine(source, file.ToString().TrimStart('.', '/'))))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var request = JsonNode.Parse(line)!;
                        await Capture(request["method"]?.GetValue<string>(), request["params"]?.DeepClone() ?? new JsonArray());
                    }
                }
                else await Capture(call.Children.TryGetValue(new YamlScalarNode("method"), out var method) ? method.ToString() : null, call.Children.TryGetValue(new YamlScalarNode("params"), out var parameters) ? ToJson(parameters) : new JsonArray());
            }
        BenchmarkProcess.Log($"deep-check: captured {sequence} responses -> {output}");

        async Task Capture(string? method, JsonNode? parameters)
        {
            var identity = new JsonArray(JsonValue.Create(method), parameters?.DeepClone());
            using var identityDocument = JsonDocument.Parse(identity.ToJsonString());
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RpcJson.Canonical(identityDocument.RootElement))))[..16];
            JsonNode? response;
            try
            {
                var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters?.DeepClone() };
                using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
                using var result = await http.PostAsync(url, content, cancellationToken); result.EnsureSuccessStatusCode();
                response = JsonNode.Parse(await result.Content.ReadAsStringAsync(cancellationToken), documentOptions: RpcJson.DocumentOptions);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { response = new JsonObject { ["_capture_error"] = exception.Message }; }
            var row = new JsonObject { ["seq"] = sequence++, ["fp"] = fingerprint, ["method"] = method, ["response"] = response };
            await writer.WriteLineAsync(row.ToJsonString(RpcJson.SerializerOptions).AsMemory(), cancellationToken);
        }
    }

    internal static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair => KeyValuePair.Create(pair.Key.ToString(), ToJson(pair.Value)))),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar when scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted => JsonValue.Create(scalar.Value),
        YamlScalarNode scalar when scalar.Value is "null" or "Null" or "NULL" or "~" or "" or null => null,
        YamlScalarNode scalar when scalar.Value is "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON" => JsonValue.Create(true),
        YamlScalarNode scalar when scalar.Value is "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF" => JsonValue.Create(false),
        YamlScalarNode scalar when long.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
        YamlScalarNode scalar when HexIntegerRegex().IsMatch(scalar.Value ?? "") => JsonValue.Create((scalar.Value![0] == '-' ? -1 : 1) * Convert.ToInt64(scalar.Value.TrimStart('+', '-')[2..], 16)),
        YamlScalarNode scalar when DecimalRegex().IsMatch(scalar.Value ?? "") && double.TryParse(scalar.Value!.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => JsonValue.Create(number),
        YamlScalarNode scalar => JsonValue.Create(scalar.Value),
        _ => throw new IOException("Unsupported benchmark YAML node")
    };
    [GeneratedRegex(@"req_name:([^,}]+)")]
    private static partial Regex RequestNameRegex();
    [GeneratedRegex(@"^[+-]?0x[0-9a-fA-F]+$")]
    private static partial Regex HexIntegerRegex();
    [GeneratedRegex(@"^[+-]?(?:[0-9][0-9_]*\.[0-9_]*|\.[0-9_]+)(?:[eE][+-][0-9]+)?$")]
    private static partial Regex DecimalRegex();
}

internal sealed class ResourceSamplerSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _task;

    internal ResourceSamplerSession(string container, string output) =>
        _task = Task.Run(async () =>
        {
            if (container.Length == 0 || output.Length == 0) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var result = await BenchmarkProcess.RunAsync("docker", ["inspect", "--format", "{{.Id}}", container], timeout.Token);
                if (result.Code != 0) throw new IOException("container not found");
                var id = result.Output.Trim();
                var roots = new[] { $"/sys/fs/cgroup/system.slice/docker-{id}.scope", $"/sys/fs/cgroup/docker/{id}", $"/sys/fs/cgroup/kubepods/docker-{id}.scope" };
                var root = roots.FirstOrDefault(path => File.Exists(Path.Combine(path, "cpu.stat"))) ?? throw new IOException("no cgroup v2 directory found for the container");
                var summary = ResourceSampling.Sample(root, () => _stop.IsCancellationRequested, () => _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(.25)));
                ResourceSampling.Write(output, summary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception)
            { Console.Error.WriteLine($"resource sampling unavailable: {exception.Message}"); }
        });

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _task; }
        finally { _stop.Dispose(); }
    }
}

partial class Program
{
    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex SeedRegex();
    [GeneratedRegex("[^a-zA-Z0-9_.-]")]
    private static partial Regex UnsafeTagRegex();
}
