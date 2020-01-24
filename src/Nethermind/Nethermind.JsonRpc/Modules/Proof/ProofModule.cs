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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModule : IProofModule
    {
        private readonly ILogger _logger;
        private readonly ITracer _tracer;
        private readonly IBlockFinder _blockFinder;
        private readonly IReceiptFinder _receiptFinder;
        private readonly HeaderDecoder _decoder = new HeaderDecoder();

        public ProofModule(ITracer tracer,
            IBlockFinder blockFinder,
            IReceiptFinder receiptFinder,
            ILogManager logManager)
        {
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter)
        {
            Transaction transaction = tx.ToTransaction();
            Block block = _blockFinder.GetBlock(blockParameter.ToFilterBlock());
            throw new NotImplementedException();
        }

        private byte[][] BuildTxProof(Transaction[] txs, int index)
        {
            throw new NotImplementedException();
        }
        
        private byte[][] BuildReceiptProof(TxReceipt[] receipts, int index)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<TransactionWithProof> proof_getTransactionByHash(Keccak txHash, bool includeHeader = true)
        {
            TxReceipt receipt = _receiptFinder.Find(txHash);
            Block block = _blockFinder.FindBlock(receipt.BlockHash);

            Transaction[] txs = block.Transactions;
            Transaction transaction = txs[receipt.Index];

            TransactionWithProof txWithProof = new TransactionWithProof();
            txWithProof.Transaction = new TransactionForRpc(block.Hash, block.Number, receipt.Index, transaction);
            if (includeHeader)
            {
                txWithProof.BlockHeader = _decoder.Encode(block.Header).Bytes;
            }
            
            txWithProof.TxProof= BuildTxProof(txs, receipt.Index);
            return ResultWrapper<TransactionWithProof>.Success(txWithProof);
        }

        public ResultWrapper<ReceiptWithProof> proof_getTransactionReceipt(Keccak txHash, bool includeHeader = true)
        {
            TxReceipt receipt = _receiptFinder.Find(txHash);
            Block block = _blockFinder.FindBlock(receipt.BlockHash);
            
            BlockReceiptsTracer receiptsTracer = new BlockReceiptsTracer();
            _tracer.Trace(receipt.BlockHash, receiptsTracer);
            
            TxReceipt[] receipts = receiptsTracer.TxReceipts;
            Transaction[] txs = block.Transactions;

            ReceiptWithProof receiptWithProof = new ReceiptWithProof();
            receiptWithProof.Receipt = new ReceiptForRpc(txHash, receipt);
            if (includeHeader)
            {
                receiptWithProof.BlockHeader = _decoder.Encode(block.Header).Bytes;
            }

            receiptWithProof.ReceiptProof = BuildReceiptProof(receipts, receipt.Index);
            receiptWithProof.TxProof = BuildTxProof(txs, receipt.Index);
            return ResultWrapper<ReceiptWithProof>.Success(receiptWithProof);
        }
    }
}