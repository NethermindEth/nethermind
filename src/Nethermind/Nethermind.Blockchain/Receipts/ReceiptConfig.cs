// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Receipts;

public class ReceiptConfig : IReceiptConfig
{
    public bool StoreReceipts { get; set; } = true;
    public bool ReceiptsMigration { get; set; } = false;
    public int ReceiptsMigrationDegreeOfParallelism { get; set; } = 0;
    public bool ForceReceiptsMigration { get; set; } = false;
    public bool CompactReceiptStore { get; set; } = true;
    public bool CompactTxIndex { get; set; } = true;
    public ulong? TxLookupLimit { get; set; } = 2350000ul;
    public int MaxBlockDepth { get; set; } = 1000;
}
