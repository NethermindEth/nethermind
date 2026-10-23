#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include ../EvmBenchmark.Helpers.cs

using System.Diagnostics;

var passed = 0;
foreach (var (input, expected) in new (string, double?)[] { ("12.34 ns", 12.34), ("1,200 us", 1200000), ("2 µs", 2000), ("3 μs", 3000), (".5 ns", null), ("-1 ns", null), ("1 NS", null), ("1 ms", 1000000), ("2 s", 2000000000), ("NA", null), ("0 ns", 0), (" 1.5 us ", 1500) })
    Check($"timing {input}", EvmBenchmark.ParseNs(input) == expected);
foreach (var (values, expected) in new (double?[], double?)[] { ([], null), ([null, 1, 3], 2), ([3, null, 2, 1], 2), ([0], 0) })
    Check("median " + string.Join(',', values), EvmBenchmark.Median(values) == expected);
var header = "| Opcode | Mean | Median | Error | StdDev | Threshold |\n|---|---:|---:|---:|---:|---:|\n";
var parsed = EvmBenchmark.Extract(header + "| OLD | 99 ns | 99 ns | 1 ns | 1 ns | 1 |\nend\n\u001b[32m" + header + "| ADD | 12\u00a0ns | 11 ns | NA | 2 ns | bad |\n| MUL | NA | 1 ns | 1 ns | 1 ns | 1 |\nstop\n");
Check("last table and ANSI/NBSP", parsed.Count == 1 && parsed["ADD"] == new EvmBenchmark.Stats(11, 12, null, 2, null));
Check("missing exact mean header", EvmBenchmark.Extract("| Opcode | MeanRatio |\n|---|---|\n| ADD | 2 |\n").Count == 0);
Check("short row ignored", EvmBenchmark.Extract(header + "| ADD |\n").Count == 0);

foreach (var (name, first, second, deviation, error, threshold, flagged, noisy) in new[]
{
    ("unchanged", 100d, 100d, 0d, 0d, 5d, false, false),
    ("regression", 100d, 110d, 0d, 0d, 5d, true, false),
    ("improvement", 100d, 90d, 0d, 0d, 5d, true, false),
    ("margin", 100d, 106d, 0d, 0d, 5d, false, false),
    ("absolute floor", 1d, 2d, 0d, 0d, 5d, false, false),
    ("noise floor", 100d, 110d, 10d, 0d, 5d, false, true),
    ("flagged noisy", 100d, 150d, 10d, 0d, 5d, true, true),
    ("uncertainty floor", 100d, 110d, 0d, 20d, 5d, false, true),
    ("zero base", 0d, 10d, 0d, 0d, 5d, true, false),
    ("both zero", 0d, 0d, 0d, 0d, 5d, false, false),
    ("zero threshold falls back", 100d, 103d, 0d, 0d, 0d, false, false)
})
{
    var before = new Dictionary<string, EvmBenchmark.Stats> { ["ADD"] = new(null, first, error, deviation, threshold) };
    var after = new Dictionary<string, EvmBenchmark.Stats> { ["ADD"] = new(null, second, error, deviation, threshold) };
    var row = EvmBenchmark.Compare(before, after, new())[0];
    Check(name, row.Flagged == flagged && row.Noisy == noisy);
}
var empty = new Dictionary<string, EvmBenchmark.Stats>();
var single = new Dictionary<string, EvmBenchmark.Stats> { ["ADD"] = new(0, 10, null, null, 0) };
Check("new opcode", EvmBenchmark.Compare(empty, single, new())[0] is { Flagged: true, Delta: null, Pr: 10 });
Check("removed opcode", EvmBenchmark.Compare(single, empty, new())[0] is { Flagged: true, Delta: null, Base: 10 });
Check("config defaults", EvmBenchmark.ReadConfig(_ => null) == new EvmBenchmark.Config());
Check("config overrides", EvmBenchmark.ReadConfig(_ => "3") == new EvmBenchmark.Config(3, 3, 3, 3, 3));
Check("no-change summary", EvmBenchmark.Summary([], 1, 2, _ => null, []) == "<!-- evm-opcode-benchmark-diff -->\n## EVM Opcode Benchmark Diff\n\nAggregated runs: base=1, pr=2\n\nNo significant regressions or improvements detected.\n");
var summary = EvmBenchmark.Summary(EvmBenchmark.Compare(empty, single, new()), 1, 1, key => key == "BASE_HARNESS_FALLBACK" ? "true" : null, ["ADD"]);
Check("fallback and new opcode summary", summary.Contains("> Harness delta between base and PR: not reported.", StringComparison.Ordinal) && summary.Contains("Noisy rerun opcodes: ADD", StringComparison.Ordinal) && summary.Contains("### New / Removed (1)", StringComparison.Ordinal));

