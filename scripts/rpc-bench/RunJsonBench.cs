#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkPaths.Helpers.cs
#:include BenchmarkFiles.Helpers.cs
#:include BenchmarkRpc.Helpers.cs
#:include NodeConfiguration.Helpers.cs
#:include JsonBenchConfiguration.Helpers.cs
#:include JsonBenchReporting.Helpers.cs
#:include RpcJson.Helpers.cs
#:include ResourceSampling.Helpers.cs
#:include ResourceSamplerSession.Helpers.cs

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
using var cancellation = new CancellationTokenSource();
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
    var corpus = Get("JB_ETH_CALL_CORPUS", "false");
    if (corpus is not ("true" or "false")) throw new ArgumentException("JB_ETH_CALL_CORPUS must be true or false");
    var seed = Get("JB_SEED", "1"); if (!Regex.IsMatch(seed, "^[0-9]+$")) throw new ArgumentException($"JB_SEED must be a non-negative integer, got '{seed}'");
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
        await Script("CorpusParity.cs", ["validate", "--corpus", corpusPath]);
        if (method != "eth_call")
            foreach (var endpoint in new[] { url, reference }.Where(value => value.Length > 0)) await Script("CorpusParity.cs", ["probe", "--corpus", corpusPath, "--rpc-url", endpoint]);
    }
    Directory.CreateDirectory(output);
    scratch = await BenchmarkPaths.CanonicalAsync(scratch, false, cancellation.Token); BenchmarkPaths.AssertSane(scratch, "SCRATCH_ROOT");
    var work = Path.Combine(scratch, "jsonbench"); var source = Path.Combine(work, "src"); var io = Path.Combine(work, "io");
    var repository = Get("JB_REPO", "https://github.com/NethermindEth/json-bench.git"); var revision = Get("JB_REF", "de1bcfadea47258ccacae2f420141032a82a9ded");
    var tag = Regex.Replace(revision, "[^a-zA-Z0-9_.-]", "-"); tag = "jsonbench-runner:" + tag[..Math.Min(24, tag.Length)];
    var fixtures = Path.Combine(source, "rpc-calls", "corpus"); var marker = Path.Combine(work, "prepared");
    var reuse = Get("JB_REUSE_PREPARED", "false") == "true"; var prepared = false; var preparationId = "";
    Environment.SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0");
    if (reuse)
    {
        var resolved = await Required("git", ["ls-remote", repository, revision]);
        var commit = resolved.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? revision;
        preparationId = repository + "@" + commit;
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
        await Required("docker", ["build", "-q", "-f", dockerfile, "-t", tag, source]);
    }
    var clients = new YamlSequenceNode { new YamlMappingNode { { "name", label }, { "type", clientType }, { "url", url }, { "timeout", "60s" }, { "max_retries", "3" } } };
    if (reference.Length > 0) clients.Add(new YamlMappingNode { { "name", referenceLabel }, { "type", referenceType }, { "url", reference }, { "timeout", "60s" }, { "max_retries", "3" } });
    var clientsPath = Path.Combine(io, "clients.yaml"); JsonBenchConfiguration.Save(clientsPath, new YamlMappingNode { { "clients", clients } });
    var benchmarkName = Get("JB_BENCHMARK_CONFIG"); YamlMappingNode? benchmark = null;
    if (mode == "benchmark")
    {
        var configPath = benchmarkName.Length == 0 ? "" : Path.IsPathRooted(benchmarkName) ? benchmarkName : benchmarkName.Contains('/') ? Path.Combine(source, benchmarkName) : Path.Combine(source, "config", "benchmark", benchmarkName + ".yaml");
        if (configPath.Length > 0 && !File.Exists(configPath)) throw new IOException($"benchmark_config '{benchmarkName}' not found (looked at {configPath})");
        if (corpus == "true" && !prepared) { Directory.CreateDirectory(fixtures); await Script("PrepareEthCallCorpus.cs", [corpusPath, fixtures]); }
        benchmark = JsonBenchConfiguration.Render(configPath, label, reference.Length > 0 ? referenceLabel : "", corpus == "true" ? Path.Combine(fixtures, "classes.json") : "", method, name => name == "JB_SEED" ? seed : Get(name));
        JsonBenchConfiguration.Save(Path.Combine(io, "benchmark.yaml"), benchmark);
    }
    if (reuse) await File.WriteAllTextAsync(marker, preparationId + "\n", cancellation.Token);
    await Required("chmod", ["-R", "a+rwX", io]);
    await BenchmarkProcess.RunAsync("docker", ["rm", "-fv", container], cancellation.Token);
    var docker = new List<string> { "run", "--rm", "--name", container, "--network", "host", "-w", "/jb", "-v", source + ":/jb:ro", "-v", io + ":/io", tag, mode };
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
    await using (var sampler = new ResourceSamplerSession(Get("RESOURCE_SAMPLER_CONTAINER"), Get("RESOURCE_SAMPLER_OUT")))
    {
        containerOwned = true;
        toolCode = await BenchmarkFiles.RunCombinedToFileAsync("docker", docker, toolLog, cancellation.Token, echo: corpus != "true");
        containerOwned = false;
    }
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
    if (corpus == "true") { File.Delete(Path.Combine(output, "summary.json")); await Script("CorpusResults.cs", ["sanitize", Path.Combine(io, "out", "summary.json"), Path.Combine(output, "summary.json")]); }
    else
    {
        await BenchmarkProcess.RunAsync("cp", ["-r", Path.Combine(io, "out") + "/.", output + "/"], cancellation.Token);
        File.Delete(Path.Combine(output, "requests.csv"));
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
        if (failPercent > double.Parse(Get("JB_MAX_FAIL_RATE_PCT", "1"), CultureInfo.InvariantCulture)) throw new IOException($"http fail rate {failPercent:F4}% exceeds max_fail_rate_pct={Get("JB_MAX_FAIL_RATE_PCT", "1")}%");
    }
    else if (differences is null || parseFailed) throw new IOException("json-bench compare produced no parseable results");
    else if (Get("JB_FAIL_ON_DIFF", "false") == "true" && differences > 0) throw new IOException($"json-bench compare found {differences} response difference(s) and fail_on_diff is enabled");
    return 0;

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
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException or YamlDotNet.Core.YamlException or System.ComponentModel.Win32Exception)
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
