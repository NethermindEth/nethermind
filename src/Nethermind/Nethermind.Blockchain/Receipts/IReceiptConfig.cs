// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Blockchain.Receipts;

public interface IReceiptConfig : IConfig
{
    [ConfigItem(Description = "Whether to persist receipts after a new block is processed. When false, recent receipts are retained in bounded memory until finalization. This setting is independent from downloading receipts in fast sync mode.", DefaultValue = "true")]
    bool StoreReceipts { get; set; }

    /// <summary>Maximum estimated transient receipt memory in MiB when storage is disabled. Default: 128.</summary>
    [ConfigItem(Description = "Maximum estimated receipt memory in MiB when StoreReceipts is false. The same separate budget bounds queued canonical notifications; notifications exceeding that delivery budget are skipped with a warning. Receipts are removed at finalization; if the budget is reached first, oldest receipts are evicted and oversized blocks are not retained. Must be positive. Queries and reorg logs outside the retained window may be unavailable.", DefaultValue = "128")]
    int MaxTransientReceiptStorageSize { get; set; }


    [ConfigItem(Description = "Whether receipt bodies are derived from state instead of persisted: their write is skipped, and a query re-executes the block over its parent state, serving the result only when it reproduces the block header's receipts root. Bodies already on disk are still served; pre-Byzantium bodies and the transaction index are always written. A skipped body is retained in memory until history capture durably covers its block, and is persisted if capture permanently stops (see the error log then), so a capture breakdown does not lose receipts. Intended for archive nodes: requires state history for the queried block, and peers are told no receipts are available. A query that misses the cache costs a full block execution, so a public endpoint should be rate limited; concurrency is bounded by JsonRpc.EthModuleConcurrentInstances.", DefaultValue = "false")]
    bool DeriveFromState { get; set; }

    [ConfigItem(Description = "Whether to migrate the receipts database to the new schema.", DefaultValue = "false")]
    bool ReceiptsMigration { get; set; }

    [ConfigItem(Description = "The degree of parallelism during receipt migration.", DefaultValue = "0", HiddenFromDocs = true)]
    int ReceiptsMigrationDegreeOfParallelism { get; set; }

    [ConfigItem(Description = "Force receipt recovery if its not able to detect it.", DefaultValue = "false", HiddenFromDocs = true)]
    bool ForceReceiptsMigration { get; set; }

    [ConfigItem(Description = "Whether to compact receipts database size at the expense of RPC performance.", DefaultValue = "true")]
    bool CompactReceiptStore { get; set; }

    /// <summary>
    /// Whether compact receipts store the block's transaction hashes, so that log queries do not read the block body.
    /// Default: <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, new receipts are written with the hashes and a background pass adds them to older
    /// receipts. A version without this option cannot read those values. When <see langword="false"/>, receipts are
    /// written in the earlier format and the pass does not run.
    /// </remarks>
    [ConfigItem(Description = "Whether to store the transaction hashes with compact receipts, so that `eth_getLogs` does not read block bodies. Older receipts get them in the background; the pass yields to block processing and continues after a restart. When disabled, receipts are written in the format that earlier versions read.", DefaultValue = "true")]
    bool StoreTxHashes { get; set; }

    [ConfigItem(Description = "Whether to compact receipts transaction index database size at the expense of RPC performance.", DefaultValue = "true")]
    bool CompactTxIndex { get; set; }

    [ConfigItem(Description = "The number of recent blocks to maintain transaction index for. `0` to never remove indices, `18446744073709551615` to never index.", DefaultValue = "2350000")]
    ulong? TxLookupLimit { get; set; }

    [ConfigItem(Description = "Whether receipt, canonical transaction-index, block-body, and block-access-list writes are persisted by a background writer instead of synchronously on the block-processing and engine API paths. Reads are served from an in-memory overlay until flushed, and a state-persistence barrier makes a block's data durable before its state, so an unclean shutdown never leaves persisted state without it.", DefaultValue = "true")]
    bool DeferredPersistence { get; set; }

    [ConfigItem(Description = "Maximum number of queued deferred block-data writes before block processing backpressures to synchronous. A BAL-enabled block can enqueue up to five writes (body, suggested BAL, receipts, generated BAL, canonical index), although superseded pending writes are coalesced. Bounds the pending-overlay memory.", DefaultValue = "128", HiddenFromDocs = true)]
    int MaxDeferredWrites { get; set; }

    [ConfigItem(Description =
        """
        The maximum number of blocks a single `eth_getLogs` or `eth_getFilterLogs` request may read receipts of
        one by one. Blocks the log index can answer for do not count towards it, so on an indexed node the limit
        only bounds what falls back to a sequential read. Requests exceeding it are rejected with an
        "invalid params" (-32602) error. Set to 0 to disable the limit.
        """, DefaultValue = "10000")]
    int MaxBlockDepth { get; set; }
}
