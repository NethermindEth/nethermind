#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include ci-workflow-tests.helpers.cs
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Workflow fixtures require Linux Bash.");
using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("run-expb-reproducible-benchmarks.yml");
string Body(string name) => CiWorkflow.Bodies(workflow, name).Single();
var resolver = Body("Resolve branch and configuration");
var analyzer = Body("Analyze benchmark output");
var text = File.ReadAllText(Path.Combine(workflow.Root, ".github/workflows/run-expb-reproducible-benchmarks.yml"));
const string automated = """[{"id":"fusaka","payload_set":"fusaka","amount":"1000","delay_seconds":"0"},{"id":"fusaka-delay1s","payload_set":"fusaka","amount":"100","delay_seconds":"1"}]""";
void Require(bool condition, string message) => RpcScriptTests.Assert(condition, message);
void Contains(string value, params string[] fragments) { foreach (var fragment in fragments) Require(value.Contains(fragment, StringComparison.Ordinal), "missing " + fragment + " in " + value); }
(int Code, Dictionary<string, string> Values, string Log) Run(string body, Dictionary<string, string> environment, string? directory = null)
{
    var output = tests.Write("outputs", "");
    environment["GITHUB_OUTPUT"] = output;
    var result = CiWorkflow.Run(body, environment, directory ?? workflow.Root);
    return (result.Code, CiWorkflow.Outputs(output), result.Output + result.Error);
}
(int Code, Dictionary<string, string> Values, string Log) Resolve(params (string Key, string Value)[] overrides)
{
    var environment = new Dictionary<string, string>
    {
        ["EVENT_NAME"] = "workflow_dispatch", ["PUSH_BRANCH"] = "feature/test-client", ["DISPATCH_CLIENT"] = "nethermind",
        ["DISPATCH_MEASUREMENT_SOURCE"] = "auto", ["DISPATCH_STATE_LAYOUT"] = "flat", ["DISPATCH_ARCH"] = "amd64",
        ["DISPATCH_PAYLOAD_SET"] = "fusaka", ["DISPATCH_EXPB_REPO"] = "NethermindEth/execution-payloads-benchmarks", ["DISPATCH_EXPB_BRANCH"] = "main",
        ["DISPATCH_DELAY_SECONDS"] = "0", ["DISPATCH_AMOUNT"] = "25", ["DISPATCH_ADDITIONAL_EXTRA_FLAGS"] = "", ["DISPATCH_FLAT_WRITE_BUFFER_FLOOR"] = "off",
        ["DISPATCH_EXPB_ENV"] = "", ["DISPATCH_REBUILD_DOCKER"] = "true", ["DISPATCH_RUN_COUNT"] = "1", ["DISPATCH_DOCKER_IMAGES"] = "vendor/client:latest",
        ["DISPATCH_ENABLE_RETROSPECTIVE"] = "false", ["DISPATCH_RETROSPECTIVE_LAST"] = "100", ["DISPATCH_RETROSPECTIVE_STEP"] = "10",
        ["DISPATCH_DOTTRACE"] = "false", ["DISPATCH_PERF"] = "false", ["DISPATCH_TRACE_BLOCKS"] = "", ["DISPATCH_CLIENT_ENV"] = "", ["DISPATCH_DOCKER_CPU_OVERRIDES"] = ""
    };
    foreach (var (key, value) in overrides) environment[key] = value;
    return Run(resolver, environment);
}
tests.Case("campaign checkpoint selection outputs reach the driver", () =>
{
    Contains(text, "      payload_set: ${{ steps.resolve.outputs.payload_set }}");
    var (code, values, log) = Resolve(("DISPATCH_PAYLOAD_SET", "fusaka"), ("DISPATCH_STATE_LAYOUT", "flat"));
    Require(code == 0 && values["payload_set"] == "fusaka", log);
    var step = text.Split("      - name: Run sequential EXPB campaign\n", StringSplitOptions.None)[1];
    var settings = step[..step.IndexOf("        run: |\n", StringComparison.Ordinal)];
    Contains(Body("Run sequential EXPB campaign"), "dotnet run --file \"${GITHUB_WORKSPACE}/scripts/expb/SequentialDriver.cs\"");
    foreach (var (name, output) in new[] { ("STATE_LAYOUT", "state_layout"), ("PAYLOAD_SET", "payload_set"), ("FLAT_SNAPSHOT_BLOCK_DIR", "flat_snapshot_block_dir") })
        Contains(settings, "          " + name + ": ${{ needs.resolve.outputs." + output + " }}");
});
tests.Case("dispatch input count", () => Require(((YamlMappingNode)WorkflowTest.Get(WorkflowTest.Get(WorkflowTest.Get(workflow.Document, "on"), "workflow_dispatch"), "inputs")!).Children.Count <= 25, "GitHub dispatch input limit exceeded"));
foreach (var (packed, cpu, cpuset, infra) in new[] { ("", "", "", ""), ("cpu=16; cpuset=0-15  infra_cpuset=0-1,8-9", "16", "0-15", "0-1,8-9"), ("CPU=0", "all", "", ""), ("cpuset=ALL", "all", "all", "") })
    tests.Case("CPU override " + packed, () =>
    {
        var result = Resolve(("DISPATCH_DOCKER_CPU_OVERRIDES", packed));
        Require(result.Code == 0, result.Log);
        Require(result.Values["docker_cpu"] == cpu && result.Values["docker_cpuset"] == cpuset && result.Values["docker_infra_cpuset"] == infra, "packed CPU overrides changed");
    });
