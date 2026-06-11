// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Blockchain.Tracing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Rlp;
using Nethermind.State.OverridableEnv;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.JsonRpc.Modules.Proof
{
    /// <summary>
    /// <inheritdoc cref="IProofRpcModule"/>
    /// </summary>
    public class ProofRpcModule(
        IOverridableEnv<ITracer> tracerEnv,
        IBlockchainBridge blockchainBridge,
        IBlockFinder blockFinder,
        IReceiptFinder receiptFinder,
        ISpecProvider specProvider,
        IJsonRpcConfig jsonRpcConfig)
        : IProofRpcModule
    {
        private readonly HeaderDecoder _headerDecoder = new();
        private static readonly IRlpDecoder<TxReceipt> _receiptEncoder = Rlp.GetDecoder<TxReceipt>()!;
        private readonly WitnessCall _witnessCall = new(blockFinder, blockchainBridge, specProvider, jsonRpcConfig);

        public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter) =>
            _witnessCall.Execute(tx, blockParameter);

        public ResultWrapper<TransactionForRpcWithProof> proof_getTransactionByHash(Hash256 txHash, bool includeHeader)
        {
            Hash256? blockHash = receiptFinder.FindBlockHash(txHash);
            if (blockHash is null)
            {
                return ResultWrapper<TransactionForRpcWithProof>.Fail($"{txHash} receipt (transaction) could not be found", ErrorCodes.ResourceNotFound);
            }

            SearchResult<Block> searchResult = blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<TransactionForRpcWithProof>.Fail(searchResult);
            }

            Block block = searchResult.Object!;
            TxReceipt[] receipts = receiptFinder.Get(block) ?? [];
            TxReceipt? receipt = receipts.ForTransaction(txHash);
            if (receipt is null)
            {
                return ResultWrapper<TransactionForRpcWithProof>.Fail($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound);
            }

            Transaction[] txs = block.Transactions;
            Transaction transaction = txs[receipt.Index];

            TransactionForRpcWithProof txWithProof = new();
            TransactionForRpcContext extraData = new(
                chainId: specProvider.ChainId,
                blockHash: block.Hash!,
                blockNumber: block.Number,
                txIndex: receipt.Index,
                blockTimestamp: block.Timestamp,
                baseFee: block.BaseFeePerGas,
                receipt: receipt);
            txWithProof.Transaction = TransactionForRpc.FromTransaction(transaction, extraData);
            txWithProof.TxProof = BuildTxProofs(txs, specProvider.GetSpec(block.Header), receipt.Index);
            if (includeHeader)
            {
                txWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }

            return ResultWrapper<TransactionForRpcWithProof>.Success(txWithProof);
        }

        public ResultWrapper<ReceiptWithProof> proof_getTransactionReceipt(Hash256 txHash, bool includeHeader)
        {
            Hash256? blockHash = receiptFinder.FindBlockHash(txHash);
            if (blockHash is null)
            {
                return ResultWrapper<ReceiptWithProof>.Fail($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound);
            }

            SearchResult<Block> searchResult = blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<ReceiptWithProof>.Fail(searchResult);
            }

            Block block = searchResult.Object!;
            using Scope<ITracer> scope = tracerEnv.BuildAndOverride(blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.None));

            TxReceipt[] storedReceipts = receiptFinder.Get(block) ?? [];
            TxReceipt? receipt = storedReceipts.ForTransaction(txHash);
            if (receipt is null)
            {
                return ResultWrapper<ReceiptWithProof>.Fail($"{txHash} receipt could not be found", ErrorCodes.ResourceNotFound);
            }

            BlockReceiptsTracer receiptsTracer = new();
            receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            scope.Component.Trace(block, receiptsTracer);

            TxReceipt[] receipts = receiptsTracer.ToReceiptArray();
            Transaction[] txs = block.Transactions;
            ReceiptWithProof receiptWithProof = new();
            IReleaseSpec spec = specProvider.GetSpec(block.Header);
            Transaction? tx = txs.FirstOrDefault(x => x.Hash == txHash);

            int logIndexStart = storedReceipts.GetBlockLogFirstIndex(receipt.Index);

            receiptWithProof.Receipt = new ReceiptForRpc(
                txHash,
                receipt,
                block.Timestamp,
                tx?.GetGasInfo(spec, block.Header) ?? new(),
                logIndexStart);
            receiptWithProof.ReceiptProof = BuildReceiptProofs(block.Header, receipts, receipt.Index);
            receiptWithProof.TxProof = BuildTxProofs(txs, specProvider.GetSpec(block.Header), receipt.Index);

            if (includeHeader)
            {
                receiptWithProof.BlockHeader = _headerDecoder.Encode(block.Header).Bytes;
            }

            return ResultWrapper<ReceiptWithProof>.Success(receiptWithProof);
        }

        public ResultWrapper<AccountProofWithMeta> proof_getProofWithMeta(Address accountAddress, HashSet<UInt256> storageKeys, BlockParameter? blockParameter)
        {
            if (storageKeys.Count > EthRpcModule.GetProofStorageKeyLimit)
            {
                return ResultWrapper<AccountProofWithMeta>.Fail(
                    $"storageKeys: {storageKeys.Count} is over the query limit {EthRpcModule.GetProofStorageKeyLimit}.",
                    ErrorCodes.InvalidParams);
            }

            SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<AccountProofWithMeta>.Fail(searchResult);
            }

            BlockHeader header = searchResult.Object!;

            if (!blockchainBridge.HasStateForBlock(header))
            {
                return ResultWrapper<AccountProofWithMeta>.Fail(
                    $"No state available for block {header.ToString(BlockHeader.Format.Short)}",
                    ErrorCodes.ResourceUnavailable);
            }

            AccountProofCollector accountProofCollector = new(accountAddress, storageKeys);
            VisitingStats diagnostics = new();
            blockchainBridge.RunTreeVisitor(accountProofCollector, header, diagnostics: diagnostics);

            return ResultWrapper<AccountProofWithMeta>.Success(new AccountProofWithMeta
            {
                Proof = accountProofCollector.BuildResult(),
                Meta = new ProofMeta
                {
                    NodeLookups = diagnostics.NodeLookups,
                    CacheHits = diagnostics.CacheHits,
                    MaxDepth = diagnostics.MaxDepth,
                },
            });
        }

        private static byte[][] BuildTxProofs(Transaction[] txs, IReleaseSpec releaseSpec, int index) => TxTrie.CalculateProof(txs, index);

        private byte[][] BuildReceiptProofs(BlockHeader blockHeader, TxReceipt[] receipts, int index) => ReceiptTrie.CalculateReceiptProofs(specProvider.GetSpec(blockHeader), receipts, index, _receiptEncoder);
    }
}
