// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.IO;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using Nethermind.Evm.State;
using Nethermind.Taiko.Config;
using static Nethermind.Taiko.TaikoBlockValidator;

namespace Nethermind.Taiko.Rpc;

public class TaikoEngineRpcModule(IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
        IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadHandlerV4,
        IAsyncHandler<byte[], GetPayloadV5Result?> getPayloadHandlerV5,
        IAsyncHandler<byte[], GetPayloadV6Result?> getPayloadHandlerV6,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        IHandler<HashSet<string>, IReadOnlyList<string>> capabilitiesHandler,
        IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>> getBlobsHandler,
        IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?> getBlobsHandlerV2,
        IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?> getBlobsHandlerV4,
        IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>> getPayloadBodiesByHashV2Handler,
        IGetPayloadBodiesByRangeV2Handler getPayloadBodiesByRangeV2Handler,
        IHandler<Hash256, InclusionListBytes> getInclusionListTransactionsHandler,
        InclusionListTxSource inclusionListTxSource,
        IEngineRequestsTracker engineRequestsTracker,
        ISpecProvider specProvider,
        GCKeeper gcKeeper,
        ILogManager logManager,
        ITxPool txPool,
        IBlockFinder blockFinder,
        IShareableTxProcessorSource txProcessorSource,
        IRlpDecoder<Transaction> txDecoder,
        IL1OriginStore l1OriginStore,
        ISurgeConfig surgeConfig) :
            EngineRpcModule(getPayloadHandlerV1,
                getPayloadHandlerV2,
                getPayloadHandlerV3,
                getPayloadHandlerV4,
                getPayloadHandlerV5,
                getPayloadHandlerV6,
                newPayloadV1Handler,
                forkchoiceUpdatedV1Handler,
                executionGetPayloadBodiesByHashV1Handler,
                executionGetPayloadBodiesByRangeV1Handler,
                transitionConfigurationHandler,
                capabilitiesHandler,
                getBlobsHandler,
                getBlobsHandlerV2,
                getBlobsHandlerV4,
                getPayloadBodiesByHashV2Handler,
                getPayloadBodiesByRangeV2Handler,
                getInclusionListTransactionsHandler,
                inclusionListTxSource,
                engineRequestsTracker,
                specProvider,
                gcKeeper,
                logManager), ITaikoEngineRpcModule
{
    /// <summary>
    /// Maximum number of blocks to scan backwards when the batch→block index is missing.
    /// Matches alethia-reth's <c>MAX_BACKWARD_SCAN_BLOCKS = 192 * 21_600</c>.
    /// </summary>
    private const int MaxBatchLookupBlocks = 192 * 21_600;

    // ResourceNotFound (-32000) instead of the default InternalError (-32603), and IsTemporary
    // so the JsonRpc framework's SuppressWarning flag fires (JsonRpcService.cs:158 ->
    // JsonRpcProcessor.cs:428). Without this, every cold-boot tryLastFinalizedCheckpoint poll
    // produces a loud "Error response handling JsonRpc..." WARN line on a known-transient miss.
    private static readonly ResultWrapper<UInt256?> BlockIdNotFound =
        ResultWrapper<UInt256?>.Fail("not found", ErrorCodes.ResourceNotFound, isTemporary: true);

    /// <summary>
    /// Cached null-result for <c>taikoAuth_lastL1OriginByBatchID</c>. Per alethia-reth and
    /// the Go taiko-client expectations, a missing L1 origin for a known batch is reported as
    /// a successful JSON-RPC response with a null result (rather than a -32603 error), so a
    /// freshly-started node that has not yet seen any L1 batches does not flood the logs
    /// with errors during normal driver polling.
    /// </summary>
    private static readonly ResultWrapper<L1Origin?> L1OriginByBatchIdNullResult =
        ResultWrapper<L1Origin?>.Success(null);

    /// <summary>
    /// Cached null-result for <c>taikoAuth_last{Certain,}BlockIDByBatchID</c> when the resolved
    /// block id sits below this network's last-Pacaya threshold. Geth and reth both report this
    /// case as success+null rather than the "not found" error, so the driver does not mistake
    /// the threshold gate for a transient miss.
    /// </summary>
    private static readonly ResultWrapper<UInt256?> BlockIdBatchLookupNullResult =
        ResultWrapper<UInt256?>.Success(null);

    /// <summary>
    /// Resolved once at construction from <see cref="ISpecProvider.ChainId"/>. <c>null</c> on
    /// networks with no last-Pacaya threshold (Devnet, Masaya, unknown). On Mainnet and Hoodi,
    /// any resolved batch-lookup block id strictly less than this value is reported as null.
    /// </summary>
    private readonly UInt256? _batchLookupThreshold =
        BatchLookupThresholds.ResolveBatchLookupThreshold(specProvider.ChainId) is { } t
            ? new UInt256(t)
            : (UInt256?)null;

    private readonly ILogger _taikoLogger = logManager.GetClassLogger<TaikoEngineRpcModule>();

    /// <summary>
    /// Returns <c>true</c> when this network has a batch-lookup threshold and the resolved
    /// block id sits strictly below it. Mirrors <c>batchLookupResultBelowThreshold</c> in
    /// taiko-geth (PR #558) and <c>batch_lookup_result_below_last_pacaya_block_id</c> in
    /// alethia-reth (PR #177).
    /// </summary>
    private bool IsBlockBelowBatchLookupThreshold(UInt256 blockId) =>
        _batchLookupThreshold is { } threshold && blockId < threshold;

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null) => base.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(TaikoExecutionPayload executionPayload)
    {
        // Inject the spec provider so TryGetBlock can restore header fields V2 payloads don't
        // carry: ParentBeaconBlockRoot (EIP-4788, [JsonIgnore]) and RequestsHash (EIP-7685, not a payload field).
        executionPayload.AttachSpecProvider(_specProvider);
        return base.engine_newPayloadV1(executionPayload);
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null) => base.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(TaikoExecutionPayload executionPayload)
    {
        executionPayload.AttachSpecProvider(_specProvider);
        return base.engine_newPayloadV2(executionPayload);
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null) => base.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(TaikoExecutionPayloadV3 executionPayload, Hash256?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot) => base.engine_newPayloadV3(executionPayload, blobVersionedHashes, parentBeaconBlockRoot);

    public ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit,
         ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists) =>
        taikoAuth_txPoolContentWithMinTip(beneficiary, baseFee, blockMaxGasLimit, maxBytesPerTxList, localAccounts, maxTransactionsLists, 0);

    public ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContentWithMinTip(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit,
       ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists, ulong minTip)
    {
        // Fetch all the pending transactions except transactions from the GoldenTouchAccount
        IEnumerable<KeyValuePair<AddressAsKey, Transaction[]>> pendingTxs = txPool.GetPendingTransactionsBySender()
            .Where(txs => !txs.Key.Value.Equals(TaikoBlockValidator.GoldenTouchAccount));

        if (localAccounts is not null)
        {
            pendingTxs = pendingTxs.OrderBy(txs => !localAccounts.Contains(txs.Key.Value));
        }

        IEnumerable<Transaction> allTxs = pendingTxs.SelectMany(txs => txs.Value).Where(tx => !tx.SupportsBlobs && tx.CanPayBaseFee(baseFee));

        if (minTip is not 0)
        {
            allTxs = allTxs.Where(tx => tx.TryCalculatePremiumPerGas(baseFee, out UInt256 premiumPerGas) && premiumPerGas >= minTip);
        }

        Transaction[] txQueue = allTxs.ToArray();

        BlockHeader? head = blockFinder.Head?.Header;

        if (txQueue.Length is 0 || head?.StateRoot is null)
        {
            return ResultWrapper<PreBuiltTxList[]?>.Success([]);
        }

        using IReadOnlyTxProcessingScope scope = txProcessorSource.Build(head);

        return ResultWrapper<PreBuiltTxList[]?>.Success(ProcessTransactions(scope.TransactionProcessor, scope.WorldState, new BlockHeader(
                head.Hash!,
                Keccak.OfAnEmptySequenceRlp,
                beneficiary,
                UInt256.Zero,
                head!.Number + 1,
                (long)blockMaxGasLimit,
                head.Timestamp + 1,
                [])
        {
            BaseFeePerGas = baseFee,
            StateRoot = head.StateRoot,
            IsPostMerge = true,
        }, txQueue, maxTransactionsLists, maxBytesPerTxList, minTip));
    }

    private PreBuiltTxList[] ProcessTransactions(ITransactionProcessor txProcessor, IWorldState worldState, BlockHeader blockHeader, Transaction[] txSource, int maxBatchCount, ulong maxBytesPerTxList, ulong minTip)
    {
        if (txSource.Length is 0 || blockHeader.StateRoot is null || maxBatchCount is 0)
        {
            return [];
        }

        List<PreBuiltTxList> Batches = [];

        void CommitAndDisposeBatch(Batch batch)
        {
            Batches.Add(new PreBuiltTxList(batch.Transactions.Select(tx => TransactionForRpc.FromTransaction(tx)).ToArray(),
                                            (ulong)blockHeader.GasUsed,
                                            batch.GetCompressedTxsLength()));
            blockHeader.GasUsed = 0;
            batch.Dispose();
        }

        BlockExecutionContext blkCtx = new(blockHeader, _specProvider.GetSpec(blockHeader));

        Batch batch = new(maxBytesPerTxList, txSource.Length, txDecoder);

        void Restore(Snapshot snapshot, long gasUsed)
        {
            worldState.Restore(snapshot);
            blockHeader.GasUsed = gasUsed;
        }

        try
        {
            for (int i = 0; i < txSource.Length;)
            {
                Transaction tx = txSource[i];
                Snapshot snapshot = worldState.TakeSnapshot(true);
                long gasUsedBefore = blockHeader.GasUsed;

                try
                {
                    TransactionResult executionResult = txProcessor.Execute(tx, in blkCtx, NullTxTracer.Instance);

                    if (!executionResult)
                    {
                        Restore(snapshot, gasUsedBefore);

                        if (executionResult == TransactionResult.BlockGasLimitExceeded && batch.Transactions.Count is not 0)
                        {
                            CommitAndDisposeBatch(batch);

                            if (maxBatchCount == Batches.Count)
                            {
                                return [.. Batches];
                            }

                            batch = new(maxBytesPerTxList, txSource.Length - i, txDecoder);

                            continue;
                        }

                        while (i < txSource.Length && txSource[i].SenderAddress == tx.SenderAddress) i++;
                        continue;
                    }

                    // For Surge, filter out any transaction with very high gas limit
                    if (surgeConfig.MaxGasLimitRatio > 0 && tx.GasLimit > tx.SpentGas * surgeConfig.MaxGasLimitRatio)
                    {
                        Restore(snapshot, gasUsedBefore);
                        while (i < txSource.Length && txSource[i].SenderAddress == tx.SenderAddress) i++;
                        continue;
                    }
                }
                catch
                {
                    Restore(snapshot, gasUsedBefore);
                    while (i < txSource.Length && txSource[i].SenderAddress == tx.SenderAddress) i++;
                    continue;
                }

                if (!batch.TryAddTx(tx))
                {
                    Restore(snapshot, gasUsedBefore);

                    if (batch.Transactions.Count is 0)
                    {
                        while (i < txSource.Length && txSource[i].SenderAddress == tx.SenderAddress) i++;
                        continue;
                    }

                    CommitAndDisposeBatch(batch);

                    if (maxBatchCount == Batches.Count)
                    {
                        return [.. Batches];
                    }

                    batch = new(maxBytesPerTxList, txSource.Length - i, txDecoder);

                    continue;
                }

                i++;
            }

            if (batch.Transactions.Count is not 0)
            {
                CommitAndDisposeBatch(batch);
            }
        }
        finally
        {
            batch.Dispose();
        }

        return [.. Batches];
    }

    struct Batch(ulong maxBytes, int transactionsListCapacity, IRlpDecoder<Transaction> txDecoder) : IDisposable
    {
        private readonly ulong _maxBytes = maxBytes;
        private ulong _length;

        public ArrayPoolList<Transaction> Transactions { get; } = new(transactionsListCapacity);

        public bool TryAddTx(Transaction tx)
        {
            ulong estimatedLength = EstimateTxLength(tx);
            if (_length + estimatedLength <= _maxBytes)
            {
                Transactions.Add(tx);
                _length += estimatedLength;
                return true;
            }

            return false;
        }

        private readonly int GetTxLength(Transaction tx) => txDecoder.GetLength(tx, RlpBehaviors.None);

        public readonly ulong GetCompressedTxsLength()
        {
            int contentLength = Transactions.Sum(GetTxLength);
            byte[] data = ArrayPool<byte>.Shared.Rent(Rlp.LengthOfSequence(contentLength));

            try
            {
                RlpStream rlpStream = new(data);

                rlpStream.StartSequence(contentLength);
                foreach (Transaction tx in Transactions.AsSpan())
                {
                    txDecoder.Encode(rlpStream, tx);
                }

                return GetCompressedLength(data, rlpStream.Position);

            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }

        private readonly ulong EstimateTxLength(Transaction tx)
        {
            int contentLength = txDecoder.GetLength(tx, RlpBehaviors.None);
            byte[] data = ArrayPool<byte>.Shared.Rent(Rlp.LengthOfSequence(contentLength));

            try
            {
                RlpStream rlpStream = new(data);

                rlpStream.StartSequence(contentLength);
                txDecoder.Encode(rlpStream, tx);

                return GetCompressedLength(data, rlpStream.Position);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }

        private static ulong GetCompressedLength(byte[] data, int length)
        {
            using RecyclableMemoryStream stream = RecyclableStream.GetStream(nameof(Batch));
            using ZLibStream compressingStream = new(stream, CompressionMode.Compress, false);

            compressingStream.Write(data, 0, length);
            compressingStream.Flush();
            return (ulong)stream.Position;
        }

        public readonly void Dispose() => Transactions.Dispose();
    }

    public ResultWrapper<UInt256> taikoAuth_setHeadL1Origin(UInt256 blockId)
    {
        l1OriginStore.WriteHeadL1Origin(blockId);
        return ResultWrapper<UInt256>.Success(blockId);
    }

    public ResultWrapper<L1Origin> taikoAuth_updateL1Origin(L1Origin l1Origin)
    {
        l1OriginStore.WriteL1Origin(l1Origin.BlockId, l1Origin);
        return ResultWrapper<L1Origin>.Success(l1Origin);
    }

    public ResultWrapper<UInt256> taikoAuth_setBatchToLastBlock(UInt256 batchId, UInt256 blockId)
    {
        l1OriginStore.WriteBatchToLastBlockID(batchId, blockId);
        return ResultWrapper<UInt256>.Success(batchId);
    }

    public ResultWrapper<L1Origin> taikoAuth_setL1OriginSignature(UInt256 blockId, byte[] signature)
    {
        if (signature.Length != L1OriginDecoder.SignatureLength)
        {
            return ResultWrapper<L1Origin>.Fail($"signature must be exactly {L1OriginDecoder.SignatureLength} bytes, got {signature.Length}");
        }

        // Atomic read-modify-write inside L1OriginStore so concurrent
        // taikoAuth_setL1OriginSignature / taikoAuth_updateL1Origin calls cannot clobber each other.
        L1Origin? l1Origin = l1OriginStore.SetL1OriginSignature(blockId, signature);
        if (l1Origin is null)
        {
            return ResultWrapper<L1Origin>.Fail($"L1 origin not found for block ID {blockId}");
        }

        return ResultWrapper<L1Origin>.Success(l1Origin);
    }

    public Task<ResultWrapper<L1Origin?>> taikoAuth_lastL1OriginByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is null)
        {
            blockId = GetLastBlockByBatchId(batchId);
            if (blockId is null)
            {
                // Debug, not Warn: this fires on every periodic poll for the latest batch
                // before NMC has ingested it; that's expected control flow, not a fault.
                if (_taikoLogger.IsDebug) _taikoLogger.Debug($"taikoAuth_lastL1OriginByBatchID: no block found for batch {batchId}");
                return L1OriginByBatchIdNullResult;
            }
        }

        if (IsBlockBelowBatchLookupThreshold(blockId.Value))
            return L1OriginByBatchIdNullResult;

        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId.Value);
        // Debug: a known block can lack its L1Origin record briefly between insertion and
        // the driver's taikoAuth_updateL1Origin writeback — expected, not a fault.
        if (origin is null && _taikoLogger.IsDebug)
            _taikoLogger.Debug($"taikoAuth_lastL1OriginByBatchID: block {blockId} found for batch {batchId} but no L1 origin entry");

        return origin is null ? L1OriginByBatchIdNullResult : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<UInt256?>> taikoAuth_lastBlockIDByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is null)
        {
            blockId = GetLastBlockByBatchId(batchId);
            if (blockId is null)
            {
                if (_taikoLogger.IsDebug) _taikoLogger.Debug($"taikoAuth_lastBlockIDByBatchID: no block found for batch {batchId}");
                return BlockIdNotFound;
            }
        }

        if (IsBlockBelowBatchLookupThreshold(blockId.Value))
            return BlockIdBatchLookupNullResult;

        return ResultWrapper<UInt256?>.Success(blockId);
    }

    public ResultWrapper<UInt256?> taikoAuth_lastCertainBlockIDByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is { } b && IsBlockBelowBatchLookupThreshold(b))
            return BlockIdBatchLookupNullResult;
        return ResultWrapper<UInt256?>.Success(blockId);
    }

    public ResultWrapper<L1Origin?> taikoAuth_lastCertainL1OriginByBatchID(UInt256 batchId)
    {
        UInt256? blockId = l1OriginStore.ReadBatchToLastBlockID(batchId);
        if (blockId is null)
        {
            return L1OriginByBatchIdNullResult;
        }

        if (IsBlockBelowBatchLookupThreshold(blockId.Value))
            return L1OriginByBatchIdNullResult;

        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId.Value);
        return ResultWrapper<L1Origin?>.Success(origin);
    }

    /// <summary>
    /// Traverses the blockchain backwards to find the last Shasta block of the given batch ID.
    /// </summary>
    /// <param name="batchId">The Shasta batch identifier for which to find the last corresponding block.</param>
    /// <returns>The block ID if found, or null if not found or lookback limit exceeded.</returns>
    private UInt256? GetLastBlockByBatchId(UInt256 batchId)
    {
        Block? currentBlock = blockFinder.Head;
        int lookbackCount = 0;

        while (currentBlock is not null &&
               currentBlock.Transactions.Length > 0 &&
               HasAnchorV4Prefix(currentBlock.Transactions[0].Data))
        {
            if (currentBlock.Number == 0)
            {
                break;
            }

            lookbackCount++;
            if (lookbackCount > MaxBatchLookupBlocks)
            {
                return null;
            }

            // Read L1Origin to check if this is a preconfirmation block
            L1Origin? l1Origin = l1OriginStore.ReadL1Origin((UInt256)currentBlock.Number);

            // Skip preconfirmation blocks
            if (l1Origin is not null && l1Origin.IsPreconfBlock)
            {
                currentBlock = blockFinder.FindBlock(currentBlock.Number - 1);
                continue;
            }

            UInt256? proposalId = currentBlock.Header.DecodeShastaProposalID();
            if (proposalId is null)
            {
                return null;
            }

            if (proposalId.Value == batchId)
            {
                return (UInt256)currentBlock.Number;
            }

            currentBlock = blockFinder.FindBlock(currentBlock.Number - 1);
        }

        return null;
    }

    private static bool HasAnchorV4Prefix(ReadOnlyMemory<byte> data) => data.Length >= 4 && (AnchorV4Selector.AsSpan().SequenceEqual(data.Span[..4])
            || AnchorV4WithSignalSlotsSelector.AsSpan().SequenceEqual(data.Span[..4]));

    /// <inheritdoc />
    public ResultWrapper<bool> taikoDebug_clearTxPoolForReorg()
    {
        txPool.ResetTxPoolState();
        return ResultWrapper<bool>.Success(true);
    }
}
