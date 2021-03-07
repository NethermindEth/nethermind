//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
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
            _receipts.TryGetValue(blockHash, out var receipts);
            return receipts;
        }

        public bool CanGetReceiptsByHash(long blockNumber) => true;
        public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator)
        {
            if (_allowReceiptIterator && _receipts.TryGetValue(blockHash, out var receipts))
            {
#pragma warning disable 618
                iterator = new ReceiptsIterator(ReceiptStorageDecoder.Instance.Encode(receipts, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts).Bytes, new MemDb());
#pragma warning restore 618
                return true;
            }
            else
            {
                iterator = new ReceiptsIterator();
                return false;
            }
        }

        public void Insert(Block block, params TxReceipt[] txReceipts)
        {
            _receipts[block.Hash] = txReceipts;
            for (int i = 0; i < txReceipts.Length; i++)
            {
                var txReceipt = txReceipts[i];
                txReceipt.BlockHash = block.Hash;
                _transactions[txReceipt.TxHash] = txReceipt;
            }
            ReceiptsInserted?.Invoke(this, new ReceiptsEventArgs(block.Header, txReceipts));
        }

        public long? LowestInsertedReceiptBlockNumber { get; set; }

        public long MigratedBlockNumber { get; set; }

        public int Count => _transactions.Count;
        
        public event EventHandler<ReceiptsEventArgs> ReceiptsInserted;
    }
}
