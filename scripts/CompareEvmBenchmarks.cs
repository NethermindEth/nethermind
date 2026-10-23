#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include EvmBenchmark.Helpers.cs

try
{
    if (args.Length != 1 || args[0] is not ("detect-noisy" or "compare"))
    {
        Console.Error.WriteLine("Usage: CompareEvmBenchmarks.cs <detect-noisy|compare>");
        return 2;
    }
    var baseline = EvmBenchmark.Logs(Environment.CurrentDirectory, "base");
    var candidate = EvmBenchmark.Logs(Environment.CurrentDirectory, "pr");
    var comparisons = EvmBenchmark.Compare(EvmBenchmark.Aggregate(baseline), EvmBenchmark.Aggregate(candidate), EvmBenchmark.ReadConfig(Environment.GetEnvironmentVariable));
    if (args[0] == "detect-noisy")
    {
        var reruns = comparisons.Where(row => row.Flagged && row.Noisy).Select(row => row.Opcode).ToArray();
        File.WriteAllText("evm-rerun-opcodes.txt", string.Concat(reruns.Select(opcode => opcode + "\n")));
        File.AppendAllText(Environment.GetEnvironmentVariable("GITHUB_OUTPUT") ?? throw new InvalidOperationException("GITHUB_OUTPUT is required"), $"rerun_needed={(reruns.Length > 0 ? "true" : "false")}\nrerun_count={reruns.Length}\n");
    }
    else
    {
        var reruns = File.Exists("evm-rerun-opcodes.txt") ? File.ReadAllLines("evm-rerun-opcodes.txt").Select(line => line.Trim()).Where(line => line.Length > 0).ToArray() : [];
        var summary = EvmBenchmark.Summary(comparisons, baseline.Length, candidate.Length, Environment.GetEnvironmentVariable, reruns);
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
