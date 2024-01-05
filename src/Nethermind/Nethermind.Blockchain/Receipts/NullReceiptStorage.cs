// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public class NullReceiptStorage : IReceiptStorage
    {
        public static NullReceiptStorage Instance { get; } = new();

#pragma warning disable CS0067
        public event EventHandler<BlockReplacementEventArgs> ReceiptsInserted;
#pragma warning restore CS0067

        public Hash256? FindBlockHash(Hash256 hash) => null;

        private NullReceiptStorage()
        {
        }

        public void Insert(Block block, TxReceipt[] txReceipts, bool ensureCanonical) { }

        public TxReceipt[] Get(Block block) => Array.Empty<TxReceipt>();
        public TxReceipt[] Get(Hash256 blockHash) => Array.Empty<TxReceipt>();
        public bool CanGetReceiptsByHash(long blockNumber) => true;

        public bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            iterator = new ReceiptsIterator();
            return false;
        }

        public long? LowestInsertedReceiptBlockNumber
        {
            get => 0;
            set { }
        }

        public long MigratedBlockNumber { get; set; } = 0;

        public bool HasBlock(long blockNumber, Hash256 hash)
        {
            return false;
        }

        public void EnsureCanonical(Block block)
        {
        }
    }
}
