#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include ../HiveResults.Helpers.cs

using System.Diagnostics;
using System.Text.Json;

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
    ("cli_working_directory", "engine", new[] { ("test_a", true) }, 0, "Unexpected fails: 0")
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
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", suite);
            writer.WritePropertyName("testCases");
            if (name == "array_cases") writer.WriteStartArray(); else writer.WriteStartObject();
            for (var index = 0; index < cases.Length; index++)
            {
                if (name != "array_cases") writer.WritePropertyName(index.ToString());
                writer.WriteStartObject();
                writer.WriteString("name", cases[index].Item1);
                writer.WriteStartObject("summaryResult");
                writer.WriteBoolean("pass", cases[index].Item2);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            if (name == "array_cases") writer.WriteEndArray(); else writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
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
        using var output = new StringWriter();
        using var error = new StringWriter();
        var arguments = name == "explicit_paths"
            ? new[] { "hive/workspace/logs/first.json", "hive/workspace/logs/second.json" }
            : ["hive/workspace/logs/*.json"];
        var code = HiveResults.Run(arguments, directory.FullName, output, error);
        if (name == "cli_working_directory")
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory.FullName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            } };
            foreach (var argument in new[] { "run", "--file", FindEntryPoint(), "--", arguments[0] })
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            code = process.ExitCode;
            output.GetStringBuilder().Clear();
            output.Write(await stdout);
            error.Write(await stderr);
        }
        if (code != expectedCode || !output.ToString().Replace("\r\n", "\n").Contains(expectedOutput, StringComparison.Ordinal) ||
            (name is "malformed_json" or "missing_known_file" && error.ToString().Length == 0))
            throw new InvalidOperationException($"{name}: expected exit {expectedCode} and '{expectedOutput}', got {code}\n{output}\n{error}");
        Console.WriteLine($"PASS {name}");
        tests++;
    }
    finally { directory.Delete(recursive: true); }
}
using (var output = new StringWriter())
using (var error = new StringWriter())
{
    if (HiveResults.Run([], Environment.CurrentDirectory, output, error) != 2 || !error.ToString().Contains("Usage:", StringComparison.Ordinal))
        throw new InvalidOperationException("Missing arguments must report usage.");
    Console.WriteLine("PASS missing_arguments");
    tests++;
}
Console.WriteLine($"Passed {tests} tests.");

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
