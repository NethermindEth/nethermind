/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityModule : IParityModule
    {
        private readonly IEcdsa _ecdsa;
        private readonly ITxPool _txPool;
        private readonly IBlockFinder _blockFinder;
        private readonly IReceiptStorage _receiptStorage;
        
        public ParityModule(IEcdsa ecdsa, ITxPool txPool, IBlockTree blockTree, IReceiptStorage  receiptStorage, ILogManager logManager)
        {
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _blockFinder = new BlockFinder(blockTree ?? throw new ArgumentNullException(nameof(blockTree)));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        }

        public ResultWrapper<ParityTransaction[]> parity_pendingTransactions()
            => ResultWrapper<ParityTransaction[]>.Success(_txPool.GetPendingTransactions().Where(pt => pt.SenderAddress != null)
                .Select(t => new ParityTransaction(t, Rlp.Encode(t).Bytes,
                    t.IsSigned ? _ecdsa.RecoverPublicKey(t.Signature, t.Hash) : null)).ToArray());
        
        public ResultWrapper<ReceiptForRpc[]> parity_getBlockReceipts(BlockParameter blockParameter)
        {
            var filterBlock = blockParameter.ToFilterBlock();
            var block = _blockFinder.GetBlock(filterBlock);
            var receipts = _receiptStorage.FindForBlock(block);
            var result = receipts.Zip(block.Transactions, (r, t) => new ReceiptForRpc(t.Hash, r));
            return ResultWrapper<ReceiptForRpc[]>.Success(result.ToArray());
        }
    }
}