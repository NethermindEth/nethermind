#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:package YamlDotNet
#:include ci-workflow-tests.helpers.cs

if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Workflow fixtures require Linux Bash and GNU timeout.");
using var tests = new RpcScriptTests();
var workflow = new WorkflowTest("hive-tests.yml");
var limits = workflow.Step("setup", "Check hive-timeout");
var limitsBody = WorkflowTest.Text(limits, "run");
var run = workflow.Step("test", "Run Hive");
var upload = workflow.Step("test", "Upload results");
var print = workflow.Step("test", "Print results");
const string outerLimit = "--kill-after=1m \"$(( HIVE_TIMEOUT + 10 ))m\"";
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
});

// hive-timeout is a whole number of minutes. The setup job checks it and derives the job timeout, as expressions have no
// arithmetic; Run Hive derives the outer limit from the checked value.
tests.Case("time limits follow hive-timeout", () =>
{
    var input = WorkflowTest.Get(WorkflowTest.Get(WorkflowTest.Get(WorkflowTest.Get(workflow.Document, "on"), "workflow_dispatch"), "inputs"), "hive-timeout");
    RpcScriptTests.Assert(WorkflowTest.Text(input, "type") == "number" && WorkflowTest.Text(input, "default") == "40" &&
        WorkflowTest.Text(WorkflowTest.Get(limits, "env"), "HIVE_TIMEOUT") == "${{ github.event.inputs.hive-timeout || '40' }}" && !limitsBody.Contains("${{", StringComparison.Ordinal),
        "hive-timeout must be a number of minutes that defaults to 40, also without the input, and reach the check through env");
    // The check is the producer of both outputs, and a failed check fails setup, which the image build and the test job need.
    RpcScriptTests.Assert(WorkflowTest.Text(limits, "id") == "limits" && WorkflowTest.Get(limits, "if") is null &&
        WorkflowTest.Get(limits, "continue-on-error") is null && WorkflowTest.Get(workflow.Job("setup"), "continue-on-error") is null &&
        WorkflowTest.Get(workflow.Job("setup"), "if") is null,
        "the check must keep the id 'limits', run unconditionally and fail the setup job on error");
    // Without an if, the image build keeps the default success() condition, so a rejected value stops it.
    RpcScriptTests.Assert(Needs(workflow.Job("build-image")).Contains("setup") && WorkflowTest.Get(workflow.Job("build-image"), "if") is null &&
        Needs(workflow.Job("test")).Contains("setup") && Needs(workflow.Job("test")).Contains("build-image"),
        "the image build must run only after a successful check, and the test job must need both setup and the image build");
    // Run Hive must see the job's checked value: a step-level HIVE_TIMEOUT would override it, and so would an earlier step
    // of the test job that writes it to GITHUB_ENV.
    RpcScriptTests.Assert(WorkflowTest.Get(WorkflowTest.Get(run, "env"), "HIVE_TIMEOUT") is null && !runBody.Contains("HIVE_TIMEOUT=", StringComparison.Ordinal),
        "Run Hive must take HIVE_TIMEOUT from the job env, never override it");
    var testSteps = ((YamlDotNet.RepresentationModel.YamlSequenceNode)WorkflowTest.Get(workflow.Job("test"), "steps")!).Children;
    var earlier = testSteps.TakeWhile(step => !ReferenceEquals(step, run)).ToArray();
    RpcScriptTests.Assert(earlier.Length < testSteps.Count && earlier.All(step => !step.AllNodes.OfType<YamlDotNet.RepresentationModel.YamlScalarNode>()
            .Any(node => node.Value?.Contains("HIVE_TIMEOUT", StringComparison.Ordinal) == true)),
        "no step of the test job before Run Hive may touch HIVE_TIMEOUT");
    // The fixture runs both blocks with plain Bash and the env it supplies; a shell setting could add tracing or an override.
    RpcScriptTests.Assert(WorkflowTest.Get(limits, "shell") is null && WorkflowTest.Get(run, "shell") is null &&
        new YamlDotNet.RepresentationModel.YamlNode[] { workflow.Document, workflow.Job("setup"), workflow.Job("test") }
            .All(node => WorkflowTest.Get(WorkflowTest.Get(WorkflowTest.Get(node, "defaults"), "run"), "shell") is not { } shell ||
                shell is YamlDotNet.RepresentationModel.YamlScalarNode { Value: "bash" }),
        "the check and Run Hive must run in the default Bash shell, with no shell setting of their own and no other default");
    var setup = WorkflowTest.Get(workflow.Job("setup"), "outputs");
    var test = workflow.Job("test");
    RpcScriptTests.Assert(WorkflowTest.Text(setup, "hive-timeout") == "${{ steps.limits.outputs.hive-timeout }}" &&
        WorkflowTest.Text(setup, "job-minutes") == "${{ steps.limits.outputs.job-minutes }}" &&
        WorkflowTest.Text(test, "timeout-minutes") == "${{ fromJSON(needs.setup.outputs.job-minutes) }}" &&
        WorkflowTest.Text(WorkflowTest.Get(test, "env"), "HIVE_TIMEOUT") == "${{ needs.setup.outputs.hive-timeout }}",
        "the test job must take its timeout and Hive's limit from the checked values of the setup job");
    // A step timer on Run Hive is independent of the derived limits: a shorter one can cancel Hive before it writes its status.
    RpcScriptTests.Assert(WorkflowTest.Get(run, "timeout-minutes") is null,
        "Run Hive must leave timeout-minutes unset so the derived limits govern the run");
    RpcScriptTests.Assert(runBody.Contains("timeout " + outerLimit + " ./hive", StringComparison.Ordinal),
        "GNU timeout must send TERM n+10 minutes after Hive starts and KILL one minute later if Hive still runs");

    // Hive's limit n per simulator; GNU timeout sends TERM n + 10 minutes after Hive starts and KILL one minute later if
    // Hive still runs; the job limit is n + 20, within GitHub's 360 minutes. The default matrix entry with two simulators
    // gets the same n for each of them.
    const string combinedSuite = "ethereum/(rpc-compat|sync)";
    RpcScriptTests.Assert(WorkflowTest.Text(((YamlDotNet.RepresentationModel.YamlSequenceNode)WorkflowTest.Get(workflow.Job("setup"), "steps")!).Children.Single(step => WorkflowTest.Text(step, "id") == "matrix"), "run").Contains("{\"suite\":\"" + combinedSuite + "\", \"name\":\"rpc-compat-sync\"}", StringComparison.Ordinal),
        "the default matrix must keep its entry with two simulators and no parallelism of its own");
    foreach (var (minutes, suite) in new[] { (1, "devp2p"), (12, "devp2p"), (40, "devp2p"), (340, "devp2p"), (40, combinedSuite) })
    {
        var root = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "limits-" + minutes + "-" + (suite == "devp2p" ? "devp2p" : "combined")));
        var output = Path.Combine(root.FullName, "github-output");
        File.WriteAllText(output, "");
        var check = CiWorkflow.Run(limitsBody, new() { ["GITHUB_OUTPUT"] = output, ["HIVE_TIMEOUT"] = minutes.ToString() }, root.FullName);
        var values = CiWorkflow.Outputs(output);
        RpcScriptTests.Assert(check.Code == 0 && values.GetValueOrDefault("hive-timeout") == minutes.ToString() &&
            values.GetValueOrDefault("job-minutes") == (minutes + 20).ToString(),
            $"hive-timeout {minutes} must give a job timeout of {minutes + 20}, got {check.Code}: {File.ReadAllText(output)}{check.Output}{check.Error}");

        File.WriteAllText(output, "");
        var arguments = Path.Combine(root.FullName, "timeout-arguments");
        const string fakeTimeout = "timeout() { printf '%s\\n' \"$@\" > \"$TIMEOUT_ARGS\"; }\n";
        var suiteBody = WorkflowTest.Expand(WorkflowTest.Text(run, "run"),
            new() { ["matrix.suite"] = suite, ["matrix.parallelism || env.DEFAULT_PARALLELISM"] = "4" });
        var step = CiWorkflow.Run(fakeTimeout + suiteBody, new()
        {
            ["GITHUB_OUTPUT"] = output, ["HIVE_TIMEOUT"] = values["hive-timeout"], ["LOG_LEVEL"] = "3", ["LIMIT"] = "", ["BUILDARG"] = "",
            ["TIMEOUT_ARGS"] = arguments
        }, root.FullName);
        RpcScriptTests.Assert(step.Code == 0 && File.ReadAllText(arguments) ==
            $"--kill-after=1m\n{minutes + 10}m\n./hive\n--client\nnethermind\n--sim\n{suite}\n--sim.loglevel\n3\n--sim.parallelism\n4\n--sim.timelimit\n{minutes}m\n",
            $"hive-timeout {minutes} must give {suite} {minutes}m per simulator and the outer limit {minutes + 10}m, got {step.Code}: {(File.Exists(arguments) ? File.ReadAllText(arguments) : "")}{step.Error}");
    }

    // Anything else stops the setup job before it writes an output; the shell never evaluates the value. 2^64 + 1 wraps to 1
    // in Bash arithmetic, so it passes the range check unless the digit count is limited first.
    foreach (var value in new[] { "", "0", "341", "999", "1000", "18446744073709551617", "040", "-5", "+5", "40.5", "4e1", "40m", " 40", "40 ", "0x28", "40\n", "x[$(touch evaluated)]" })
    {
        var root = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "limits-invalid-" + Guid.NewGuid().ToString("N")));
        var output = Path.Combine(root.FullName, "github-output");
        File.WriteAllText(output, "");
        var check = CiWorkflow.Run(limitsBody, new() { ["GITHUB_OUTPUT"] = output, ["HIVE_TIMEOUT"] = value }, root.FullName);
        // Only the fixed annotation reaches the log: the rejected value is never printed.
        RpcScriptTests.Assert(check.Code == 1 && File.ReadAllText(output) == "" && !File.Exists(Path.Combine(root.FullName, "evaluated")) &&
            check.Output == "::error::hive-timeout must be a whole number of minutes from 1 to 340.\n" && check.Error == "",
            $"hive-timeout '{value}' must be rejected, got {check.Code}: {File.ReadAllText(output)}{check.Output}{check.Error}");
    }
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
            $"--client\nnethermind\n--sim\ndevp2p\n--sim.loglevel\n3\n--sim.parallelism\n4\n--sim.timelimit\n{hive.Timeout}m\n--sim.limit\neth/GetCells\n",
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

