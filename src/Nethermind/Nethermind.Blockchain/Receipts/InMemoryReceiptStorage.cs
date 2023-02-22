// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Receipts
{
    public class InMemoryReceiptStorage : IReceiptStorage
    {
        private readonly bool _allowReceiptIterator;
        private readonly ConcurrentDictionary<Keccak, TxReceipt[]> _receipts = new();

        private readonly ConcurrentDictionary<Keccak, TxReceipt> _transactions = new();

        public InMemoryReceiptStorage(bool allowReceiptIterator = true)
        {
            _allowReceiptIterator = allowReceiptIterator;
        }

        public Keccak FindBlockHash(Keccak txHash)
        {
            _transactions.TryGetValue(txHash, out var receipt);
            return receipt?.BlockHash;
        }

        public TxReceipt[] Get(Block block) => Get(block.Hash);

        public TxReceipt[] Get(Keccak blockHash)
        {
            if (_receipts.TryGetValue(blockHash, out var receipts))
            {
                return receipts;
            }

            return new TxReceipt[] { };
        }

        public bool CanGetReceiptsByHash(long blockNumber) => true;
        public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator)
        {
            if (_allowReceiptIterator && _receipts.TryGetValue(blockHash, out var receipts))
            {
#pragma warning disable 618
                ReceiptStorageDecoder decoder = ReceiptStorageDecoder.Instance;
                RlpStream stream = new RlpStream(decoder.GetLength(receipts, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts));
                decoder.Encode(stream, receipts, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
                iterator = new ReceiptsIterator(stream.Data, new MemDb());
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

        public bool HasBlock(Keccak hash)
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
