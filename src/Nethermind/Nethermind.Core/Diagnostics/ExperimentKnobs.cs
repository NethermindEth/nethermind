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

    /// <summary>Gas limit above which a transaction is a storage-discovery candidate; 0 keeps the built-in threshold.</summary>
    public static readonly long DiscoveryGasThreshold = Int("NETHERMIND_EXP_DISCOVERY_GAS", 0);

    /// <summary>MULMOD with a four-limb modulus seen twice in a row uses a cached Barrett reducer.</summary>
    public static readonly bool MulModBarrett = On("NETHERMIND_EXP_MULMOD_BARRETT");

    /// <summary>The payload's transactions-trie root is computed on the request thread rather than through Task.Run.</summary>
    public static readonly bool TxRootInline = On("NETHERMIND_EXP_TXROOT_INLINE");

    /// <summary>How many pool work items spin at the start of an engine request to wake idle cores; 0 = off.</summary>
    public static readonly int PreWakeWorkers = Int("NETHERMIND_EXP_PREWAKE", 0);

    /// <summary>How long each pre-wake work item spins, in microseconds.</summary>
    public static readonly int PreWakeMicroseconds = Int("NETHERMIND_EXP_PREWAKE_US", 300);

    /// <summary>Committed account values are applied and hashed into an early state trie while the block executes.</summary>
    public static readonly bool EarlyAccountApply = On("NETHERMIND_EXP_EARLY_ACCOUNTS");

    /// <summary>Accounts applied to the early state trie between hashes (when its queue is not idle).</summary>
    public static readonly int EarlyAccountHashEvery = Int("NETHERMIND_EXP_EARLY_ACCOUNTS_HASH_EVERY", 256);

    /// <summary>The early account applier runs under SCHED_IDLE rather than at normal priority.</summary>
    public static readonly bool EarlyAccountIdlePriority = On("NETHERMIND_EXP_EARLY_ACCOUNTS_IDLE");

    /// <summary>The prewarmer also reads the accounts that large calldata names as ABI address words.</summary>
    public static readonly bool CalldataAddressWarm = On("NETHERMIND_EXP_CALLDATA_ADDRESSES");

    /// <summary>When at least this many of the root's children are dirty, hash two nibbles down (up to 256 units); 0 = off.</summary>
    public static readonly int HashTwoLevels = Int("NETHERMIND_EXP_HASH_TWO_LEVELS", 0);
}