// Only a limit with a test part lets suites without cases pass: Hive then starts every suite that the suite part
// selects, also those where no test matches. Without a test part, every started suite runs all its tests, so a suite
// without cases stays a problem. The test part is the text after the first / outside [...] and (...) and not escaped
// by a backslash, as in Hive's splitRegexp.
const string withEmptySuite = """{"name":"graphql","testCases":{"1":{"name":"client launch (nethermind)","summaryResult":{"pass":true}}}}""";
const string ethHeaders = """{"name":"eth","testCases":{"1":{"name":"GetBlockHeaders","summaryResult":{"pass":true}},"2":{"name":"client launch (nethermind)","summaryResult":{"pass":true}}}}""";
const string snapWrapperOnly = """{"name":"snap","testCases":{"1":{"name":"client launch (nethermind)","summaryResult":{"pass":true}}}}""";
const string ethWrapperOnly = """{"name":"eth","testCases":{"1":{"name":"client launch (nethermind)","summaryResult":{"pass":true}}}}""";
const string reExecute = """{"name":"engine-cancun","simLog":"1-simulator-a.log","testCases":{"1":{"name":"Re-Execute Payload","summaryResult":{"pass":true}}}}""";
const string cancunEmpty = """{"name":"engine-cancun","simLog":"1-simulator-a.log","testCases":{}}""";
tests.Case("empty suites allowed only under a limit with a test part", () =>
{
    var failures = new List<string>();
    var index = 0;
    foreach (var (limit, status, files, code, problem) in new (string, string, string[], int, string?)[]
    {
        ("/Status", "1", [listedFailure, withEmptySuite], 0, null),
        ("eth/Status", "1", [listedFailure, withEmptySuite], 0, null),
        // Hive starts both suites; eth runs the test, snap only its devp2p wrapper.
        ("^(eth|snap)$/^GetBlockHeaders$", "0", [ethHeaders, snapWrapperOnly], 0, null),
        // Only the first / splits: a test pattern that ends in a literal / is still a test part.
        ("^(eth|snap)$/^GetBlockHeaders$|^archive/", "0", [ethHeaders, snapWrapperOnly], 0, null),
        // A test pattern with spaces, as in the default matrix; both files share one simulator log.
        ("engine-cancun/^Re-Execute Payload", "0", [reExecute, cancunEmpty], 0, null),
        ("^(eth|snap)$", "0", [ethHeaders, snapWrapperOnly], 1, "Suite 'snap' ran no test cases."),
        // Empty suite and test patterns select every test of every suite.
        ("/", "0", [ethHeaders, snapWrapperOnly], 1, "Suite 'snap' ran no test cases."),
        ("eth", "1", [listedFailure, withEmptySuite], 1, "Suite 'graphql' ran no test cases."),
        ("eth/", "1", [listedFailure, withEmptySuite], 1, "Suite 'graphql' ran no test cases."),
        ("", "1", [listedFailure, withEmptySuite], 1, "Suite 'graphql' ran no test cases."),
        // A / inside a character class, inside parentheses or escaped does not split the limit, so these have no test part.
        ("^[^/]+$", "1", [listedFailure, withEmptySuite], 1, "Suite 'graphql' ran no test cases."),
        ("^(eth|snap|a/b)$", "0", [ethHeaders, snapWrapperOnly], 1, "Suite 'snap' ran no test cases."),
        (@"eth\/x", "1", [listedFailure, withEmptySuite], 1, "Suite 'graphql' ran no test cases."),
        // Classes and groups nest: the / sits inside the outer one after the inner one closes.
        ("^[[:alpha:]/]+$", "0", [ethHeaders, snapWrapperOnly], 1, "Suite 'snap' ran no test cases."),
        ("^((eth|snap)/x|eth|snap)$", "0", [ethHeaders, snapWrapperOnly], 1, "Suite 'snap' ran no test cases."),
        // The first / outside them splits, also after a class with a /, an unmatched ] (a literal) or a class with a (.
        ("^[a/b]+$/^Status$", "1", [listedFailure, withEmptySuite], 0, null),
        ("]/Status", "1", [listedFailure, withEmptySuite], 0, null),
        ("[(]/Status", "1", [listedFailure, withEmptySuite], 0, null),
        // Hive rejoins the fields after the first split, so the test pattern of eth// is /, not empty.
        ("eth//", "0", [ethHeaders, ethWrapperOnly], 0, null),
        // Under a test part the selected suites must still run a case between them.
        ("^(eth|snap)$/^GetBlockHeaders$", "0", [withEmptySuite, snapWrapperOnly], 1, "Hive ran no test cases."),
    })
    {
        var root = Directory.CreateDirectory(Path.Combine(tests.DirectoryPath, "limit-" + index++));
        var logs = Directory.CreateDirectory(Path.Combine(root.FullName, "hive", "workspace", "logs"));
        var scripts = Directory.CreateDirectory(Path.Combine(root.FullName, "nethermind", "scripts"));
        File.WriteAllText(Path.Combine(scripts.FullName, "known-failing-hive-tests.txt"), "# suite\n\nknown failing (nethermind)\n");
        File.WriteAllText(Path.Combine(scripts.FullName, "known-flaky-hive-tests.txt"), "# suite\n");
        for (var file = 0; file < files.Length; file++)
            File.WriteAllText(Path.Combine(logs.FullName, $"{file + 1}-suite.json"), files[file]);
        if (files.Any(file => file.Contains("1-simulator-a.log", StringComparison.Ordinal)))
            File.WriteAllText(Path.Combine(logs.FullName, "1-simulator-a.log"), "");
        var check = CiWorkflow.Run(resultsShim + printBody, new()
        {
            ["HIVE_STATUS"] = status, ["LIMIT"] = limit, ["HIVE_RESULTS"] = Path.Combine(tests.DirectoryPath, "build", "hive-results.dll")
        }, Path.Combine(root.FullName, "nethermind"));
        if (check.Code != code || (problem is null ? check.Output.Contains("ran no test cases", StringComparison.Ordinal) : !check.Output.Contains(problem, StringComparison.Ordinal)))
            failures.Add($"limit '{limit}' must give exit {code}{(problem is null ? "" : " with '" + problem + "'")}, got {check.Code}: {check.Output}{check.Error}");
    }
    RpcScriptTests.Assert(failures.Count == 0, string.Join("\n", failures));
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

// A job's needs, written as one name or a list.
static string[] Needs(YamlDotNet.RepresentationModel.YamlNode job) => WorkflowTest.Get(job, "needs") switch
{
    YamlDotNet.RepresentationModel.YamlScalarNode name => [name.Value ?? ""],
    YamlDotNet.RepresentationModel.YamlSequenceNode names => [.. names.Children.OfType<YamlDotNet.RepresentationModel.YamlScalarNode>().Select(name => name.Value ?? "")],
    _ => []
};

// A fake Hive run: its script, the expected step status, whether it must see TERM, the suite file the test writes after
// the step (null when the fake writes its own), the expected checker result, the outer limit that replaces the derived one,
// and the hive-timeout value in minutes.
sealed record HiveRun(string Name, string Script, int Status, bool Terminated, string? Suite, int CheckerCode, string[] CheckerOutput,
    string? Absent = null, string? Outer = null, string Timeout = "12");
