// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptMigrationStore
    {
        void InsertForMigration(Block block, TxReceipt[] receipts);
    }
}
