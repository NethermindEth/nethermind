// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public class InMemoryReceiptStorage : IReceiptStorage
    {
        private readonly bool _allowReceiptIterator;
        private readonly IBlockTree? _blockTree;
        private readonly ConcurrentDictionary<Hash256, TxReceipt[]> _receipts = new();

        private readonly ConcurrentDictionary<Hash256, TxReceipt> _transactions = new();

#pragma warning disable CS0067
        public event EventHandler<BlockReplacementEventArgs> ReceiptsInserted;
#pragma warning restore CS0067

        public InMemoryReceiptStorage(bool allowReceiptIterator = true, IBlockTree? blockTree = null)
        {
            _allowReceiptIterator = allowReceiptIterator;
            _blockTree = blockTree;
            if (_blockTree is not null)
                _blockTree.BlockAddedToMain += BlockTree_BlockAddedToMain;
        }

        private void BlockTree_BlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            EnsureCanonical(e.Block);
            ReceiptsInserted?.Invoke(this, e);
        }

        public Hash256 FindBlockHash(Hash256 txHash)
        {
            _transactions.TryGetValue(txHash, out var receipt);
            return receipt?.BlockHash;
        }

        public TxReceipt[] Get(Block block) => Get(block.Hash);

        public TxReceipt[] Get(Hash256 blockHash)
        {
            if (_receipts.TryGetValue(blockHash, out var receipts))
            {
                return receipts;
            }

            return Array.Empty<TxReceipt>();
        }

        public bool CanGetReceiptsByHash(long blockNumber) => true;
        public bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            if (_allowReceiptIterator && _receipts.TryGetValue(blockHash, out var receipts))
            {
#pragma warning disable 618
                iterator = new ReceiptsIterator(receipts);
#pragma warning restore 618
                return true;
            }
            else
            {
                iterator = new ReceiptsIterator();
                return false;
            }
        }

        public void Insert(Block block, TxReceipt[] txReceipts, bool ensureCanonical = true)
        {
            _receipts[block.Hash] = txReceipts;
            if (ensureCanonical)
            {
                EnsureCanonical(block);
            }
        }

        public bool HasBlock(long blockNumber, Hash256 hash)
        {
            return _receipts.ContainsKey(hash);
        }

        public void EnsureCanonical(Block block)
        {
            TxReceipt[] txReceipts = Get(block);
            for (int i = 0; i < txReceipts.Length; i++)
            {
                var txReceipt = txReceipts[i];
                txReceipt.BlockHash = block.Hash;
                _transactions[txReceipt.TxHash] = txReceipt;
            }
        }

        public long? LowestInsertedReceiptBlockNumber { get; set; }

        public long MigratedBlockNumber { get; set; }

        public int Count => _transactions.Count;
    }
}
