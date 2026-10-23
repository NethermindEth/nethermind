#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.RegularExpressions;

try
{
    if (args.Length != 1 || args[0] is not ("detect-noisy" or "compare"))
    {
        Console.Error.WriteLine("Usage: CompareEvmBenchmarks.cs <detect-noisy|compare>");
        return 2;
    }
    var baseline = Logs(Environment.CurrentDirectory, "base");
    var candidate = Logs(Environment.CurrentDirectory, "pr");
    var comparisons = Compare(Aggregate(baseline), Aggregate(candidate), ReadConfig());
    if (args[0] == "detect-noisy")
    {
        var reruns = comparisons.Where(row => row.Flagged && row.Noisy).Select(row => row.Opcode).ToArray();
        File.WriteAllText("evm-rerun-opcodes.txt", string.Concat(reruns.Select(opcode => opcode + "\n")));
        File.AppendAllText(Environment.GetEnvironmentVariable("GITHUB_OUTPUT") ?? throw new InvalidOperationException("GITHUB_OUTPUT is required"), $"rerun_needed={(reruns.Length > 0 ? "true" : "false")}\nrerun_count={reruns.Length}\n");
    }
    else
    {
        var reruns = File.Exists("evm-rerun-opcodes.txt") ? File.ReadAllLines("evm-rerun-opcodes.txt").Select(line => line.Trim()).Where(line => line.Length > 0).ToArray() : [];
        var summary = Summary(comparisons, baseline.Length, candidate.Length, reruns);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } path) File.AppendAllText(path, summary);
        File.WriteAllText("evm-opcode-comment.md", summary);
        Console.WriteLine(summary);
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static Config ReadConfig() => new(
    Number(Environment.GetEnvironmentVariable("THRESHOLD_PERCENT") ?? "5"), Number(Environment.GetEnvironmentVariable("NOISE_MULTIPLIER") ?? "2.0"),
    Number(Environment.GetEnvironmentVariable("ERROR_MULTIPLIER") ?? "1.0"), Number(Environment.GetEnvironmentVariable("ABS_DELTA_NS_FLOOR") ?? "2.0"),
    Number(Environment.GetEnvironmentVariable("DELTA_MARGIN_PERCENT") ?? "2.0"));
static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
static string[] Logs(string directory, string side)
{
    var paths = Directory.GetFiles(directory, $"evm-opcodes-{side}*.log").Order(StringComparer.Ordinal).ToArray();
    return paths.Length == 0 ? [Path.Combine(directory, $"evm-opcodes-{side}.log")] : paths;
}
static double? ParseNs(string value)
{
    var match = TimingRegex().Match(value.Trim());
    if (!match.Success) return null;
    var scale = match.Groups[2].Value switch { "ns" => 1d, "us" or "µs" or "μs" => 1_000d, "ms" => 1_000_000d, "s" => 1_000_000_000d, _ => (double?)null };
    return scale * Number(match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal));
}
static double? Median(IEnumerable<double?> values)
{
    var sorted = values.Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
    return sorted.Length == 0 ? null : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}
