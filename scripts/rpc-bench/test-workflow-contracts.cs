#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include workflow-test.helpers.cs
#:include rpc-script-tests.helpers.cs

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("run-rpc-benchmarks.yml");
var expb = new WorkflowTest("run-expb-reproducible-benchmarks.yml");
string StepText(string name) => WorkflowTest.Text(workflow.Step("benchmark", name), "run");
void Contains(string text, params string[] fragments)
{
    foreach (var fragment in fragments) RpcScriptTests.Assert(text.Contains(fragment, StringComparison.Ordinal), "missing workflow contract: " + fragment);
}
foreach (var file in new[] { "run-a-geth-node.yml", "run-a-single-node-from-branch.yml", "test-assertoor.yml" })
    tests.Case("discovery timestamp precedes dispatch " + file, () =>
    {
        var caller = new WorkflowTest(file);
        var waits = 0;
        foreach (var job in ((YamlMappingNode)WorkflowTest.Get(caller.Document, "jobs")!).Children.Values)
        {
            var captured = false;
            if (WorkflowTest.Get(job, "steps") is not YamlSequenceNode steps) continue;
            foreach (var step in steps.Children)
            {
                var body = WorkflowTest.Text(step, "run");
                if (body.Contains("DISCOVER_SINCE=", StringComparison.Ordinal)) captured = true;
                if (WorkflowTest.Text(step, "uses").StartsWith("benc-uk/workflow-dispatch@", StringComparison.Ordinal) || body.Contains("gh workflow run", StringComparison.Ordinal))
                    RpcScriptTests.Assert(captured, "dispatch timestamp must precede SDK setup and compilation");
                if (body.Contains("wait-for-workflow.cs", StringComparison.Ordinal)) { RpcScriptTests.Assert(captured, "missing discovery floor"); waits++; }
            }
        }
        RpcScriptTests.Assert(waits > 0, "no discovery caller tested");
    });
