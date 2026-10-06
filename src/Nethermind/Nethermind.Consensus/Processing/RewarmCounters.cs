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

    public static long Marked, Unchanged, Stored, Dropped, Overtaken, Ticks;
}
