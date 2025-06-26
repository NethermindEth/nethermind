// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.IO;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.Taiko.Rpc;

public class TaikoEngineRpcModule(IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
        IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadHandlerV4,
        IAsyncHandler<byte[], GetPayloadV5Result?> getPayloadHandlerV5,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        IHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> getBlobsHandler,
        IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>?> getBlobsHandlerV2,
        IEngineRequestsTracker engineRequestsTracker,
        ISpecProvider specProvider,
        GCKeeper gcKeeper,
        ILogManager logManager,
        ITxPool txPool,
        IBlockFinder blockFinder,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
        IRlpStreamDecoder<Transaction> txDecoder) :
            EngineRpcModule(getPayloadHandlerV1,
                getPayloadHandlerV2,
                getPayloadHandlerV3,
                getPayloadHandlerV4,
                getPayloadHandlerV5,
                newPayloadV1Handler,
                forkchoiceUpdatedV1Handler,
                executionGetPayloadBodiesByHashV1Handler,
                executionGetPayloadBodiesByRangeV1Handler,
                transitionConfigurationHandler,
                capabilitiesHandler,
                getBlobsHandler,
                getBlobsHandlerV2,
                engineRequestsTracker,
                specProvider,
                gcKeeper,
                logManager), ITaikoEngineRpcModule
{
    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV1(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null)
    {
        return base.engine_forkchoiceUpdatedV1(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV1(TaikoExecutionPayload executionPayload)
    {
        return base.engine_newPayloadV1(executionPayload);
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV2(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null)
    {
        return base.engine_forkchoiceUpdatedV2(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV2(TaikoExecutionPayload executionPayload)
    {
        return base.engine_newPayloadV2(executionPayload);
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV3(ForkchoiceStateV1 forkchoiceState, TaikoPayloadAttributes? payloadAttributes = null)
    {
        return base.engine_forkchoiceUpdatedV3(forkchoiceState, payloadAttributes);
    }

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(TaikoExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes, Hash256? parentBeaconBlockRoot)
    {
        return base.engine_newPayloadV3(executionPayload, blobVersionedHashes, parentBeaconBlockRoot);
    }

    public ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit,
         ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists) =>
        taikoAuth_txPoolContentWithMinTip(beneficiary, baseFee, blockMaxGasLimit, maxBytesPerTxList, localAccounts, maxTransactionsLists, 0);

    public ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContentWithMinTip(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit,
       ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists, ulong minTip)
    {
        IEnumerable<KeyValuePair<AddressAsKey, Transaction[]>> pendingTxs = txPool.GetPendingTransactionsBySender();

        if (localAccounts is not null)
        {
            pendingTxs = pendingTxs.OrderBy(txs => !localAccounts.Contains(txs.Key.Value));
        }

        IEnumerable<Transaction> allTxs = pendingTxs.SelectMany(txs => txs.Value).Where(tx => !tx.SupportsBlobs && tx.CanPayBaseFee(baseFee));

        Transaction[] txQueue = [.. minTip is 0 ? allTxs : allTxs.Where(tx => tx.TryCalculatePremiumPerGas(baseFee, out UInt256 premiumPerGas) && premiumPerGas >= minTip)];

        BlockHeader? head = blockFinder.Head?.Header;

        if (txQueue.Length is 0 || head?.StateRoot is null)
        {
            return ResultWrapper<PreBuiltTxList[]?>.Success([]);
        }

        IReadOnlyTxProcessorSource readonlyTxProcessingEnv = readOnlyTxProcessingEnvFactory.Create();
        using IReadOnlyTxProcessingScope scope = readonlyTxProcessingEnv.Build(head.StateRoot);

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
            TotalDifficulty = 0,
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
        worldState.StateRoot = blockHeader.StateRoot;

        Batch batch = new(maxBytesPerTxList, txSource.Length, txDecoder);

        try
        {
            for (int i = 0; i < txSource.Length;)
            {
                Transaction tx = txSource[i];
                Snapshot snapshot = worldState.TakeSnapshot(true);

                try
                {
                    TransactionResult executionResult = txProcessor.Execute(tx, in blkCtx, NullTxTracer.Instance);

                    if (!executionResult)
                    {
                        worldState.Restore(snapshot);

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
                }
                catch
                {
                    worldState.Restore(snapshot);
                    while (i < txSource.Length && txSource[i].SenderAddress == tx.SenderAddress) i++;
                    continue;
                }

                if (!batch.TryAddTx(tx))
                {
                    worldState.Restore(snapshot);

                    if (batch.Transactions.Count is 0)
                    {
                        while (i < txSource.Length && txSource[i].SenderAddress == tx.SenderAddress) i++;
                        continue;
                    }
                    else
                    {
                        CommitAndDisposeBatch(batch);

                        if (maxBatchCount == Batches.Count)
                        {
                            return [.. Batches];
                        }

                        batch = new(maxBytesPerTxList, txSource.Length - i, txDecoder);

                        continue;
                    }
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

    struct Batch(ulong maxBytes, int transactionsListCapacity, IRlpStreamDecoder<Transaction> txDecoder) : IDisposable
    {
        private readonly ulong _maxBytes = maxBytes;
        private ulong _length;

        public ArrayPoolList<Transaction> Transactions { get; } = new ArrayPoolList<Transaction>(transactionsListCapacity);

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
            byte[] data = ArrayPool<byte>.Shared.Rent(contentLength);

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

        public readonly void Dispose()
        {
            Transactions.Dispose();
        }
    }
}
