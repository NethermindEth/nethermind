#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include script-process.helpers.cs

var measured = "[{\"Statistics\":{\"Mean\":1}}]";
var cases = new (string Name, string? Measurements, string Log, bool Valid)[]
{
    ("missing report", null, "", false),
    ("empty cases", "[]", "", false),
    ("null statistics", "[{\"Statistics\":null}]", "", false),
    ("measured case", measured, "", true),
    ("invalid block", measured, "Invalid Block", false),
    ("exception handling message", measured, "Exception handling is enabled", true),
    ("IO exception", measured, "System.IO.IOException: disk full", false),
    ("inner exception", measured, " ---> System.InvalidOperationException: setup failed", false),
    ("unhandled exception", measured, "Unhandled exception. System.NullReferenceException: failed", false),
    ("indented exception", measured, "   System.IO.IOException: disk full", false),
    ("numbered inner exception", measured, " ---> (Inner Exception #0) System.IO.IOException: disk full", false),
    ("bare exception", measured, "Exception: failed", false),
    ("missing statistics", "[{}]", "", false),
    ("empty statistics", "[{\"Statistics\":{}}]", "", false),
    ("empty statistics array", "[{\"Statistics\":[]}]", "", false),
    ("empty statistics string", "[{\"Statistics\":\"\"}]", "", false),
    ("zero statistics", "[{\"Statistics\":0}]", "", false),
    ("false statistics", "[{\"Statistics\":false}]", "", false),
    ("mixed measurements", "[{\"Statistics\":{\"Mean\":1}},{}]", "", false),
    ("invalid blocks on later line", measured, "Starting\nInvalid Blocks", false),
    ("exception on later line", measured, "Starting\nSystem.Exception: failed", false)
};
var passed = 0;
var directory = Directory.CreateTempSubdirectory("bal catchup tests ");
try
{
    var root = new DirectoryInfo(Environment.CurrentDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts/check-bal-catchup-results.cs"))) root = root.Parent;
    var build = await ScriptProcess.Run("dotnet", ["build", Path.Combine(root!.FullName, "scripts/check-bal-catchup-results.cs"), "--output", directory.FullName], CancellationToken.None);
    if (build.Code != 0) throw new InvalidOperationException(build.Output + build.Error);
    var results = Directory.CreateDirectory(Path.Combine(directory.FullName, "results"));
    var report = Path.Combine(results.FullName, "test-report-full-compressed.json");
    foreach (var item in cases)
    {
        File.WriteAllText(Path.Combine(directory.FullName, "run.log"), item.Log);
        File.Delete(report);
        if (item.Measurements is not null)
            File.WriteAllText(report, "{\"Benchmarks\":" + item.Measurements + "}");
        await Verify(item.Name, 1, item.Valid);
        await Verify(item.Name + " with wrong count", 2, false);
    }

    File.WriteAllText(Path.Combine(directory.FullName, "run.log"), "");
    foreach (var malformed in new[] { "{", "{}", "{\"Benchmarks\":null}", "{\"Benchmarks\":{}}" })
    {
        File.WriteAllText(report, malformed);
        await Verify("malformed report " + malformed, 1, false);
    }

    File.WriteAllText(report, "{\"Benchmarks\":" + measured + "}");
    var second = Path.Combine(results.FullName, "second-report-full-compressed.json");
    File.Copy(report, second);
    await Verify("multiple reports", 2, true);
    await Verify("multiple reports wrong count", 1, false, "Expected 1 benchmark cases, got 2");
    File.WriteAllText(second, "{\"Benchmarks\":[]}");
    await Verify("empty second report", 1, false, $"Missing measurements in {second}");
    File.Delete(report);
    File.Delete(second);
    Directory.Delete(results.FullName);
    await Verify("missing results directory", 1, false, "No BenchmarkDotNet JSON report was produced");
    File.WriteAllText(Path.Combine(directory.FullName, "run.log"), "Invalid Block");
    await Verify("log failure precedes report failure", 1, false, "Benchmark log contains an exception or invalid block");
    File.Delete(Path.Combine(directory.FullName, "run.log"));
    await Verify("missing log", 1, false);
    Console.WriteLine($"Passed {passed} tests.");
}
finally
{
    directory.Delete(recursive: true);
}

async Task Verify(string name, int expectedCases, bool valid, string? expectedMessage = null)
{
    var result = await ScriptProcess.Run("dotnet", [Path.Combine(directory.FullName, "check-bal-catchup-results.dll"), directory.FullName, expectedCases.ToString(System.Globalization.CultureInfo.InvariantCulture)], CancellationToken.None);
    if (valid != (result.Code == 0))
        throw new InvalidOperationException($"{name}: expected valid={valid}: {result.Error}");
    if (expectedMessage is not null && result.Error.TrimEnd('\r', '\n') != expectedMessage)
        throw new InvalidOperationException($"{name}: expected diagnostic '{expectedMessage}', got '{result.Error}'");
    Console.WriteLine($"PASS {name}");
    passed++;
}
