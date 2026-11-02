#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Print a Markdown comparison of BenchmarkDotNet runs at a base and a head commit.
//
//   compare.cs <base artifacts directory> <head artifacts directory>
//
// Each directory holds one joined run per round in round-<n> subdirectories, or a single joined run itself.
// The nth base round is paired with the nth head round, as they ran next to each other.
// Reads BASE_SHA, HEAD_SHA, BASE_OUTCOME, HEAD_OUTCOME and RUNNER_NAME from the environment.
// Benchmarks are listed in the head run's order; one missing from the base run shows dashes.
// Exits with 1 when the head run has no results or any of its benchmarks failed: the benchmark runner
// itself exits with 0 regardless.

using System.Globalization;
using System.Text.Json;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: compare.cs <base artifacts directory> <head artifacts directory>");
    return 2;
}

// A null result is a benchmark that failed.
var baseRounds = LoadRounds(args[0]);
var headRounds = LoadRounds(args[1]);
var rounds = Math.Min(baseRounds.Count, headRounds.Count);
// A round a side did not finish, as when the job is cancelled, has no results to pair.
var pairedRounds = Enumerable.Range(0, rounds).Count(round => baseRounds[round].Count > 0 && headRounds[round].Count > 0);

Console.WriteLine("## Network benchmarks");
Console.WriteLine();
Console.WriteLine($"Runner `{Env("RUNNER_NAME")}`, base `{Short(Env("BASE_SHA"))}` ({Env("BASE_OUTCOME")}), " +
    $"head `{Short(Env("HEAD_SHA"))}` ({Env("HEAD_OUTCOME")}), {pairedRounds} paired round{(pairedRounds == 1 ? "" : "s")}.");
Console.WriteLine();
Console.WriteLine("Each round runs the base and the head next to each other. Δ is the mean of the rounds' changes in median time, " +
    $"with its 99% confidence interval from a paired t-test across the rounds, which needs at least two. A change is significant (✓) " +
    $"when the interval excludes zero and the change is at least {MinimumChange}%. Times are medians across the rounds; allocations " +
    "are per operation.");
Console.WriteLine();

if (headRounds.Count == 0 || headRounds.All(static round => round.Count == 0))
{
    Console.WriteLine("_No head results; see the run log._");
    return 1;
}

Console.WriteLine("| Benchmark | Base | Head | Δ | 99% interval | Significant | Alloc base | Alloc head | Δ alloc |");
Console.WriteLine("|---|---:|---:|---:|---:|:---:|---:|---:|---:|");
List<string> failed = [];
foreach (var name in headRounds.SelectMany(static round => round.Keys).Distinct())
{
    var @base = Collect(baseRounds, name);
    var head = Collect(headRounds, name);
    if (head.Failed) failed.Add(name);

    List<double> deltas = [];
    for (var round = 0; round < rounds; round++)
    {
        if (baseRounds[round].GetValueOrDefault(name) is { } b && headRounds[round].GetValueOrDefault(name) is { } h && b.Median != 0)
            deltas.Add((h.Median - b.Median) / b.Median * 100);
    }

    var mean = deltas.Count == 0 ? (double?)null : deltas.Average();
    var halfWidth = deltas.Count < 2 ? (double?)null : HalfWidth99(deltas);
    var significant = mean is { } m && halfWidth is { } w && Math.Abs(m) >= MinimumChange && Math.Abs(m) > w;
    Console.WriteLine(
        $"| {name} | {Format(@base, FormatTime)} | {Format(head, FormatTime)} " +
        $"| {(mean is { } meanChange ? FormatPercent(meanChange) : "—")} " +
        $"| {(mean is { } center && halfWidth is { } width ? $"{FormatPercent(center - width)} to {FormatPercent(center + width)}" : "—")} " +
        $"| {(significant ? "✓" : "")} " +
        $"| {FormatBytes(@base.Allocated)} | {FormatBytes(head.Allocated)} | {FormatDelta(head.Allocated, @base.Allocated)} |");
}

if (failed.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"_Failed at head: {string.Join(", ", failed)}; see the run log._");
    return 1;
}

return 0;

