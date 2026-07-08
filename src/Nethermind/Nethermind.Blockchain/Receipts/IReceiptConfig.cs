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

    [ConfigItem(Description = "Whether receipt and canonical transaction index writes are persisted by a background writer instead of synchronously on the block-processing and engine API paths. Reads are always served consistently from an in-memory overlay until flushed. Safe across an unclean shutdown: the background writer drains within a few blocks of the head, well inside the state-persistence lag (`Pruning.MinUnpersistedBlockCount`), so any block whose state is persisted has already had its receipts written, and any newer block is re-executed on restart (regenerating its receipts and transaction index). Keep `ReceiptsMaxDeferredWrites` well below that lag.", DefaultValue = "true")]
    bool DeferredPersistence { get; set; }

    [ConfigItem(Description = "Whether block body writes are also deferred by the background writer. Off by default: unlike receipts, a body cannot be regenerated locally on restart (it is a processing input, not an output), so a body lost on an unclean shutdown must be re-downloaded from peers. Only has an effect when `DeferredPersistence` is enabled.", DefaultValue = "false")]
    bool DeferBlockBodyPersistence { get; set; }

    [ConfigItem(Description = "The maximum number of queued deferred write items before block processing backpressures. Counts individual writes, not blocks (a block enqueues up to three), so the block headroom is roughly a third of this value. Must stay well below `Pruning.MinUnpersistedBlockCount` so restart re-execution always covers anything lost on an unclean shutdown.", DefaultValue = "8", HiddenFromDocs = true)]
    int MaxDeferredBlocks { get; set; }

    [ConfigItem(Description =
        """
        The maximum block range (toBlock - fromBlock + 1) allowed in a single `eth_getLogs` request.
        Requests exceeding this range are rejected with an "invalid params" (-32602) error.
        Set to 0 to disable the limit. Value is ignored (no limits) if log index is enabled.
        """, DefaultValue = "1000")]
    int MaxBlockDepth { get; set; }
}
