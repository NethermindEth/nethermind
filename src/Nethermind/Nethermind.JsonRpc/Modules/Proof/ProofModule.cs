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
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Proofs;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

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

        public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<CallResultWithProof>.Fail(searchResult);
            }

            BlockHeader sourceHeader = searchResult.Object;
            BlockHeader callHeader = new BlockHeader(
                sourceHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                0,
                sourceHeader.Number + 1,
                sourceHeader.GasLimit,
                sourceHeader.Timestamp,
                Bytes.Empty);

            callHeader.TxRoot = Keccak.EmptyTreeHash;
            callHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            callHeader.Author = Address.SystemUser;
            callHeader.TotalDifficulty = sourceHeader.TotalDifficulty + callHeader.Difficulty;
            callHeader.Hash = callHeader.CalculateHash();

            Transaction transaction = tx.ToTransaction();
            transaction.SenderAddress ??= Address.SystemUser;

            if (transaction.GasLimit == 0)
            {
                transaction.GasLimit = callHeader.GasLimit;
            }

            Block block = new Block(callHeader, new[] {transaction}, Enumerable.Empty<BlockHeader>());

            ProofBlockTracer proofBlockTracer = new ProofBlockTracer(null, transaction.SenderAddress == Address.SystemUser);
            _tracer.Trace(block, proofBlockTracer);

            CallResultWithProof callResultWithProof = new CallResultWithProof();
            ProofTxTracer proofTxTracer = proofBlockTracer.BuildResult().Single();

            callResultWithProof.BlockHeaders = CollectHeaderBytes(proofTxTracer, sourceHeader);
            callResultWithProof.Result = proofTxTracer.Output;

            // we collect proofs from before execution (after learning which addresses will be touched)
            // if we wanted to collect post execution proofs then we would need to use BeforeRestore on the tracer
            callResultWithProof.Accounts = CollectAccountProofs(sourceHeader.StateRoot, proofTxTracer);

            return ResultWrapper<CallResultWithProof>.Success(callResultWithProof);
        }

        public ResultWrapper<TransactionWithProof> proof_getTransactionByHash(Keccak txHash, bool includeHeader)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash == null)
            {
                return ResultWrapper<TransactionWithProof>.Fail($"{txHash} receipt (transaction) could not be found", ErrorCodes.ResourceNotFound);
            }

            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<TransactionWithProof>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            TxReceipt receipt = _receiptFinder.Get(block).ForTransaction(txHash);
            Transaction[] txs = block.Transactions;
            Transaction transaction = txs[receipt.Index];

            TransactionWithProof txWithProof = new TransactionWithProof();
            txWithProof.Transaction = new TransactionForRpc(block.Hash, block.Number, receipt.Index, transaction);
            txWithProof.TxProof = BuildTxProofs(txs, receipt.Index);
            if (includeHeader)
            {
                txWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }

            return ResultWrapper<TransactionWithProof>.Success(txWithProof);
        }

        public ResultWrapper<ReceiptWithProof> proof_getTransactionReceipt(Keccak txHash, bool includeHeader)
        {
            Keccak blockHash = _receiptFinder.FindBlockHash(txHash);
            if (blockHash == null)
            {
                return ResultWrapper<ReceiptWithProof>.Fail($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound);
            }

            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<ReceiptWithProof>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            TxReceipt receipt = _receiptFinder.Get(block).ForTransaction(txHash);
            
            BlockReceiptsTracer receiptsTracer = new BlockReceiptsTracer();
            receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            _tracer.Trace(block, receiptsTracer);

            TxReceipt[] receipts = receiptsTracer.TxReceipts;
            Transaction[] txs = block.Transactions;

            ReceiptWithProof receiptWithProof = new ReceiptWithProof();
            receiptWithProof.Receipt = new ReceiptForRpc(txHash, receipt);
            receiptWithProof.ReceiptProof = BuildReceiptProofs(block.Number, receipts, receipt.Index);
            receiptWithProof.TxProof = BuildTxProofs(txs, receipt.Index);
            if (includeHeader)
            {
                receiptWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }

            return ResultWrapper<ReceiptWithProof>.Success(receiptWithProof);
        }

        private AccountProof[] CollectAccountProofs(Keccak stateRoot, ProofTxTracer proofTxTracer)
        {
            List<AccountProof> accountProofs = new List<AccountProof>();
            foreach (Address address in proofTxTracer.Accounts)
            {
                AccountProofCollector collector = new AccountProofCollector(address, proofTxTracer.Storages
                    .Where(s => s.Address == address)
                    .Select(s => s.Index).ToArray());

                _tracer.Accept(collector, stateRoot);
                accountProofs.Add(collector.BuildResult());
            }

            return accountProofs.ToArray();
        }

        private byte[][] CollectHeaderBytes(ProofTxTracer proofTxTracer, BlockHeader tracedBlockHeader)
        {
            List<BlockHeader> relevantHeaders = new List<BlockHeader> {tracedBlockHeader};
            foreach (Keccak blockHash in proofTxTracer.BlockHashes)
            {
                relevantHeaders.Add(_blockFinder.FindHeader(blockHash));
            }

            return relevantHeaders
                .Select(h => _headerDecoder.Encode(h).Bytes).ToArray();
        }

        private byte[][] BuildTxProofs(Transaction[] txs, int index)
        {
            return new TxTrie(txs, true).BuildProof(index);
        }

        private byte[][] BuildReceiptProofs(long blockNumber, TxReceipt[] receipts, int index)
        {
            return new ReceiptTrie(blockNumber, _specProvider, receipts, true).BuildProof(index);
        }
    }
}