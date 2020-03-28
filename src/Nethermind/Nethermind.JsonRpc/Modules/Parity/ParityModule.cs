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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityModule : IParityModule
    {
        private readonly IEcdsa _ecdsa;
        private readonly ITxPool _txPool;
        private readonly IBlockFinder _blockFinder;
        private readonly IReceiptFinder _receiptFinder;
        
        public ParityModule(IEcdsa ecdsa, ITxPool txPool, IBlockFinder blockFinder, IReceiptFinder receiptFinder, ILogManager logManager)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        }

        public ResultWrapper<ParityTransaction[]> parity_pendingTransactions()
            => ResultWrapper<ParityTransaction[]>.Success(_txPool.GetPendingTransactions().Where(pt => pt.SenderAddress != null)
                .Select(t => new ParityTransaction(t, Rlp.Encode(t).Bytes,
                    t.IsSigned ? _ecdsa.RecoverPublicKey(t.Signature, t.Hash) : null)).ToArray());
        
        public ResultWrapper<ReceiptForRpc[]> parity_getBlockReceipts(BlockParameter blockParameter)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<ReceiptForRpc[]>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            TxReceipt[] receipts = _receiptFinder.Get(block) ?? new TxReceipt[block.Transactions.Length];
            IEnumerable<ReceiptForRpc> result = receipts.Zip(block.Transactions, (r, t) => new ReceiptForRpc(t.Hash, r));
            return ResultWrapper<ReceiptForRpc[]>.Success(result.ToArray());
        }
    }
}