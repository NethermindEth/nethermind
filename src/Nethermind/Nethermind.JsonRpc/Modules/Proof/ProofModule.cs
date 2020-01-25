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
using Nethermind.Blockchain.Proofs;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Proofs;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Store.Proofs;

namespace Nethermind.JsonRpc.Modules.Proof
{
    /// <summary>
    /// <inheritdoc cref="IProofModule"/>
    /// </summary>
    public class ProofModule : IProofModule
    {
        private readonly ILogger _logger;
        private readonly ITracer _tracer;
        private readonly IBlockFinder _blockFinder;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ISpecProvider _specProvider;
        private readonly HeaderDecoder _headerDecoder = new HeaderDecoder();

        public ProofModule(
            ITracer tracer,
            IBlockFinder blockFinder,
            IReceiptFinder receiptFinder,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        [Todo(Improve.Review, "Double check the way the trace header is onstructed based on the block parameter. SHall we assign exactly the same hash / difficulty and other properties?")]
        public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter)
        {
            Transaction transaction = tx.ToTransaction();

            BlockHeader parentHeader = _blockFinder.FindBlock(blockParameter).Header;
            
            BlockHeader traceHeader = new BlockHeader(
                parentHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                parentHeader.Difficulty,
                parentHeader.Number + 1,
                parentHeader.GasLimit,
                parentHeader.Timestamp + 1,
                parentHeader.ExtraData);

            Block block = new Block(traceHeader, new[] {transaction}, Enumerable.Empty<BlockHeader>());
            block.Author = Address.Zero;
            block.TotalDifficulty = parentHeader.TotalDifficulty + traceHeader.Difficulty;

            ProofBlockTracer proofBlockTracer = new ProofBlockTracer(null);

            BlockReceiptsTracer receiptsTracer = new BlockReceiptsTracer();
            receiptsTracer.SetOtherTracer(proofBlockTracer);
            _tracer.Trace(block, receiptsTracer);

            CallResultWithProof callResultWithProof = new CallResultWithProof();
            ProofTxTracer proofTxTracer = proofBlockTracer.BuildResult().Single();
            
            CollectHeaders(proofTxTracer, callResultWithProof);
            callResultWithProof.Result = proofTxTracer.Output;
            CollectAccountProofs(proofTxTracer, parentHeader, callResultWithProof);

            return ResultWrapper<CallResultWithProof>.Success(callResultWithProof);
        }

        private void CollectAccountProofs(ProofTxTracer proofTxTracer, BlockHeader parentHeader, CallResultWithProof callResultWithProof)
        {
            List<AccountProof> accountProofs = new List<AccountProof>();
            foreach (Address address in proofTxTracer.Accounts)
            {
                AccountProofCollector collector = new AccountProofCollector(address, proofTxTracer.Storages
                    .Where(s => s.Address == address)
                    .Select(s => s.Index).ToArray());
                
                _tracer.Accept(collector, parentHeader.StateRoot);
                accountProofs.Add(collector.BuildResult());
            }

            callResultWithProof.Accounts = accountProofs.ToArray();
        }

        private void CollectHeaders(ProofTxTracer proofTxTracer, CallResultWithProof callResultWithProof)
        {
            List<BlockHeader> relevantHeaders = new List<BlockHeader>();
            foreach (Keccak blockHash in proofTxTracer.BlockHashes)
            {
                relevantHeaders.Add(_blockFinder.FindHeader(blockHash));
            }

            callResultWithProof.BlockHeaders = relevantHeaders
                .Select(h => _headerDecoder.Encode(h).Bytes).ToArray();
        }

        private byte[][] BuildTxProof(Transaction[] txs, int index)
        {
            return new TxTrie(txs, true).BuildProof(index);
        }

        private byte[][] BuildReceiptProof(long blockNumber, TxReceipt[] receipts, int index)
        {
            return new ReceiptTrie(blockNumber, _specProvider, receipts, true).BuildProof(index);
        }

        public ResultWrapper<TransactionWithProof> proof_getTransactionByHash(Keccak txHash, bool includeHeader)
        {
            TxReceipt receipt = _receiptFinder.Find(txHash);
            Block block = _blockFinder.FindBlock(receipt.BlockHash);

            Transaction[] txs = block.Transactions;
            Transaction transaction = txs[receipt.Index];

            TransactionWithProof txWithProof = new TransactionWithProof();
            txWithProof.Transaction = new TransactionForRpc(block.Hash, block.Number, receipt.Index, transaction);
            txWithProof.TxProof = BuildTxProof(txs, receipt.Index);
            if (includeHeader)
            {
                txWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }

            return ResultWrapper<TransactionWithProof>.Success(txWithProof);
        }
        
        public ResultWrapper<ReceiptWithProof> proof_getTransactionReceipt(Keccak txHash, bool includeHeader)
        {
            TxReceipt receipt = _receiptFinder.Find(txHash);
            Block block = _blockFinder.FindBlock(receipt.BlockHash);

            BlockReceiptsTracer receiptsTracer = new BlockReceiptsTracer();
            receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            _tracer.Trace(receipt.BlockHash, receiptsTracer);

            TxReceipt[] receipts = receiptsTracer.TxReceipts;
            Transaction[] txs = block.Transactions;

            ReceiptWithProof receiptWithProof = new ReceiptWithProof();
            receiptWithProof.Receipt = new ReceiptForRpc(txHash, receipt);
            receiptWithProof.ReceiptProof = BuildReceiptProof(block.Number, receipts, receipt.Index);
            receiptWithProof.TxProof = BuildTxProof(txs, receipt.Index);
            if (includeHeader)
            {
                receiptWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }
            
            return ResultWrapper<ReceiptWithProof>.Success(receiptWithProof);
        }
    }
}