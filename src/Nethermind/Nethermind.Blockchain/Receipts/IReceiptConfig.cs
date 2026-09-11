// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Blockchain.Receipts;

public interface IReceiptConfig : IConfig
{
    [ConfigItem(Description = "Whether to store receipts after a new block is processed. This setting is independent from downloading receipts in fast sync mode.", DefaultValue = "true")]
    bool StoreReceipts { get; set; }

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
        The maximum block range (toBlock - fromBlock + 1) allowed in a single `eth_getLogs` request.
        Requests exceeding this range are rejected with an "invalid params" (-32602) error.
        Set to 0 to disable the limit. Value is ignored (no limits) if log index is enabled.
        """, DefaultValue = "1000")]
    int MaxBlockDepth { get; set; }
}
