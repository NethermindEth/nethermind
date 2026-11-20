#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include ci-workflow-tests.helpers.cs

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Workflow fixtures require Linux Bash and GNU timeout.");
using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("hive-tests.yml");
var run = workflow.Step("test", "Run Hive");
var upload = workflow.Step("test", "Upload results");
var print = workflow.Step("test", "Print results");
const string outerLimit = "--kill-after=1m 50m";
var runBody = WorkflowTest.Expand(WorkflowTest.Text(run, "run"),
    new() { ["matrix.suite"] = "devp2p", ["matrix.parallelism || env.DEFAULT_PARALLELISM"] = "4" });
var printBody = WorkflowTest.Text(print, "run");

tests.Case("results steps run after a failed Hive run", () =>
{
    RpcScriptTests.Assert(WorkflowTest.Text(run, "id") == "hive" && WorkflowTest.Text(run, "continue-on-error") == "true",
        "Run Hive must keep its id and continue on error");
    RpcScriptTests.Assert(WorkflowTest.Get(upload, "if") is null && WorkflowTest.Get(print, "if") is null,
        "Upload results and Print results must run whenever Run Hive ran");
    RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(print, "env"), "HIVE_STATUS") == "${{ steps.hive.outputs.status }}",
        "Print results must read the status of Run Hive");
    RpcScriptTests.Assert(WorkflowTest.Text(WorkflowTest.Get(print, "env"), "LIMIT") == "${{ matrix.limit }}" && !printBody.Contains("${{", StringComparison.Ordinal),
        "Print results must read the limit through env, never by interpolation");
    RpcScriptTests.Assert(WorkflowTest.Get(print, "continue-on-error") is null, "a failing results check must fail the job");
    var input = WorkflowTest.Get(WorkflowTest.Get(WorkflowTest.Get(WorkflowTest.Get(workflow.Document, "on"), "workflow_dispatch"), "inputs"), "hive-timeout");
    RpcScriptTests.Assert(WorkflowTest.Text(input, "default") == "40m" &&
        WorkflowTest.Text(WorkflowTest.Get(workflow.Job("test"), "env"), "HIVE_TIMEOUT") == "${{ github.event.inputs.hive-timeout || '40m' }}",
        "Hive's time limit must default to 40 minutes, in the dispatch input and on push and pull requests");
    RpcScriptTests.Assert(runBody.Contains("timeout " + outerLimit + " ./hive", StringComparison.Ordinal),
        "Run Hive must stop Hive with TERM at 50 minutes and KILL a minute later");
    // The outer limit ends Hive by minute 51; the steps around it need the rest of the job's time.
    RpcScriptTests.Assert(int.TryParse(WorkflowTest.Text(workflow.Job("test"), "timeout-minutes"), out var jobMinutes) && jobMinutes >= 51 + 5,
        "the job timeout must leave at least 5 minutes after the outer limit");
});

// The results script, built once and called the way Print results calls it.
var build = ScriptProcess.Run("dotnet", ["build", Path.Combine(workflow.Root, "scripts", "hive-results.cs"), "--output", Path.Combine(tests.DirectoryPath, "build")],
    CancellationToken.None).GetAwaiter().GetResult();
RpcScriptTests.Assert(build.Code == 0, build.Output + build.Error);
var resultsShim = """
    dotnet() {
      if [[ $1 == run && $2 == --file && $3 == scripts/hive-results.cs && $4 == -- ]]; then
        shift 4
        command dotnet "$HIVE_RESULTS" "$@"
      else
        echo "unexpected dotnet call: $*" >&2
        return 99
      fi
    }

    """;
const string listedFailure = """{"name":"engine","testCases":{"1":{"name":"test_a","summaryResult":{"pass":true}},"2":{"name":"known failing (nethermind)","summaryResult":{"pass":false}}}}""";
const string endedLaunch = """{"name":"eth","testCases":{"1":{"name":"test_a","summaryResult":{"pass":true}},"2":{"name":"client launch (nethermind)","summaryResult":{"pass":false,"timeout":true}}}}""";