tests.Case("baseline cache keys include cell fingerprint on lookup and save", () =>
{
    foreach (var step in new[] { "Restore the cached master baseline", "Save the master baseline" })
    {
        var job = workflow.Job("benchmark");
        var candidates = ((YamlSequenceNode)WorkflowTest.Get(job, "steps")!).Children.Where(candidate => WorkflowTest.Text(candidate, "name").Contains(step.StartsWith("Restore", StringComparison.Ordinal) ? "Restore the cached" : "Save", StringComparison.Ordinal) && WorkflowTest.Text(WorkflowTest.Get(candidate, "with"), "key").Contains("rpc-corpus-baseline", StringComparison.Ordinal)).ToArray();
        RpcScriptTests.Assert(candidates.Length > 0, "cache step absent");
        foreach (var candidate in candidates) Contains(WorkflowTest.Text(WorkflowTest.Get(candidate, "with"), "key"), "needs.resolve.outputs.arch", "needs.resolve.outputs.corpus_key", "needs.resolve.outputs.cell_key");
    }
});
tests.Case("all shaping sweep options belong to the cell fingerprint", () =>
{
    var nonShaping = new HashSet<string>(["clients", "corpus_baseline", "rounds", "iso_configs", "iso_duration", "db_isolation_allow_snapshot_mutation", "resource_sampling", "parity_diffs", "max_divergence_indexes", "max_fail_rate_pct"]);
    var exported = ExportedInputRegex().Matches(StepText("Run RPC sweep")).Select(match => match.Groups[1].Value).Where(key => !nonShaping.Contains(key)).ToHashSet();
    var resolve = WorkflowTest.Text(workflow.Step("resolve", "Resolve configuration"), "run");
    var cellKeys = JsonNode.Parse(CellKeysRegex().Match(resolve).Groups[1].Value)!.AsArray().Select(node => node!.ToString()).Where(key => key != "rps_list").ToHashSet();
    RpcScriptTests.Assert(exported.SetEquals(cellKeys), "sweep shape and fingerprint differ");
    var shape = CellShapeRegex().Match(resolve).Value;
    Contains(shape, "cpu_max_freq_khz", "cpuset", "memory", "corpus_method", "trace_call_tracer", "trace_call_types");
});
tests.Case("cached baselines are checked before use and retain metadata", () =>
{
    Contains(StepText("Validate the cached master baseline"), ".schema // empty", ".cell_key // empty", "usable=${usable}", "rm -rf");
    Contains(StepText("Assemble this run as the master baseline"), "cell_key: $cell_key", "cell: $cell");
    var steps = ((YamlSequenceNode)WorkflowTest.Get(workflow.Job("benchmark"), "steps")!).Children.Select(step => WorkflowTest.Text(step, "name")).ToList();
    RpcScriptTests.Assert(steps.IndexOf("Restore the cached master baseline") < steps.IndexOf("Validate the cached master baseline") && steps.IndexOf("Validate the cached master baseline") < steps.IndexOf("Run RPC sweep"), "cache validation ordered too late");
    RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(workflow.Document, "concurrency"), "cancel-in-progress") == "false", "baseline refresh can be starved");
});
tests.Case("image reclamation uses sweep parsing and image-store headroom", () => Contains(StepText("Reclaim root disk before pulling"), "run-rpc-sweep.cs -- --arm-image", "{{.DockerRootDir}}", "have_space 1 /", "have_space \"${MIN_FREE_GB}\" \"${image_roots[@]}\""));
tests.Case("both node instances receive resolved gas cap", () =>
{
    foreach (var name in new[] { "Start node", "Start reference node" }) Contains(WorkflowTest.Text(WorkflowTest.Get(workflow.Step("benchmark", name), "env"), "RPC_GAS_CAP"), "needs.resolve.outputs.rpc_gas_cap");
});
tests.Case("response bound reaches warmup measured cell and sweep", () =>
{
    foreach (var step in new[] { "Warm up node", "Run json-bench benchmark", "Run RPC sweep" }) Contains(StepText(step), "RPC_BENCH_MAX_RESPONSE_BYTES");
});
tests.Case("profile artifacts survive invalid perf captures", () =>
{
    foreach (var job in new[] { "benchmark", "benchmark-multi" })
    {
        foreach (var name in new[] { "Collect and upload profiling artifacts", "Upload profiling artifact" })
        {
            var condition = WorkflowTest.Text(expb.Step(job, name), "if");
            Contains(condition, "always()", "needs.resolve.outputs.dottrace == 'true'", "needs.resolve.outputs.perf == 'true'");
        }
        var collect = WorkflowTest.Text(expb.Step(job, "Collect and upload profiling artifacts"), "run");
        Contains(collect, "validate-folded-profile.cs", "-x '*/perf.data'", "artifact_prefix=\"dottrace\"", "artifact_prefix=\"profiling\"");
        RpcScriptTests.Assert(collect.IndexOf("zip -9r", StringComparison.Ordinal) < collect.IndexOf("if [[ \"${perf_validation_failed}\" == \"true\" ]]", StringComparison.Ordinal), "failed before archiving diagnostics");
    }
    Contains(StepText("Collect perf profile"), "validate-folded-profile.cs", "-x '*/perf.data'");
    Contains(StepText("Verify perf profiling prerequisites"), "start-profilers.cs -- --check-perf");
});
tests.Case("campaign explicit image comparison controls fail-fast", () => Contains(WorkflowTest.Text(WorkflowTest.Get(expb.Step("benchmark-multi", "Run sequential EXPB campaign"), "env"), "CAMPAIGN_FAIL_FAST"), "needs.resolve.outputs.docker_images != ''"));
tests.Case("profilers cover measured load and their artifacts survive failure", () =>
{
    var order = ((YamlSequenceNode)WorkflowTest.Get(workflow.Job("benchmark"), "steps")!).Children.Select(step => WorkflowTest.Text(step, "name")).ToList();
    foreach (var names in new[] { new[] { "Start node", "Warm up node", "Start profilers", "Run json-bench benchmark", "Stop node and verify DB integrity" }, new[] { "Stop node and verify DB integrity", "Collect dotnet-trace", "Upload perf profile", "Upload dotnet-trace" } })
    {
        var positions = names.Select(name => order.IndexOf(name)).ToArray(); RpcScriptTests.Assert(positions.All(position => position >= 0) && positions.SequenceEqual(positions.Order()), "profile phase ordering changed");
    }
    var warmup = workflow.Step("benchmark", "Warm up node");
    Contains(WorkflowTest.Text(warmup, "run"), "${SCRATCH_ROOT}/warmup-cell/single", "JB_MAX_FAIL_RATE_PCT=\"100\"", "::error::warm-up failed with profiling enabled", "::warning::warm-up failed");
    RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(warmup, "env"), "JB_REUSE_PREPARED") == "true", "warmup preparation not reusable");
    Contains(WorkflowTest.Text(workflow.Step("benchmark", "Start profilers"), "if"), "warmup_seconds != '0'", "dotnet_trace == 'true'");
    foreach (var step in new[] { "Collect dotnet-trace", "Upload dotnet-trace" }) Contains(WorkflowTest.Text(workflow.Step("benchmark", step), "if"), "always()", "steps.scripts.outcome == 'success'", "dotnet_trace == 'true'");
    var collect = StepText("Collect perf profile"); Contains(collect, "perf-record*.log");
    RpcScriptTests.Assert(collect.IndexOf("zip -9r", StringComparison.Ordinal) < collect.IndexOf("if [[ \"${perf_validation_failed}\" == \"true\" ]]", StringComparison.Ordinal), "failed perf loses recorder log");
});
tests.Case("all migrated workflow commands have preceding SDK setup", () =>
{
    foreach (var file in new[] { "run-rpc-benchmarks.yml", "run-expb-reproducible-benchmarks.yml", "run-a-geth-node.yml", "run-a-single-node-from-branch.yml", "test-assertoor.yml" })
    {
        var parsed = new WorkflowTest(file);
        foreach (var job in ((YamlMappingNode)WorkflowTest.Get(parsed.Document, "jobs")!).Children.Values)
        {
            var setup = false;
            if (WorkflowTest.Get(job, "steps") is not YamlSequenceNode steps) continue;
            foreach (var step in steps.Children)
            {
                var uses = WorkflowTest.Text(step, "uses");
                setup |= uses.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal) || uses.EndsWith("/.github/actions/setup-dotnet", StringComparison.Ordinal);
                if (WorkflowTest.Text(step, "run").Contains("dotnet run --file", StringComparison.Ordinal)) RpcScriptTests.Assert(setup, file + " invokes app before SDK setup");
            }
        }
    }
});
if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux workflow execution harness"); tests.Complete(); return; }

