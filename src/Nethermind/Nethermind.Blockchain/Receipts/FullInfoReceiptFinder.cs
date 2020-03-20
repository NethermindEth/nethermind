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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public class FullInfoReceiptFinder : IReceiptFinder
    {
        private readonly IReceiptFinder _innerFinder;
        private readonly IReceiptsRecovery _receiptsRecovery;

        public FullInfoReceiptFinder(IReceiptFinder innerFinder, IReceiptsRecovery receiptsRecovery)
        {
            _innerFinder = innerFinder ?? throw new ArgumentNullException(nameof(innerFinder));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
        }
        
        public Keccak FindBlockHash(Keccak txHash) => _innerFinder.FindBlockHash(txHash);

        public TxReceipt[] Get(Block block)
        {
            var receipts = _innerFinder.Get(block);
            if (receipts != null)
            {
                _receiptsRecovery.TryRecover(block, receipts);
            }

            return receipts;
        }

        public TxReceipt[] Get(Keccak blockHash) => _innerFinder.Get(blockHash);
        public bool CanGetReceiptsByHash(long blockNumber) => _innerFinder.CanGetReceiptsByHash(blockNumber);
    }
}