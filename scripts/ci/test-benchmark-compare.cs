#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include script-process.helpers.cs

var entry = (string)AppContext.GetData("EntryPointFilePath")!;
var compare = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(entry)!, "../benchmark/compare.cs"));
var temp = Directory.CreateTempSubdirectory("nethermind-benchmark-compare-");
try
{
    var flat = temp.CreateSubdirectory("flat").FullName;
    var grouped = temp.CreateSubdirectory("grouped").FullName;
    var empty = temp.CreateSubdirectory("empty").FullName;
    WriteReport(flat, "RunnerBenchmarks", 10);
    WriteReport(flat, "OwnBenchmarks", 30);
    WriteReport(Path.Combine(grouped, "runner-job"), "RunnerBenchmarks", 20);
    WriteReport(Path.Combine(grouped, "own-job"), "OwnBenchmarks", 40);

    await Verify("flat reports", flat, 0, "| RunnerBenchmarks.Measure | 10.0 ns | 10.0 ns |", "| OwnBenchmarks.Measure | 30.0 ns | 30.0 ns |");
    await Verify("empty head", empty, 1, "_No head results;");
    await Verify("resume reports", grouped, 0, "| RunnerBenchmarks.Measure | 10.0 ns | 20.0 ns |", "| OwnBenchmarks.Measure | 30.0 ns | 40.0 ns |");
    Directory.CreateDirectory(Path.Combine(grouped, "results"));
    await Verify("resume reports with an empty flat results directory", grouped, 0, "| RunnerBenchmarks.Measure | 10.0 ns | 20.0 ns |", "| OwnBenchmarks.Measure | 30.0 ns | 40.0 ns |");
    WriteReport(Path.Combine(flat, "runner-job"), "RunnerBenchmarks", 999);
    await Verify("flat reports take precedence over old resume reports", flat, 0, "| RunnerBenchmarks.Measure | 10.0 ns | 10.0 ns |");
    Console.WriteLine("Passed 5 tests.");

    async Task Verify(string name, string head, int expectedExit, params string[] expectedRows)
    {
        var result = await ScriptProcess.Run("dotnet", ["run", "--file", compare, "--", flat, head], CancellationToken.None);
        if (result.Code != expectedExit || expectedRows.Any(row => !result.Output.Contains(row, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{name}: expected exit {expectedExit}, got {result.Code}.\n{result.Output}{result.Error}");
        Console.WriteLine($"PASS: {name}");
    }
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
