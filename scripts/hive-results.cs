#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property PublishAot=false

using System.Text.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: hive-results.cs <result-file-or-glob> [...]");
    return 2;
}

try
{
    var scripts = Path.Combine(Environment.CurrentDirectory, "scripts");
    var launchTest = "client launch (nethermind)";
    var knownFails = File.ReadAllLines(Path.Combine(scripts, "known-failing-hive-tests.txt")).ToHashSet(StringComparer.Ordinal);
    var knownFlaky = File.ReadAllLines(Path.Combine(scripts, "known-flaky-hive-tests.txt")).ToHashSet(StringComparer.Ordinal);
    var results = new List<(string Name, bool Passed, bool Terminated)>();
    foreach (var argument in args)
    {
        var path = Path.GetFullPath(argument, Environment.CurrentDirectory);
        var directory = Path.GetDirectoryName(path)!;
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory, Path.GetFileName(path)).Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        foreach (var file in files)
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
                foreach (var test in entries)
                {
                    if (!test.TryGetProperty("summaryResult", out var summary) || summary.ValueKind == JsonValueKind.Null ||
                        !summary.TryGetProperty("pass", out var passed) ||
                        passed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        continue;
                    var name = test.GetProperty("name").GetString() ?? throw new JsonException("Missing test name.");
                    // Hive sets "timeout" only on a case it ended itself: at its time limit, on an interrupt or when the simulator exits.
                    var terminated = summary.TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.True;
                    // Sync suites reuse case names, so retain their suite prefix to distinguish failures.
                    suiteResults.Add((suite is "snapsync" or "sync" ? $"{suite}/{name}" : name, passed.GetBoolean(), terminated));
                }
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
        var names = results.Where(result => result.Passed == passed).Select(result => result.Name)
            .Order(StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"\n{(passed ? "Passed" : "Failed")} {names.Length}:\n");
        foreach (var name in names)
        {
            var symbol = passed ? "✔" : "✖";
            if (knownFlaky.Contains(name))
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

    // A simulator can launch the client and stop before running any real cases.
    if (!results.Any(result => result.Name != launchTest))
    {
        Console.WriteLine("Hive ran no test cases.");
        return 1;
    }

    Console.WriteLine($"\n  Unexpected passes: {unexpectedPasses}");
    Console.WriteLine($"  Unexpected fails: {unexpectedFails}");
    return unexpectedPasses + unexpectedFails > 0 ? 1 : 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