foreach (var packed in new[] { "cores=4", "cpu=1.5", "cpuset=2-7:10", "cpu=8 cpuset=all" })
    tests.Case("invalid CPU override " + packed, () => { var result = Resolve(("DISPATCH_DOCKER_CPU_OVERRIDES", packed)); Require(result.Code != 0, "accepted malformed overrides"); Contains(result.Log, "docker_cpu_overrides"); });
foreach (var arch in new[] { "amd64", "arm64" })
    tests.Case("reth snapshot " + arch, () =>
    {
        var result = Resolve(("DISPATCH_CLIENT", "reth"), ("DISPATCH_ARCH", arch));
        Require(result.Code == 0, result.Log);
        Require(result.Values["client"] == "reth" && result.Values["measurement_source"] == "engine-api" && result.Values["snapshot_mount_path"] == "/execution-data" && result.Values["rebuild_docker"] == "false", "reference client timing/policy");
        Require(result.Values["client_snapshot_dir"] == (arch == "arm64" ? "/data/reth/reth-25490000" : "/mnt/sda/reth-25490000"), "reference snapshot");
        if (arch == "arm64") Require(result.Values["runner_label"] == "reproducible-benchmarks-arm", "ARM runner");
    });
foreach (var (client, key, value, message) in new[] { ("reth", "DISPATCH_STATE_LAYOUT", "halfpath", "requires state_layout=flat"), ("geth", "DISPATCH_STATE_LAYOUT", "halfpath", "requires state_layout=flat"), ("geth", "DISPATCH_ARCH", "arm64", "client=geth requires arch=amd64"), ("reth", "DISPATCH_PAYLOAD_SET", "realblocks", "payload_set=fusaka only"), ("reth", "DISPATCH_DOCKER_IMAGES", "", "requires explicit docker_images") })
    tests.Case($"reject {client} {key}={value}", () => { var result = Resolve(("DISPATCH_CLIENT", client), (key, value)); Require(result.Code != 0, "unsupported client configuration accepted"); Contains(result.Log, message); });
foreach (var eventName in new[] { "push", "pull_request" })
    tests.Case("automated cells " + eventName, () =>
    {
        var result = Resolve(("EVENT_NAME", eventName), ("PUSH_BRANCH", "master"), ("PR_LABEL", "performance is good"), ("PR_HEAD_BRANCH", "perf/example"), ("PR_HEAD_REPO", "NethermindEth/nethermind"), ("CURRENT_REPO", "NethermindEth/nethermind"));
        Require(result.Code == 0, result.Log);
        Require(!result.Values.ContainsKey("payload_sets") && JsonNode.DeepEquals(JsonNode.Parse(result.Values["cells"]), JsonNode.Parse(automated)), "automated cells differ");
        if (eventName == "pull_request") Require(result.Values["should_run"] == "true", "performance label ignored");
    });
