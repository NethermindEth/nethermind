#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include rpc-script-tests.helpers.cs
#:package YamlDotNet
#:property DefineConstants=SCRIPT_TEST
#:include run-json-bench.cs

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

using var tests = new RpcScriptTests();
foreach (var (yaml, expected) in new[]
{
    ("[1.5, -2.25, 0x10, -0x10]", "[1.5,-2.25,16,-16]"),
    ("['1.5', \"0x10\", 'yes', 'NULL']", "[\"1.5\",\"0x10\",\"yes\",\"NULL\"]"),
    ("[yes, OFF, Null, NULL, 1e3, 1.0e3, 1.0e+3]", "[true,false,null,null,\"1e3\",\"1.0e3\",1000]")
})
    tests.Case($"deep-check preserves YAML scalar types {yaml}", () =>
    {
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        RpcScriptTests.Assert(JsonBenchReporting.ToJson(stream.Documents[0].RootNode)!.ToJsonString() == expected, "YAML parameter types changed");
    });
foreach (var nested in new[] { false, true })
{
    JsonObject Summary(int delivered = 10, int failed = 0, int dropped = 0)
    {
        JsonObject Metric(JsonObject values) => nested ? new JsonObject { ["values"] = values } : values;
        var summary = new JsonObject { ["metrics"] = new JsonObject
        {
            ["http_reqs"] = Metric(new() { ["count"] = delivered }), ["http_req_failed"] = Metric(new() { ["rate"] = failed / 10.0 }),
            ["checks"] = Metric(new() { ["passes"] = 20 - failed, ["fails"] = failed }), ["dropped_iterations"] = Metric(new() { ["count"] = dropped })
        } };
        return JsonNode.Parse(summary.ToJsonString())!.AsObject();
    }
    tests.Case("delivered count normalizes resources for " + (nested ? "nested" : "flat") + " k6 summary", () =>
    {
        var summary = Summary();
        JsonBenchReporting.ValidateWarmup(summary);
        var resources = tests.Write("normalize.json", "{\"cpu_seconds\":2,\"cpu_avg_cores\":1,\"io_read_bytes\":100,\"memory_peak_bytes\":1}");
        ResourceSampling.Normalize(resources, JsonBenchReporting.DeliveredRequests(summary));
        var result = JsonNode.Parse(File.ReadAllText(resources))!;
        RpcScriptTests.Assert(result["requests"]!.GetValue<int>() == 10 && result["cpu_ms_per_request"]!.GetValue<double>() == 200, "flat/nested delivered normalization failed");
    });
    foreach (var defect in new[] { "empty", "http", "rpc", "dropped", "missing-checks", "missing-fails", "missing-fail-rate" })
        tests.Case($"strict warmup rejects {defect}, nested={nested}", () =>
        {
            var summary = Summary(defect == "empty" ? 0 : 10, defect == "http" ? 1 : 0, defect == "dropped" ? 1 : 0);
            var metrics = summary["metrics"]!.AsObject();
            JsonObject Values(string metric) => (nested ? metrics[metric]!["values"] : metrics[metric])!.AsObject();
            if (defect == "rpc") Values("checks")["fails"] = 1;
            if (defect == "missing-checks") metrics.Remove("checks");
            if (defect == "missing-fails") Values("checks").Remove("fails");
            if (defect == "missing-fail-rate") metrics.Remove("http_req_failed");
            try { JsonBenchReporting.ValidateWarmup(summary); }
            catch (IOException) { return; }
            throw new InvalidOperationException("bad warmup accepted");
        });
}
void WithRpc(string response, int count, Action<string, List<string>> action, int status = 200)
{
    using var port = new TcpListener(IPAddress.Loopback, 0);
    port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
    var url = $"http://127.0.0.1:{number}/";
    using var listener = new HttpListener(); listener.Prefixes.Add(url); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var methods = new List<string>();
    var server = Task.Run(async () =>
    {
        for (var index = 0; index < count; index++)
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var input = new StreamReader(context.Request.InputStream);
            var request = JsonNode.Parse(await input.ReadToEndAsync(timeout.Token))!;
            methods.Add(request["method"]!.ToString());
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode = status; context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, timeout.Token); context.Response.Close();
        }
    });
    try { action(url, methods); }
    finally { timeout.Cancel(); listener.Stop(); server.GetAwaiter().GetResult(); }
}
foreach (var (response, status) in new[] { ("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[]}", 200), ("{\"error\":{\"code\":-32000}}", 200), ("not JSON", 200), ("{}", 200), ("unavailable", 503) })
    foreach (var strict in new[] { false, true })
        tests.Case($"capture order and error policy strict={strict} status={status} response={response}", () =>
        {
            var success = response.Contains("result", StringComparison.Ordinal);
            tests.Write("calls.jsonl", "{\"method\":\"second\",\"params\":[]}\n{\"method\":\"third\",\"params\":[]}\n");
            var yaml = new YamlStream(); yaml.Load(new StringReader("calls:\n- {method: first, params: [], weight: 1}\n- {file: './calls.jsonl', weight: 100}\n"));
            var output = Path.Combine(tests.DirectoryPath, "capture.jsonl"); var timings = output + ".timings";
            WithRpc(response, strict && !success ? 1 : 3, (url, methods) =>
            {
                var failed = false;
                try { JsonBenchReporting.CaptureAsync((YamlMappingNode)yaml.Documents[0].RootNode, tests.DirectoryPath, url, output, CancellationToken.None, timings, strict).GetAwaiter().GetResult(); }
                catch (IOException) { failed = true; }
                RpcScriptTests.Assert(failed == (strict && !success), "capture failure policy changed");
                var rows = File.ReadAllLines(output).Select(line => JsonNode.Parse(line)!).ToArray();
                var timingRows = File.ReadAllLines(timings).Select(line => JsonNode.Parse(line)!).ToArray();
                RpcScriptTests.Assert(methods.SequenceEqual(strict && !success ? ["first"] : ["first", "second", "third"]), "capture is not one deterministic pass");
                RpcScriptTests.Assert(rows.Length == methods.Count && timingRows.Length == rows.Length, "missing partial evidence");
                for (var index = 0; index < rows.Length; index++)
                    RpcScriptTests.Assert(timingRows[index]["seq"]!.GetValue<int>() == index && timingRows[index]["fp"]!.ToString() == rows[index]["fp"]!.ToString() && timingRows[index]["elapsed_ms"]!.GetValue<double>() >= 0 && timingRows[index]["success"]!.GetValue<bool>() == success, "timing sidecar attribution failed");
            }, status);
        });
