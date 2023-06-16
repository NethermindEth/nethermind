// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Blockchain.Receipts;

public interface IReceiptConfig : IConfig
{
    [ConfigItem(Description = "If set to 'false' then transaction receipts will not be stored in the database after a new block is processed. This setting is independent from downloading receipts in fast sync mode.", DefaultValue = "true")]
    bool StoreReceipts { get; set; }

    [ConfigItem(Description = "If set to 'true' then receipts db will be migrated to new schema.", DefaultValue = "false")]
    bool ReceiptsMigration { get; set; }

    [ConfigItem(Description = "If set to 'true' then reduce receipt db size at expense of rpc performance.", DefaultValue = "true")]
    bool CompactReceiptStore { get; set; }

    [ConfigItem(Description = "If set to 'true' then reduce receipt tx index db size at expense of rpc performance.", DefaultValue = "true")]
    bool CompactTxIndex { get; set; }

    [ConfigItem(Description = "Number of recent blocks to maintain transaction index. 0 to never remove tx index. -1 to never index.", DefaultValue = "2350000")]
    long? TxLookupLimit { get; set; }

    [ConfigItem(Description = "Max num of block per eth_getLogs request.", DefaultValue = "10000", HiddenFromDocs = true)]
    int MaxBlockDepth { get; set; }

    [ConfigItem(Description = "Force receipt recovery if its not able to detect it.", DefaultValue = "false", HiddenFromDocs = true)]
    bool ForceReceiptsMigration { get; set; }
}
