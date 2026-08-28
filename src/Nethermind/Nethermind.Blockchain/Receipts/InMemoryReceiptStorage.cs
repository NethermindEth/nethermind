// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using NonBlocking;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Receipts
{
    public class InMemoryReceiptStorage : IReceiptMigrationStore
    {
        private readonly bool _allowReceiptIterator;
        private readonly IBlockTree? _blockTree;
        private readonly ConcurrentDictionary<Hash256AsKey, TxReceipt[]> _receipts = new();

        private readonly ConcurrentDictionary<Hash256AsKey, TxReceipt> _transactions = new();

        // A receipt does not reliably carry its height - Insert and EnsureCanonical never set it.
        private readonly ConcurrentDictionary<Hash256AsKey, ulong> _blockNumbers = new();

#pragma warning disable CS0067
        public event EventHandler<BlockReplacementEventArgs>? NewCanonicalReceipts;
        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted;
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
            NewCanonicalReceipts?.Invoke(this, e);
        }

        public Hash256 FindBlockHash(Hash256 txHash)
        {
            _transactions.TryGetValue(txHash, out TxReceipt receipt);
            return receipt?.BlockHash;
        }

        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => Get(block.Hash);

        public TxReceipt[] Get(Hash256 blockHash, bool recover = true) =>
            _receipts.TryGetValue(blockHash, out TxReceipt[] receipts) ? receipts : [];

        public bool CanGetReceiptsByHash(ulong blockNumber) => true;
        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            if (_allowReceiptIterator && _receipts.TryGetValue(blockHash, out TxReceipt[] receipts))
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

        public void Insert(Block block, TxReceipt[] txReceipts, bool ensureCanonical = true, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null)
            => Insert(block, txReceipts, null, ensureCanonical, writeFlags, lastBlockNumber);

        public void Insert(Block block, TxReceipt[] txReceipts, IReleaseSpec spec, bool ensureCanonical = true, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null)
        {
            _receipts[block.Hash] = txReceipts;
            _blockNumbers[block.Hash] = block.Number;
            if (ensureCanonical)
            {
                EnsureCanonical(block);
            }

            ReceiptsInserted?.Invoke(this, new(block.Header, txReceipts));
        }

        public void InsertForMigration(Block block, TxReceipt[] receipts) => Insert(block, receipts);

        public TxReceipt?[] GetForMigration(ulong blockNumber, Hash256 blockHash) => Get(blockHash, recover: false);

        public bool HasBlock(ulong blockNumber, Hash256 hash)
            => _receipts.ContainsKey(hash);

        public void EnsureCanonical(Block block)
        {
            TxReceipt[] txReceipts = Get(block);
            for (int i = 0; i < txReceipts.Length; i++)
            {
                TxReceipt txReceipt = txReceipts[i];
                txReceipt.BlockHash = block.Hash;
                _transactions[txReceipt.TxHash] = txReceipt;
            }
        }

        public void RemoveReceipts(Block block)
        {
            _receipts.TryRemove(block.Hash, out _);
            _blockNumbers.TryRemove(block.Hash, out _);
            foreach (Transaction tx in block.Transactions)
            {
                _transactions.TryRemove(tx.Hash, out _);
            }
        }

        /// <summary>Takes the transaction index with it: unlike the persistent store, nothing sweeps behind this one.
        /// </summary>
        public void RemoveReceiptsRange(ulong fromInclusive, ulong toExclusive)
        {
            foreach (KeyValuePair<Hash256AsKey, ulong> entry in _blockNumbers)
            {
                if (entry.Value < fromInclusive || entry.Value >= toExclusive) continue;

                _blockNumbers.TryRemove(entry.Key, out _);
                if (_receipts.TryRemove(entry.Key, out TxReceipt[]? removed))
                {
                    foreach (TxReceipt receipt in removed)
                    {
                        _transactions.TryRemove(receipt.TxHash, out _);
                    }
                }
            }
        }

        public ulong MigratedBlockNumber { get; set; }

        public int Count => _transactions.Count;
    }
}
