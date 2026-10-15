#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Print a Markdown comparison of two joined BenchmarkDotNet runs.
//
//   compare.cs <base artifacts directory> <head artifacts directory>
//
// Reads BASE_SHA, HEAD_SHA, BASE_OUTCOME, HEAD_OUTCOME and RUNNER_LABEL from the environment.
// Benchmarks are listed in the head run's order; one missing from the base run shows dashes.

using System.Globalization;
using System.Text.Json;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: compare.cs <base artifacts directory> <head artifacts directory>");
    return 2;
}

Dictionary<string, Result> baseResults = Load(args[0]);
Dictionary<string, Result> headResults = Load(args[1]);

Console.WriteLine("## Network benchmarks");
Console.WriteLine();
Console.WriteLine($"Runner `{Env("RUNNER_LABEL")}`, base `{Short(Env("BASE_SHA"))}` ({Env("BASE_OUTCOME")}), " +
    $"head `{Short(Env("HEAD_SHA"))}` ({Env("HEAD_OUTCOME")}).");
Console.WriteLine();
Console.WriteLine("Medians are compared; a delta within the CV of either side is noise. Allocations are per operation.");
Console.WriteLine();

if (headResults.Count == 0)
{
    Console.WriteLine("_No head results; see the run log._");
    return 0;
}

Console.WriteLine("| Benchmark | Base | Head | Δ | CV base | CV head | Alloc base | Alloc head | Δ alloc |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
foreach ((string name, Result head) in headResults)
{
    Result? @base = baseResults.GetValueOrDefault(name);
    Console.WriteLine(
        $"| {name} | {FormatTime(@base?.Median)} | {FormatTime(head.Median)} | {FormatDelta(head.Median, @base?.Median)} " +
        $"| {FormatPercent(@base?.Cv)} | {FormatPercent(head.Cv)} " +
        $"| {FormatBytes(@base?.Allocated)} | {FormatBytes(head.Allocated)} | {FormatDelta(head.Allocated, @base?.Allocated)} |");
}

return 0;

static Dictionary<string, Result> Load(string artifactsDirectory)
{
    Dictionary<string, Result> results = [];
    string resultsDirectory = Path.Combine(artifactsDirectory, "results");
    if (!Directory.Exists(resultsDirectory)) return results;

    foreach (string path in Directory.EnumerateFiles(resultsDirectory, "*-report-full-compressed.json"))
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (JsonElement benchmark in document.RootElement.GetProperty("Benchmarks").EnumerateArray())
        {
            // A benchmark that failed has no statistics.
            if (!TryGetObject(benchmark, "Statistics", out JsonElement statistics)) continue;

            string name = $"{benchmark.GetProperty("Type").GetString()}.{benchmark.GetProperty("Method").GetString()}";
            if (benchmark.TryGetProperty("Parameters", out JsonElement parameters) && parameters.GetString() is { Length: > 0 } parameterText)
            {
                name += $"({parameterText})";
            }

            double mean = statistics.GetProperty("Mean").GetDouble();
            double allocated = TryGetObject(benchmark, "Memory", out JsonElement memory)
                && memory.TryGetProperty("BytesAllocatedPerOperation", out JsonElement bytes)
                ? bytes.GetDouble()
                : 0;
            results[name] = new Result(
                statistics.GetProperty("Median").GetDouble(),
                mean == 0 ? 0 : statistics.GetProperty("StandardDeviation").GetDouble() / mean * 100,
                allocated);
        }
    }

    return results;
}

static bool TryGetObject(JsonElement element, string property, out JsonElement value) =>
    element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

static string FormatTime(double? nanoseconds) => nanoseconds switch
{
    null => "—",
    >= 1e9 => $"{nanoseconds / 1e9:F2} s",
    >= 1e6 => $"{nanoseconds / 1e6:F2} ms",
    >= 1e3 => $"{nanoseconds / 1e3:F2} μs",
    _ => $"{nanoseconds:F1} ns",
};

static string FormatBytes(double? bytes) => bytes switch
{
    null => "—",
    >= 1 << 20 => $"{bytes / (1 << 20):F2} MB",
    >= 1 << 10 => $"{bytes / (1 << 10):F2} KB",
    _ => $"{bytes:F0} B",
};

static string FormatPercent(double? percent) => percent is null ? "—" : $"{percent:F1}%";

static string FormatDelta(double head, double? @base) => @base switch
{
    null => "—",
    0 => head == 0 ? "0%" : "new",
    _ => $"{(head - @base) / @base * 100:+0.0;-0.0;0.0}%",
};

record Result(double Median, double Cv, double Allocated);
