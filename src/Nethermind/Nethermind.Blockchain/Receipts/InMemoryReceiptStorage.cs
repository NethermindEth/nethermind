//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public class InMemoryReceiptStorage : IReceiptStorage
    {
        private readonly ConcurrentDictionary<Keccak, TxReceipt[]> _receipts = new ConcurrentDictionary<Keccak, TxReceipt[]>();
        
        private readonly ConcurrentDictionary<Keccak, TxReceipt> _transactions = new ConcurrentDictionary<Keccak, TxReceipt>();

        public Keccak Find(Keccak txHash)
        {
            _transactions.TryGetValue(txHash, out var receipt);
            return receipt?.BlockHash;
        }

        public TxReceipt[] Get(Block block)
        {
            _receipts.TryGetValue(block.Hash, out var receipts);
            return receipts;
        }

        public void Insert(Block block, params TxReceipt[] txReceipts)
        {
            _receipts[block.Hash] = txReceipts;
            for (int i = 0; i < txReceipts.Length; i++)
            {
                var txReceipt = txReceipts[i];
                _transactions[txReceipt.TxHash] = txReceipt;
            }

            LowestInsertedReceiptBlock = block.Number;
        }

        public long? LowestInsertedReceiptBlock { get; private set; }
        
        public int Count => _receipts.Count;
    }
}