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
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public class NullReceiptStorage : IReceiptStorage
    {
        public static NullReceiptStorage Instance { get; } = new();

        public Keccak? FindBlockHash(Keccak hash) => null;

        private NullReceiptStorage()
        {
        }

        public void Insert(Block block, params TxReceipt[] txReceipts) { }

        public TxReceipt[] Get(Block block) => Array.Empty<TxReceipt>();
        public TxReceipt[] Get(Keccak blockHash) => Array.Empty<TxReceipt>();
        public bool CanGetReceiptsByHash(long blockNumber) => true;

        public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator)
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

        public event EventHandler<ReceiptsEventArgs> ReceiptsInserted
        {
            add { }
            remove { }
        }
    }
}
