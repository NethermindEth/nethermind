#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include script-process.helpers.cs
using System.Text.Json.Nodes;

var buildDirectory = Directory.CreateTempSubdirectory("hive-results-build-");
try
{
    var build = await ScriptProcess.Run("dotnet", ["build", FindEntryPoint(), "--output", buildDirectory.FullName], CancellationToken.None);
    if (build.Code != 0) throw new InvalidOperationException(build.Output + build.Error);
    var executable = Path.Combine(buildDirectory.FullName, "hive-results.dll");
    var launch = "client launch (nethermind)";
    var failing = "known failing (nethermind)";
    var flaky = "flaky (nethermind)";
    var tests = 0;
    var failures = new List<string>();
    foreach (var scenario in new[]
    {
        ("missing_result_files", "engine", Array.Empty<(string, bool)>(), 1, "Hive ran no test cases."),
        ("empty_suite", "engine", Array.Empty<(string, bool)>(), 1, "Hive ran no test cases."),
        ("launch_only", "engine", new[] { (launch, true) }, 1, "Hive ran no test cases."),
        ("failed_launch_only", "engine", new[] { (launch, false) }, 1, "Hive ran no test cases."),
        ("passing_run", "engine", new[] { (launch, true), ("test_a", true), (failing, false) }, 0, "Unexpected fails: 0"),
        ("unexpected_failure", "engine", new[] { (launch, true), ("test_a", false) }, 1, "Unexpected fails: 1"),
        ("unexpected_pass", "engine", new[] { (launch, true), (failing, true) }, 1, "Unexpected passes: 1"),
        ("failed_launch", "rpc-compat", new[] { (launch, false), ("test_a", true) }, 1, "Unexpected fails: 1"),
        ("listed_launch_pass", "graphql", new[] { (launch, true), ("test_a", true) }, 1, "Unexpected passes: 1"),
        ("eth_launch_ignored", "eth", new[] { (launch, false), (failing, false) }, 0, "Unexpected fails: 0"),
        ("snap_launch_ignored", "snap", new[] { (launch, false), (failing, false) }, 0, "Unexpected fails: 0"),
        ("snap2_launch_ignored", "snap2", new[] { (launch, false), (failing, false) }, 0, "Unexpected fails: 0"),
        ("eth_case_counted", "eth", new[] { (launch, true), ("test_a", false) }, 1, "Unexpected fails: 1"),
        ("snap_failed_launch_only", "snap", new[] { (launch, false) }, 1, "Unexpected fails: 1"),
        ("eth_failed_launch_only", "eth", new[] { (launch, false) }, 1, "\u001b[31m✖\u001b[0m client launch (nethermind)"),
        ("flaky_pass", "engine", new[] { (flaky, true) }, 0, "\u001b[33m✔ flaky (nethermind) (flaky)\u001b[0m"),
        ("flaky_fail", "engine", new[] { (flaky, false) }, 0, "\u001b[33m✖ flaky (nethermind) (flaky)\u001b[0m"),
        ("sync_prefix", "sync", new[] { (failing, false) }, 0, "sync/known failing (nethermind)"),
        ("snapsync_prefix", "snapsync", new[] { (failing, false) }, 0, "snapsync/known failing (nethermind)"),
        ("no_other_prefix", "ethereum/sync", new[] { (failing, false) }, 0, "\u001b[90m✖ known failing (nethermind)\u001b[0m"),
        ("exact_known_match", "engine", new[] { (failing + " extra", false) }, 1, "Unexpected fails: 1"),
        ("case_sensitive_known_match", "engine", new[] { (failing.ToUpperInvariant(), false) }, 1, "Unexpected fails: 1"),
        ("multiple_files", "engine", new[] { ("test_a", true) }, 0, "Passed 2:"),
        ("explicit_paths", "engine", new[] { ("test_a", true) }, 0, "Passed 2:"),
        ("array_cases", "engine", new[] { ("test_a", true) }, 0, "Passed 1:"),
        ("sort_names", "engine", new[] { ("zulu", true), ("Alpha", true), ("beta", true) }, 0, "\u001b[32m✔\u001b[0m Alpha\n\u001b[32m✔\u001b[0m beta\n\u001b[32m✔\u001b[0m zulu"),
        ("null_summary_ignored", "engine", Array.Empty<(string, bool)>(), 1, "Hive ran no test cases."),
        ("missing_pass_ignored", "engine", Array.Empty<(string, bool)>(), 1, "Hive ran no test cases."),
        ("malformed_json", "engine", new[] { ("test_a", true) }, 1, ""),
        ("missing_known_file", "engine", new[] { ("test_a", true) }, 1, ""),
    })
    {
        var (name, suite, cases, expectedCode, expectedOutput) = scenario;
        var directory = Directory.CreateTempSubdirectory("hive-results-tests-");
        try
        {
            var (scripts, logs) = Prepare(directory, listLaunch: name == "listed_launch_pass");
            var entries = cases.Select(test => (JsonNode)new JsonObject { ["name"] = test.Item1, ["summaryResult"] = new JsonObject { ["pass"] = test.Item2 } }).ToArray();
            var testCases = name == "array_cases" ? (JsonNode)new JsonArray(entries)
                : new JsonObject(entries.Select((test, index) => KeyValuePair.Create(index.ToString(), (JsonNode?)test)));
            var json = new JsonObject { ["name"] = suite, ["testCases"] = testCases }.ToJsonString();
            if (name == "missing_pass_ignored")
                json = "{\"name\":\"engine\",\"testCases\":{\"1\":{\"name\":\"pending\",\"summaryResult\":{}}}}";
            if (name == "null_summary_ignored")
                json = "{\"name\":\"engine\",\"testCases\":{\"1\":{\"name\":\"pending\",\"summaryResult\":null}}}";
            if (name != "missing_result_files")
                File.WriteAllText(Path.Combine(logs, "first.json"), json);
            // A passing sibling suite.
            if (name == "snap_failed_launch_only")
                File.WriteAllText(Path.Combine(logs, "eth.json"), "{\"name\":\"eth\",\"testCases\":{\"1\":{\"name\":\"test_a\",\"summaryResult\":{\"pass\":true}}}}");
            if (name is "multiple_files" or "explicit_paths")
                File.WriteAllText(Path.Combine(logs, "second.json"), json);
            if (name == "malformed_json")
                File.WriteAllText(Path.Combine(logs, "broken.json"), "invalid");
            if (name == "missing_known_file")
                File.Delete(Path.Combine(scripts, "known-failing-hive-tests.txt"));
            var arguments = name == "explicit_paths"
                ? new[] { "hive/workspace/logs/first.json", "hive/workspace/logs/second.json" }
                : ["hive/workspace/logs/*.json"];
            var (code, output, error) = await ScriptProcess.Run("dotnet", [executable, .. arguments], CancellationToken.None,
                workingDirectory: directory.FullName);
            if (code != expectedCode || !output.Replace("\r\n", "\n").Contains(expectedOutput, StringComparison.Ordinal) ||
                (name is "malformed_json" or "missing_known_file" && error.Length == 0))
                Fail(name, $"expected exit {expectedCode} and '{expectedOutput}', got {code}\n{output}\n{error}");
            else if (name == "malformed_json" && !error.Contains("broken.json", StringComparison.Ordinal))
                Fail(name, "malformed JSON must identify its source file");
            else
                Pass(name);
        }
        finally { directory.Delete(recursive: true); }
    }

    // Scenarios with several suite files, per-case Hive flags, simulator logs and options.
    foreach (var scenario in new Scenario[]
    {
        // A failed devp2p tool wrapper counts unless a failed sub-test of the same suite file explains it.
        new("eth_launch_fails_after_passes", [new("eth", [Failed(launch), Passed("test_a")])], 1, ["Unexpected fails: 1"]),
        new("snap_launch_fails_after_passes", [new("snap", [Failed(launch), Passed("test_a")])], 1, ["Unexpected fails: 1"]),
        new("snap2_launch_fails_after_passes", [new("snap2", [Failed(launch), Passed("test_a")])], 1, ["Unexpected fails: 1"]),
        new("eth_launch_next_to_flaky_pass", [new("eth", [Failed(launch), Passed(flaky)])], 1, ["Unexpected fails: 1"]),
        new("eth_launch_explained_by_flaky", [new("eth", [Failed(launch), Failed(flaky)])], 0, ["Unexpected fails: 0"]),
        new("eth_launch_explained_once", [new("eth", [Failed(launch), Failed("test_a")])], 1, ["Unexpected fails: 1"]),
        new("eth_launch_passes", [new("eth", [Passed(launch), Passed("test_a")])], 0, ["\u001b[32m✔\u001b[0m client launch (nethermind)", "Unexpected fails: 0"]),
        new("eth_launch_terminated_next_to_known", [new("eth", [Terminated(launch), Failed(failing)])], 1, ["\u001b[31m✖ client launch (nethermind) (terminated by Hive)\u001b[0m", "Unexpected fails: 1"]),
        new("rpc_launch_next_to_known", [new("rpc-compat", [Failed(launch), Failed(failing)])], 1, ["Unexpected fails: 1"]),
        new("discv4_wrapper_explained", [new("discv4", [Failed("nethermind"), Failed(failing)])], 0, ["Unexpected fails: 0"]),
        new("discv5_wrapper_explained", [new("discv5", [Failed("nethermind"), Failed(failing)])], 0, ["Unexpected fails: 0"]),
        new("discv5_wrapper_fails_after_passes", [new("discv5", [Failed("nethermind"), Passed("test_a")])], 1, ["\u001b[31m✖\u001b[0m nethermind", "Unexpected fails: 1"]),
        // A failed sub-test in another file, of the same suite, explains nothing, whichever file is read first.
        new("eth_launch_sibling_read_after", [new("eth", [Failed(launch), Passed("test_a")], "a.json"), new("eth", [Failed(failing)], "b.json")], 1, ["Unexpected fails: 1"]),
        new("eth_launch_sibling_read_before", [new("eth", [Failed(failing)], "a.json"), new("eth", [Failed(launch), Passed("test_a")], "b.json")], 1, ["Unexpected fails: 1"]),
        // Every suite file needs a case other than its launch case or devp2p wrapper, whatever the other suites ran.
        new("launch_only_next_to_results", [new("engine", [Passed(launch)]), new("rpc-compat", [Passed("test_a")])], 1, ["Suite 'engine' ran no test cases.", "Unexpected fails: 0"]),
        new("empty_suite_next_to_results", [new("engine", []), new("rpc-compat", [Passed("test_a")])], 1, ["Suite 'engine' ran no test cases."]),
        new("pending_only_next_to_results", [new("engine", [new("test_b", null, false)]), new("rpc-compat", [Passed("test_a")])], 1, ["Suite 'engine' ran no test cases."]),
        new("same_suite_empty_and_valid", [new("engine", [], "a.json"), new("engine", [Passed("test_a")], "b.json")], 1, ["Suite 'engine' ran no test cases."]),
        // As in a real job, where file names start with a timestamp: the suite without cases comes after the results.
        new("launch_only_after_results", [new("rpc-compat", [Passed("test_a")], "1-a.json"), new("engine", [Passed(launch)], "2-b.json")], 1, ["Suite 'engine' ran no test cases."]),
        new("sync_launch_only", [new("sync", [Passed(launch)]), new("rpc-compat", [Passed("test_a")])], 1, ["Suite 'sync' ran no test cases."]),
        new("discv4_wrapper_only", [new("discv4", [Passed("nethermind")]), new("eth", [Passed(launch), Passed("test_a")])], 1, ["Suite 'discv4' ran no test cases."]),
        new("nethermind_case_outside_discovery", [new("engine", [Passed("nethermind")])], 0, ["Unexpected fails: 0"]),
        new("suites_all_with_cases", [new("discv5", [Passed("nethermind"), Passed("test_b")]), new("eth", [Passed(launch), Passed("test_a")])], 0, ["Passed 4:", "Unexpected fails: 0"]),
        // A devp2p wrapper without a result leaves the tool's outcome unknown, even next to an explaining failure.
        new("eth_launch_pending", [new("eth", [new(launch, null, false), Passed("test_a")])], 1, ["Suite 'eth' has no result for 'client launch (nethermind)'."]),
        new("discv5_wrapper_pending_next_to_known", [new("discv5", [new("nethermind", null, false), Failed(failing)])], 1, ["Suite 'discv5' has no result for 'nethermind'.", "Unexpected fails: 0"]),
        // Every simulator log must be named by a suite file with cases.
        new("simulator_without_suites", [new("engine", [Passed("test_a")], SimLog: "1-simulator-a.log")], 1, ["Simulator log '2-simulator-b.log' has no suite with test cases."], Logs: ["1-simulator-a.log", "2-simulator-b.log"]),
        new("simulator_with_launch_only", [new("engine", [Passed("test_a")], SimLog: "1-simulator-a.log"), new("graphql", [Passed(launch)], SimLog: "2-simulator-b.log")], 1, ["Simulator log '2-simulator-b.log' has no suite with test cases."], Logs: ["1-simulator-a.log", "2-simulator-b.log"]),
        new("first_simulator_without_suites", [new("engine", [Passed("test_a")], SimLog: "2-simulator-b.log")], 1, ["Simulator log '1-simulator-a.log' has no suite with test cases."], Logs: ["1-simulator-a.log", "2-simulator-b.log"]),
        new("simulators_all_reported", [new("engine", [Passed("test_a")], SimLog: "1-simulator-a.log"), new("rpc-compat", [Passed("test_a")], SimLog: "2-simulator-b.log")], 0, ["Unexpected fails: 0"], Logs: ["1-simulator-a.log", "2-simulator-b.log"]),
        new("simulator_shared_by_suites", [new("discv4", [Passed("nethermind"), Passed("test_b")], SimLog: "1-simulator-a.log"), new("eth", [Passed(launch), Passed("test_a")], SimLog: "1-simulator-a.log")], 0, ["Unexpected fails: 0"], Logs: ["1-simulator-a.log"]),
        // Hive's exit status: 0 is a finished run, 1 needs a failed case, anything else was stopped or failed.
        new("hive_exit_1_without_failures", [new("engine", [Passed("test_a")])], 1, ["Hive did not finish: exit status 1 without a failed case. The results are incomplete.", "Unexpected fails: 0"], Options: ["--hive-exit-code", "1"]),
        new("hive_exit_1_with_flaky_failure", [new("engine", [Passed("test_a"), Failed(flaky)])], 0, ["Unexpected fails: 0"], Options: ["--hive-exit-code", "1"], Absent: ["did not finish"]),
        new("hive_exit_timeout", [new("engine", [Passed("test_a")])], 1, ["Hive did not finish: exit status 124. The results are incomplete.", "Unexpected fails: 0"], Options: ["--hive-exit-code", "124"]),
        new("hive_exit_killed", [new("engine", [Passed("test_a")])], 1, ["Hive did not finish: exit status 137."], Options: ["--hive-exit-code", "137"]),
        new("hive_exit_tests_failed", [new("engine", [Passed("test_a"), Failed(failing)])], 0, ["Unexpected fails: 0"], Options: ["--hive-exit-code", "1"], Absent: ["did not finish"]),
        new("hive_exit_success", [new("engine", [Passed("test_a")])], 0, ["Unexpected fails: 0"], Options: ["--hive-exit-code", "0"]),
        new("hive_exit_after_paths", [new("engine", [Passed("test_a")])], 1, ["Hive did not finish: exit status 2."], Options: [], Paths: ["hive/workspace/logs/*.json", "--hive-exit-code", "2"]),
        new("hive_exit_empty", [new("engine", [Passed("test_a")])], 2, ["Usage:"], Options: ["--hive-exit-code", ""]),
        new("hive_exit_not_numeric", [new("engine", [Passed("test_a")])], 2, ["Usage:"], Options: ["--hive-exit-code", "-1"]),
        new("hive_exit_missing_value", [new("engine", [Passed("test_a")])], 2, ["Usage:"], Options: [], Paths: ["hive/workspace/logs/*.json", "--hive-exit-code"]),
        new("hive_exit_twice", [new("engine", [Passed("test_a")])], 2, ["Usage:"], Options: ["--hive-exit-code", "0", "--hive-exit-code", "0"]),
        new("unknown_option", [new("engine", [Passed("test_a")])], 2, ["Usage:"], Options: ["--hive-exit-status", "0"]),
        new("option_without_paths", [new("engine", [Passed("test_a")])], 2, ["Usage:"], Options: ["--hive-exit-code", "0"], Paths: []),
        // A case Hive ended itself did not finish: no list excuses it.
        new("terminated_known_failure", [new("engine", [Terminated(failing), Passed("test_a")])], 1, ["\u001b[31m✖ known failing (nethermind) (terminated by Hive)\u001b[0m", "Unexpected fails: 1"]),
        new("terminated_flaky", [new("engine", [Terminated(flaky), Passed("test_a")])], 1, ["\u001b[31m✖ flaky (nethermind) (terminated by Hive)\u001b[0m", "Unexpected fails: 1"]),
        new("terminated_unlisted", [new("rpc-compat", [Terminated(launch), Passed("test_a")])], 1, ["client launch (nethermind) (terminated by Hive)", "Unexpected fails: 1"]),
        // "timeout": true decides, whatever "pass" says or whether it is there.
        new("timeout_with_pass_true", [new("engine", [Raw("test_b", """{"pass":true,"timeout":true}"""), Passed("test_a")])], 1, ["\u001b[31m✖ test_b (terminated by Hive)\u001b[0m", "Unexpected fails: 1"]),
        new("timeout_with_listed_pass", [new("engine", [Raw(flaky, """{"pass":true,"timeout":true}"""), Passed("test_a")])], 1, ["\u001b[31m✖ flaky (nethermind) (terminated by Hive)\u001b[0m", "Unexpected fails: 1"]),
        new("timeout_with_null_pass", [new("engine", [Raw("test_b", """{"pass":null,"timeout":true}"""), Passed("test_a")])], 1, ["\u001b[31m✖ test_b (terminated by Hive)\u001b[0m", "Unexpected fails: 1"]),
        new("timeout_without_pass", [new("engine", [Raw("test_b", """{"timeout":true}"""), Passed("test_a")])], 1, ["\u001b[31m✖ test_b (terminated by Hive)\u001b[0m", "Unexpected fails: 1"]),
        new("timeout_false", [new("engine", [Raw("test_b", """{"pass":true,"timeout":false}"""), Passed("test_a")])], 0, ["Passed 2:", "Unexpected fails: 0"], Absent: ["terminated by Hive"]),
        // Hive's own time limit exits 1 like failed tests: the run stays red through the cases Hive ended.
        new("incident_shape", [new("discv4", [Passed("nethermind"), Passed("test_a")], SimLog: "1-simulator-a.log"), new("eth", [Terminated(launch), Passed("test_a")], SimLog: "1-simulator-a.log")], 1,
            ["client launch (nethermind) (terminated by Hive)", "Unexpected fails: 1"], Logs: ["1-simulator-a.log"], Options: ["--hive-exit-code", "1"], Absent: ["did not finish"]),
        new("time_limit_next_to_known", [new("engine", [Terminated("test_b"), Passed("test_a"), Failed(failing)])], 1,
            ["\u001b[31m✖ test_b (terminated by Hive)\u001b[0m", "Unexpected fails: 1"], Options: ["--hive-exit-code", "1"], Absent: ["did not finish"]),
        // The outer limit stopped Hive before it wrote anything, or after.
        new("outer_limit_without_files", [], 1, ["Hive ran no test cases.", "Hive did not finish: exit status 124."], Logs: ["1-simulator-a.log"], Options: ["--hive-exit-code", "124"]),
        new("outer_limit_after_files", [new("discv4", [Passed("nethermind"), Passed("test_a")], SimLog: "1-simulator-a.log"), new("eth", [Terminated(launch), Passed("test_a")], SimLog: "1-simulator-a.log")], 1,
            ["client launch (nethermind) (terminated by Hive)", "Hive did not finish: exit status 137."], Logs: ["1-simulator-a.log"], Options: ["--hive-exit-code", "137"]),
    })
    {
        var directory = Directory.CreateTempSubdirectory("hive-results-tests-");
        try
        {
            var (_, logs) = Prepare(directory, listLaunch: false);
            for (var index = 0; index < scenario.Files.Length; index++)
            {
                var file = scenario.Files[index];
                var testCases = new JsonObject();
                for (var test = 0; test < file.Cases.Length; test++)
                {
                    var (testName, passed, terminated, raw) = file.Cases[test];
                    var summary = raw is not null ? JsonNode.Parse(raw) : passed is { } pass ? new JsonObject { ["pass"] = pass } : null;
                    if (terminated && summary is not null)
                        summary["timeout"] = true;
                    testCases[(test + 1).ToString()] = new JsonObject { ["name"] = testName, ["summaryResult"] = summary };
                }
                var suite = new JsonObject { ["name"] = file.Name, ["testCases"] = testCases };
                if (file.SimLog is not null)
                    suite["simLog"] = file.SimLog;
                File.WriteAllText(Path.Combine(logs, file.File ?? $"{index}.json"), suite.ToJsonString());
            }
            foreach (var log in scenario.Logs ?? [])
                File.WriteAllText(Path.Combine(logs, log), "");
            var (code, output, error) = await ScriptProcess.Run("dotnet",
                [executable, .. scenario.Options ?? [], .. scenario.Paths ?? ["hive/workspace/logs/*.json"]], CancellationToken.None,
                workingDirectory: directory.FullName);
            var text = (output + error).Replace("\r\n", "\n");
            var missing = scenario.Output.Where(expected => !text.Contains(expected, StringComparison.Ordinal)).ToArray();
            var unwanted = (scenario.Absent ?? []).Where(absent => text.Contains(absent, StringComparison.Ordinal)).ToArray();
            if (code != scenario.Code || missing.Length > 0 || unwanted.Length > 0)
                Fail(scenario.Name, $"expected exit {scenario.Code} with '{string.Join("', '", scenario.Output)}' and without '{string.Join("', '", scenario.Absent ?? [])}', got {code}\n{output}\n{error}");
            else
                Pass(scenario.Name);
        }
        finally { directory.Delete(recursive: true); }
    }

    var missingArguments = await ScriptProcess.Run("dotnet", [executable], CancellationToken.None);
    if (missingArguments.Code != 2 || !missingArguments.Error.Contains("Usage:", StringComparison.Ordinal))
        Fail("missing_arguments", "missing arguments must report usage");
    else
        Pass("missing_arguments");
    if (failures.Count > 0)
        throw new InvalidOperationException($"{failures.Count} scenario(s) failed: {string.Join(", ", failures)}");
    Console.WriteLine($"Passed {tests} tests.");

    void Pass(string name)
    {
        Console.WriteLine($"PASS {name}");
        tests++;
    }

    void Fail(string name, string message)
    {
        Console.WriteLine($"FAIL {name}: {message}");
        failures.Add(name);
    }

    (string Scripts, string Logs) Prepare(DirectoryInfo directory, bool listLaunch)
    {
        var scripts = Directory.CreateDirectory(Path.Combine(directory.FullName, "scripts"));
        var listedLaunch = listLaunch ? $"{launch}\n" : "";
        File.WriteAllText(Path.Combine(scripts.FullName, "known-failing-hive-tests.txt"), $"# suite\n\n{failing}\nsync/{failing}\nsnapsync/{failing}\n{flaky}\n{listedLaunch}");
        File.WriteAllText(Path.Combine(scripts.FullName, "known-flaky-hive-tests.txt"), $"# suite\n\n{flaky}\n");
        return (scripts.FullName, Directory.CreateDirectory(Path.Combine(directory.FullName, "hive", "workspace", "logs")).FullName);
    }
}
finally { buildDirectory.Delete(recursive: true); }

static TestCase Passed(string name) => new(name, true, false);
static TestCase Failed(string name) => new(name, false, false);
static TestCase Terminated(string name) => new(name, false, true);
static TestCase Raw(string name, string summary) => new(name, null, false, summary);

static string FindEntryPoint()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        var path = Path.Combine(directory.FullName, "scripts", "hive-results.cs");
        if (File.Exists(path))
            return path;
    }
    throw new DirectoryNotFoundException("Run this test from within the Nethermind checkout.");
}

// A case of a suite file: a null Passed writes a summary that Hive has not filled in; Terminated sets Hive's "timeout";
// Summary, when given, is written as the summaryResult as it stands.
readonly record struct TestCase(string Name, bool? Passed, bool Terminated, string? Summary = null);

// A suite file in the logs directory; File defaults to "<index>.json", in the order of the scenario.
sealed record SuiteFile(string Name, TestCase[] Cases, string? File = null, string? SimLog = null);

// Options go before Paths, which default to the logs glob; Logs are empty files created next to the suite files.
// Output must all appear in stdout or stderr, Absent must not.
sealed record Scenario(string Name, SuiteFile[] Files, int Code, string[] Output, string[]? Logs = null, string[]? Options = null, string[]? Paths = null, string[]? Absent = null);