foreach (var (payload, delay, amount, expectedAmount) in new[] { ("fusaka", "1", "25", "25"), ("fusaka", "0.25", "25", "25"), ("superblocks", "0", "", "100") })
    tests.Case($"dispatch cell {payload} delay={delay}", () =>
    {
        var result = Resolve(("DISPATCH_PAYLOAD_SET", payload), ("DISPATCH_DELAY_SECONDS", delay), ("DISPATCH_AMOUNT", amount), ("DISPATCH_DOCKER_IMAGES", ""));
        Require(result.Code == 0, result.Log);
        var cells = JsonNode.Parse(result.Values["cells"])!.AsArray();
        Require(cells.Count == 1 && cells[0]!["id"]!.ToString() == payload && cells[0]!["payload_set"]!.ToString() == payload && cells[0]!["delay_seconds"]!.ToString() == delay && cells[0]!["amount"]!.ToString() == expectedAmount, "dispatch cell changed");
    });
foreach (var (key, value) in new[] { ("DELAY_SECONDS", "1s"), ("DELAY_SECONDS", "-1"), ("DELAY_SECONDS", "1x5"), ("AMOUNT", "10\"0"), ("AMOUNT", "abc"), ("AMOUNT", "0") })
    tests.Case($"invalid {key}={value}", () =>
    {
        var result = Resolve(("DISPATCH_" + key, value));
        Require(result.Code != 0 && !result.Values.ContainsKey("cells"), "invalid numeric input produced cells");
        Contains(result.Log, key == "AMOUNT" ? "amount must be a positive whole number of payloads" : "delay_seconds must be a non-negative number of seconds");
    });
tests.Case("cell-specific cache keys", () =>
{
    Require(!text.Contains("matrix.payload_set", StringComparison.Ordinal), "old matrix field");
    Contains(text, "cell: ${{ fromJson(needs.resolve.outputs.cells) }}", "Restore master metrics (fusaka)", "Restore master metrics (fusaka-delay1s)");
    var keys = MetricsCacheRegex().Matches(text);
    Require(keys.Count > 0 && keys.All(key => key.Value.Contains("-run-", StringComparison.Ordinal)), "baseline restore prefix can collide");
});
foreach (var (floor, extra, expected) in new[] { ("67108864", "", "FlatDb.CompactionOffset=0"), ("67108864", "--FlatDb.CompactionOffset=5", "FlatDb.CompactionOffset=5"), ("off", "", "") })
    tests.Case("nethermind floor and offset " + floor + extra, () =>
    {
        var result = Resolve(("DISPATCH_FLAT_WRITE_BUFFER_FLOOR", floor), ("DISPATCH_ADDITIONAL_EXTRA_FLAGS", extra));
        Require(result.Code == 0, result.Log);
        Require(result.Values["client"] == "nethermind" && result.Values["measurement_source"] == "auto" && result.Values["client_snapshot_dir"] == "" && result.Values["snapshot_mount_path"] == "", "default timing/snapshot changed");
        var flags = result.Values["additional_extra_flags"];
        Require(CompactionOffsetRegex().Matches(flags).Count == (expected.Length == 0 ? 0 : 1), "caller offset duplicated or floor-off altered");
        if (expected.Length > 0) Contains(flags, expected, "FlatDb.PersistenceWriteBufferFloor=67108864");
    });
foreach (var images in new[] { " , , ", "a/x:latest,b/x:latest,a/x:latest" })
    tests.Case("image matrix " + images, () =>
    {
        var result = Run(Body("Resolve Docker images"), new() { ["DOCKER_IMAGES"] = images, ["LAST"] = "100", ["STEP"] = "10", ["RUN_COUNT"] = "1" });
        if (images.Trim().StartsWith(',')) { Require(result.Code != 0 && !result.Values.ContainsKey("images"), "empty images accepted"); Contains(result.Log, "must contain at least one non-empty image reference"); return; }
        Require(result.Code == 0, result.Log);
        var rows = JsonNode.Parse(result.Values["images"])!.AsArray();
        Require(rows.Select(row => row!["image"]!.ToString()).SequenceEqual(images.Split(',')) && rows.All(row => row!["tag"]!.ToString() == "latest"), "full image references lost");
        var ids = rows.Select(row => row!["id"]!.ToString()).ToArray();
        Require(ids.Distinct().Count() == 3 && ids.Select((id, index) => ImageIdRegex().IsMatch(id) && id.StartsWith($"image-{index + 1}-", StringComparison.Ordinal)).All(valid => valid), "duplicate tags share ids");
    });
