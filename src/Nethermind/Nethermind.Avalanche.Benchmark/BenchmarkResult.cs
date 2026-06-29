// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Avalanche.Benchmark;

/// <summary>Per-block execution outcome.</summary>
/// <param name="Number">Block number.</param>
/// <param name="GasUsed">Gas used by the block (header value).</param>
/// <param name="TxCount">Number of transactions in the block.</param>
/// <param name="ElapsedMs">Wall-clock execution time in milliseconds.</param>
/// <param name="Succeeded">Whether <c>BranchProcessor.Process</c> completed without throwing.</param>
/// <param name="Error">Error description when <paramref name="Succeeded"/> is false; otherwise null.</param>
public readonly record struct BlockResult(
    ulong Number,
    ulong GasUsed,
    int TxCount,
    double ElapsedMs,
    bool Succeeded,
    string? Error = null);

/// <summary>
/// Aggregated throughput statistics over a benchmark run. Throughput figures are computed over
/// successfully executed blocks only; failures are reported separately.
/// </summary>
public sealed class BenchmarkResult
{
    public IReadOnlyList<BlockResult> Blocks { get; }

    public int TotalBlocks { get; }
    public int SucceededBlocks { get; }
    public int FailedBlocks { get; }

    public ulong TotalGas { get; }
    public long TotalTxs { get; }

    /// <summary>Sum of per-block wall-clock times (ms) over successful blocks.</summary>
    public double TotalExecutionMs { get; }

    /// <summary>Million gas per second: <c>TotalGas / TotalExecutionSeconds / 1e6</c>.</summary>
    public double MGasPerSecond { get; }

    /// <summary>Blocks executed per second of execution time.</summary>
    public double BlocksPerSecond { get; }

    public double MeanMs { get; }
    public double P50Ms { get; }
    public double P90Ms { get; }
    public double P99Ms { get; }
    public double MinMs { get; }
    public double MaxMs { get; }

    public BenchmarkResult(IReadOnlyList<BlockResult> blocks)
    {
        Blocks = blocks;
        TotalBlocks = blocks.Count;

        List<double> okTimes = [];
        ulong totalGas = 0;
        long totalTxs = 0;
        double totalMs = 0;
        int ok = 0;

        foreach (BlockResult b in blocks)
        {
            if (!b.Succeeded)
            {
                continue;
            }

            ok++;
            totalGas += b.GasUsed;
            totalTxs += b.TxCount;
            totalMs += b.ElapsedMs;
            okTimes.Add(b.ElapsedMs);
        }

        SucceededBlocks = ok;
        FailedBlocks = TotalBlocks - ok;
        TotalGas = totalGas;
        TotalTxs = totalTxs;
        TotalExecutionMs = totalMs;

        double totalSeconds = totalMs / 1000.0;
        MGasPerSecond = totalSeconds > 0 ? totalGas / totalSeconds / 1_000_000.0 : 0;
        BlocksPerSecond = totalSeconds > 0 ? ok / totalSeconds : 0;

        if (okTimes.Count > 0)
        {
            okTimes.Sort();
            MeanMs = totalMs / okTimes.Count;
            MinMs = okTimes[0];
            MaxMs = okTimes[^1];
            P50Ms = Percentile(okTimes, 0.50);
            P90Ms = Percentile(okTimes, 0.90);
            P99Ms = Percentile(okTimes, 0.99);
        }
    }

    /// <summary>Nearest-rank percentile over a pre-sorted ascending list.</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        int rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        rank = Math.Clamp(rank, 0, sorted.Count - 1);
        return sorted[rank];
    }
}