tests.Case("failed sweep image resolution aborts reclamation without errexit", () =>
{
    var step = StepText("Reclaim root disk before pulling");
    var start = step.IndexOf("sweep_keep=\"\"", StringComparison.Ordinal);
    var end = step.IndexOf("keep_re=", start, StringComparison.Ordinal);
    var result = workflow.Run("set +e\ndotnet() { return 81; }\n" + step[start..end] + "\necho UNSAFE_RECLAIM\n",
        new() { ["KEEP_TOOL_CONFIG"] = "{\"clients\":\"nethermind@local-arm+--Flag\"}" }, tests.DirectoryPath);
    RpcScriptTests.Assert(result.Code == 1 && result.Output.Contains("refusing stale-image reclamation", StringComparison.Ordinal) && !result.Output.Contains("UNSAFE_RECLAIM", StringComparison.Ordinal), result.ToString());
});

Dictionary<string, string> Input(string tool = "jsonbench", string config = "{}") => new()
{
    ["IN_TOOL"] = tool, ["IN_CLIENT"] = "nethermind", ["IN_REFERENCE_CLIENT"] = "none", ["IN_ARCH"] = "amd64", ["IN_DOCKER_IMAGE"] = "", ["IN_BASELINE_IMAGE"] = "cache",
    ["IN_RPS"] = "1", ["IN_REQUESTS"] = "1", ["IN_ROUNDS"] = "1", ["IN_TIMINGS_PASSES"] = "0", ["IN_WARMUP"] = "0", ["IN_CORPUS"] = "eth-call-corpus-test.jsonl.gz",
    ["IN_DURATION"] = "1", ["IN_SNAPSHOT_BLOCK"] = "", ["IN_DOTTRACE"] = "false", ["IN_PERF"] = "false", ["IN_DOTNET_TRACE"] = "false", ["IN_STATE_LAYOUT"] = "flat", ["IN_FLAGS"] = "",
    ["IN_TOOL_CONFIG"] = config, ["IN_NODE_CONFIG"] = "{}", ["IN_DEBUG_TRACE_CALL_CORPUS"] = "false", ["IN_TRACE_CALL_CORPUS"] = "false"
};
foreach (var (config, node, expected) in new[] { ("{}", "{}", "1000000000"), ("{\"eth_call_corpus\":true}", "{}", "1000000000000"), ("{}", "{\"rpc_gas_cap\":2000000000}", "2000000000"), ("{}", "{\"rpc_gas_cap\":999999999999999}", "999999999999999") })
    tests.Case("single node gas cap " + expected, () =>
    {
        var input = Input(config: config); input["IN_NODE_CONFIG"] = node;
        var result = workflow.Resolve(input, tests.DirectoryPath);
        RpcScriptTests.Assert(result.Code == 0 && result.Values["rpc_gas_cap"] == expected, result.Log);
    });
