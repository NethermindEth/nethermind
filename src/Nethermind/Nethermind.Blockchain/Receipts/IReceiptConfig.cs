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

    [ConfigItem(Description = "Whether receipt and canonical transaction-index writes are persisted by a background writer instead of synchronously on the block-processing and engine API paths. Reads are served from an in-memory overlay until flushed, and a state-persistence barrier makes a block's data durable before its state, so an unclean shutdown never leaves persisted state without it.", DefaultValue = "true")]
    bool DeferredPersistence { get; set; }

    [ConfigItem(Description = "Whether block body and block-access-list writes are also deferred (only when `DeferredPersistence` is enabled). A body/BAL is a processing input rather than a regenerable output; the state-persistence barrier keeps it durable before its block's state is persisted, and a not-yet-persisted one lost on an unclean shutdown is re-downloaded from peers.", DefaultValue = "true")]
    bool DeferBlockBodyPersistence { get; set; }

    [ConfigItem(Description = "Maximum number of queued deferred block-data writes before block processing backpressures to synchronous. A block enqueues up to three writes (body, receipts, canonical index). Bounds the pending-overlay memory.", DefaultValue = "32", HiddenFromDocs = true)]
    int MaxDeferredWrites { get; set; }

    [ConfigItem(Description =
        """
        The maximum block range (toBlock - fromBlock + 1) allowed in a single `eth_getLogs` request.
        Requests exceeding this range are rejected with an "invalid params" (-32602) error.
        Set to 0 to disable the limit. Value is ignored (no limits) if log index is enabled.
        """, DefaultValue = "1000")]
    int MaxBlockDepth { get; set; }
}