tests.Case("empty first-use fails closed", () =>
{
    var yaml = new YamlStream(); yaml.Load(new StringReader("calls: []"));
    try { JsonBenchReporting.CaptureAsync((YamlMappingNode)yaml.Documents[0].RootNode, tests.DirectoryPath, "http://localhost:1", Path.Combine(tests.DirectoryPath, "empty.jsonl"), CancellationToken.None, strict: true).GetAwaiter().GetResult(); }
    catch (IOException) { return; }
    throw new InvalidOperationException("empty capture passed");
});
tests.Case("missing paths fail before external commands", () =>
{
    var output = Environment.GetEnvironmentVariable("OUT_DIR");
    try
    {
        Environment.SetEnvironmentVariable("OUT_DIR", null);
        var result = RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs");
        RpcScriptTests.Assert(result.Code == 1 && result.Error.Contains("OUT_DIR and SCRATCH_ROOT are required"), result.ToString());
    }
    finally { Environment.SetEnvironmentVariable("OUT_DIR", output); }
});
if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux json-bench command fixtures"); tests.Complete(); return; }
var bin = Path.Combine(tests.DirectoryPath, "bin"); Directory.CreateDirectory(bin);
var state = Path.Combine(tests.DirectoryPath, "state"); Directory.CreateDirectory(Path.Combine(state, "images"));
void Executable(string name, string text)
{
    var path = Path.Combine(bin, name); File.WriteAllText(path, "#!/bin/bash\n" + text);
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
Executable("id", "printf '0\\n'\n");
Executable("git", """
    printf '%s\n' "$*" >> "$FAKE_STATE/git.log"
    if [[ "$1" == "ls-remote" ]]; then
      [[ -z "${FAKE_GIT_SHA:-}" ]] || printf '%s\trefs/heads/%s\n' "$FAKE_GIT_SHA" "$3"
    fi
    if [[ "$1" == "init" ]]; then mkdir -p "${@: -1}/runner" && : > "${@: -1}/runner/Dockerfile"; fi
    exit 0
    """);
Executable("docker", """
    printf '%s\n' "$*" >> "$FAKE_STATE/docker.log"
    case "$1 $2" in
      "build -q") prev=""; for a in "$@"; do [[ "$prev" == "-t" ]] && : > "$FAKE_STATE/images/${a//:/_}"; prev="$a"; done ;;
      "image inspect") [[ -f "$FAKE_STATE/images/${3//:/_}" ]] ;;
      "run --rm")
        n=$(( $(cat "$FAKE_STATE/runs" 2>/dev/null || echo 0) + 1 )); printf '%s' "$n" > "$FAKE_STATE/runs"
        for a in "$@"; do [[ "$a" == *:/io ]] && printf 'run %s\n' "$n" > "${a%:/io}/out/results.csv"; done
        true ;;
    esac
    """);
var changes = new Dictionary<string, string?> { ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH"), ["FAKE_STATE"] = state, ["RPC_URL"] = "http://localhost:1", ["SCRATCH_ROOT"] = Path.Combine(tests.DirectoryPath, "scratch"), ["JB_REF"] = "testref", ["JB_REUSE_PREPARED"] = "true", ["OUT_DIR"] = null, ["FAKE_GIT_SHA"] = null, ["JB_ETH_CALL_CORPUS"] = "false", ["JB_MODE"] = "benchmark", ["JB_DEEP_CHECK"] = "false", ["JB_CAPTURE_ONLY"] = null, ["JB_STRICT_WARMUP"] = null, ["JB_BENCHMARK_CONFIG"] = null, ["RESOURCE_SAMPLER_CONTAINER"] = null, ["RESOURCE_SAMPLER_OUT"] = null, ["RPC_BENCH_SCRIPT_ROOT"] = null, ["PROBE_LOG"] = null, ["PROBE_FAILURE_URL"] = null, ["REFERENCE_RPC_URL"] = null, ["JB_ETH_CALL_CORPUS_FILE"] = null, ["CORPUS_METHOD"] = null };
var previous = changes.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    foreach (var (name, value) in changes) Environment.SetEnvironmentVariable(name, value);
    var index = 0;
    foreach (var (reuse, revision, sha, preparations) in new[] { (true, "testref", "", 1), (true, "testref", "", 1), (false, "testref", "", 2), (true, "otherref", "", 3), (true, "movingref", new string('a', 40), 4), (true, "movingref", new string('a', 40), 4), (true, "movingref", new string('b', 40), 5) })
        tests.Case($"preparation case {++index}: {revision} reuse={reuse} commit={sha}", () =>
        {
            var output = Path.Combine(tests.DirectoryPath, "out" + index);
            Environment.SetEnvironmentVariable("OUT_DIR", output); Environment.SetEnvironmentVariable("JB_REF", revision); Environment.SetEnvironmentVariable("JB_REUSE_PREPARED", reuse ? "true" : "false"); Environment.SetEnvironmentVariable("FAKE_GIT_SHA", sha);
            var result = RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs");
            RpcScriptTests.Assert(result.Code == 0, result.ToString());
            var fetches = File.ReadLines(Path.Combine(state, "git.log")).Count(line => (" " + line + " ").Contains(" fetch "));
            var builds = File.ReadLines(Path.Combine(state, "docker.log")).Count(line => line.StartsWith("build ", StringComparison.Ordinal));
            RpcScriptTests.Assert(fetches == preparations && builds == preparations && File.ReadAllText(Path.Combine(output, "results.csv")) == $"run {index}\n", result.ToString());
            if (reuse) RpcScriptTests.Assert(File.ReadAllText(Path.Combine(changes["SCRATCH_ROOT"]!, "jsonbench", "prepared")) == $"https://github.com/NethermindEth/json-bench.git@{(sha.Length > 0 ? sha : revision)}\n", "marker key changed");
        });
    tests.Case("finished benchmark survives unavailable resource sampling", () =>
    {
        Environment.SetEnvironmentVariable("RESOURCE_SAMPLER_CONTAINER", "missing"); Environment.SetEnvironmentVariable("RESOURCE_SAMPLER_OUT", Path.Combine(tests.DirectoryPath, "resource.json"));
        var result = RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs");
        RpcScriptTests.Assert(result.Code == 0 && result.Error.Contains("resource sampling unavailable", StringComparison.Ordinal), result.ToString());
        Environment.SetEnvironmentVariable("RESOURCE_SAMPLER_CONTAINER", null);
    });
    var configuredSeed = tests.Write("seed.yaml", "seed: 17\ncalls: []\n");
    var priorSeed = Environment.GetEnvironmentVariable("JB_SEED");
    try
    {
        foreach (var seed in new string?[] { null, "0", "23" }) tests.Case($"benchmark preserves configured seed unless overridden: {seed ?? "unset"}", () =>
        {
            Environment.SetEnvironmentVariable("JB_SEED", seed);
            Environment.SetEnvironmentVariable("JB_BENCHMARK_CONFIG", configuredSeed);
            var result = RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs");
            RpcScriptTests.Assert(result.Code == 0, result.ToString());
            using var input = File.OpenText(Path.Combine(changes["SCRATCH_ROOT"]!, "jsonbench/io/benchmark.yaml"));
            var yaml = new YamlStream(); yaml.Load(input);
            RpcScriptTests.Assert(((YamlScalarNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("seed")]).Value == (seed ?? "17"), "configured seed replaced");
        });
    }
    finally { Environment.SetEnvironmentVariable("JB_SEED", priorSeed); Environment.SetEnvironmentVariable("JB_BENCHMARK_CONFIG", null); }
    tests.Case("capture-only skips load generator and summaries", () =>
    {
        var output = Path.Combine(tests.DirectoryPath, "first-use-only");
        var config = tests.Write("first-use.yaml", "calls:\n- {method: first, params: []}\n");
        Environment.SetEnvironmentVariable("OUT_DIR", output); Environment.SetEnvironmentVariable("JB_CAPTURE_ONLY", "true"); Environment.SetEnvironmentVariable("JB_BENCHMARK_CONFIG", config);
        var before = File.ReadAllText(Path.Combine(state, "runs"));
        try
        {
            WithRpc("{\"result\":[]}", 1, (url, _) =>
            {
                Environment.SetEnvironmentVariable("RPC_URL", url);
                var result = RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs");
                RpcScriptTests.Assert(result.Code == 0 && File.Exists(Path.Combine(output, "first-use.jsonl")) && File.Exists(Path.Combine(output, "first-use-timings.jsonl")), result.ToString());
                RpcScriptTests.Assert(!File.Exists(Path.Combine(output, "jsonbench-summary.md")) && !File.Exists(Path.Combine(output, "summary.json")) && File.ReadAllText(Path.Combine(state, "runs")) == before, "capture-only started timed load or emitted a steady-state summary");
            });
        }
        finally { Environment.SetEnvironmentVariable("JB_CAPTURE_ONLY", null); Environment.SetEnvironmentVariable("JB_BENCHMARK_CONFIG", null); }
    });
    tests.Case("strict warmup cannot pass with results CSV alone", () =>
    {
        Environment.SetEnvironmentVariable("OUT_DIR", Path.Combine(tests.DirectoryPath, "strict-warmup")); Environment.SetEnvironmentVariable("JB_STRICT_WARMUP", "true");
        try { RpcScriptTests.Assert(RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs").Code != 0, "unobservable warmup passed"); }
        finally { Environment.SetEnvironmentVariable("JB_STRICT_WARMUP", null); }
    });
    var scripts = Path.Combine(tests.DirectoryPath, "scripts"); Directory.CreateDirectory(scripts);
    File.WriteAllText(Path.Combine(scripts, "corpus-parity.cs"), """
        File.AppendAllText(Environment.GetEnvironmentVariable("PROBE_LOG")!, string.Join(' ', args) + "\n");
        if (args[0] == "probe" && args.Contains(Environment.GetEnvironmentVariable("PROBE_FAILURE_URL"))) { Console.Error.WriteLine("rpc_error:-32000"); return 7; }
        return 0;
        """);
    File.WriteAllText(Path.Combine(scripts, "prepare-eth-call-corpus.cs"), """
        Directory.CreateDirectory(args[1]);
        File.WriteAllText(Path.Combine(args[1], "class_1.json"), "[]");
        File.WriteAllText(Path.Combine(args[1], "classes.json"), "{\"class_1\":1}");
        """);
    File.WriteAllText(Path.Combine(scripts, "corpus-results.cs"), """
        File.WriteAllText(args[2], "{\"metrics\":{\"http_req_failed\":{\"values\":{\"rate\":0}},\"http_reqs\":{\"values\":{\"count\":1}}}}");
        """);
    var corpus = tests.Write("eth-call-corpus.jsonl.gz", "fixture");
    foreach (var method in new[] { "trace_call", "debug_traceCall" })
        tests.Case("reuse probes both endpoints and rejects bad reference " + method, () =>
        {
            var log = Path.Combine(tests.DirectoryPath, method + ".log");
            Environment.SetEnvironmentVariable("RPC_BENCH_SCRIPT_ROOT", scripts); Environment.SetEnvironmentVariable("PROBE_LOG", log);
            Environment.SetEnvironmentVariable("RPC_URL", "http://primary.invalid:8545"); Environment.SetEnvironmentVariable("REFERENCE_RPC_URL", "http://reference.invalid:8546");
            Environment.SetEnvironmentVariable("JB_ETH_CALL_CORPUS", "true"); Environment.SetEnvironmentVariable("JB_ETH_CALL_CORPUS_FILE", corpus); Environment.SetEnvironmentVariable("CORPUS_METHOD", method);
            Environment.SetEnvironmentVariable("PROBE_FAILURE_URL", null);
            var buildsBefore = File.ReadLines(Path.Combine(state, "docker.log")).Count(line => line.StartsWith("build ", StringComparison.Ordinal));
            for (var run = 0; run < 2; run++)
            {
                Environment.SetEnvironmentVariable("OUT_DIR", Path.Combine(tests.DirectoryPath, method + run));
                var result = RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs"); RpcScriptTests.Assert(result.Code == 0, result.ToString());
                if (run == 1) RpcScriptTests.Assert(result.Output.Contains("Reusing the json-bench checkout", StringComparison.Ordinal), result.ToString());
            }
            var probes = File.ReadLines(log).Where(line => line.StartsWith("probe ", StringComparison.Ordinal)).ToArray();
            RpcScriptTests.Assert(probes.Length == 4 && probes.Count(line => line.Contains("primary.invalid", StringComparison.Ordinal)) == 2 && probes.Count(line => line.Contains("reference.invalid", StringComparison.Ordinal)) == 2, "endpoint probe skipped");
            RpcScriptTests.Assert(File.ReadLines(Path.Combine(state, "docker.log")).Count(line => line.StartsWith("build ", StringComparison.Ordinal)) == buildsBefore + 1, "reuse rebuilt image");
            Environment.SetEnvironmentVariable("PROBE_FAILURE_URL", "http://reference.invalid:8546");
            var output = Path.Combine(tests.DirectoryPath, method + "failed"); Environment.SetEnvironmentVariable("OUT_DIR", output);
            var before = File.ReadAllText(Path.Combine(state, "docker.log"));
            var failed = RpcScriptTests.Run("scripts/rpc-bench/run-json-bench.cs");
            RpcScriptTests.Assert(failed.Code != 0 && failed.Error.Contains("rpc_error:-32000", StringComparison.Ordinal) && !Directory.Exists(output) && before == File.ReadAllText(Path.Combine(state, "docker.log")), failed.ToString());
        });
}
finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
tests.Complete();
