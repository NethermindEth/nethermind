// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Processing;

/// <summary>Experiment only: what warming invalidated footprints again came to, summed over the process.</summary>
/// <remarks>NETHERMIND_EXP_REWARM=0 turns the re-warm off.</remarks>
internal static class RewarmCounters
{
    /// <summary>Settable for tests.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_EXP_REWARM") != "0";

    /// <summary>How many sweepers a block starts once its warm pass has handed out its jobs; NETHERMIND_EXP_REWARM_SWEEPERS overrides.</summary>
    public static int Sweepers { get; } = int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_EXP_REWARM_SWEEPERS"), out int sweepers) && sweepers > 0
        ? sweepers
        : DefaultSweepers;

    private const int DefaultSweepers = 1;

    public static long Marked, Unchanged, Stored, Dropped, Overtaken, Ticks;

    /// <summary>
    /// Experiment only: block processing reports the storage writes of the transactions it executes, so the footprints
    /// that read those slots are warmed again on the values the block really leaves. NETHERMIND_EXP_REWARM_FEEDBACK=0 off.
    /// </summary>
    /// <remarks>Settable for tests.</remarks>
    public static bool Feedback { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_EXP_REWARM_FEEDBACK") != "0";

    public static long FeedbackTxs, FeedbackWrites, FeedbackMarked;
}
