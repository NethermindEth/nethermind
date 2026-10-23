#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ScriptProcess.Helpers.cs
using System.Text.Json.Nodes;

var buildDirectory = Directory.CreateTempSubdirectory("hive-results-build-");
try
{
    var build = await ScriptProcess.Run("dotnet", ["build", FindEntryPoint(), "--output", buildDirectory.FullName], CancellationToken.None);
    if (build.Code != 0) throw new InvalidOperationException(build.Output + build.Error);
    var executable = Path.Combine(buildDirectory.FullName, "HiveResults.dll");
    var launch = "client launch (nethermind)";
    var failing = "known failing (nethermind)";
    var flaky = "flaky (nethermind)";
    var tests = 0;
    foreach (var scenario in new[]
    {
        ("missing_result_files", "engine", Array.Empty<(string, bool)>(), 1, "Hive ran no test cases."),
        ("empty_suite", "engine", Array.Empty<(string, bool)>(), 1, "Hive ran no test cases."),
        ("launch_only", "engine", new[] { (launch, true) }, 1, "Hive ran no test cases."),
        ("failed_launch_only", "engine", new[] { (launch, false) }, 1, "Hive ran no test cases."),
        ("passing_run", "engine", new[] { (launch, true), ("test_a", true), (failing, false) }, 0, "Unexpected fails: 0"),
        ("unexpected_failure", "engine", new[] { (launch, true), ("test_a", false) }, 1, "Unexpected fails: 1"),
        ("unexpected_pass", "engine", new[] { (launch, true), (failing, true) }, 1, "Unexpected passes: 1"),
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
        ("cli_working_directory", "engine", new[] { ("test_a", true) }, 0, "Unexpected fails: 0"),
        ("cli_checkout_directory", "engine", new[] { ("test_a", true) }, 0, "Unexpected fails: 0")
    })
    {
        var (name, suite, cases, expectedCode, expectedOutput) = scenario;
        var directory = Directory.CreateTempSubdirectory("hive-results-tests-");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(directory.FullName, "nethermind", "scripts"));
            File.WriteAllText(Path.Combine(scripts.FullName, "known-failing-hive-tests.txt"), $"# suite\n\n{failing}\nsync/{failing}\nsnapsync/{failing}\n{flaky}\n{launch}\n");
            File.WriteAllText(Path.Combine(scripts.FullName, "known-flaky-hive-tests.txt"), $"# suite\n\n{flaky}\n");
            var logs = Directory.CreateDirectory(Path.Combine(directory.FullName, "hive", "workspace", "logs"));
            var entries = cases.Select(test => (JsonNode)new JsonObject { ["name"] = test.Item1, ["summaryResult"] = new JsonObject { ["pass"] = test.Item2 } }).ToArray();
            var testCases = name == "array_cases" ? (JsonNode)new JsonArray(entries)
                : new JsonObject(entries.Select((test, index) => KeyValuePair.Create(index.ToString(), (JsonNode?)test)));
            var json = new JsonObject { ["name"] = suite, ["testCases"] = testCases }.ToJsonString();
            if (name == "missing_pass_ignored")
                json = "{\"name\":\"engine\",\"testCases\":{\"1\":{\"name\":\"pending\",\"summaryResult\":{}}}}";
            if (name == "null_summary_ignored")
                json = "{\"name\":\"engine\",\"testCases\":{\"1\":{\"name\":\"pending\",\"summaryResult\":null}}}";
            if (name != "missing_result_files")
                File.WriteAllText(Path.Combine(logs.FullName, "first.json"), json);
            if (name is "multiple_files" or "explicit_paths")
                File.WriteAllText(Path.Combine(logs.FullName, "second.json"), json);
            if (name == "malformed_json")
                File.WriteAllText(Path.Combine(logs.FullName, "broken.json"), "invalid");
            if (name == "missing_known_file")
                File.Delete(Path.Combine(scripts.FullName, "known-failing-hive-tests.txt"));
            var arguments = name == "explicit_paths"
                ? new[] { "hive/workspace/logs/first.json", "hive/workspace/logs/second.json" }
                : [name == "cli_checkout_directory" ? "../hive/workspace/logs/*.json" : "hive/workspace/logs/*.json"];
            var (code, output, error) = await ScriptProcess.Run("dotnet", [executable, .. arguments], CancellationToken.None,
                workingDirectory: name == "cli_checkout_directory" ? Path.Combine(directory.FullName, "nethermind") : directory.FullName);
            if (code != expectedCode || !output.Replace("\r\n", "\n").Contains(expectedOutput, StringComparison.Ordinal) ||
                (name is "malformed_json" or "missing_known_file" && error.Length == 0))
                throw new InvalidOperationException($"{name}: expected exit {expectedCode} and '{expectedOutput}', got {code}\n{output}\n{error}");
            if (name == "malformed_json" && !error.Contains("broken.json", StringComparison.Ordinal))
                throw new InvalidOperationException("Malformed JSON must identify its source file.");
            Console.WriteLine($"PASS {name}");
            tests++;
        }
        finally { directory.Delete(recursive: true); }
    }
    var missing = await ScriptProcess.Run("dotnet", [executable], CancellationToken.None);
    if (missing.Code != 2 || !missing.Error.Contains("Usage:", StringComparison.Ordinal))
        throw new InvalidOperationException("Missing arguments must report usage.");
    Console.WriteLine("PASS missing_arguments");
    Console.WriteLine($"Passed {tests + 1} tests.");
}
finally { buildDirectory.Delete(recursive: true); }

static string FindEntryPoint()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        var path = Path.Combine(directory.FullName, "scripts", "HiveResults.cs");
        if (File.Exists(path))
            return path;
    }
    throw new DirectoryNotFoundException("Run this test from within the Nethermind checkout.");
}
