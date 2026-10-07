// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Evm.State;

/// <summary>Experiment only: what the predicted storage trees came to, summed over the process.</summary>
public static class PredictedStorageCounters
{
    /// <summary>
    /// NETHERMIND_EXP_FOOTPRINT_ROOTS=dry: the predicted writes are kept and compared with each account's write batch,
    /// and the batch's trie work is timed; no tree is built ahead and nothing is adopted.
    /// </summary>
    public static readonly bool DryRun = false;

    public static long Built, BuiltWrites, BuildTicks, Adopted, Unclaimed, StaleBase, Late;

    // Dry run, by account write batch: predicted and exact (every write as predicted, no predicted write left over at
    // another value), predicted and not, and without a prediction; the writes and trie ticks of each, and per-block maxima.
    public static long DryExactAccounts, DryExactWrites, DryExactTicks, DryExactMaxTicks;
    public static long DryInexactAccounts, DryInexactWrites, DryInexactMatched, DryInexactLeftovers, DryInexactTicks, DryInexactMaxTicks;
    public static long UnpredictedAccounts, UnpredictedWrites, UnpredictedTicks, UnpredictedMaxTicks;

    public static void Max(ref long target, long value)
    {
        long current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