var temporary = Directory.CreateTempSubdirectory("evm benchmark tests ");
try
{
    Check("missing log fallback", EvmBenchmark.Logs(temporary.FullName, "base").SequenceEqual([Path.Combine(temporary.FullName, "evm-opcodes-base.log")]));
    foreach (var (side, mean) in new[] { ("base", 100), ("pr", 150) })
        File.WriteAllText(Path.Combine(temporary.FullName, $"evm-opcodes-{side}.log"), header + $"| ADD | {mean} ns | {mean} ns | 0 ns | 10 ns | 5 |\n");
    File.WriteAllText(Path.Combine(temporary.FullName, "evm-opcodes-base-rerun-1.log"), header + "| ADD | 100 ns | 100 ns | 0 ns | 10 ns | 5 |\n");
    Check("multiple log aggregation", EvmBenchmark.Aggregate(EvmBenchmark.Logs(temporary.FullName, "base"))["ADD"].Mean == 100);
    var repository = new DirectoryInfo(Environment.CurrentDirectory);
    while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts/CompareEvmBenchmarks.cs"))) repository = repository.Parent;
    var script = Path.Combine(repository?.FullName ?? throw new DirectoryNotFoundException("Run tests within the checkout."), "scripts/CompareEvmBenchmarks.cs");
    foreach (var mode in new[] { "detect-noisy", "compare", "invalid" })
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = temporary.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in new[] { "run", "--file", script, "--", mode }) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["GITHUB_OUTPUT"] = Path.Combine(temporary.FullName, "outputs");
        process.StartInfo.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(temporary.FullName, "summary");
        foreach (var name in new[] { "THRESHOLD_PERCENT", "NOISE_MULTIPLIER", "ERROR_MULTIPLIER", "ABS_DELTA_NS_FLOOR", "DELTA_MARGIN_PERCENT", "BASE_HARNESS_FALLBACK", "BASE_HARNESS_DELTA" }) process.StartInfo.Environment.Remove(name);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var output = await stdout + await stderr;
        Check("CLI " + mode + " " + output, process.ExitCode == (mode == "invalid" ? 2 : 0));
    }
    Check("rerun outputs", File.ReadAllText(Path.Combine(temporary.FullName, "outputs")) == "rerun_needed=true\nrerun_count=1\n" && File.ReadAllText(Path.Combine(temporary.FullName, "evm-rerun-opcodes.txt")) == "ADD\n");
    var comment = File.ReadAllText(Path.Combine(temporary.FullName, "evm-opcode-comment.md"));
    Check("comment and summary", comment == File.ReadAllText(Path.Combine(temporary.FullName, "summary")) && comment.Contains("| ADD | 100.000 | 150.000 | +50.00% | 50.000 | 10.0% | 6.7% | ±5.0% | ±0.0% | ±20.0% |", StringComparison.Ordinal));
}
finally { temporary.Delete(recursive: true); }
Console.WriteLine($"Passed {passed} tests.");

void Check(string name, bool success)
{
    if (!success) throw new InvalidOperationException(name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
