#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include SweepConfiguration.Helpers.cs
#:include SweepRunner.Helpers.cs
#:include BenchmarkProcess.Helpers.cs
#:include BenchmarkPaths.Helpers.cs
#:include RpcScriptTests.Helpers.cs

using System.Text.Json.Nodes;

using var tests = new RpcScriptTests();
Dictionary<string, string> EnvironmentValues() => new()
{
    ["OUT_DIR"] = Path.Combine(tests.DirectoryPath, "out"), ["STATE_ROOT"] = Path.Combine(tests.DirectoryPath, "state"),
    ["SCRATCH_ROOT"] = Path.Combine(tests.DirectoryPath, "scratch"), ["NM_IMAGE"] = "nethermind:test", ["SNAPSHOT_BLOCK"] = "1",
    ["JB_BENCHMARK_CONFIG"] = "config.yaml", ["SNAPSHOT_ROOT"] = Path.Combine(tests.DirectoryPath, "snapshots"), ["RPS_LIST"] = ""
};
foreach (var entry in new[] { "nethermind+--a;;--b", "nethermind+", "nethermind+Foo=1", "nethermind+--Foo=1#X=2", "nethermind@", "unknown", "nethermind\nreth", " " })
    tests.Case("reject malformed client " + entry.Replace('\n', ' '), () =>
    {
        var values = EnvironmentValues(); values["CLIENTS"] = entry;
        try { _ = new SweepConfiguration(name => values.GetValueOrDefault(name)); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("malformed entry accepted");
    });
foreach (var (name, value) in new[] { ("ROUNDS", "0"), ("JB_SEED", "-1"), ("CORPUS_TIMINGS_RPS", "-1"), ("CORPUS_WARMUP_DURATION", "1.5s"), ("CORPUS_WARMUP_RPS_MAX", "x"), ("RPS_LIST", "10 0"), ("CORPUS_BASELINE", "other"), ("STATE_LAYOUT", "full"), ("JB_ETH_CALL_CORPUS", "yes"), ("CORPUS_METHOD", "other"), ("DB_ISOLATION_ALL", "direct") })
    tests.Case("reject invalid " + name, () =>
    {
        var values = EnvironmentValues(); values[name] = value;
        try { _ = new SweepConfiguration(key => values.GetValueOrDefault(key)); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("invalid setting accepted");
    });
tests.Case("request and pass counts cannot both size a cell", () =>
{
    var values = EnvironmentValues(); values["CORPUS_REQUESTS"] = "10"; values["CORPUS_PASSES"] = "2";
    try { _ = new SweepConfiguration(key => values.GetValueOrDefault(key)); }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("conflicting cell sizes accepted");
});
foreach (var (entry, expected) in new[] { ("nethermind@repo/nm:pr+--Foo=a@b", "repo/nm:pr"), ("nethermind@repo/nm:pr#ENV=1+--Bar=x,y", "repo/nm:pr"), ("reth@repo@sha256:123+--x=1", "repo@sha256:123"), ("geth#ENV=a@b", "fallback") })
    tests.Case("bare arm image " + entry, () => RpcScriptTests.Assert(SweepArm.Parse(entry, "fallback").Image == expected, "image options leaked"));
tests.Case("arm labels include full flags digest and separate environment", () =>
{
    var first = SweepArm.Parse("nethermind@repo:image#NETHERMIND_FOOCONFIG_BAR=1+--SharedLongPrefixAcrossBothArms=one", "fallback");
    var second = SweepArm.Parse("nethermind@repo:image+--SharedLongPrefixAcrossBothArms=two", "fallback");
    RpcScriptTests.Assert(first.Label.StartsWith("nethermind_image_BAR_1_", StringComparison.Ordinal) && first.Label != second.Label, "labels collide");
    RpcScriptTests.Assert(first.Environment == "NETHERMIND_FOOCONFIG_BAR=1" && second.Environment == "" && !first.Flags.Contains('#'), "environment leaked");
});
tests.Case("explicit empty rates and tracer survive defaults", () =>
{
    var values = EnvironmentValues(); values["CORPUS_TRACER"] = "";
    var configuration = new SweepConfiguration(key => values.GetValueOrDefault(key));
    RpcScriptTests.Assert(configuration.Rates.Length == 0 && configuration["CORPUS_TRACER"] == "", "empty settings replaced");
    values.Remove("RPS_LIST");
    RpcScriptTests.Assert(new SweepConfiguration(key => values.GetValueOrDefault(key)).Rates.SequenceEqual([100L, 250, 500]), "default rates changed");
});
foreach (var (file, expected) in new[] { ("eth-call-corpus.jsonl.gz", "default"), ("eth-call-corpus-foo.jsonl.gz", "foo"), ("eth-call-corpus-a+b.jsonl.gz", "a-b") })
    tests.Case("corpus label " + file, () => RpcScriptTests.Assert(SweepConfiguration.CorpusLabel(file) == expected, "incorrect label"));
foreach (var (client, contents, expected) in new[] { ("nethermind", "Nethermind is shut down\n", false), ("nethermind", "Exception: secret calldata\nNethermind is shut down\n", true), ("geth", "Exception: secret calldata\n", false), ("nethermind", "invalid_block\nNethermind is shut down\n", true), ("nethermind", "still running\n", true), ("nethermind", "Incorrect JSON RPC parameters Exception\nNethermind is shut down\n", false) })
    tests.Case("private node log health " + client + " " + expected + " " + contents.Split('\n')[0], () =>
    {
        var log = tests.Write("private.log", contents);
        RpcScriptTests.Assert(SweepRunner.ScanNodeLog(log, client, "arm", true) == expected && !File.Exists(log), "health gate or private cleanup changed");
    });
if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux sweep process harness"); tests.Complete(); return; }

var scriptRoot = Path.Combine(tests.DirectoryPath, "scripts"); Directory.CreateDirectory(scriptRoot);
File.WriteAllText(Path.Combine(scriptRoot, "StartNode.cs"), """
using System.Text.Json.Nodes;
var names = new[] { "STATE_DIR", "ADDITIONAL_FLAGS", "ARM_SCRATCH_DIR", "NODE_ENV_VARS", "CLIENT", "DB_SOURCE", "DB_ISOLATION" };
var values = new JsonObject();
foreach (var name in names) values[name] = Environment.GetEnvironmentVariable(name) ?? "";
File.AppendAllText(Environment.GetEnvironmentVariable("SWEEP_CAPTURE")!, values.ToJsonString() + "\n");
return Environment.GetEnvironmentVariable("FAIL_START_CLIENT") == values["CLIENT"]!.ToString() ? 1 : 0;
""");
File.WriteAllText(Path.Combine(scriptRoot, "StopNode.cs"), """
File.WriteAllText(Environment.GetEnvironmentVariable("LOG_OUT")!, "Nethermind is shut down\n");
return Environment.GetEnvironmentVariable("FAIL_STOP") == "true" ? 1 : 0;
""");
File.WriteAllText(Path.Combine(scriptRoot, "RunJsonBench.cs"), """
var output = Environment.GetEnvironmentVariable("OUT_DIR")!;
File.WriteAllText(Path.Combine(output, "jsonbench-summary.md"), "summary");
File.WriteAllText(Path.Combine(output, "summary.json"), "{\"metrics\":{\"http_req_failed\":{\"values\":{\"rate\":0}}}}");
return Environment.GetEnvironmentVariable("FAIL_CELL") == "true" ? 1 : 0;
""");
File.WriteAllText(Path.Combine(scriptRoot, "PerCategoryMatrix.cs"), "Console.WriteLine(\"matrix\");");
File.WriteAllText(Path.Combine(scriptRoot, "CorpusParity.cs"), """
var options = new Dictionary<string, string>();
for (var index = 1; index + 1 < args.Length; index += 2) options[args[index]] = args[index + 1];
switch (args[0])
{
    case "validate": Console.WriteLine("corpus OK: 2"); return 0;
    case "baseline": File.WriteAllText(options["--state"], "baseline"); return 0;
    case "compare": File.WriteAllText(options["--report"], "{\"total\":2,\"matched\":2,\"both_rpc_errors\":0}"); return int.Parse(Environment.GetEnvironmentVariable("COMPARE_CODE") ?? "0");
    default: return 0;
}
""");
var bin = Path.Combine(tests.DirectoryPath, "bin"); Directory.CreateDirectory(bin);
var docker = Path.Combine(bin, "docker"); File.WriteAllText(docker, "#!/bin/sh\nexit 0\n"); File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var identity = Path.Combine(bin, "id"); File.WriteAllText(identity, "#!/bin/sh\necho 0\n"); File.SetUnixFileMode(identity, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var changedNames = new[] { "RPC_BENCH_SCRIPT_ROOT", "PATH", "SWEEP_CAPTURE", "GITHUB_STEP_SUMMARY", "FAIL_START_CLIENT", "FAIL_STOP", "FAIL_CELL", "COMPARE_CODE", "RPC_BENCH_CORPUS_METHOD", "RPC_BENCH_CORPUS_TRACER", "RPC_BENCH_CORPUS_TRACE_TYPES" };
var original = changedNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    Environment.SetEnvironmentVariable("RPC_BENCH_SCRIPT_ROOT", scriptRoot); Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + original["PATH"]);
    Environment.SetEnvironmentVariable("SWEEP_CAPTURE", Path.Combine(tests.DirectoryPath, "capture")); Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", Path.Combine(tests.DirectoryPath, "summary"));
    foreach (var client in new[] { "nethermind-flat", "reth", "geth" }) Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "snapshots", client + "-1"));
    int Run(Dictionary<string, string> values) => new SweepRunner(new SweepConfiguration(name => values.GetValueOrDefault(name))).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
    tests.Case("ABBA schedule keeps arm flags and environments isolated", () =>
    {
        var values = EnvironmentValues(); values["CLIENTS"] = "nethermind#X=1+--Cache.Path={ARM_SCRATCH} reth+--Flag=two"; values["ROUNDS"] = "2";
        RpcScriptTests.Assert(Run(values) == 0, "sweep failed");
        var rows = File.ReadAllLines(Environment.GetEnvironmentVariable("SWEEP_CAPTURE")!).Select(line => JsonNode.Parse(line)!).ToArray();
        RpcScriptTests.Assert(rows.Select(row => (string)row["CLIENT"]!).SequenceEqual(["nethermind", "reth", "reth", "nethermind"]), "ABBA lost");
        RpcScriptTests.Assert(rows[0]["NODE_ENV_VARS"]!.ToString().Trim() == "X=1" && rows[1]["NODE_ENV_VARS"]!.ToString().Trim() == "", "environment leaked");
        var armScratch = rows[0]["ARM_SCRATCH_DIR"]!.ToString();
        RpcScriptTests.Assert(rows[0]["ADDITIONAL_FLAGS"]!.ToString() == "--Cache.Path=" + armScratch && Directory.Exists(armScratch), "scratch not expanded");
        File.WriteAllText(Path.Combine(armScratch, "stale"), "old");
        values["ROUNDS"] = "1"; values["CLIENTS"] = "nethermind#X=1+--Cache.Path={ARM_SCRATCH}";
        RpcScriptTests.Assert(Run(values) == 0 && !File.Exists(Path.Combine(armScratch, "stale")), "scratch retained old run");
    });
    foreach (var target in new[] { Path.Combine(tests.DirectoryPath, "snapshots"), Path.Combine(tests.DirectoryPath, "snapshots", "reth-1"), Path.Combine(tests.DirectoryPath, "snapshots", "reth-1", "scratch") })
        tests.Case("reject scratch overlap " + target, () =>
        {
            var values = EnvironmentValues(); values["CLIENTS"] = "nethermind reth"; values["SCRATCH_ROOT"] = target;
            try { Run(values); } catch (IOException) { return; }
            throw new InvalidOperationException("overlap accepted");
        });
    foreach (var failure in new[] { "FAIL_START_CLIENT", "FAIL_STOP", "FAIL_CELL" })
        tests.Case("failed arm phase fails sweep " + failure, () =>
        {
            Environment.SetEnvironmentVariable(failure, failure == "FAIL_START_CLIENT" ? "nethermind" : "true");
            var values = EnvironmentValues(); values["RPS_LIST"] = "10";
            try { RpcScriptTests.Assert(Run(values) == 1, "partial run passed"); }
            finally { Environment.SetEnvironmentVariable(failure, null); }
        });
    foreach (var baseline in new[] { "missing", "unreadable", "saved", "save" })
        tests.Case("corpus baseline gate " + baseline, () =>
        {
            var directory = Path.Combine(tests.DirectoryPath, "corpus-" + baseline); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "eth-call-corpus.jsonl.gz"), "fixture");
            var saved = Path.Combine(directory, "baselines"); Directory.CreateDirectory(saved);
            if (baseline is "unreadable" or "saved") File.WriteAllText(Path.Combine(saved, "default.json.gz"), "baseline");
            Environment.SetEnvironmentVariable("COMPARE_CODE", baseline == "unreadable" ? "2" : "0");
            var values = EnvironmentValues(); values["JB_ETH_CALL_CORPUS"] = "true"; values["CORPUS_DIR"] = directory; values["CORPUS_WARMUP_DURATION"] = "0"; values["CORPUS_BASELINE"] = baseline == "save" ? "save" : "use";
            RpcScriptTests.Assert(Run(values) == (baseline is "missing" or "unreadable" ? 1 : 0), "baseline gate changed");
            if (baseline == "save") RpcScriptTests.Assert(File.Exists(Path.Combine(saved, "default.json.gz")) && File.Exists(Path.Combine(saved, "default.label")), "baseline not saved");
        });
}
finally { foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value); }
tests.Complete();
