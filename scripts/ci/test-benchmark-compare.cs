#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:property PublishAot=false
#:include script-process.helpers.cs

const string comparePath = "scripts/benchmark/compare.cs";
var root = new DirectoryInfo(Environment.CurrentDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, comparePath))) root = root.Parent;
var compare = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Run from the repository or one of its subdirectories."), comparePath);
var temp = Directory.CreateTempSubdirectory("nethermind-benchmark-compare-");
try
{
    var flat = temp.CreateSubdirectory("flat").FullName;
    var grouped = temp.CreateSubdirectory("grouped").FullName;
    var empty = temp.CreateSubdirectory("empty").FullName;
    var groupedWithEmptyResults = temp.CreateSubdirectory("grouped-with-empty-results").FullName;
    var mixed = temp.CreateSubdirectory("mixed").FullName;

    WriteReport(flat, "RunnerBenchmarks", 10);
    WriteReport(flat, "OwnBenchmarks", 30);
    WriteReport(Path.Combine(grouped, "runner-job"), "RunnerBenchmarks", 20);
    WriteReport(Path.Combine(grouped, "own-job"), "OwnBenchmarks", 40);

    Directory.CreateDirectory(Path.Combine(groupedWithEmptyResults, "results"));
    WriteReport(Path.Combine(groupedWithEmptyResults, "own-job"), "OwnBenchmarks", 40);
    WriteReport(mixed, "RunnerBenchmarks", 10);
    var mixedGroup = Path.Combine(mixed, "runner-job");
    WriteReport(mixedGroup, "RunnerBenchmarks", 999);
    WriteReport(mixedGroup, "GroupOnlyBenchmarks", 999);

    var cases = new (string Name, string Base, string Head, int ExitCode, string[] Expected, string? Unexpected)[]
    {
        ("flat reports", flat, flat, 0,
            ["| RunnerBenchmarks.Measure | 10.0 ns | 10.0 ns |", "| OwnBenchmarks.Measure | 30.0 ns | 30.0 ns |"], null),
        ("empty head", flat, empty, 1, ["_No head results;"], null),
        ("grouped head reports", flat, grouped, 0,
            ["| RunnerBenchmarks.Measure | 10.0 ns | 20.0 ns |", "| OwnBenchmarks.Measure | 30.0 ns | 40.0 ns |"], null),
        ("grouped base report with an empty flat results directory", groupedWithEmptyResults, flat, 0,
            ["| OwnBenchmarks.Measure | 40.0 ns | 30.0 ns |"], null),
        ("flat reports take precedence over grouped reports", flat, mixed, 0,
            ["| RunnerBenchmarks.Measure | 10.0 ns | 10.0 ns |"], "GroupOnlyBenchmarks.Measure")
    };

    foreach (var (name, @base, head, exitCode, expected, unexpected) in cases)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await ScriptProcess.Run("dotnet", ["run", "--file", compare, "--", @base, head], timeout.Token);
        if (result.Code != exitCode)
            throw new InvalidOperationException($"{name}: expected exit {exitCode}, got {result.Code}.\n{result.Output}{result.Error}");
        foreach (var text in expected)
            if (!result.Output.Contains(text, StringComparison.Ordinal))
                throw new InvalidOperationException($"{name}: missing '{text}'.\n{result.Output}");
        if (unexpected is not null && result.Output.Contains(unexpected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{name}: unexpected '{unexpected}'.\n{result.Output}");
        Console.WriteLine($"PASS: {name}");
    }
    Console.WriteLine($"Passed {cases.Length} tests.");
}
finally
{
    temp.Delete(recursive: true);
}

static void WriteReport(string artifacts, string type, int median)
{
    var directory = Directory.CreateDirectory(Path.Combine(artifacts, "results"));
    File.WriteAllText(Path.Combine(directory.FullName, $"{type}-report-full-compressed.json"), $$"""
        {
            "Benchmarks": [
                {
                    "Type": "{{type}}",
                    "Method": "Measure",
                    "Statistics": { "Mean": {{median}}, "Median": {{median}}, "StandardDeviation": 1 }
                }
            ]
        }
        """);
}