var installers = CiWorkflow.Bodies(workflow, "Install or upgrade expb").Concat(CiWorkflow.Bodies(workflow, "Install or upgrade expb and yq")).ToArray();
Require(installers.Length == 2, "both EXPB install paths required");
foreach (var (installer, index) in installers.Select((body, index) => (body, index)))
    foreach (var failure in new[] { false, true })
        tests.Case($"capability probe installer={index} failure={failure}", () =>
        {
            var mock = Path.Combine(tests.DirectoryPath, "expb-mock");
            var observed = Path.Combine(tests.DirectoryPath, "args");
            CiWorkflow.Executable(mock, "printf '%s\\n' \"$@\" > \"$MOCK_ARGS\"\n[[ $* == 'execute-scenarios --perf --no-client-metrics --help' ]] || exit 3\n[[ $MOCK_FAIL_HELP != true ]] || { echo 'No such option: --no-client-metrics' >&2; exit 2; }");
            var probe = installer[installer.IndexOf("requested_flags=()", StringComparison.Ordinal)..installer.IndexOf("echo \"$(uv tool dir --bin)\" >> \"${GITHUB_PATH}\"", StringComparison.Ordinal)];
            var result = Run("expb_bin=\"${MOCK_EXPB}\"\nexpb_source=mock-source\n" + probe, new() { ["PERF"] = "true", ["MEASUREMENT_SOURCE"] = "engine-api", ["MOCK_EXPB"] = mock, ["MOCK_ARGS"] = observed, ["MOCK_FAIL_HELP"] = failure.ToString().ToLowerInvariant() });
            Require((result.Code != 0) == failure && File.ReadAllLines(observed).SequenceEqual(["execute-scenarios", "--perf", "--no-client-metrics", "--help"]), "capability probe arguments/status changed");
            if (failure) Contains(result.Log, "Installed expb from mock-source does not support requested capability flag(s): --perf --no-client-metrics.");
        });
