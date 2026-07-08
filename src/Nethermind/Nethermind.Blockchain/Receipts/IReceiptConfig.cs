// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Blockchain.Receipts;

public interface IReceiptConfig : IConfig
{
    [ConfigItem(Description = "Whether to store receipts after a new block is processed. This setting is independent from downloading receipts in fast sync mode.", DefaultValue = "true")]
    bool StoreReceipts { get; set; }

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

    [ConfigItem(Description = "Whether receipt, canonical transaction index and block body writes are persisted by a background writer instead of on the block-processing and engine API paths. Reads are always served consistently from an in-memory overlay until flushed. Off by default: a crash can lose writes still queued at the moment of failure, and startup recovery of that window is not yet implemented, so enabling this trades durability of the most recent few blocks for lower latency.", DefaultValue = "false")]
    bool DeferredPersistence { get; set; }

    [ConfigItem(Description = "The maximum number of queued deferred write items before block processing backpressures. Counts individual writes, not blocks (a block enqueues up to three), so the block headroom is roughly a third of this value.", DefaultValue = "8", HiddenFromDocs = true)]
    int MaxDeferredBlocks { get; set; }

    [ConfigItem(Description =
        """
        The maximum block range (toBlock - fromBlock + 1) allowed in a single `eth_getLogs` request.
        Requests exceeding this range are rejected with an "invalid params" (-32602) error.
        Set to 0 to disable the limit. Value is ignored (no limits) if log index is enabled.
        """, DefaultValue = "1000")]
    int MaxBlockDepth { get; set; }
}
