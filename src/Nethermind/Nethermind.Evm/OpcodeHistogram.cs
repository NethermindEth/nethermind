// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Nethermind.Evm;

/// <summary>
/// Diagnostic per-opcode execution counter for profiling interpreter dispatch frequency.
/// Enabled by setting the NETHERMIND_OPCODE_HISTOGRAM environment variable to an output
/// file path (or "1" for ./opcode-histogram.txt). The report is rewritten every 30 seconds
/// and on process exit, sorted by execution count.
/// When the variable is unset, <see cref="IsEnabled"/> is a JIT-time constant false and the
/// recording call site in the interpreter loop is eliminated entirely.
/// Increments are deliberately not interlocked: concurrent EVM threads may lose counts, which
/// is acceptable because the output is used for frequency ranking, not exact totals.
/// </summary>
public static class OpcodeHistogram
{
    public static readonly bool IsEnabled;

    private static readonly long[]? s_counts;
    private static readonly long[]? s_pairCounts;
    private static readonly string? s_outputPath;
    private static readonly Timer? s_flushTimer;

    // Holds (previous opcode + 1) so the thread-static zero default means "no previous opcode yet".
    // Tracking per thread rather than per frame means a pair can span a frame switch on the same
    // thread (e.g. CALL → first opcode of the callee); that noise is negligible for ranking.
    [ThreadStatic]
    private static int t_previousOpcodePlusOne;

    static OpcodeHistogram()
    {
        string? setting = Environment.GetEnvironmentVariable("NETHERMIND_OPCODE_HISTOGRAM");
        if (string.IsNullOrEmpty(setting))
            return;

        IsEnabled = true;
        s_counts = new long[byte.MaxValue + 1];
        s_pairCounts = new long[(byte.MaxValue + 1) * (byte.MaxValue + 1)];
        s_outputPath = setting == "1" ? "opcode-histogram.txt" : setting;
        s_flushTimer = new Timer(static _ => Flush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Flush();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Record(Instruction instruction)
    {
        s_counts![(int)instruction]++;
        int previousPlusOne = t_previousOpcodePlusOne;
        if (previousPlusOne != 0)
            s_pairCounts![((previousPlusOne - 1) << 8) | (int)instruction]++;
        t_previousOpcodePlusOne = (int)instruction + 1;
    }

    private static void Flush()
    {
        // Diagnostics must never take down the node; swallow I/O failures.
        try
        {
            File.WriteAllText(s_outputPath!, BuildReport());
        }
        catch (Exception)
        {
        }
    }

    private static string BuildReport()
    {
        long[] counts = s_counts!;
        long total = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            total += counts[i];
        }

        int[] order = new int[counts.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) => counts[b].CompareTo(counts[a]));

        StringBuilder report = new();
        report.AppendLine($"# Opcode execution histogram — total {total:N0} opcodes");
        report.AppendLine($"# {"opcode",-16} {"count",16} {"share",8}");
        foreach (int opcode in order)
        {
            long count = counts[opcode];
            if (count == 0)
                continue;
            report.AppendLine($"{(Instruction)opcode,-18} {count,16:N0} {(double)count / total,8:P2}");
        }

        AppendTopPairs(report, total);
        return report.ToString();
    }

    private static void AppendTopPairs(StringBuilder report, long totalOpcodes)
    {
        const int TopPairCount = 40;

        long[] pairCounts = s_pairCounts!;
        int[] order = new int[pairCounts.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) => pairCounts[b].CompareTo(pairCounts[a]));

        report.AppendLine();
        report.AppendLine($"# Top {TopPairCount} opcode pairs (fusion candidates)");
        report.AppendLine($"# {"pair",-30} {"count",16} {"share",8}");
        for (int rank = 0; rank < TopPairCount; rank++)
        {
            int pair = order[rank];
            long count = pairCounts[pair];
            if (count == 0)
                break;
            string pairName = $"{(Instruction)(pair >> 8)} -> {(Instruction)(pair & 0xFF)}";
            report.AppendLine($"{pairName,-32} {count,16:N0} {(double)count / totalOpcodes,8:P2}");
        }
    }
}
