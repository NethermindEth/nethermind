// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.IO;
using Nethermind.Core;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Shared helpers for timing breakdown reports used by NewPayload and NewPayloadMeasured benchmarks.
/// </summary>
internal static class TimingReportHelper
{
    public static void PrintLine(TextWriter writer, string label, double stageMs, double totalMs, int iterations)
    {
        double share = totalMs > 0 ? (100.0 * stageMs / totalMs) : 0.0;
        double avg = iterations > 0 ? (stageMs / iterations) : 0.0;
        writer.WriteLine($"  {label,-20} {stageMs,9:F3} ms  ({share,5:F1}%)  avg {avg:F3} ms/iter");
    }

    public static void PrintTxTypeBreakdown(TextWriter writer, string title, long[] ticksByType, int[] countByType, double stageTotalMs)
    {
        writer.WriteLine($"    {title}:");
        for (int txTypeIndex = 0; txTypeIndex < countByType.Length; txTypeIndex++)
        {
            int count = countByType[txTypeIndex];
            if (count == 0)
            {
                continue;
            }

            double ms = TicksToMs(ticksByType[txTypeIndex]);
            double share = GetShare(ms, stageTotalMs);
            double avg = GetAverage(ms, count);
            writer.WriteLine($"      {FormatTxType((TxType)txTypeIndex),-16} {ms,9:F3} ms  ({share,5:F1}%)  avg {avg:F3} ms/tx  count {count}");
        }
    }

    public static string FormatTxType(TxType txType)
    {
        return txType switch
        {
            TxType.Legacy => "Legacy",
            TxType.AccessList => "AccessList",
            TxType.EIP1559 => "EIP1559",
            TxType.Blob => "Blob",
            TxType.SetCode => "SetCode",
            TxType.DepositTx => "DepositTx",
            _ => $"Type(0x{(byte)txType:X2})"
        };
    }

    public static void AddTypeBreakdown(long[] destination, long[] source)
    {
        int length = destination.Length < source.Length ? destination.Length : source.Length;
        for (int i = 0; i < length; i++)
        {
            destination[i] += source[i];
        }
    }

    public static void AddTypeBreakdown(int[] destination, int[] source)
    {
        int length = destination.Length < source.Length ? destination.Length : source.Length;
        for (int i = 0; i < length; i++)
        {
            destination[i] += source[i];
        }
    }

    public static int GetTotalCount(int[] countByType)
    {
        int total = 0;
        for (int i = 0; i < countByType.Length; i++)
        {
            total += countByType[i];
        }

        return total;
    }

    public static double GetShare(double part, double whole)
    {
        return whole > 0 ? (100.0 * part / whole) : 0.0;
    }

    public static double GetAverage(double totalMs, int count)
    {
        return count > 0 ? (totalMs / count) : 0.0;
    }

    public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Distributes sender-recovery elapsed ticks across transaction types proportionally.
    /// Shared between NewPayload and NewPayloadMeasured benchmarks.
    /// </summary>
    public static void AddSenderRecoveryTypeBreakdown(Block block, long elapsedTicks, long[] byTypeTicks, int[] byTypeCount)
    {
        int txCount = block.Transactions.Length;
        if (txCount == 0)
        {
            return;
        }

        int[] countsPerType = new int[byTypeCount.Length];
        int firstTypeIndex = -1;
        for (int i = 0; i < txCount; i++)
        {
            int txTypeIndex = (int)block.Transactions[i].Type;
            if (firstTypeIndex == -1)
            {
                firstTypeIndex = txTypeIndex;
            }

            countsPerType[txTypeIndex]++;
            byTypeCount[txTypeIndex]++;
        }

        long allocatedTicks = 0;
        for (int txTypeIndex = 0; txTypeIndex < countsPerType.Length; txTypeIndex++)
        {
            int count = countsPerType[txTypeIndex];
            if (count == 0)
            {
                continue;
            }

            long typeTicks = elapsedTicks * count / txCount;
            byTypeTicks[txTypeIndex] += typeTicks;
            allocatedTicks += typeTicks;
        }

        if (firstTypeIndex >= 0 && allocatedTicks != elapsedTicks)
        {
            byTypeTicks[firstTypeIndex] += elapsedTicks - allocatedTicks;
        }
    }
}
