// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Diagnostics;

/// <summary>Experiment switches read once from the environment; every one defaults to the current behaviour.</summary>
public static class ExperimentKnobs
{
    private static bool On(string name) => Environment.GetEnvironmentVariable(name) == "1";

    private static int Int(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    /// <summary>BulkSet splits into parallel jobs at the top level only, as its comment says.</summary>
    public static readonly bool BulkSetTopLevelOnly = On("NETHERMIND_EXP_BULKSET_TOP_ONLY");

    /// <summary>The trie warmer stops when the block-end write batch starts instead of after the account insert.</summary>
    public static readonly bool StopWarmerAtWriteBatch = On("NETHERMIND_EXP_STOP_WARMER_AT_WRITE_BATCH");

    /// <summary>A late-recovered same-sender run stops at the heavy-chain gas budget, so the rest warm in parallel.</summary>
    public static readonly bool SplitLateHeavyChains = On("NETHERMIND_EXP_SPLIT_LATE_HEAVY");

    /// <summary>When at least this many of the root's children are dirty, hash two nibbles down (up to 256 units); 0 = off.</summary>
    public static readonly int HashTwoLevels = Int("NETHERMIND_EXP_HASH_TWO_LEVELS", 0);
}
