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
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public class FullInfoReceiptFinder : IReceiptFinder
    {
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private readonly IBlockFinder _blockFinder;

        public FullInfoReceiptFinder(IReceiptStorage receiptStorage, IReceiptsRecovery receiptsRecovery, IBlockFinder blockFinder)
        {
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
        }
        
        public Keccak FindBlockHash(Keccak txHash) => _receiptStorage.FindBlockHash(txHash);

        public TxReceipt[] Get(Block block)
        {
            var receipts = _receiptStorage.Get(block);
            if (_receiptsRecovery.TryRecover(block, receipts))
            {
                _receiptStorage.Insert(block, receipts);
            }
            
            return receipts;
        }

        public TxReceipt[] Get(Keccak blockHash)
        {
            TxReceipt[]? receipts = _receiptStorage.Get(blockHash);
            
            if (_receiptsRecovery.NeedRecover(receipts))
            {
                var block = _blockFinder.FindBlock(blockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (_receiptsRecovery.TryRecover(block, receipts))
                {
                    _receiptStorage.Insert(block, receipts);
                }
            }

            return receipts;
        }

        public bool CanGetReceiptsByHash(long blockNumber) => _receiptStorage.CanGetReceiptsByHash(blockNumber);
        public bool TryGetReceiptsIterator(long blockNumber, Keccak blockHash, out ReceiptsIterator iterator) => _receiptStorage.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);
    }
}