foreach (var cap in new[] { "0", "-1", "1.5", "\"2000000000\"", "true", "false", "[]", "{}", "1000000000000000", "1000000000000000000" })
    tests.Case("invalid gas cap " + cap, () =>
    {
        var input = Input(); input["IN_NODE_CONFIG"] = "{\"rpc_gas_cap\":" + cap + "}";
        var result = workflow.Resolve(input, tests.DirectoryPath); RpcScriptTests.Assert(result.Code != 0 && result.Log.Contains("rpc_gas_cap must be", StringComparison.Ordinal), result.Log);
    });
foreach (var (config, succeeds) in new[] { ("{}", false), ("{\"clients\":[]}", false), ("{\"clients\":\"   \"}", false), ("{\"clients\":\"nethermind@base:image nethermind@candidate:image\"}", true) })
    tests.Case("sweep client resolver " + config, () =>
    {
        var result = workflow.Resolve(Input("jsonbench-sweep", config), tests.DirectoryPath);
        RpcScriptTests.Assert((result.Code == 0) == succeeds, result.Log);
        if (succeeds) RpcScriptTests.Assert(JsonNode.Parse(result.Values["tool_config"])!["clients"]!.ToString() == "nethermind@base:image nethermind@candidate:image", "explicit arms overwritten");
    });
tests.Case("explicit corpus passes replaces default request sizing", () =>
{
    var result = workflow.Resolve(new() { ["IN_TOOL_CONFIG"] = "{\"corpus_passes\":40}" }, tests.DirectoryPath);
    RpcScriptTests.Assert(result.Code == 0, result.Log);
    var config = JsonNode.Parse(result.Values["tool_config"])!;
    RpcScriptTests.Assert(config["corpus_passes"]!.GetValue<int>() == 40 && config["corpus_requests"] is null, "derived requests conflict with explicit passes");
});
foreach (var (flag, expected) in new[] { ("IN_DEBUG_TRACE_CALL_CORPUS", "Eth,Debug"), ("IN_TRACE_CALL_CORPUS", "Eth,Trace"), ("", "Eth") })
    tests.Case("corpus method limits RPC modules " + expected, () =>
    {
        var input = Input(config: "{\"eth_call_corpus\":true}"); if (flag.Length > 0) input[flag] = "true";
        var result = workflow.Resolve(input, tests.DirectoryPath); RpcScriptTests.Assert(result.Code == 0 && result.Values["jsonrpc_modules"] == expected, result.Log);
    });
foreach (var bound in new[] { "0", "-1", "1.5", "\"123\"", "123456" })
    tests.Case("response byte limit " + bound, () =>
    {
        var result = workflow.Resolve(Input(config: "{\"max_response_bytes\":" + bound + "}"), tests.DirectoryPath);
        RpcScriptTests.Assert((result.Code == 0) == (bound == "123456"), result.Log);
    });
foreach (var collector in new[] { "IN_PERF", "IN_DOTTRACE", "IN_DOTNET_TRACE" })
    tests.Case("collector rejects non-Nethermind before runner allocation " + collector, () =>
    {
        var input = Input(); input[collector] = "true"; input["IN_CLIENT"] = "geth";
        var result = workflow.Resolve(input, tests.DirectoryPath); RpcScriptTests.Assert(result.Code != 0, result.Log);
    });
