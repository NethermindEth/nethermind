// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Blockchain.Receipts
{
    public interface IReceiptStorage : IReceiptFinder
    {
        void Insert(Block block, params TxReceipt[]? txReceipts) => Insert(block, txReceipts, true);
        void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical);
        long? LowestInsertedReceiptBlockNumber { get; set; }
        long MigratedBlockNumber { get; set; }
        bool HasBlock(long blockNumber, Hash256 hash);
        void EnsureCanonical(Block block);

        /// <summary>
        /// Receipts for a block are inserted
        /// </summary>
        event EventHandler<BlockReplacementEventArgs> ReceiptsInserted;
    }
}
