// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public interface ITxPoolConfig : IConfig
{
    [ConfigItem(DefaultValue = "5", Description = "The average percentage of transaction hashes from persistent broadcast sent to a peer together with hashes of the last added transactions.")]
    int PeerNotificationThreshold { get; set; }

    [ConfigItem(DefaultValue = "70", Description = "The minimal percentage of the current base fee that must be surpassed by the max fee (`max_fee_per_gas`) for the transaction to be broadcasted.")]
    int MinBaseFeeThreshold { get; set; }

    [ConfigItem(DefaultValue = "2048", Description = "The max number of transactions held in the mempool (the more transactions in the mempool, the more memory used).")]
    int Size { get; set; }

    [ConfigItem(Description = "The blobs support mode.", DefaultValue = nameof(BlobsSupportMode.StorageWithReorgs))]
    BlobsSupportMode BlobsSupport { get; set; }

    [ConfigItem(
        DefaultValue = "15",
        Description = "The EIP-8070 full-provider selection probability for normal sparse blob-pool nodes, in percent. Values are clamped to the protocol-compliant range `15..100`. Nodes with at least 64 custody columns act as supernodes and request every announced cell.")]
    int SparseBlobProviderProbabilityPercent { get; set; }

    [ConfigItem(DefaultValue = "16384", Description = "The max number of full blob transactions stored in the database (increasing the number of transactions in the blob pool also results in higher memory usage). The default value uses max 13GB for 6 blobs where one blob is 2GB (16386 * 128KB).")]
    int PersistentBlobStorageSize { get; set; }

    [ConfigItem(DefaultValue = "256", Description = "The max number of full blob transactions cached in memory. The default value uses max 200MB for 6 blobs where one blob is 33MB (256 * 128KB)")]
    int BlobCacheSize { get; set; }

    [ConfigItem(DefaultValue = "512", Description = "The max number of full blob transactions stored in memory. Used only if persistent storage is disabled.")]
    int InMemoryBlobPoolSize { get; set; }

    [ConfigItem(DefaultValue = "0", Description = "The max number of pending transactions per single sender. `0` to lift the limit.")]
    int MaxPendingTxsPerSender { get; set; }

    [ConfigItem(DefaultValue = "300000", Description = "EIP-8141 `MAX_VERIFY_GAS`: the max gas a frame transaction's validation prefix and signature verification may cost for it to be accepted into the public mempool. `0` to lift the limit, though a simulated prefix and per-signature recovery stay capped frame by frame at the fixed `Eip8141Constants.MaxVerifyGas` either way. Raise it only on a test network.")]
    ulong FrameTxMaxVerifyGas { get; set; }

    [ConfigItem(DefaultValue = "500000", Description = "EIP-8141 `MAX_VERIFY_STATE_GAS`: the max state gas a frame transaction's validation prefix may budget across its `limits.state` for it to be accepted into the public mempool. EIP-8250 charges a first use of each keyed nonce key 97,920 state gas, so at the default value a first-use set of more than five keys does not propagate. `0` to lift the limit. Raise it only on a test network.")]
    ulong FrameTxMaxVerifyStateGas { get; set; }

    [ConfigItem(DefaultValue = "250", Description = "The max time, in milliseconds, one EIP-8141 validation-prefix simulation may run before the transaction is rejected. A local submission may also wait this long for a busy simulator, so its ceiling is twice this value. `0` to lift the limit.")]
    int FrameTxSimulationTimeoutMs { get; set; }

    [ConfigItem(DefaultValue = "1000", Description = "The total time, in milliseconds, spent simulating EIP-8141 validation prefixes per chain head. Once spent, opaque frame transactions are rejected until the next head. `0` to lift the limit.")]
    int FrameTxSimulationBudgetPerHeadMs { get; set; }

    [ConfigItem(DefaultValue = "1", Description = "EIP-8141: the number of distinct chain heads a frame transaction may fail to approve any payment across, during block production, before the pool evicts it. Some failures turn on head state that only a new head can clear (an out-of-range recent-root reference, a SENDER frame reached before its approval), so the budget is counted per head: repeated production passes against the same head spend a single unit. A value above `1` keeps a transiently-failing transaction for that many heads at the cost of re-simulating its validation prefix once per head; `1` (or below) evicts on the first failed attempt, the pre-budget behaviour. The budget is counted per pool residency, not per transaction: an evicted transaction may be resubmitted, and a resubmitted one is granted a fresh budget, because the failures it buys heads for turn on chain state that the time out of the pool may have cleared. Raising it therefore multiplies by that factor the production-time simulation a peer can provoke by re-gossiping a transaction that keeps failing.")]
    int FrameTxEvictionRetryBudget { get; set; }

    /// <remarks>
    /// The two carried paths are bounded for opposite reasons. A carried validation-prefix simulation costs
    /// a head's work under the pool's head write lock, metered by
    /// <see cref="FrameTxSimulationBudgetPerHeadMs"/>, so an unbounded carry turns a backlog one head cannot
    /// clear into a permanent per-head stall. A carried blob-pool read is cheap per head — it declines before
    /// decoding anything — and it is bounded because a transaction no head ever judges holds its payer's
    /// pending-cost reservation for good; spending the carry is what forces the verdict, at the cost of one
    /// full sidecar decode on that last head alone. So lowering this value makes the blob path decode sooner
    /// and more often, rather than less. One allowance covers both paths, so a transaction alternating
    /// between them cannot carry for twice as long as either alone. Only the carry feeding itself is
    /// bounded: re-arming the budget costs a block that genuinely touched the transaction's dependencies and
    /// would have triggered a revalidation anyway.
    /// </remarks>
    [ConfigItem(DefaultValue = "2", Description = "EIP-8141: the number of *consecutive* chain heads a pending frame transaction whose revalidation reached no verdict may be carried across before the pool stops re-queuing it. A revalidation reaches no verdict when it hit a bound or a fault this node imposed on itself while simulating, or when the blob pool declined to read the transaction's record back, and the two share this one allowance. Any head that revalidates it without deferring it again resets the count. Exhausting the budget on the simulation path neither evicts nor approves the transaction: it stays pending and unjudged. On the blob-pool path exhaustion instead forces a verdict, reading the full record and dropping the transaction if even that cannot be read, so a value set too low turns blob-pool read contention into evictions. `0` stops re-queuing entirely.")]
    int FrameTxRevalidationDeferralBudget { get; set; }

    [ConfigItem(DefaultValue = "16", Description = "The max number of pending blob transactions per single sender. `0` to lift the limit.")]
    int MaxPendingBlobTxsPerSender { get; set; }

    [ConfigItem(DefaultValue = "524288",
        Description = "The max number of cached hashes of already known transactions. Set automatically by the memory hint.")]
    int HashCacheSize { get; set; }

    [ConfigItem(DefaultValue = "null",
        Description = "The max transaction gas allowed.")]

    ulong? GasLimit { get; set; }

    [ConfigItem(DefaultValue = "131072",
        Description = "The max transaction size allowed, in bytes.")]
    long? MaxTxSize { get; set; }

    [ConfigItem(DefaultValue = "1048576",
        Description = "The max blob transaction size allowed, excluding blobs, in bytes.")]
    long? MaxBlobTxSize { get; set; }

    [ConfigItem(DefaultValue = "true", Description = "Whether to require the max fee per blob gas to be greater than or equal to the current blob base fee when adding a blob transaction to the pool.")]
    bool CurrentBlobBaseFeeRequired { get; set; }

    [ConfigItem(DefaultValue = "false",
        Description = "Enable transformation of blob txs between network wrapper version 0x0 (blob proofs) and version 0x1 (cell proofs).",
        HiddenFromDocs = true)]
    bool ProofsTranslationEnabled { get; set; }

    [ConfigItem(DefaultValue = "null",
        Description = "The current transaction pool state reporting interval, in minutes.")]
    int? ReportMinutes { get; set; }

    [ConfigItem(DefaultValue = "false",
        Description = "Accept transactions when not synced.")]
    bool AcceptTxWhenNotSynced { get; set; }

    [ConfigItem(DefaultValue = "true",
        Description = "Add local transactions to persistent broadcast.")]
    bool PersistentBroadcastEnabled { get; set; }

    [ConfigItem(DefaultValue = "0",
        Description = "The minimum priority fee in wei for blob transactions to be accepted into the transaction pool.")]
    UInt256 MinBlobTxPriorityFee { get; set; }
}