// The fake Hive records its arguments, then behaves as named. A marker shows that a fake which waits saw TERM.
foreach (var hive in new HiveRun[]
{
    new("hive passes", "exit 0", 0, false, listedFailure, 0, ["Unexpected fails: 0"]),
    // The input reaches Hive as one argument, which Hive then rejects or accepts as a whole.
    new("time limit stays one argument", "exit 0", 0, false, listedFailure, 0, ["Unexpected fails: 0"], Timeout: "1m --sim evil"),
    new("hive reports failed tests", "exit 1", 1, false, listedFailure, 0, ["Unexpected fails: 0"]),
    // Hive's own time limit: the suite file has the case Hive ended, and Hive exits 1.
    new("hive ends the simulation at its time limit", $"printf '%s' '{endedLaunch}' > workspace/logs/1-suite.json\nexit 1", 1, false, null, 1,
        ["\u001b[31m✖ client launch (nethermind) (terminated by Hive)\u001b[0m", "Unexpected fails: 1"], Absent: "did not finish"),
    // The outer limit, shortened to seconds: TERM, then KILL when TERM is ignored. Three seconds give the fake time to
    // set its trap on a loaded runner.
    new("outer limit stops hive", "trap 'echo terminated > \"$MARKER\"; kill \"$child\"; exit 143' TERM\nsleep 30 > /dev/null 2>&1 &\nchild=$!\nwait \"$child\"",
        124, true, listedFailure, 1, ["Hive did not finish: exit status 124."], Outer: "--kill-after=1m 3s"),
    new("outer limit kills hive that ignores TERM", "trap '' TERM\nsleep 10 > /dev/null 2>&1 &\nwait $!",
        137, false, listedFailure, 1, ["Hive did not finish: exit status 137."], Outer: "--kill-after=1s 1s"),
})
    tests.Case(hive.Name, () =>
    {
        var root = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, hive.Name.Replace(' ', '-')));
        var hiveDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "hive"));
        var logs = Directory.CreateDirectory(Path.Combine(hiveDirectory.FullName, "workspace", "logs"));
        var scripts = Directory.CreateDirectory(Path.Combine(root.FullName, "nethermind", "scripts"));
        CiWorkflow.Executable(Path.Combine(hiveDirectory.FullName, "hive"), "printf '%s\\n' \"$@\" > \"$ARGS\"\n" + hive.Script);
        var output = Path.Combine(root.FullName, "github-output");
        File.WriteAllText(output, "");
        var arguments = Path.Combine(root.FullName, "arguments");
        var marker = Path.Combine(root.FullName, "marker");
        var body = hive.Outer is null ? runBody : runBody.Replace(outerLimit, hive.Outer, StringComparison.Ordinal);
        var started = DateTime.UtcNow;
        var step = CiWorkflow.Run(body, new()
        {
            ["GITHUB_OUTPUT"] = output, ["HIVE_TIMEOUT"] = hive.Timeout, ["LOG_LEVEL"] = "3", ["LIMIT"] = "eth/GetCells", ["BUILDARG"] = "",
            ["ARGS"] = arguments, ["MARKER"] = marker
        }, hiveDirectory.FullName);
        RpcScriptTests.Assert(step.Code == hive.Status && CiWorkflow.Outputs(output).GetValueOrDefault("status") == hive.Status.ToString(),
            $"Run Hive must exit with and report status {hive.Status}, got {step.Code}: {File.ReadAllText(output)}{step.Output}{step.Error}");
        RpcScriptTests.Assert(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the outer limit must stop Hive");
        RpcScriptTests.Assert(File.Exists(marker) == hive.Terminated, $"Hive must {(hive.Terminated ? "" : "not ")}see TERM");
        RpcScriptTests.Assert(File.ReadAllText(arguments) ==
            $"--client\nnethermind\n--sim\ndevp2p\n--sim.loglevel\n3\n--sim.parallelism\n4\n--sim.timelimit\n{hive.Timeout}\n--sim.limit\neth/GetCells\n",
            "Hive arguments changed: " + File.ReadAllText(arguments));

        // Print results reads the status from the step output; hive.json is not a suite file and must be removed first.
        File.WriteAllText(Path.Combine(scripts.FullName, "known-failing-hive-tests.txt"), "# suite\n\nknown failing (nethermind)\n");
        File.WriteAllText(Path.Combine(scripts.FullName, "known-flaky-hive-tests.txt"), "# suite\n");
        File.WriteAllText(Path.Combine(logs.FullName, "hive.json"), "{}");
        if (hive.Suite is not null)
            File.WriteAllText(Path.Combine(logs.FullName, "1-suite.json"), hive.Suite);
        var check = CiWorkflow.Run(resultsShim + printBody, new()
        {
            ["HIVE_STATUS"] = CiWorkflow.Outputs(output)["status"], ["LIMIT"] = "", ["HIVE_RESULTS"] = Path.Combine(tests.DirectoryPath, "build", "hive-results.dll")
        }, Path.Combine(root.FullName, "nethermind"));
        RpcScriptTests.Assert(check.Code == hive.CheckerCode && hive.CheckerOutput.All(line => check.Output.Contains(line, StringComparison.Ordinal)) &&
            (hive.Absent is null || !check.Output.Contains(hive.Absent, StringComparison.Ordinal)),
            $"Print results must exit {hive.CheckerCode} with '{string.Join("', '", hive.CheckerOutput)}', got {check.Code}: {check.Output}{check.Error}");
    });

