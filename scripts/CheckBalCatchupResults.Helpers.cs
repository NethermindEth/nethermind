// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.RegularExpressions;

static class BalCatchupResults
{
    internal static void Check(string directory, int expectedCases)
    {
        if (Regex.IsMatch(File.ReadAllText(Path.Combine(directory, "run.log")),
            @"(?:^[ \t]*|--->[ \t]*(?:\(Inner Exception #\d+\)[ \t]*)?|Unhandled exception\.[ \t]*)[\w.]*Exception:|Invalid Blocks?",
            RegexOptions.Multiline))
            throw new InvalidDataException("Benchmark log contains an exception or invalid block");

        var results = Path.Combine(directory, "results");
        var reports = Directory.Exists(results)
            ? Directory.GetFiles(results, "*-report-full-compressed.json")
            : [];
        if (reports.Length == 0)
            throw new InvalidDataException("No BenchmarkDotNet JSON report was produced");

        var count = 0;
        foreach (var report in reports)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(report));
            var cases = document.RootElement.GetProperty("Benchmarks");
            count += cases.GetArrayLength();
            if (cases.GetArrayLength() == 0 || cases.EnumerateArray().Any(item =>
                !item.TryGetProperty("Statistics", out var statistics) || !HasMeasurements(statistics)))
                throw new InvalidDataException($"Missing measurements in {report}");
        }

        if (count != expectedCases)
            throw new InvalidDataException($"Expected {expectedCases} benchmark cases, got {count}");
    }

    private static bool HasMeasurements(JsonElement statistics) => statistics.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.Object => statistics.EnumerateObject().Any(),
        JsonValueKind.Array => statistics.GetArrayLength() != 0,
        JsonValueKind.String => statistics.GetString()!.Length != 0,
        JsonValueKind.Number => statistics.GetDouble() != 0,
        _ => true
    };
}
