#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ../CheckBalCatchupResults.Helpers.cs

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
    var results = Directory.CreateDirectory(Path.Combine(directory.FullName, "results"));
    var report = Path.Combine(results.FullName, "test-report-full-compressed.json");
    foreach (var item in cases)
    {
        File.WriteAllText(Path.Combine(directory.FullName, "run.log"), item.Log);
        File.Delete(report);
        if (item.Measurements is not null)
            File.WriteAllText(report, "{\"Benchmarks\":" + item.Measurements + "}");
        Verify(item.Name, 1, item.Valid);
        Verify(item.Name + " with wrong count", 2, false);
    }

    File.WriteAllText(Path.Combine(directory.FullName, "run.log"), "");
    foreach (var malformed in new[] { "{", "{}", "{\"Benchmarks\":null}", "{\"Benchmarks\":{}}" })
    {
        File.WriteAllText(report, malformed);
        Verify("malformed report " + malformed, 1, false);
    }

    File.WriteAllText(report, "{\"Benchmarks\":" + measured + "}");
    var second = Path.Combine(results.FullName, "second-report-full-compressed.json");
    File.Copy(report, second);
    Verify("multiple reports", 2, true);
    Verify("multiple reports wrong count", 1, false, "Expected 1 benchmark cases, got 2");
    File.WriteAllText(second, "{\"Benchmarks\":[]}");
    Verify("empty second report", 1, false, $"Missing measurements in {second}");
    File.Delete(report);
    File.Delete(second);
    Directory.Delete(results.FullName);
    Verify("missing results directory", 1, false, "No BenchmarkDotNet JSON report was produced");
    File.WriteAllText(Path.Combine(directory.FullName, "run.log"), "Invalid Block");
    Verify("log failure precedes report failure", 1, false, "Benchmark log contains an exception or invalid block");
    File.Delete(Path.Combine(directory.FullName, "run.log"));
    Verify("missing log", 1, false);
    Console.WriteLine($"Passed {passed} tests.");
}
finally
{
    directory.Delete(recursive: true);
}

void Verify(string name, int expectedCases, bool valid, string? expectedMessage = null)
{
    Exception? failure = null;
    try
    {
        BalCatchupResults.Check(directory.FullName, expectedCases);
    }
    catch (Exception exception)
    {
        failure = exception;
    }
    if (valid != (failure is null))
        throw new InvalidOperationException($"{name}: expected valid={valid}", failure);
    if (expectedMessage is not null && failure?.Message != expectedMessage)
        throw new InvalidOperationException($"{name}: expected diagnostic '{expectedMessage}'", failure);
    Console.WriteLine($"PASS {name}");
    passed++;
}