// Only a limit without a suite part (/<test>) lets suites without cases pass: Hive then starts every suite.
const string withEmptySuite = """{"name":"graphql","testCases":{"1":{"name":"client launch (nethermind)","summaryResult":{"pass":true}}}}""";
tests.Case("empty suites allowed only under a limit without a suite part", () =>
{
    foreach (var (limit, code) in new[] { ("/Status", 0), ("eth/Status", 1), ("", 1) })
    {
        var root = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "limit-" + limit.Replace('/', '_')));
        var logs = Directory.CreateDirectory(Path.Combine(root.FullName, "hive", "workspace", "logs"));
        var scripts = Directory.CreateDirectory(Path.Combine(root.FullName, "nethermind", "scripts"));
        File.WriteAllText(Path.Combine(scripts.FullName, "known-failing-hive-tests.txt"), "# suite\n\nknown failing (nethermind)\n");
        File.WriteAllText(Path.Combine(scripts.FullName, "known-flaky-hive-tests.txt"), "# suite\n");
        File.WriteAllText(Path.Combine(logs.FullName, "1-suite.json"), listedFailure);
        File.WriteAllText(Path.Combine(logs.FullName, "2-suite.json"), withEmptySuite);
        var check = CiWorkflow.Run(resultsShim + printBody, new()
        {
            ["HIVE_STATUS"] = "1", ["LIMIT"] = limit, ["HIVE_RESULTS"] = Path.Combine(tests.DirectoryPath, "build", "hive-results.dll")
        }, Path.Combine(root.FullName, "nethermind"));
        RpcScriptTests.Assert(check.Code == code && check.Output.Contains("Suite 'graphql' ran no test cases.", StringComparison.Ordinal) == (code == 1),
            $"limit '{limit}' must give exit {code}, got {check.Code}: {check.Output}{check.Error}");
    }
});

tests.Case("results step fails without a status", () =>
{
    var root = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "no-status"));
    Directory.CreateDirectory(Path.Combine(root.FullName, "nethermind"));
    var check = CiWorkflow.Run(resultsShim + printBody, new() { ["HIVE_STATUS"] = "", ["LIMIT"] = "", ["HIVE_RESULTS"] = Path.Combine(tests.DirectoryPath, "build", "hive-results.dll") },
        Path.Combine(root.FullName, "nethermind"));
    RpcScriptTests.Assert(check.Code == 2 && check.Error.Contains("Usage:", StringComparison.Ordinal), $"an empty status must be a usage error, got {check.Code}: {check.Error}");
});
tests.Complete();

// A fake Hive run: its script, the expected step status, whether it must see TERM, the suite file the test writes after
// the step (null when the fake writes its own), the expected checker result, the outer limit that replaces 50m, and the
// hive-timeout value.
sealed record HiveRun(string Name, string Script, int Status, bool Terminated, string? Suite, int CheckerCode, string[] CheckerOutput,
    string? Absent = null, string? Outer = null, string Timeout = "12m");