static Dictionary<string, Stats> Extract(string text)
{
    var lines = AnsiEscapeRegex().Replace(text.Replace('\u00a0', ' '), "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    var header = Array.FindLastIndex(lines, line => line.Trim().StartsWith('|') && line.Contains("Opcode", StringComparison.Ordinal) && line.Contains("Mean", StringComparison.Ordinal));
    var result = new Dictionary<string, Stats>(StringComparer.Ordinal);
    if (header < 0) return result;
    string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
    var headers = Cells(lines[header]);
    var opcodeColumn = Array.IndexOf(headers, "Opcode");
    var meanColumn = Array.IndexOf(headers, "Mean");
    if (opcodeColumn < 0 || meanColumn < 0) return result;
    for (var index = header + 2; index < lines.Length; index++)
    {
        if (!lines[index].Trim().StartsWith('|')) break;
        var cells = Cells(lines[index]);
        if (cells.Length <= Math.Max(opcodeColumn, meanColumn)) continue;
        var mean = ParseNs(cells[meanColumn]);
        if (cells[opcodeColumn].Length == 0 || mean is null) continue;
        double? Read(string name)
        {
            var column = Array.IndexOf(headers, name);
            if (column < 0 || column >= cells.Length) return null;
            if (name != "Threshold") return ParseNs(cells[column]);
            return double.TryParse(cells[column], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        result[cells[opcodeColumn]] = new(Read("Median"), mean, Read("Error"), Read("StdDev"), Read("Threshold"));
    }
    return result;
}
static Dictionary<string, Stats> Aggregate(IEnumerable<string> files)
{
    var runs = files.Select(file => Extract(File.ReadAllText(file))).ToArray();
    var result = new Dictionary<string, Stats>(StringComparer.Ordinal);
    foreach (var opcode in runs.SelectMany(run => run.Keys).Distinct().Order(StringComparer.Ordinal))
    {
        var rows = runs.Where(run => run.ContainsKey(opcode)).Select(run => run[opcode]).ToArray();
        result[opcode] = new(
            Median(rows.Select(row => row.Median)), Median(rows.Select(row => row.Mean)),
            Median(rows.Select(row => row.Error)), Median(rows.Select(row => row.StdDev)),
            Median(rows.Select(row => row.Threshold)));
    }
    return result;
}
static double? Cv(Stats? value) => value?.Mean is > 0 && value.StdDev is { } deviation ? deviation / value.Mean * 100 : null;
static string FormatCv(Stats? value) => value?.Mean is null or 0 || value.StdDev is null ? "N/A" : Format(value.StdDev / value.Mean * 100, "F1") + "%";
static string Format(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "N/A";
static List<Comparison> Compare(Dictionary<string, Stats> baseline, Dictionary<string, Stats> candidate, Config config)
{
    var results = new List<Comparison>();
    foreach (var opcode in baseline.Keys.Union(candidate.Keys).Order(StringComparer.Ordinal))
    {
        baseline.TryGetValue(opcode, out var before);
        candidate.TryGetValue(opcode, out var after);
        var first = before?.Median is not null and not 0 ? before.Median : before?.Mean;
        var second = after?.Median is not null and not 0 ? after.Median : after?.Mean;
        var threshold = (before ?? after)?.Threshold is { } configured && configured != 0 ? configured : config.Threshold;
        var cv = new[] { Cv(before), Cv(after) }.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var noise = cv.Length > 0 ? cv.Max() * config.Noise : 0;
        var uncertainty = first is > 0 ? ((before?.Error ?? 0) + (after?.Error ?? 0)) / first.Value * 100 * config.Error : 0;
        var effective = Math.Max(threshold, Math.Max(noise, uncertainty));
        double? delta = null;
        double? absolute = null;
        var flagged = false;
        if (first is null || second is null) flagged = true;
        else if (first == 0) flagged = second != 0;
        else
        {
            delta = (second - first) / first * 100;
            absolute = Math.Abs(second.Value - first.Value);
            flagged = Math.Abs(delta.Value) >= effective + config.Margin && absolute >= config.AbsoluteFloor;
        }
        results.Add(new(opcode, first, second, delta, absolute, before, after, threshold, noise, uncertainty, effective, flagged, noise > threshold || uncertainty > threshold));
    }
    return results;
}
static string Summary(List<Comparison> comparisons, int baseRuns, int prRuns, string[] reruns)
{
    var flagged = comparisons.Where(row => row.Flagged).ToArray();
    var lines = new List<string> { "## EVM Opcode Benchmark Diff", "", $"Aggregated runs: base={baseRuns}, pr={prRuns}" };
    if (Environment.GetEnvironmentVariable("BASE_HARNESS_FALLBACK") == "true")
    {
        var harness = Environment.GetEnvironmentVariable("BASE_HARNESS_DELTA");
        lines.AddRange(["", "> [!WARNING]", "> The PR benchmark harness does not compile against the base commit, so the base side ran its own harness sources rather than the PR's.", $"> Harness delta between base and PR: {(string.IsNullOrEmpty(harness) ? "not reported" : harness)}.", ""]);
    }
    if (reruns.Length > 0) lines.Add($"Noisy rerun opcodes: {string.Join(", ", reruns)}");
    lines.Add("");
    foreach (var (title, rows) in new[]
    {
        ("Regressions", flagged.Where(row => row.Delta > 0).ToArray()),
        ("Improvements", flagged.Where(row => row.Delta < 0).ToArray()),
        ("New / Removed", flagged.Where(row => row.Delta is null).ToArray())
    })
    {
        if (rows.Length == 0) continue;
        lines.AddRange([$"### {title} ({rows.Length})", "", "| Opcode | Base Median (ns) | PR Median (ns) | Delta | Abs Δ (ns) | Base CV | PR CV | Threshold | Uncertainty | Effective |", "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]);
        foreach (var row in rows)
        {
            var delta = row.Delta is null ? "N/A" : (row.Delta >= 0 ? "+" : "") + Format(row.Delta, "F2") + "%";
            lines.Add($"| {row.Opcode} | {Format(row.Base, "F3")} | {Format(row.Pr, "F3")} | {delta} | " +
                $"{Format(row.AbsoluteDelta, "F3")} | {FormatCv(row.BaseStats)} | {FormatCv(row.PrStats)} | " +
                $"±{Format(row.Threshold, "F1")}% | ±{Format(row.Uncertainty, "F1")}% | ±{Format(row.Effective, "F1")}% |");
        }
        lines.Add("");
    }
    if (flagged.Length == 0) lines.Add("No significant regressions or improvements detected.");
    return "<!-- evm-opcode-benchmark-diff -->\n" + string.Join('\n', lines) + "\n";
}

sealed record Config(double Threshold = 5, double Noise = 2, double Error = 1, double AbsoluteFloor = 2, double Margin = 2);
sealed record Stats(double? Median, double? Mean, double? Error, double? StdDev, double? Threshold);
sealed record Comparison(string Opcode, double? Base, double? Pr, double? Delta, double? AbsoluteDelta,
    Stats? BaseStats, Stats? PrStats, double Threshold, double Noise, double Uncertainty, double Effective, bool Flagged, bool Noisy);

partial class Program
{
    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscapeRegex();
    [GeneratedRegex(@"^\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*([a-zA-Zµμ]+)\s*$")]
    private static partial Regex TimingRegex();
}