tests.Case("PR comment compares each cell with its own baseline", () =>
{
    var metrics = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "metrics"));
    var cache = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "cache"));
    foreach (var (cell, average) in new[] { ("fusaka", "40.0"), ("fusaka-delay1s", "30.0") })
    {
        var directory = Directory.CreateDirectory(Path.Combine(metrics.FullName, $"expb-single-metrics-{cell}-run1"));
        File.WriteAllText(Path.Combine(directory.FullName, "expb-metrics.env"), $"SOURCE=k6\nAVG={average}\nMEDIAN={average}\n");
    }
    var baseline = Directory.CreateDirectory(Path.Combine(cache.FullName, "expb-master-metrics-cache-fusaka-delay1s"));
    File.WriteAllText(Path.Combine(baseline.FullName, "master-metrics.env"), "SOURCE=k6\nAVG=33.0\nMEDIAN=33.0\n");
    var result = Run(Body("Build PR comparison comment"), new() { ["METRICS_DIR"] = metrics.FullName, ["MASTER_CACHE_ROOT"] = cache.FullName,
        ["CELLS"] = automated, ["STATE_LAYOUT"] = "flat", ["CLEAN_BRANCH"] = "perf-example", ["IMAGE"] = "nethermindeth/nethermind:perf-example",
        ["IMAGE_REVISION"] = new string('a', 40), ["RUN_URL"] = "https://example.invalid/run", ["CLIENT"] = "nethermind", ["RUNNER_TEMP"] = tests.DirectoryPath });
    Require(result.Code == 0, result.Log);
    var comment = result.Values["body"];
    Contains(comment, "#### fusaka: 1000 payloads, 0 s delay", "#### fusaka: 100 payloads, 1 s delay", "nethermind-flat-fusaka-perf-example-delay0s", "nethermind-flat-fusaka-perf-example-delay1s", "No cached master baseline for `fusaka`.", "| Avg | 33.0 | 30.0 | -9% |");
    Require(!comment.Contains("No cached master baseline for `fusaka-delay1s`.", StringComparison.Ordinal), "wrong missing-baseline cell");
});
foreach (var (rows, source, amount, feed, expectedCode, expectedMessage) in new[]
{
    (3, "engine-api", "3", -1, 0, ""), (2, "engine-api", "3", -1, 1, "refusing a partial engine-api measurement"),
    (2, "auto", "3", -1, 0, ""), (3, "auto", "3", 3, 0, ""), (3, "auto", "3", 2, 0, ""), (3, "auto", "3", 1, 0, ""),
    (2, "engine-api", "", -1, 0, "completeness check is disabled"), (2, "engine-api", "all", -1, 0, "completeness check is disabled"),
    (0, "engine-api", "3", -1, 1, "Could not extract any processing_ms data")
})
    tests.Case($"analyze rows={rows} source={source} amount={amount} feed={feed}", () =>
    {
        var directory = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, Guid.NewGuid().ToString("N")));
        var log = string.Concat(Enumerable.Range(0, Math.Max(0, feed)).Select(index => $"[payload-server] client_metric block_number={100 + index} processing_ms=20\n"))
            + string.Concat(Enumerable.Range(1, rows).Select(index => $"| {index} | 30000000 | {20 + index}.0 |\n"));
        var logPath = Path.Combine(directory.FullName, "run.log");
        File.WriteAllText(logPath, log.Length > 0 ? log : "nothing parseable here\n");
        var result = Run("set +e\n" + analyzer, new() { ["RAW_RUN_LOG"] = logPath, ["RUNNER_TEMP"] = directory.FullName, ["TAG"] = "latest-0", ["RUN"] = "1",
            ["MEASUREMENT_SOURCE"] = source, ["MEASUREMENT_MODE"] = "standard", ["EXPECTED_AMOUNT"] = amount, ["GITHUB_STEP_SUMMARY"] = Path.Combine(directory.FullName, "summary"), ["LC_ALL"] = "C.UTF-8" });
        Require((result.Code == 0) == (expectedCode == 0), result.Log);
        if (expectedMessage.Length > 0) Contains(result.Log, expectedMessage);
        var metricPath = Path.Combine(directory.FullName, feed >= 0 ? "expb-metrics.env" : "expb-metrics-latest-0-run1/metrics.env");
        var metrics = File.Exists(metricPath) ? File.ReadAllText(metricPath) : "";
        if (expectedCode != 0 && metrics.Length > 0) Contains(metrics, rows == 0 ? "ERROR=no_metrics" : "ERROR=partial_engine_api_metrics");
        if (feed >= 0)
        {
            Contains(metrics, "SOURCE=sse", "TTFB_MGAS_S=");
            Require(("\n" + metrics).Contains("\nMGAS_S=", StringComparison.Ordinal) == (feed >= 2) && metrics.Contains("OUTSIDE_AVG=", StringComparison.Ordinal) == (feed >= 2), "incomplete feed paired with k6");
        }
    });
foreach (var (client, child, expected) in new[] { ("reth", "absent", "does not exist on this runner"), ("reth", "", "does not contain the db directory"), ("geth", "", "does not contain chaindata contents"), ("geth", "db", "does not contain chaindata contents"), ("reth", "db", ""), ("geth", "chaindata", "") })
    tests.Case($"snapshot preflight {client}/{child}", () =>
    {
        var directory = Path.Combine(tests.DirectoryPath, Guid.NewGuid().ToString("N"));
        if (child != "absent") Directory.CreateDirectory(Path.Combine(directory, child));
        if (child.Length > 0 && child != "absent") File.WriteAllText(Path.Combine(directory, "_snapshot_metadata.json"), "{\"head\":25490000}");
        var result = Run(Body("Verify client snapshot and provenance"), new() { ["CLIENT"] = client, ["SNAPSHOT_SOURCE"] = child == "absent" ? "" : directory, ["SNAPSHOT_MOUNT_PATH"] = "/snapshot" });
        Require((result.Code == 0) == (expected.Length == 0), result.Log);
        if (expected.Length > 0) Contains(result.Log, expected);
        else Contains(result.Log, "Snapshot provenance: client=" + client, "_snapshot_metadata.json");
    });
