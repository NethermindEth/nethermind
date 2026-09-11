// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Autofac.Features.AttributeFilters;

namespace Nethermind.JsonRpc.Modules.Proof
{
    /// <summary>
    /// <inheritdoc cref="IProofRpcModule"/>
    /// </summary>
    public class ProofRpcModule(
        IOverridableEnv<ITracer> tracerEnv,
        IBlockchainBridge blockchainBridge,
        IBlockFinder blockFinder,
        [KeyFilter(IReceiptFinder.RegenerableKey)] IReceiptFinder receiptFinder,
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

        public ResultWrapper<TransactionForRpcWithProof?> proof_getTransactionByHash(Hash256 txHash, bool includeHeader)
        {
            if (!TryResolveTransaction(txHash, out ResolvedTransaction resolved, out SearchResult<Block> failure))
            {
                return failure.IsError ? ResultWrapper<TransactionForRpcWithProof>.Fail(failure) : ResultWrapper<TransactionForRpcWithProof>.Success(null);
            }

            (Block block, int txIndex, _, TxReceipt? receipt) = resolved;
            Transaction[] txs = block.Transactions;
            Transaction transaction = txs[txIndex];

            TransactionForRpcWithProof txWithProof = new();
            TransactionForRpcContext extraData = new(
                chainId: specProvider.ChainId,
                blockHash: block.Hash,
                blockNumber: block.Number,
                txIndex: txIndex,
                blockTimestamp: block.Timestamp,
                baseFee: block.BaseFeePerGas,
                receipt: receipt);
            txWithProof.Transaction = TransactionForRpc.FromTransaction(transaction, extraData);
            txWithProof.TxProof = BuildTxProofs(txs, specProvider.GetSpec(block.Header), txIndex);
            if (includeHeader)
            {
                txWithProof.BlockHeader = _headerDecoder.EncodeAsBytes(block.Header);
            }

            return ResultWrapper<TransactionForRpcWithProof>.Success(txWithProof);
        }

        public ResultWrapper<ReceiptWithProof?> proof_getTransactionReceipt(Hash256 txHash, bool includeHeader)
        {
            if (!TryResolveTransaction(txHash, out ResolvedTransaction resolved, out SearchResult<Block> failure))
            {
                return failure.IsError ? ResultWrapper<ReceiptWithProof>.Fail(failure) : ResultWrapper<ReceiptWithProof>.Success(null);
            }

            (Block block, int txIndex, TxReceipt[] storedReceipts, TxReceipt? receipt) = resolved;
            if (receipt is null)
            {
                // Only this method needs the receipt itself; without one there is nothing to return a proof for.
                return ResultWrapper<ReceiptWithProof>.Success(null);
            }

            Transaction[] txs = block.Transactions;

            using Scope<ITracer> scope = tracerEnv.BuildAndOverride(blockFinder.FindParentHeader(block.Header, BlockTreeLookupOptions.None));

            BlockReceiptsTracer receiptsTracer = new();
            receiptsTracer.SetOtherTracer(NullBlockTracer.Instance);
            scope.Component.Trace(block, receiptsTracer);

            TxReceipt[] tracedReceipts = receiptsTracer.TxReceipts.ToArray();
            ReceiptWithProof receiptWithProof = new();
            IReleaseSpec spec = specProvider.GetSpec(block.Header);

            // Counted over the stored set the served logs come from, so the served logIndex identifies the same log
            // eth_getTransactionReceipt and eth_getLogs do; the block-derived position keeps a stale stored Index
            // from shifting it.
            int logIndexStart = storedReceipts.GetBlockLogFirstIndex(txIndex);

            receiptWithProof.Receipt = new ReceiptForRpc(
                txHash,
                receipt,
                block.Timestamp,
                txs[txIndex].GetGasInfo(spec, block.Header),
                logIndexStart);
            // ReceiptForRpc (and each LogEntryForRpc) copies the stored Index; the proofs below attest to the block-derived position.
            receiptWithProof.Receipt.TransactionIndex = txIndex;
            foreach (LogEntryForRpc log in receiptWithProof.Receipt.Logs)
            {
                log.TransactionIndex = txIndex;
            }
            receiptWithProof.ReceiptProof = BuildReceiptProofs(block.Header, tracedReceipts, txIndex);
            receiptWithProof.TxProof = BuildTxProofs(txs, specProvider.GetSpec(block.Header), txIndex);

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

        /// <param name="Receipt">
        /// The stored receipt, or <c>null</c> when the resolved block's receipt set no longer carries one for this
        /// transaction. Proving the transaction's inclusion needs only its position, so only the receipt method
        /// treats a missing receipt as unservable.
        /// </param>
        private readonly record struct ResolvedTransaction(Block Block, int TxIndex, TxReceipt[] StoredReceipts, TxReceipt? Receipt);

        /// <remarks>
        /// <paramref name="failure"/> is populated only for a search error the caller must surface, and left default
        /// when the caller should return a null result instead.
        /// </remarks>
        private bool TryResolveTransaction(Hash256 txHash, out ResolvedTransaction resolved, out SearchResult<Block> failure)
        {
            resolved = default;
            failure = default;

            // A tx hash with no stored block never made it into the chain — mirrors the eth_ equivalents' null-on-miss result.
            Hash256 blockHash = receiptFinder.FindBlockHash(txHash);
            if (blockHash is null) return false;

            SearchResult<Block> searchResult = blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                // Unknown blocks yield null and mirror eth_; other search failures (e.g. pruned history) keep their error.
                failure = searchResult.ErrorCode == ErrorCodes.ResourceNotFound ? default : searchResult;
                return false;
            }

            Block block = searchResult.Object;
            int txIndex = block.GetTransactionIndex(txHash.ValueHash256);
            if (txIndex < 0)
            {
                // The resolved block may not contain this transaction — e.g. a reorg re-resolved the stored block
                // number to a different canonical block. Not an error, mirrors eth_.
                return false;
            }

            TxReceipt[] storedReceipts = receiptFinder.Get(block);
            resolved = new ResolvedTransaction(block, txIndex, storedReceipts, storedReceipts.ForTransaction(txHash));
            return true;
        }

        private static byte[][] BuildTxProofs(Transaction[] txs, IReleaseSpec releaseSpec, int index) => TxTrie.CalculateProof(txs, index);

        private byte[][] BuildReceiptProofs(BlockHeader blockHeader, TxReceipt[] receipts, int index) => ReceiptTrie.CalculateReceiptProofs(specProvider.GetSpec(blockHeader), receipts, index, _receiptEncoder);
    }
}