foreach (var (tool, config, reference, succeeds, warmup) in new[] { ("jsonbench", "{\"duration\":\"600s\"}", "none", true, "60"), ("jsonbench", "{\"duration\":\"600s\",\"corpus_warmup_duration\":120}", "none", true, "120"), ("jsonbench", "{\"corpus_warmup_duration\":0}", "none", false, ""), ("flood", "{}", "none", false, ""), ("jsonbench", "{}", "geth", false, "") })
    tests.Case("EventPipe attach requires measured warmup " + tool + config + reference, () =>
    {
        var input = Input(tool, config); input["IN_DOTNET_TRACE"] = "true"; input["IN_REFERENCE_CLIENT"] = reference;
        var result = workflow.Resolve(input, tests.DirectoryPath); RpcScriptTests.Assert((result.Code == 0) == succeeds, result.Log);
        if (succeeds) RpcScriptTests.Assert(result.Values["warmup_seconds"] == warmup && result.Values["dotnet_trace_max_seconds"] == "1200", "attach window or cap changed");
    });
foreach (var (client, snapshot, isolation) in new[] { ("reth", "/data/reth/reth-25490000", "direct"), ("nethermind", "/data/nethermind/nethermind-flat-25490000", "overlay") })
    tests.Case("ARM single node uses own snapshot " + client, () =>
    {
        var input = Input(); input["IN_ARCH"] = "arm64"; input["IN_CLIENT"] = client; input["IN_DOCKER_IMAGE"] = "registry/client:tag";
        var result = workflow.Resolve(input, tests.DirectoryPath);
        RpcScriptTests.Assert(result.Code == 0 && result.Values["db_source"] == snapshot && result.Values["db_isolation"] == isolation && result.Values["image_ref"] == "registry/client:tag", result.Log);
        input["IN_REFERENCE_CLIENT"] = client == "reth" ? "nethermind" : "geth";
        RpcScriptTests.Assert(workflow.Resolve(input, tests.DirectoryPath).Code != 0, "ARM accepted a second node");
    });
foreach (var (clients, image) in new[] { ("nethermind@registry/base:tag#ENV=1 nethermind@registry/pr:head+--Foo=a@b", "registry/pr:head"), ("nethermind@registry/pr:head nethermind@registry/base:tag", "registry/base:tag"), ("nethermind@registry/base:tag reth@registry/reth:latest", "registry/base:tag"), ("reth@registry/reth:first reth@registry/reth:second", "registry/reth:first") })
    tests.Case("ARM sweep image selection " + clients, () =>
    {
        var input = Input("jsonbench-sweep", new JsonObject { ["clients"] = clients }.ToJsonString()); input["IN_ARCH"] = "arm64";
        var result = workflow.Resolve(input, tests.DirectoryPath);
        RpcScriptTests.Assert(result.Code == 0 && result.Values["image_mode"] == "provided" && result.Values["image_ref"] == image, result.Log);
    });
foreach (var tool in new[] { "jsonbench-sweep", "corpus-ab", "corpus-baseline" })
    tests.Case("snapshot block input reaches sweep preset " + tool, () =>
    {
        var input = Input(tool, "{\"clients\":\"nethermind@registry/pr:head\"}"); input["IN_SNAPSHOT_BLOCK"] = "123456";
        var result = workflow.Resolve(input, tests.DirectoryPath);
        RpcScriptTests.Assert(result.Code == 0 && JsonNode.Parse(result.Values["tool_config"])!["snapshot_block"]!.ToString() == "123456", result.Log);
    });
foreach (var payload in new[] { "--filter \"(eth_call|eth_getLogs)\"", "it's (quoted)", "$(touch SENTINEL)", "`touch SENTINEL`" })
    tests.Case("tool config logs as data " + payload, () =>
    {
        var sentinel = Path.Combine(tests.DirectoryPath, "injected");
        var json = new JsonObject { ["extra_args"] = payload.Replace("SENTINEL", sentinel, StringComparison.Ordinal) }.ToJsonString();
        var result = workflow.Run(StepText("Print resolved configuration"), new() { ["TOOL_CONFIG"] = json }, tests.DirectoryPath, new() { ["needs.resolve.outputs.tool_config"] = json });
        RpcScriptTests.Assert(result.Code == 0 && result.Output.Contains("tool_config: " + json, StringComparison.Ordinal) && !File.Exists(sentinel), result.ToString());
    });
