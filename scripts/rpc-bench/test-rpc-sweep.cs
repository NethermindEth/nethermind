#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property DefineConstants=SCRIPT_TEST
#:include run-rpc-sweep.cs
#:include rpc-script-tests.helpers.cs

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
foreach (var (name, value) in new[] { ("ROUNDS", "0"), ("JB_SEED", "-1"), ("CORPUS_TIMINGS_RPS", "-1"), ("CORPUS_WARMUP_DURATION", "1.5s"), ("CORPUS_WARMUP_RPS_MAX", "x"), ("RPS_LIST", "10 0"), ("CORPUS_BASELINE", "other"), ("STATE_LAYOUT", "full"), ("JB_ETH_CALL_CORPUS", "yes"), ("CORPUS_METHOD", "other"), ("DB_ISOLATION_ALL", "direct"), ("RECEIPT_HASHES_MIGRATION_TIMEOUT", "-1"), ("RECEIPT_HASHES_MIGRATION_TIMEOUT", "14401"), ("RECEIPT_HASHES_MIGRATION_TIMEOUT", "x"), ("NODE_IO_DIAGNOSTICS", "yes"), ("GENERIC_FIRST_USE", "yes"), ("GENERIC_RESOURCE_SAMPLING", "1"), ("GENERIC_WARMUP_DURATION", "1.5s"), ("GENERIC_WARMUP_RPS", "0") })
    tests.Case("reject invalid " + name, () =>
    {
        var values = EnvironmentValues(); values[name] = value;
        try { _ = new SweepConfiguration(key => values.GetValueOrDefault(key)); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("invalid setting accepted");
    });
tests.Case("migration gating and I/O diagnostics default off", () =>
{
    var values = EnvironmentValues();
    var configuration = new SweepConfiguration(key => values.GetValueOrDefault(key));
    RpcScriptTests.Assert(configuration.Number("RECEIPT_HASHES_MIGRATION_TIMEOUT") == 0 && configuration["NODE_IO_DIAGNOSTICS"] == "false", "readiness defaults changed");
});
tests.Case("generic phases and resources default off", () =>
{
    var configuration = new SweepConfiguration(key => EnvironmentValues().GetValueOrDefault(key));
    RpcScriptTests.Assert(configuration["GENERIC_FIRST_USE"] == "false" && configuration.Number("GENERIC_WARMUP_SECONDS") == 0 && configuration["GENERIC_RESOURCE_SAMPLING"] == "false", "generic defaults changed");
});
foreach (var (name, value) in new[] { ("GENERIC_FIRST_USE", "true"), ("GENERIC_WARMUP_DURATION", "1s"), ("GENERIC_RESOURCE_SAMPLING", "true") })
    tests.Case("private corpus refuses public phase option " + name, () =>
    {
        var values = EnvironmentValues(); values["JB_ETH_CALL_CORPUS"] = "true"; values[name] = value;
        try { _ = new SweepConfiguration(key => values.GetValueOrDefault(key)); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("private corpus accepted public instrumentation");
    });
foreach (var (client, isolation) in new[] { ("nethermind", "readonly-bind"), ("nethermind", "direct"), ("reth", "overlay") })
    tests.Case("migration gating refuses unsupported isolation/client " + client + " " + isolation, () =>
    {
        var values = EnvironmentValues();
        values["CLIENTS"] = client; values["DB_ISOLATION_ALL"] = isolation; values["DB_ISOLATION_ALLOW_SNAPSHOT_MUTATION"] = "true";
        values["RECEIPT_HASHES_MIGRATION_TIMEOUT"] = "10";
        try { _ = new SweepConfiguration(key => values.GetValueOrDefault(key)); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("unsafe migration gate accepted");
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
File.WriteAllText(Path.Combine(scriptRoot, "start-node.cs"), """
using System.Text.Json.Nodes;
var names = new[] { "STATE_DIR", "ADDITIONAL_FLAGS", "ARM_SCRATCH_DIR", "NODE_ENV_VARS", "CLIENT", "DB_SOURCE", "DB_ISOLATION" };
var values = new JsonObject();
foreach (var name in names) values[name] = Environment.GetEnvironmentVariable(name) ?? "";
File.AppendAllText(Environment.GetEnvironmentVariable("SWEEP_CAPTURE")!, values.ToJsonString() + "\n");
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PREPARED_SNAPSHOT_ROOT"))) File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("STATE_DIR")!, "node.env"), "owned stub state");
return Environment.GetEnvironmentVariable("FAIL_START_CLIENT") == values["CLIENT"]!.ToString() ? 1 : 0;
""");
File.WriteAllText(Path.Combine(scriptRoot, "stop-node.cs"), """
File.WriteAllText(Environment.GetEnvironmentVariable("LOG_OUT")!, "Nethermind is shut down\n");
return Environment.GetEnvironmentVariable("FAIL_STOP") == "true" ? 1 : 0;
""");
File.WriteAllText(Path.Combine(scriptRoot, "prepared-snapshot.py"), """
import json, os, pathlib, sys
path = pathlib.Path(os.environ['PREPARED_CAPTURE'])
action = sys.argv[1]
rows = path.read_text().splitlines() if path.exists() else []
with path.open('a') as stream: stream.write(json.dumps({'action': action}) + '\n')
fail = os.environ.get('FAIL_PREPARED_ACTION', '')
if fail == action or (fail == 'gate2' and action == 'gate' and sum(json.loads(row)['action'] == 'gate' for row in rows) == 1): sys.exit(1)
""");
File.WriteAllText(Path.Combine(scriptRoot, "run-json-bench.cs"), """
using System.Text.Json.Nodes;
if (Environment.GetEnvironmentVariable("PREPARED_CAPTURE") is { } trace) File.AppendAllText(trace, "{\"action\":\"cell\"}\n");
var names = new[] { "OUT_DIR", "JB_BENCHMARK_CONFIG", "JB_CAPTURE_ONLY", "JB_STRICT_WARMUP", "JB_MAX_FAIL_RATE_PCT", "JB_SEED", "JB_RPS", "JB_DURATION", "JB_DEEP_CHECK", "RESOURCE_SAMPLER_CONTAINER", "RESOURCE_SAMPLER_OUT" };
var row = new JsonObject();
foreach (var name in names) row[name] = Environment.GetEnvironmentVariable(name) ?? "";
if (Environment.GetEnvironmentVariable("CELL_CAPTURE") is { } capture) File.AppendAllText(capture, row.ToJsonString() + "\n");
if (Environment.GetEnvironmentVariable("FAIL_PHASE") is { } phase && row[phase]!.ToString() == "true") return 1;
if (row["JB_CAPTURE_ONLY"]!.ToString() == "true") return 0;
var output = Environment.GetEnvironmentVariable("OUT_DIR")!;
File.WriteAllText(Path.Combine(output, "jsonbench-summary.md"), "summary");
File.WriteAllText(Path.Combine(output, "summary.json"), "{\"metrics\":{\"http_req_failed\":{\"values\":{\"rate\":0}}}}");
return Environment.GetEnvironmentVariable("FAIL_CELL") == "true" ? 1 : 0;
""");
File.WriteAllText(Path.Combine(scriptRoot, "per-category-matrix.cs"), "Console.WriteLine(\"matrix\");");
File.WriteAllText(Path.Combine(scriptRoot, "corpus-parity.cs"), """
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
var docker = Path.Combine(bin, "docker"); File.WriteAllText(docker, "#!/bin/sh\nif [ \"$1\" = inspect ]; then echo true; fi\nexit 0\n"); File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var identity = Path.Combine(bin, "id"); File.WriteAllText(identity, "#!/bin/sh\necho 0\n"); File.SetUnixFileMode(identity, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var disk = Path.Combine(bin, "df"); File.WriteAllText(disk, "#!/bin/sh\nprintf 'Avail\\n999999999999999\\n'\n"); File.SetUnixFileMode(disk, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var changedNames = new[] { "RPC_BENCH_SCRIPT_ROOT", "PATH", "SWEEP_CAPTURE", "GITHUB_STEP_SUMMARY", "FAIL_START_CLIENT", "FAIL_STOP", "FAIL_CELL", "FAIL_PHASE", "CELL_CAPTURE", "COMPARE_CODE", "RPC_BENCH_CORPUS_METHOD", "RPC_BENCH_CORPUS_TRACER", "RPC_BENCH_CORPUS_TRACE_TYPES", "PREPARED_CAPTURE", "FAIL_PREPARED_ACTION" };
var original = changedNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
try
{
    Environment.SetEnvironmentVariable("RPC_BENCH_SCRIPT_ROOT", scriptRoot); Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + original["PATH"]);
    Environment.SetEnvironmentVariable("SWEEP_CAPTURE", Path.Combine(tests.DirectoryPath, "capture")); Environment.SetEnvironmentVariable("GITHUB_STEP_SUMMARY", Path.Combine(tests.DirectoryPath, "summary"));
    foreach (var client in new[] { "nethermind-flat", "reth", "geth" }) Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "snapshots", client + "-1"));
    int Run(Dictionary<string, string> values) => new SweepRunner(new SweepConfiguration(name => values.GetValueOrDefault(name))).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
    SweepConfiguration PreparedConfiguration()
    {
        var values = EnvironmentValues();
        values["PREPARED_SNAPSHOT_ROOT"] = Path.Combine(tests.DirectoryPath, "prepared-fixture");
        Directory.CreateDirectory(Path.Combine(values["PREPARED_SNAPSHOT_ROOT"], "merged"));
        values["PREPARED_SNAPSHOT_SHA256"] = new string('a', 64);
        values["SNAPSHOT_BLOCK"] = "25490000";
        values["CLIENTS"] = string.Join(' ', SweepConfiguration.PreparedImages.Select(image => "nethermind@" + image + "+" + SweepConfiguration.PreparedFlags.Replace(' ', ';')));
        values["DB_ISOLATION_ALL"] = "overlay";
        values["MIGRATION_LATEST_START_UTC"] = "2026-10-29T10:00:00Z";
        values["MIGRATION_FINISH_BY_UTC"] = "2026-10-29T11:00:00Z";
        values["SCRATCH_ROOT"] = "/mnt/sda/expb-data/rpc-prepared-benchmark-test";
        values["RPS_LIST"] = "1";
        var configuration = new SweepConfiguration(key => values.GetValueOrDefault(key));
        // Substitute owned tiny paths after testing the production namespace constraint.
        configuration.Values["SCRATCH_ROOT"] = Path.Combine(tests.DirectoryPath, "prepared-scratch");
        configuration.Values["OUT_DIR"] = Path.Combine(tests.DirectoryPath, "prepared-out");
        configuration.Values["STATE_ROOT"] = Path.Combine(tests.DirectoryPath, "prepared-state");
        return configuration;
    }
    foreach (var failure in new[] { "", "gate2", "running", "verify" })
        tests.Case("prepared qualification precedes every timed arm " + failure, () =>
        {
            var configuration = PreparedConfiguration();
            var trace = Path.Combine(tests.DirectoryPath, "prepared-trace-" + failure);
            var previousCapture = Environment.GetEnvironmentVariable("SWEEP_CAPTURE");
            Environment.SetEnvironmentVariable("SWEEP_CAPTURE", trace + "-starts");
            Environment.SetEnvironmentVariable("PREPARED_CAPTURE", trace);
            Environment.SetEnvironmentVariable("FAIL_PREPARED_ACTION", failure);
            try
            {
                var result = new SweepRunner(configuration).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
                RpcScriptTests.Assert(failure.Length == 0 && result == 0, "unqualified prepared sweep succeeded");
            }
            catch (IOException) when (failure.Length > 0) { }
            var rows = File.ReadAllLines(trace).Select(line => JsonNode.Parse(line)!["action"]!.ToString()).ToArray();
            if (failure.Length == 0)
            {
                var firstCell = Array.IndexOf(rows, "cell");
                RpcScriptTests.Assert(firstCell >= 0 && rows.Take(firstCell).Count(action => action == "gate") == 2, "timing preceded both-image qualification");
                RpcScriptTests.Assert(rows.Count(action => action == "running") == 2 && rows[^1] == "verify", "per-arm gates missing");
            }
            else RpcScriptTests.Assert(!rows.Contains("cell"), "failed prepared gate reached timing");
            Environment.SetEnvironmentVariable("PREPARED_CAPTURE", null);
            Environment.SetEnvironmentVariable("FAIL_PREPARED_ACTION", null);
            Environment.SetEnvironmentVariable("SWEEP_CAPTURE", previousCapture);
        });
    tests.Case("prepared startup failure stops the persisted owned child before aborting", () =>
    {
        var configuration = PreparedConfiguration();
        configuration.Values["STATE_ROOT"] = Path.Combine(tests.DirectoryPath, "prepared-start-failure-state");
        var trace = Path.Combine(tests.DirectoryPath, "prepared-start-failure");
        var previousCapture = Environment.GetEnvironmentVariable("SWEEP_CAPTURE");
        Environment.SetEnvironmentVariable("SWEEP_CAPTURE", trace + "-starts");
        Environment.SetEnvironmentVariable("PREPARED_CAPTURE", trace);
        Environment.SetEnvironmentVariable("FAIL_START_CLIENT", "nethermind");
        try { new SweepRunner(configuration).RunAsync(CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("startup failure accepted"); }
        catch (IOException)
        {
            RpcScriptTests.Assert(Directory.GetFiles(configuration["STATE_ROOT"], "node.log", SearchOption.AllDirectories).Length > 0, "owned startup state not torn down");
            RpcScriptTests.Assert(!File.ReadAllText(trace).Contains("cell", StringComparison.Ordinal), "failed startup reached timing");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PREPARED_CAPTURE", null);
            Environment.SetEnvironmentVariable("FAIL_START_CLIENT", null);
            Environment.SetEnvironmentVariable("SWEEP_CAPTURE", previousCapture);
        }
    });
    foreach (var (name, value) in new[] { ("PREPARED_SNAPSHOT_SHA256", ""), ("PREPARED_SNAPSHOT_SHA256", "bad"), ("RECEIPT_HASHES_MIGRATION_TIMEOUT", "1"), ("DB_ISOLATION_ALL", "copy"), ("JB_ETH_CALL_CORPUS", "true"), ("CLIENTS", "nethermind"), ("SCRATCH_ROOT", "/mnt/sda/expb-data/rpc-prepared"), ("MIGRATION_FINISH_BY_UTC", "2026-10-30T05:47:34Z"), ("SNAPSHOT_BLOCK", "1"), ("NETWORK", "other") })
        tests.Case("prepared mode rejects incompatible " + name + value, () =>
        {
            var values = PreparedConfiguration().Values;
            values["SCRATCH_ROOT"] = "/mnt/sda/expb-data/rpc-prepared-benchmark-test";
            values[name] = value;
            try { _ = new SweepConfiguration(key => values.GetValueOrDefault(key)); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("unsafe prepared configuration accepted");
        });
    tests.Case("latest-start cutoff rejects queued run before scratch or node work", () =>
    {
        var values = EnvironmentValues();
        values["RECEIPT_HASHES_MIGRATION_TIMEOUT"] = "14400";
        values["MIGRATION_LATEST_START_UTC"] = "2000-01-01T00:00:00Z";
        values["MIGRATION_FINISH_BY_UTC"] = "2000-01-02T00:00:00Z";
        try { Run(values); }
        catch (IOException exception) when (exception.Message.Contains("latest-start cutoff", StringComparison.Ordinal))
        {
            RpcScriptTests.Assert(!Directory.Exists(values["SCRATCH_ROOT"]) && !File.Exists(Environment.GetEnvironmentVariable("SWEEP_CAPTURE")!), "expired queue entry touched scratch/node");
            return;
        }
        throw new InvalidOperationException("expired queue entry accepted");
    });
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
    tests.Case("missing migration completion aborts timing and still tears down", () =>
    {
        var values = EnvironmentValues();
        values["RECEIPT_HASHES_MIGRATION_TIMEOUT"] = "1"; values["RPS_LIST"] = "1";
        values["STATE_ROOT"] = Path.Combine(tests.DirectoryPath, "gated-state");
        values["OUT_DIR"] = Path.Combine(tests.DirectoryPath, "gated-out");
        try { Run(values); }
        catch (IOException exception) when (exception.Message.Contains("did not positively complete", StringComparison.Ordinal))
        {
            RpcScriptTests.Assert(Directory.GetFiles(values["STATE_ROOT"], "node.log", SearchOption.AllDirectories).Length == 1, "gate failure skipped teardown");
            RpcScriptTests.Assert(!Directory.Exists(Path.Combine(values["OUT_DIR"], "mix")), "requests were timed before migration completion");
            return;
        }
        throw new InvalidOperationException("unqualified migration reached timing");
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
    foreach (var phases in new[] { false, true })
        foreach (var sampling in new[] { false, true })
            tests.Case($"generic phase order and sampling phases={phases} sampling={sampling}", () =>
            {
                var values = EnvironmentValues();
                var output = Path.Combine(tests.DirectoryPath, $"generic-{phases}-{sampling}");
                values["OUT_DIR"] = output; values["RPS_LIST"] = "10 20"; values["ISO_CONFIGS"] = "iso.yaml"; values["JB_SEED"] = "12";
                values["GENERIC_FIRST_USE"] = phases ? "true" : "false"; values["GENERIC_WARMUP_DURATION"] = phases ? "3s" : "0";
                values["GENERIC_RESOURCE_SAMPLING"] = sampling ? "true" : "false";
                values["CORPUS_RESOURCE_SAMPLING"] = "false";
                var capture = output + ".jsonl"; Environment.SetEnvironmentVariable("CELL_CAPTURE", capture);
                try
                {
                    RpcScriptTests.Assert(Run(values) == 0, "generic sweep failed");
                    var rows = File.ReadAllLines(capture).Select(line => JsonNode.Parse(line)!).ToArray();
                    string Phase(JsonNode row) => row["JB_CAPTURE_ONLY"]!.ToString() == "true" ? "first-use" : row["JB_STRICT_WARMUP"]!.ToString() == "true" ? "warmup" : "measured";
                    RpcScriptTests.Assert(rows.Select(Phase).SequenceEqual(phases ? ["first-use", "warmup", "measured", "first-use", "warmup", "measured", "warmup", "measured", "warmup", "measured"] : ["measured", "measured", "measured", "measured"]), "phase order/repeated first-use changed");
                    RpcScriptTests.Assert(rows.Select(row => row["OUT_DIR"]!.ToString()).Distinct().Count() == rows.Length, "phase outputs collided");
                    foreach (var row in rows)
                    {
                        var measured = Phase(row) == "measured";
                        RpcScriptTests.Assert((row["RESOURCE_SAMPLER_CONTAINER"]!.ToString().StartsWith("rpcbench-sweep-nethermind-", StringComparison.Ordinal)) == (measured && sampling), "node sampler interval leaked");
                        RpcScriptTests.Assert(row["JB_DEEP_CHECK"]!.ToString() == (measured ? "true" : "false"), "extra capture contaminated warmup");
                        if (Phase(row) == "warmup") RpcScriptTests.Assert(row["JB_MAX_FAIL_RATE_PCT"]!.ToString() == "0" && row["JB_SEED"]!.ToString() == "1012" && row["JB_RPS"]!.ToString() == "25" && row["JB_DURATION"]!.ToString() == "3s", "warmup is not identical and strict");
                    }
                    var summaries = File.ReadAllLines(Path.Combine(output, "summaries.manifest"));
                    RpcScriptTests.Assert(summaries.Length == 4 && summaries.All(line => !line.Contains("/phases/", StringComparison.Ordinal)), "premeasurement summary leaked into measured matrix");
                }
                finally { Environment.SetEnvironmentVariable("CELL_CAPTURE", null); }
            });
    foreach (var phase in new[] { "JB_CAPTURE_ONLY", "JB_STRICT_WARMUP" })
        tests.Case("generic phase failure aborts measurement and tears down " + phase, () =>
        {
            var values = EnvironmentValues(); values["RPS_LIST"] = "10"; values["GENERIC_FIRST_USE"] = "true"; values["GENERIC_WARMUP_DURATION"] = "1s";
            values["OUT_DIR"] = Path.Combine(tests.DirectoryPath, phase); values["STATE_ROOT"] = values["OUT_DIR"] + "-state";
            Environment.SetEnvironmentVariable("FAIL_PHASE", phase);
            try
            {
                try { Run(values); }
                catch (IOException exception) when (exception.Message.Contains("measured cells aborted", StringComparison.Ordinal))
                {
                    RpcScriptTests.Assert(!Directory.Exists(Path.Combine(values["OUT_DIR"], "mix")) && Directory.GetFiles(values["STATE_ROOT"], "node.log", SearchOption.AllDirectories).Length == 1, "failed phase timed requests or skipped teardown");
                    return;
                }
                throw new InvalidOperationException("failed generic phase accepted");
            }
            finally { Environment.SetEnvironmentVariable("FAIL_PHASE", null); }
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
