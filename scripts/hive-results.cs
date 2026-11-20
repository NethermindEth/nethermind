#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false

using System.Globalization;
using System.Text.Json;

// The workflow passes the exit status of the Hive run. 0: Hive finished and no case failed. 1: a case failed, or Hive
// stopped at its own time limit (the cases it ended carry "timeout": true) or on another fatal error. Anything else:
// Hive was stopped from outside or did not run.
int? hiveExitCode = null;
// A limit without a suite part (/<test>) makes Hive start every suite, also those where no test matches.
var allowEmptySuites = false;
var patterns = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--hive-exit-code")
    {
        if (hiveExitCode is not null || ++index == args.Length ||
            !int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var exitCode))
            return Usage();
        hiveExitCode = exitCode;
    }
    else if (args[index] == "--allow-empty-suites")
    {
        if (allowEmptySuites)
            return Usage();
        allowEmptySuites = true;
    }
    else if (args[index].StartsWith("--", StringComparison.Ordinal))
        return Usage();
    else
        patterns.Add(args[index]);
}
if (patterns.Count == 0)
    return Usage();

try
{
    var scripts = Path.Combine(Environment.CurrentDirectory, "scripts");
    var launchTest = "client launch (nethermind)";
    var knownFails = File.ReadAllLines(Path.Combine(scripts, "known-failing-hive-tests.txt")).ToHashSet(StringComparer.Ordinal);
    var knownFlaky = File.ReadAllLines(Path.Combine(scripts, "known-flaky-hive-tests.txt")).ToHashSet(StringComparer.Ordinal);
    var results = new List<(string Name, bool Passed, bool Terminated)>();
    var realCases = 0;
    var emptySuites = new List<string>();
    var unfinishedWrappers = new List<string>();
    // Each simulator run writes one log next to the suite files, and each suite file names its simulator's log.
    var simulatorLogs = new SortedSet<string>(StringComparer.Ordinal);
    var simulatorsWithCases = new HashSet<string>(StringComparer.Ordinal);
    foreach (var argument in patterns)
    {
        var path = Path.GetFullPath(argument, Environment.CurrentDirectory);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            continue;
        simulatorLogs.UnionWith(Directory.GetFiles(directory, "*-simulator-*.log").Select(log => Path.GetFileName(log)));
        foreach (var file in Directory.GetFiles(directory, Path.GetFileName(path)).Order(StringComparer.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var suite = document.RootElement.GetProperty("name").GetString();
                var cases = document.RootElement.GetProperty("testCases");
                var entries = cases.ValueKind == JsonValueKind.Array
                    ? cases.EnumerateArray().ToArray()
                    : cases.EnumerateObject().Select(property => property.Value).ToArray();
                // devp2p runs its test tool inside one case of these suites, which therefore fails with any failed sub-test.
                var wrapper = suite switch
                {
                    "eth" or "snap" or "snap2" => launchTest,
                    "discv4" or "discv5" => "nethermind",
                    _ => null
                };
                var suiteResults = new List<(string Name, bool Passed, bool Terminated)>();
                var suiteCases = 0;
                foreach (var test in entries)
                {
                    var name = test.GetProperty("name").GetString() ?? throw new JsonException("Missing test name.");
                    var hasSummary = test.TryGetProperty("summaryResult", out var summary) && summary.ValueKind != JsonValueKind.Null;
                    // Hive sets "timeout" only on a case it ended itself: at its time limit, on an interrupt or when the
                    // simulator exits. Such a case did not finish, whatever "pass" says or whether it is there at all.
                    var terminated = hasSummary && summary.TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.True;
                    bool passed;
                    if (terminated)
                        passed = false;
                    else if (hasSummary && summary.TryGetProperty("pass", out var pass) && pass.ValueKind is (JsonValueKind.True or JsonValueKind.False))
                        passed = pass.GetBoolean();
                    else
                    {
                        // Without the wrapper's result the tool's exit is unknown, and so is whether its sub-tests all ran.
                        if (name == wrapper)
                            unfinishedWrappers.Add($"Suite '{suite}' has no result for '{wrapper}'.");
                        continue;
                    }
                    // Count on the raw name, before the prefix below, so that no launch case passes for a real one.
                    if (name != launchTest && name != wrapper)
                        suiteCases++;
                    // Sync suites reuse case names, so retain their suite prefix to distinguish failures.
                    suiteResults.Add((suite is "snapsync" or "sync" ? $"{suite}/{name}" : name, passed, terminated));
                }
                // A suite that launched the client and stopped, or ran nothing, must fail even next to suites with results.
                realCases += suiteCases;
                if (suiteCases == 0)
                    emptySuites.Add(suite ?? Path.GetFileName(file));
                else if (document.RootElement.TryGetProperty("simLog", out var simLog) && simLog.ValueKind == JsonValueKind.String)
                    simulatorsWithCases.Add(simLog.GetString()!);
                // Drop the failed wrapper only when a failed sub-test explains it and Hive did not end it: a tool that
                // crashes or hangs after passing sub-tests, or that Hive stops, must still fail the job.
                if (wrapper is not null && suiteResults.Any(result => result.Name != wrapper && !result.Passed))
                    suiteResults.RemoveAll(result => result.Name == wrapper && !result.Passed && !result.Terminated);
                results.AddRange(suiteResults);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new InvalidOperationException($"Cannot process '{file}': {exception.Message}", exception);
            }
        }
    }

    var unexpectedPasses = 0;
    var unexpectedFails = 0;
    foreach (var passed in new[] { true, false })
    {
        var selected = results.Where(result => result.Passed == passed)
            .OrderBy(result => result.Name, StringComparer.OrdinalIgnoreCase).ThenBy(result => result.Name, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"\n{(passed ? "Passed" : "Failed")} {selected.Length}:\n");
        foreach (var (name, _, terminated) in selected)
        {
            var symbol = passed ? "✔" : "✖";
            // A case Hive ended itself did not finish, so no list excuses it.
            if (terminated && !passed)
            {
                unexpectedFails++;
                Console.WriteLine($"\u001b[31m{symbol} {name} (terminated by Hive)\u001b[0m");
            }
            else if (knownFlaky.Contains(name))
                Console.WriteLine($"\u001b[33m{symbol} {name} (flaky)\u001b[0m");
            else if (knownFails.Contains(name) && passed)
            {
                unexpectedPasses++;
                Console.WriteLine($"\u001b[90m{symbol} {name}\u001b[0m");
            }
            else if (!knownFails.Contains(name) && !passed)
            {
                unexpectedFails++;
                Console.WriteLine($"\u001b[31m{symbol}\u001b[0m {name}");
            }
            else
                Console.WriteLine(passed ? $"\u001b[32m{symbol}\u001b[0m {name}" : $"\u001b[90m{symbol} {name}\u001b[0m");
        }
    }

    Console.WriteLine($"\n  Unexpected passes: {unexpectedPasses}");
    Console.WriteLine($"  Unexpected fails: {unexpectedFails}");

    // A simulator can launch the client and stop before running any real cases, or write no suite file at all.
    var problems = new List<string>();
    if (realCases == 0)
        problems.Add("Hive ran no test cases.");
    else if (!allowEmptySuites)
        problems.AddRange(emptySuites.Select(suite => $"Suite '{suite}' ran no test cases."));
    problems.AddRange(unfinishedWrappers);
    problems.AddRange(simulatorLogs.Where(log => !simulatorsWithCases.Contains(log))
        .Select(log => $"Simulator log '{log}' has no suite with test cases."));
    // Any other status means Hive was stopped from outside (124 at the workflow's outer limit, 137 when it had to be
    // killed) or failed to run, so suite files may be missing.
    if (hiveExitCode is not (null or 0 or 1))
        problems.Add($"Hive did not finish: exit status {hiveExitCode}. The results are incomplete.");
    // Status 1 with no failed case is not a run with failed tests: Hive stopped on its time limit while no case was
    // running, or on another fatal error, and the cases it did not reach are missing.
    else if (hiveExitCode == 1 && results.All(result => result.Passed))
        problems.Add("Hive did not finish: exit status 1 without a failed case. The results are incomplete.");
    if (problems.Count > 0)
        Console.WriteLine();
    foreach (var problem in problems)
        Console.WriteLine(problem);
    return unexpectedPasses + unexpectedFails > 0 || problems.Count > 0 ? 1 : 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: hive-results.cs [--hive-exit-code <n>] [--allow-empty-suites] <result-file-or-glob> [...]");
    return 2;
}