var snapshots = Path.Combine(tests.DirectoryPath, "path-snapshots");
foreach (var name in new[] { "nethermind-flat-25490000", "nethermind-flat-25000000", "reth-25490000" }) Directory.CreateDirectory(Path.Combine(snapshots, name));
var parked = Path.Combine(tests.DirectoryPath, "parked"); Directory.CreateDirectory(parked);
Dictionary<string, string> Paths(string tool, string config = "{}")
{
    var scratch = Path.Combine(tests.DirectoryPath, "path-scratch");
    return new() { ["BENCHMARK_TOOL"] = tool, ["DB_SOURCE"] = parked, ["REFERENCE_DB_SOURCE"] = "", ["SCRATCH_ROOT"] = scratch, ["SNAPSHOT_ROOT"] = snapshots, ["TOOL_CONFIG"] = config, ["DIAG_DIR"] = scratch + "/diag", ["BENCH_TEMP"] = scratch + "/rpcbench.test", ["OUT_DIR"] = scratch + "/rpcbench.test/out", ["STATE_DIR"] = scratch + "/rpcbench.test/state" };
}
foreach (var (tool, config, succeeds) in new[] { ("jsonbench", "{}", true), ("jsonbench-sweep", "{}", true), ("jsonbench-sweep", "{\"snapshot_block\":\"25000000\"}", true), ("jsonbench-sweep", "{\"snapshot_block\":\"1\"}", false), ("jsonbench-sweep", "{\"state_layout\":\"halfpath\"}", false), ("jsonbench-sweep", "{\"clients\":\"nethermind+--X=1 reth+--Y=2\"}", true) })
    tests.Case("workflow validates mounted snapshot only " + tool + config, () =>
    {
        var environment = Paths(tool, config);
        if (tool == "jsonbench-sweep") environment["DB_SOURCE"] = Path.Combine(tests.DirectoryPath, "absent");
        var result = workflow.Run(StepText("Validate snapshot and output paths"), environment, tests.DirectoryPath);
        RpcScriptTests.Assert((result.Code == 0) == succeeds && !Directory.Exists(environment["BENCH_TEMP"]), result.ToString());
    });
foreach (var target in new[] { "nethermind-flat-25490000", "reth-25490000" })
    tests.Case("workflow scratch disjoint from every arm " + target, () =>
    {
        var environment = Paths("jsonbench-sweep", "{\"clients\":\"nethermind reth\"}"); environment["SCRATCH_ROOT"] = Path.Combine(snapshots, target);
        RpcScriptTests.Assert(workflow.Run(StepText("Validate snapshot and output paths"), environment, tests.DirectoryPath).Code != 0, "overlapping scratch accepted");
    });
tests.Case("workflow canonicalizes output symlink before overlap check", () =>
{
    var environment = Paths("jsonbench"); Directory.CreateDirectory(environment["SCRATCH_ROOT"]);
    Directory.CreateSymbolicLink(environment["BENCH_TEMP"], parked);
    var result = workflow.Run(StepText("Validate snapshot and output paths"), environment, tests.DirectoryPath);
    RpcScriptTests.Assert(result.Code != 0 && !Directory.EnumerateFileSystemEntries(parked).Any(), result.ToString());
});
tests.Complete();

partial class Program
{
    [GeneratedRegex(@"\b([a-z][a-z_0-9]*):([A-Z][A-Z_0-9]*)\b")]
    private static partial Regex ExportedInputRegex();
    [GeneratedRegex(@"cell_keys='(\[.*?\])'", RegexOptions.Singleline)]
    private static partial Regex CellKeysRegex();
    [GeneratedRegex("cell_shape=\"\\$\\(jq.*?\\)\"", RegexOptions.Singleline)]
    private static partial Regex CellShapeRegex();
}
