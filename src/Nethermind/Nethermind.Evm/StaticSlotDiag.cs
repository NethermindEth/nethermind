// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Evm;

/// <summary>
/// Diagnostic counters answering one question: of the SLOADs that miss the block-state caches
/// and load from the tree, what share read a statically-known slot (a PUSH constant in the
/// bytecode) — i.e., what share a bytecode-driven prefetcher could have issued ahead of time?
/// Enabled with --Evm.StaticSlotDiag; the report is logged every 30 seconds. Counters are
/// deliberately not interlocked: lossy counts are fine for a ratio.
/// </summary>
public static class StaticSlotDiag
{
    public static bool IsEnabled { get; private set; }

    private static ILogger s_logger;
    private static Timer? s_reportTimer;

    public static long StaticSloadExecutions;
    public static long DynamicSloadExecutions;
    public static long StaticSloadMisses;
    public static long DynamicSloadMisses;

    [ThreadStatic]
    private static bool t_missedTree;

    public static void Enable(ILogger logger)
    {
        s_logger = logger;
        IsEnabled = true;
        s_reportTimer = new Timer(static _ => Report(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Called by the executing SLOAD site right before the state read.</summary>
    public static void BeginSload() => t_missedTree = false;

    /// <summary>Called by the storage provider when a read had to load from the tree.</summary>
    public static void MarkTreeLoad() => t_missedTree = true;

    /// <summary>Called by the executing SLOAD site after the state read.</summary>
    public static void EndSload(bool staticSlot)
    {
        if (staticSlot)
        {
            StaticSloadExecutions++;
            if (t_missedTree) StaticSloadMisses++;
        }
        else
        {
            DynamicSloadExecutions++;
            if (t_missedTree) DynamicSloadMisses++;
        }
    }

    private static void Report()
    {
        long se = StaticSloadExecutions, de = DynamicSloadExecutions;
        long sm = StaticSloadMisses, dm = DynamicSloadMisses;
        long executions = se + de, misses = sm + dm;
        if (s_logger.IsInfo)
            s_logger.Info(
                $"StaticSlotDiag: SLOAD executions {executions} (static-slot {se}, {Percent(se, executions)}); " +
                $"tree loads {misses} (static-slot {sm}, {Percent(sm, misses)}); " +
                $"miss rate static {Percent(sm, se)} vs dynamic {Percent(dm, de)}");

        static string Percent(long part, long whole) => whole == 0 ? "-" : $"{100.0 * part / whole:F1}%";
    }
}