static List<Dictionary<string, Result?>> LoadRounds(string artifactsDirectory)
{
    if (!Directory.Exists(artifactsDirectory)) return [];
    if (Directory.Exists(Path.Combine(artifactsDirectory, "results"))) return [Load(artifactsDirectory)];

    return [.. Directory.EnumerateDirectories(artifactsDirectory, "round-*")
        .Select(static path => (Path: path, Round: int.TryParse(Path.GetFileName(path)["round-".Length..], out var round) ? round : int.MaxValue))
        .OrderBy(static entry => entry.Round)
        .Select(static entry => Load(entry.Path))];
}

static Dictionary<string, Result?> Load(string artifactsDirectory)
{
    Dictionary<string, Result?> results = [];
    var resultsDirectory = Path.Combine(artifactsDirectory, "results");
    if (!Directory.Exists(resultsDirectory)) return results;

    foreach (var path in Directory.EnumerateFiles(resultsDirectory, "*-report-full-compressed.json"))
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var benchmark in document.RootElement.GetProperty("Benchmarks").EnumerateArray())
        {
            var name = $"{benchmark.GetProperty("Type").GetString()}.{benchmark.GetProperty("Method").GetString()}";
            if (benchmark.TryGetProperty("Parameters", out var parameters) && parameters.GetString() is { Length: > 0 } parameterText)
            {
                name += $"({parameterText})";
            }

            // A benchmark that failed has no statistics.
            if (!TryGetObject(benchmark, "Statistics", out var statistics))
            {
                results[name] = null;
                continue;
            }

            double? allocated = TryGetObject(benchmark, "Memory", out var memory)
                && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes)
                ? bytes.GetDouble()
                : null;
            results[name] = new Result(statistics.GetProperty("Median").GetDouble(), allocated);
        }
    }

    return results;
}

// The median time and largest allocation across the rounds that measured the benchmark, and whether any round ran it and failed.
static Summary Collect(List<Dictionary<string, Result?>> rounds, string name)
{
    List<double> medians = [];
    List<double> allocated = [];
    var ran = false;
    var failed = false;
    foreach (var round in rounds)
    {
        if (!round.TryGetValue(name, out var result)) continue;
        ran = true;
        if (result is null)
        {
            failed = true;
            continue;
        }

        medians.Add(result.Median);
        if (result.Allocated is { } bytes) allocated.Add(bytes);
    }

    // Allocations repeat exactly between rounds, except that BenchmarkDotNet sometimes reports none for a benchmark that allocates.
    return new Summary(ran, failed, medians.Count == 0 ? null : Median(medians), allocated.Count == 0 ? null : allocated.Max());
}

// Half the width of the 99% confidence interval of the mean, from Student's t with n - 1 degrees of freedom.
static double HalfWidth99(List<double> values)
{
    // Two-sided 99% quantiles for 1 to 30 degrees of freedom; the last is a conservative bound beyond.
    ReadOnlySpan<double> quantiles =
    [
        63.657, 9.925, 5.841, 4.604, 4.032, 3.707, 3.499, 3.355, 3.250, 3.169,
        3.106, 3.055, 3.012, 2.977, 2.947, 2.921, 2.898, 2.878, 2.861, 2.845,
        2.831, 2.819, 2.807, 2.797, 2.787, 2.779, 2.771, 2.763, 2.756, 2.750,
    ];

    var mean = values.Average();
    var variance = values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1);
    return quantiles[Math.Min(values.Count - 1, quantiles.Length) - 1] * Math.Sqrt(variance / values.Count);
}

static double Median(List<double> values)
{
    var sorted = values.Order().ToArray();
    var middle = sorted.Length / 2;
    return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

static bool TryGetObject(JsonElement element, string property, out JsonElement value) =>
    element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.Object;

static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

static string Format(Summary summary, Func<double, string> format) =>
    !summary.Ran ? "—" : summary.Median is { } median ? format(median) : "failed";

static string FormatTime(double nanoseconds) => nanoseconds switch
{
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

static string FormatPercent(double percent) => $"{percent:+0.0;-0.0;0.0}%";

static string FormatDelta(double? head, double? @base) => (head, @base) switch
{
    (null, _) or (_, null) => "—",
    (0, 0) => "0%",
    (_, 0) => "new",
    _ => FormatPercent((head.Value - @base.Value) / @base.Value * 100),
};

partial class Program
{
    // A smaller change is not worth acting on for these benchmarks, however sure.
    private const double MinimumChange = 2;
}

record Result(double Median, double? Allocated);

record Summary(bool Ran, bool Failed, double? Median, double? Allocated);
