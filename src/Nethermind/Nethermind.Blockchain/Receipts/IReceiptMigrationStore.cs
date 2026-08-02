// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptMigrationStore : IReceiptStorage
    {
        TxReceipt?[] GetForMigration(ulong blockNumber, Hash256 blockHash);
        void InsertForMigration(Block block, TxReceipt[] receipts);
    }
}
