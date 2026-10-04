// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Diagnostics;

/// <summary>Experiment switches read once from the environment; every one defaults to the current behaviour.</summary>
public static class ExperimentKnobs
{
    static ExperimentKnobs() => ThreadNicer.Start(PoolNice, RocksNice, ThreadReportSeconds);

    /// <summary>Nice value for thread-pool workers (0 leaves them alone); see <see cref="ThreadNicer"/>.</summary>
    public static readonly int PoolNice = Int("NETHERMIND_EXP_POOL_NICE", 0);

    /// <summary>Nice value for RocksDB background threads (0 leaves them alone).</summary>
    public static readonly int RocksNice = Int("NETHERMIND_EXP_ROCKS_NICE", 0);

    /// <summary>Every N seconds, print on-CPU and run-queue wait per thread name (0 = off); see <see cref="ThreadNicer"/>.</summary>
    public static readonly int ThreadReportSeconds = Int("NETHERMIND_EXP_THREAD_REPORT", 0);

    /// <summary>The newPayload request starts the block's prewarm on a provisional block right after decoding its transactions.</summary>
    public static readonly bool EarlyPrewarm = On("NETHERMIND_EXP_EARLY_PREWARM");

    /// <summary>Trie warmer processors allowed while the block's transactions execute (0 = no cap); all of them after.</summary>
    public static readonly int TrieWarmExecutionCap = Int("NETHERMIND_EXP_TRIE_WARM_EXEC_CAP", 0);

    /// <summary>RocksDB compaction threads (background compactions and the low-priority pool); 0 keeps ProcessorCount.</summary>
    public static readonly int RocksCompactionThreads = Int("NETHERMIND_EXP_ROCKS_COMPACTION_THREADS", 0);

    /// <summary>RocksDB max subcompactions; 0 follows the compaction threads.</summary>
    public static readonly int RocksSubcompactions = Int("NETHERMIND_EXP_ROCKS_SUBCOMPACTIONS", 0);

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

    /// <summary>Storage discovery goes on for a candidate the main thread is executing, not only for ones ahead of it.</summary>
    public static readonly bool DiscoveryWhileRunning = On("NETHERMIND_EXP_DISCOVERY_WHILE_RUNNING");

    /// <summary>A discovery run that fails on the placeholder 1 is run again with every byte 0x01, so packed fields read non-zero.</summary>
    public static readonly bool DiscoveryRetryPlaceholder = On("NETHERMIND_EXP_DISCOVERY_RETRY_PLACEHOLDER");

    /// <summary>Each discovery candidate warms its own new cells as soon as its run ends, not at the round's barrier.</summary>
    public static readonly bool DiscoveryWarmPerCandidate = On("NETHERMIND_EXP_DISCOVERY_WARM_PER_CANDIDATE");

    /// <summary>ProcessingCores=Dedicated also works on a CPU with one kind of core, treating every core as a performance core.</summary>
    public static readonly bool DedicatedNonHybrid = On("NETHERMIND_EXP_DEDICATED_NONHYBRID");

    /// <summary>From the end of a block's transactions to its state root, an idle group runner spins this long for more work before leaving; 0 = off.</summary>
    public static readonly int RunnerLingerUs = Int("NETHERMIND_EXP_RUNNER_LINGER_US", 0);

    /// <summary>With the dedicated core, a janitor thread keeps every thread but the processing one off that core.</summary>
    public static readonly bool DedicatedExclude = On("NETHERMIND_EXP_DEDICATED_EXCLUDE");

    /// <summary>The background blooms use at most this many workers and the receipts root is built serially; 0 = off.</summary>
    public static readonly int ReceiptsDegree = Int("NETHERMIND_EXP_RECEIPTS_DOP", 0);

    /// <summary>Early sender recovery starts after the payload's transactions root is joined rather than before.</summary>
    public static readonly bool RecoveryAfterRoot = On("NETHERMIND_EXP_RECOVERY_AFTER_ROOT");

    /// <summary>RocksDB's compaction pool runs at lowered CPU priority (1) and I/O priority too (2); 0 = off.</summary>
    public static readonly int RocksDbLowPriority = Int("NETHERMIND_EXP_ROCKSDB_LOW_PRIORITY", 0);

    /// <summary>A prewarm run that makes this many backing-store storage reads hands its transaction to storage discovery; 0 = off.</summary>
    public static readonly int DiscoveryOnMisses = Int("NETHERMIND_EXP_DISCOVERY_ON_MISSES", 0);

    /// <summary>A heavy transaction is warmed once with expensive precompiles answered by placeholders and computed in parallel.</summary>
    public static readonly bool PrecompileLookahead = On("NETHERMIND_EXP_PRECOMPILE_LOOKAHEAD");

    /// <summary>MODEXP whose exponent is a known prime modulus minus two is computed as a modular inverse.</summary>
    public static readonly bool ModExpInvert = On("NETHERMIND_EXP_MODEXP_INVERT");

    /// <summary>The block-end account insert hashes each top-level subtree in the job that set it.</summary>
    public static readonly bool HashAfterSet = On("NETHERMIND_EXP_HASH_AFTER_SET");

    /// <summary>When at least this many of the root's children are dirty, hash two nibbles down (up to 256 units); 0 = off.</summary>
    public static readonly int HashTwoLevels = Int("NETHERMIND_EXP_HASH_TWO_LEVELS", 0);

    /// <summary>A cancelled prewarm run stops at its next backing-store read, not at the EVM's next cancellation poll.</summary>
    public static readonly bool PrewarmCancelAtReads = On("NETHERMIND_EXP_PREWARM_CANCEL_AT_READS");

    /// <summary>Bulk set and subtree hashing fan out on pool workers of their own, outside the block's worker group.</summary>
    public static readonly bool MerkleDetached = On("NETHERMIND_EXP_MERKLE_DETACHED");

    /// <summary>Early sender recovery runs on at most this many workers of the payload's group; 0 = the whole group.</summary>
    public static readonly int RecoveryWorkers = Int("NETHERMIND_EXP_RECOVERY_WORKERS", 0);
}
