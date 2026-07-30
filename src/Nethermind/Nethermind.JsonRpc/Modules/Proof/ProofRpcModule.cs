// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
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
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
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
        // Registry-resolved so AuRa chains encode headers with step + signature (see AuRaHeaderModule).
        private readonly IRlpDecoder<BlockHeader> _headerDecoder = Rlp.GetDecoderOrThrow<BlockHeader>();
        private static readonly IRlpDecoder<TxReceipt> _receiptEncoder = Rlp.GetDecoder<TxReceipt>();
        private readonly WitnessCall _witnessCall = new(blockFinder, blockchainBridge, specProvider, jsonRpcConfig);

        public ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter) =>
            _witnessCall.Execute(tx, blockParameter);

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
            TransactionForRpcContext extraData = new(
                chainId: specProvider.ChainId,
                blockHash: block.Hash,
                blockNumber: block.Number,
                txIndex: receipt.Index,
                blockTimestamp: block.Timestamp,
                baseFee: block.BaseFeePerGas,
                receipt: receipt);
            txWithProof.Transaction = TransactionForRpc.FromTransaction(transaction, extraData);
            txWithProof.TxProof = BuildTxProofs(txs, specProvider.GetSpec(block.Header), receipt.Index);
            if (includeHeader)
            {
                txWithProof.BlockHeader = _headerDecoder.EncodeAsBytes(block.Header);
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
                receiptWithProof.BlockHeader = _headerDecoder.EncodeAsBytes(block.Header);
            }

            return ResultWrapper<ReceiptWithProof>.Success(receiptWithProof);
        }

        public ResultWrapper<AccountProofWithMeta> proof_getProofWithMeta(Address accountAddress, StorageKeys storageKeys, BlockParameter? blockParameter)
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

            BlockHeader header = searchResult.Object;

            if (!blockchainBridge.HasStateForBlock(header!))
            {
                return ResultWrapper<AccountProofWithMeta>.Fail(
                    $"No state available for block {header!.ToString(BlockHeader.Format.Short)}",
                    ErrorCodes.ResourceUnavailable);
            }

            using CancellationTokenSource timeout = jsonRpcConfig.BuildTimeoutCancellationToken();
            AccountProofCollector accountProofCollector = new(accountAddress, storageKeys, timeout.Token);
            VisitingStats diagnostics = new();
            blockchainBridge.RunTreeVisitor(accountProofCollector, header!, diagnostics: diagnostics);

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
