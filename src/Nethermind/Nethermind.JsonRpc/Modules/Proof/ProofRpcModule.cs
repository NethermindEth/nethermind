// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.Proofs;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.OverridableEnv;
using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc.Modules.Proof
{
    /// <summary>
    /// <inheritdoc cref="IProofRpcModule"/>
    /// </summary>
    public class ProofRpcModule(
        IOverridableEnv<ITracer> tracerEnv,
        IBlockFinder blockFinder,
        IReceiptFinder receiptFinder,
        ISpecProvider specProvider)
        : IProofRpcModule
    {
        private readonly HeaderDecoder _headerDecoder = new();
        private static readonly IRlpStreamDecoder<TxReceipt> _receiptDecoder = Rlp.GetStreamDecoder<TxReceipt>();

        public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<CallResultWithProof>.Fail(searchResult);
            }

            BlockHeader sourceHeader = searchResult.Object;
            using Scope<ITracer> scope = tracerEnv.BuildAndOverride(sourceHeader);

            BlockHeader callHeader = new(
                sourceHeader.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                0,
                sourceHeader.Number + 1,
                sourceHeader.GasLimit,
                sourceHeader.Timestamp,
                [])
            {
                TxRoot = Keccak.EmptyTreeHash,
                ReceiptsRoot = Keccak.EmptyTreeHash,
                Author = Address.SystemUser
            };

            callHeader.TotalDifficulty = sourceHeader.TotalDifficulty + callHeader.Difficulty;
            callHeader.Hash = callHeader.CalculateHash();

            Transaction transaction = tx.ToTransaction();
            transaction.SenderAddress ??= Address.SystemUser;

            if (transaction.GasLimit == 0)
            {
                transaction.GasLimit = callHeader.GasLimit;
            }

            Block block = new(callHeader, new[] { transaction }, []);

            ProofBlockTracer proofBlockTracer = new(null, transaction.SenderAddress == Address.SystemUser);
            scope.Component.Trace(block, proofBlockTracer);

            CallResultWithProof callResultWithProof = new();
            ProofTxTracer proofTxTracer = proofBlockTracer.BuildResult().Single();

            callResultWithProof.BlockHeaders = CollectHeaderBytes(proofTxTracer, sourceHeader);
            callResultWithProof.Result = proofTxTracer.Output;

            // we collect proofs from before execution (after learning which addresses will be touched)
            // if we wanted to collect post execution proofs then we would need to use BeforeRestore on the tracer
            callResultWithProof.Accounts = CollectAccountProofs(scope.Component, sourceHeader.StateRoot, proofTxTracer);

            return ResultWrapper<CallResultWithProof>.Success(callResultWithProof);
        }

        public ResultWrapper<TransactionForRpcWithProof> proof_getTransactionByHash(Hash256 txHash, bool includeHeader)
        {
            Hash256 blockHash = receiptFinder.FindBlockHash(txHash);
            if (blockHash is null)
            {
                return ResultWrapper<TransactionForRpcWithProof>.Fail($"{txHash} receipt (transaction) could not be found", ErrorCodes.ResourceNotFound);
            }

            SearchResult<Block> searchResult = blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<TransactionForRpcWithProof>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            TxReceipt receipt = receiptFinder.Get(block).ForTransaction(txHash);
            Transaction[] txs = block.Transactions;
            Transaction transaction = txs[receipt.Index];

            TransactionForRpcWithProof txWithProof = new();
            txWithProof.Transaction = TransactionForRpc.FromTransaction(transaction, block.Hash, block.Number, receipt.Index, block.BaseFeePerGas, specProvider.ChainId);
            txWithProof.TxProof = BuildTxProofs(txs, specProvider.GetSpec(block.Header), receipt.Index);
            if (includeHeader)
            {
                txWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }

            return ResultWrapper<TransactionForRpcWithProof>.Success(txWithProof);
        }

        public ResultWrapper<ReceiptWithProof> proof_getTransactionReceipt(Hash256 txHash, bool includeHeader)
        {
            Hash256 blockHash = receiptFinder.FindBlockHash(txHash);
            if (blockHash is null)
            {
                return ResultWrapper<ReceiptWithProof>.Fail($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound);
            }

            SearchResult<Block> searchResult = blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<ReceiptWithProof>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            using Scope<ITracer> scope = tracerEnv.BuildAndOverride(blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.None));

            TxReceipt receipt = receiptFinder.Get(block).ForTransaction(txHash);
            BlockReceiptsTracer receiptsTracer = new();
            receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            scope.Component.Trace(block, receiptsTracer);

            TxReceipt[] receipts = receiptsTracer.TxReceipts.ToArray();
            Transaction[] txs = block.Transactions;
            ReceiptWithProof receiptWithProof = new();
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            Transaction? tx = txs.FirstOrDefault(x => x.Hash == txHash);

            int logIndexStart = receiptFinder.Get(block).GetBlockLogFirstIndex(receipt.Index);

            receiptWithProof.Receipt = new ReceiptForRpc(txHash, receipt, tx?.GetGasInfo(spec, block.Header) ?? new(), logIndexStart);
            receiptWithProof.ReceiptProof = BuildReceiptProofs(block.Header, receipts, receipt.Index);
            receiptWithProof.TxProof = BuildTxProofs(txs, specProvider.GetSpec(block.Header), receipt.Index);

            if (includeHeader)
            {
                receiptWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }

            return ResultWrapper<ReceiptWithProof>.Success(receiptWithProof);
        }

        private AccountProof[] CollectAccountProofs(ITracer tracer, Hash256 stateRoot, ProofTxTracer proofTxTracer)
        {
            List<AccountProof> accountProofs = new();
            foreach (Address address in proofTxTracer.Accounts)
            {
                AccountProofCollector collector = new(address, proofTxTracer.Storages
                    .Where(s => s.Address == address)
                    .Select(s => s.Index).ToArray());

                tracer.Accept(collector, stateRoot);
                accountProofs.Add(collector.BuildResult());
            }

            return accountProofs.ToArray();
        }

        private byte[][] CollectHeaderBytes(ProofTxTracer proofTxTracer, BlockHeader tracedBlockHeader)
        {
            List<BlockHeader> relevantHeaders = new() { tracedBlockHeader };
            foreach (Hash256 blockHash in proofTxTracer.BlockHashes)
            {
                relevantHeaders.Add(blockFinder.FindHeader(blockHash));
            }

            return relevantHeaders
                .Select(h => _headerDecoder.Encode(h).Bytes).ToArray();
        }

        private static byte[][] BuildTxProofs(Transaction[] txs, IReleaseSpec releaseSpec, int index)
        {
            return TxTrie.CalculateProof(txs, index);
        }

        private byte[][] BuildReceiptProofs(BlockHeader blockHeader, TxReceipt[] receipts, int index)
        {
            return ReceiptTrie.CalculateReceiptProofs(specProvider.GetSpec(blockHeader), receipts, index, _receiptDecoder);
        }
    }
}
