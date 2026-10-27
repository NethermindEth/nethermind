#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include RpcScriptTests.Helpers.cs
#:package YamlDotNet
#:property DefineConstants=SCRIPT_TEST
#:include RunJsonBench.cs

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
tests.Case("missing paths fail before external commands", () =>
{
    var output = Environment.GetEnvironmentVariable("OUT_DIR");
    try
    {
        Environment.SetEnvironmentVariable("OUT_DIR", null);
        var result = RpcScriptTests.Run("scripts/rpc-bench/RunJsonBench.cs");
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
var changes = new Dictionary<string, string?> { ["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH"), ["FAKE_STATE"] = state, ["RPC_URL"] = "http://localhost:1", ["SCRATCH_ROOT"] = Path.Combine(tests.DirectoryPath, "scratch"), ["JB_REF"] = "testref", ["JB_REUSE_PREPARED"] = "true", ["OUT_DIR"] = null, ["FAKE_GIT_SHA"] = null, ["JB_ETH_CALL_CORPUS"] = "false", ["JB_MODE"] = "benchmark", ["JB_DEEP_CHECK"] = "false", ["JB_BENCHMARK_CONFIG"] = null, ["RESOURCE_SAMPLER_CONTAINER"] = null, ["RESOURCE_SAMPLER_OUT"] = null, ["RPC_BENCH_SCRIPT_ROOT"] = null, ["PROBE_LOG"] = null, ["PROBE_FAILURE_URL"] = null, ["REFERENCE_RPC_URL"] = null, ["JB_ETH_CALL_CORPUS_FILE"] = null, ["CORPUS_METHOD"] = null };
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
            var result = RpcScriptTests.Run("scripts/rpc-bench/RunJsonBench.cs");
            RpcScriptTests.Assert(result.Code == 0, result.ToString());
            var fetches = File.ReadLines(Path.Combine(state, "git.log")).Count(line => (" " + line + " ").Contains(" fetch "));
            var builds = File.ReadLines(Path.Combine(state, "docker.log")).Count(line => line.StartsWith("build ", StringComparison.Ordinal));
            RpcScriptTests.Assert(fetches == preparations && builds == preparations && File.ReadAllText(Path.Combine(output, "results.csv")) == $"run {index}\n", result.ToString());
            if (reuse) RpcScriptTests.Assert(File.ReadAllText(Path.Combine(changes["SCRATCH_ROOT"]!, "jsonbench", "prepared")) == $"https://github.com/NethermindEth/json-bench.git@{(sha.Length > 0 ? sha : revision)}\n", "marker key changed");
        });
    tests.Case("finished benchmark survives unavailable resource sampling", () =>
    {
        Environment.SetEnvironmentVariable("RESOURCE_SAMPLER_CONTAINER", "missing"); Environment.SetEnvironmentVariable("RESOURCE_SAMPLER_OUT", Path.Combine(tests.DirectoryPath, "resource.json"));
        var result = RpcScriptTests.Run("scripts/rpc-bench/RunJsonBench.cs");
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
            var result = RpcScriptTests.Run("scripts/rpc-bench/RunJsonBench.cs");
            RpcScriptTests.Assert(result.Code == 0, result.ToString());
            using var input = File.OpenText(Path.Combine(changes["SCRATCH_ROOT"]!, "jsonbench/io/benchmark.yaml"));
            var yaml = new YamlStream(); yaml.Load(input);
            RpcScriptTests.Assert(((YamlScalarNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("seed")]).Value == (seed ?? "17"), "configured seed replaced");
        });
    }
    finally { Environment.SetEnvironmentVariable("JB_SEED", priorSeed); Environment.SetEnvironmentVariable("JB_BENCHMARK_CONFIG", null); }
    var scripts = Path.Combine(tests.DirectoryPath, "scripts"); Directory.CreateDirectory(scripts);
    File.WriteAllText(Path.Combine(scripts, "CorpusParity.cs"), """
        File.AppendAllText(Environment.GetEnvironmentVariable("PROBE_LOG")!, string.Join(' ', args) + "\n");
        if (args[0] == "probe" && args.Contains(Environment.GetEnvironmentVariable("PROBE_FAILURE_URL"))) { Console.Error.WriteLine("rpc_error:-32000"); return 7; }
        return 0;
        """);
    File.WriteAllText(Path.Combine(scripts, "PrepareEthCallCorpus.cs"), """
        Directory.CreateDirectory(args[1]);
        File.WriteAllText(Path.Combine(args[1], "class_1.json"), "[]");
        File.WriteAllText(Path.Combine(args[1], "classes.json"), "{\"class_1\":1}");
        """);
    File.WriteAllText(Path.Combine(scripts, "CorpusResults.cs"), """
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
                var result = RpcScriptTests.Run("scripts/rpc-bench/RunJsonBench.cs"); RpcScriptTests.Assert(result.Code == 0, result.ToString());
                if (run == 1) RpcScriptTests.Assert(result.Output.Contains("Reusing the json-bench checkout", StringComparison.Ordinal), result.ToString());
            }
            var probes = File.ReadLines(log).Where(line => line.StartsWith("probe ", StringComparison.Ordinal)).ToArray();
            RpcScriptTests.Assert(probes.Length == 4 && probes.Count(line => line.Contains("primary.invalid", StringComparison.Ordinal)) == 2 && probes.Count(line => line.Contains("reference.invalid", StringComparison.Ordinal)) == 2, "endpoint probe skipped");
            RpcScriptTests.Assert(File.ReadLines(Path.Combine(state, "docker.log")).Count(line => line.StartsWith("build ", StringComparison.Ordinal)) == buildsBefore + 1, "reuse rebuilt image");
            Environment.SetEnvironmentVariable("PROBE_FAILURE_URL", "http://reference.invalid:8546");
            var output = Path.Combine(tests.DirectoryPath, method + "failed"); Environment.SetEnvironmentVariable("OUT_DIR", output);
            var before = File.ReadAllText(Path.Combine(state, "docker.log"));
            var failed = RpcScriptTests.Run("scripts/rpc-bench/RunJsonBench.cs");
            RpcScriptTests.Assert(failed.Code != 0 && failed.Error.Contains("rpc_error:-32000", StringComparison.Ordinal) && !Directory.Exists(output) && before == File.ReadAllText(Path.Combine(state, "docker.log")), failed.ToString());
        });
}
finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
tests.Complete();
