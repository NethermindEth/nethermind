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

    [ConfigItem(Description = "The number of recent blocks to maintain transaction index for. `0` to never remove indices, `-1` to never index.", DefaultValue = "2350000")]
    long? TxLookupLimit { get; set; }

    [ConfigItem(Description = "The max number of blocks per `eth_getLogs` request.", DefaultValue = "10000", HiddenFromDocs = true)]
    int MaxBlockDepth { get; set; }
}