var yq = Environment.GetEnvironmentVariable("YQ") ?? CiWorkflow.Run("command -v yq", [], workflow.Root).Output.Trim();
if (yq.Length == 0) Console.WriteLine("SKIP resource rendering: Mike Farah yq is unavailable (set YQ).");
else foreach (var (cpu, cpuset, infra, expectedCpuSet, expectedInfra) in new[] { ("", "", "", "2,3,4,5,10,11,12,13", "0-1,8-9"), ("12", "0-15", "0-1", "0,1,2,3,4,5,8,9,10,11,12,13", "0-1"), ("all", "all", "all", "", ""), ("all", "", "", "2-7,10-15", "0-1,8-9") })
    tests.Case($"render resource overrides {cpu}/{cpuset}/{infra}", () =>
    {
        var directory = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, Guid.NewGuid().ToString("N")));
        var topology = Path.Combine(directory.FullName, "cpu");
        for (var index = 0; index < 16; index++)
        {
            var core = Directory.CreateDirectory(Path.Combine(topology, $"cpu{index}/topology"));
            File.WriteAllText(Path.Combine(core.FullName, "thread_siblings_list"), $"{index % 8},{index % 8 + 8}\n");
        }
        const string original = "resources:\n  cpu: 8\n  cpuset: \"2-7,10-15\"\n  infra_cpuset: \"0-1,8-9\"\n  mem: 64g\nscenarios:\n  nethermind:\n    amount: 10\n";
        var source = Path.Combine(directory.FullName, "source.yaml");
        var rendered = Path.Combine(directory.FullName, "rendered.yaml");
        File.WriteAllText(source, original);
        var render = Body("Render benchmark config");
        var result = Run(render[render.IndexOf("sed \\", StringComparison.Ordinal)..render.IndexOf("scenario_key=\"${SCENARIO_NAME}\"", StringComparison.Ordinal)],
            new() { ["YQ"] = yq, ["SOURCE_CONFIG_FILE"] = source, ["RENDERED_CONFIG_FILE"] = rendered, ["DOCKER_TAG"] = "test", ["DELAY_SECONDS"] = "0", ["AMOUNT"] = "10",
                ["EXPB_DATA_DIR"] = "/data/expb-data", ["FLAT_SNAPSHOT_DIR"] = "/data/snapshot", ["FLAT_SNAPSHOT_BLOCK_DIR"] = "/data/snapshot-block", ["SCENARIO_NAME"] = "test",
                ["CPU_TOPOLOGY_DIR"] = topology, ["DOCKER_CPU"] = cpu, ["DOCKER_CPUSET"] = cpuset, ["DOCKER_INFRA_CPUSET"] = infra });
        Require(result.Code == 0 && File.ReadAllText(source) == original, result.Log);
        var parsed = CiWorkflow.Run(CiWorkflow.Quote(yq) + " -o=json .resources " + CiWorkflow.Quote(rendered), [], workflow.Root);
        Require(parsed.Code == 0, parsed.Error);
        var resources = JsonNode.Parse(parsed.Output)!;
        Require(resources["cpu"]!.GetValue<int>() == 0 && resources["mem"]!.ToString() == "64g" && (resources["cpuset"]?.ToString() ?? "") == expectedCpuSet && (resources["infra_cpuset"]?.ToString() ?? "") == expectedInfra, "resource limits differ");
    });
tests.Complete();

partial class Program
{
    [GeneratedRegex(@"^image-[0-9]+-[0-9a-f]{12}$")]
    private static partial Regex ImageIdRegex();
    [GeneratedRegex(@"expb-master-metrics-v\d+-.*")]
    private static partial Regex MetricsCacheRegex();
    [GeneratedRegex("CompactionOffset")]
    private static partial Regex CompactionOffsetRegex();
}
