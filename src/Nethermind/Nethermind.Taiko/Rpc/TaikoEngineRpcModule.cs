// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.IO;
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
using Nethermind.Facade.Eth;
using Nethermind.TxPool;
using Nethermind.Blockchain.Find;

namespace Nethermind.Taiko.Rpc;

public class TaikoEngineRpcModule(IAsyncHandler<byte[], ExecutionPayload?> getPayloadHandlerV1,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadHandlerV2,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadHandlerV3,
        IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadHandlerV4,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadV1Handler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedV1Handler,
        IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>> executionGetPayloadBodiesByHashV1Handler,
        IGetPayloadBodiesByRangeV1Handler executionGetPayloadBodiesByRangeV1Handler,
        IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>> executionGetPayloadBodiesByHashV2Handler,
        IGetPayloadBodiesByRangeV2Handler executionGetPayloadBodiesByRangeV2Handler,
        IHandler<TransitionConfigurationV1, TransitionConfigurationV1> transitionConfigurationHandler,
        IHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        IAsyncHandler<byte[][], GetBlobsV1Result> getBlobsHandler,
        ISpecProvider specProvider,
        GCKeeper gcKeeper,
        ILogManager logManager,
        ITxPool txPool = null!,
        IBlockFinder blockFinder = null!,
        IReadOnlyTxProcessorSource readonlyTxProcessingEnv = null!) :
            EngineRpcModule(getPayloadHandlerV1,
                getPayloadHandlerV2,
                getPayloadHandlerV3,
                getPayloadHandlerV4,
                newPayloadV1Handler,
                forkchoiceUpdatedV1Handler,
                executionGetPayloadBodiesByHashV1Handler,
                executionGetPayloadBodiesByRangeV1Handler,
                executionGetPayloadBodiesByHashV2Handler,
                executionGetPayloadBodiesByRangeV2Handler,
                transitionConfigurationHandler,
                capabilitiesHandler,
                getBlobsHandler,
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
         ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists)
    {
        IEnumerable<KeyValuePair<AddressAsKey, Transaction[]>> pendingTxs =
            txPool.GetPendingTransactionsBySender();

        if (localAccounts is not null)
        {
            pendingTxs = pendingTxs.OrderBy(txs => !localAccounts.Contains(txs.Key.Value));
        }

        Transaction[] txQueue = pendingTxs.SelectMany(txs => txs.Value).Where(tx => !tx.SupportsBlobs && tx.CanPayBaseFee(baseFee)).ToArray();

        BlockHeader? head = blockFinder.Head?.Header;

        if (txQueue.Length is 0 || head is null)
        {
            return ResultWrapper<PreBuiltTxList[]?>.Success([]);
        }

        using IReadOnlyTxProcessingScope scope = readonlyTxProcessingEnv.Build(Keccak.EmptyTreeHash);

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
        }, txQueue, maxTransactionsLists, maxBytesPerTxList));
    }


    private readonly TxDecoder _txDecoder = Rlp.GetStreamDecoder<Transaction>() as TxDecoder ?? throw new NullReferenceException(nameof(_txDecoder));

    private PreBuiltTxList[] ProcessTransactions(ITransactionProcessor txProcessor, IWorldState worldState, BlockHeader blockHeader, Transaction[] txSource, int maxBatchCount, ulong maxBytesPerTxList)
    {
        lock (worldState)
        {
            if (txSource.Length is 0 || blockHeader.StateRoot is null)
            {
                return [];
            }

            List<PreBuiltTxList> Batches = [];

            void CommitAndDisposeBatch(Batch batch)
            {
                Batches.Add(new PreBuiltTxList(batch.Transactions.Select(tx => new TransactionForRpc(tx)).ToArray(),
                                               (ulong)blockHeader.GasUsed,
                                               batch.GetCompressedTxsLength()));
                blockHeader.GasUsed = 0;
                batch.Dispose();
            }

            BlockExecutionContext blkCtx = new(blockHeader);
            worldState.StateRoot = blockHeader.StateRoot;

            Batch batch = new(maxBytesPerTxList, _txDecoder);

            for (int i = 0; i < txSource.Length;)
            {
                Transaction tx = txSource[i];
                Snapshot snapshot = worldState.TakeSnapshot(true);
                long gasUsed = blockHeader.GasUsed;

                void IgnoreCurrentSender()
                {
                    while (i < txSource.Length && txSource[i].SenderAddress == tx.SenderAddress) i++;
                }

                void RestoreState()
                {
                    worldState.Restore(snapshot);
                }

                try
                {
                    TransactionResult executionResult = txProcessor.Execute(tx, in blkCtx, NullTxTracer.Instance);

                    if (!executionResult)
                    {
                        RestoreState();

                        if (executionResult.Error == TransactionResult.BlockGasLimitExceeded && batch.Transactions.Count is not 0)
                        {
                            CommitAndDisposeBatch(batch);
                            batch = new(maxBytesPerTxList, _txDecoder);

                            if (maxBatchCount == Batches.Count)
                            {
                                return [.. Batches];
                            }

                            continue;
                        }

                        IgnoreCurrentSender();
                        continue;
                    }
                }
                catch
                {
                    RestoreState();
                    IgnoreCurrentSender();
                    continue;
                }

                if (!batch.TryAddTx(tx))
                {
                    RestoreState();

                    if (batch.Transactions.Count is 0)
                    {
                        IgnoreCurrentSender();
                        continue;
                    }
                    else
                    {
                        CommitAndDisposeBatch(batch);
                        batch = new(maxBytesPerTxList, _txDecoder);

                        if (maxBatchCount == Batches.Count)
                        {
                            return [.. Batches];
                        }

                        continue;
                    }
                }

                i++;
            }

            if (batch.Transactions.Count is not 0)
            {
                CommitAndDisposeBatch(batch);
            }

            return [.. Batches];
        }
    }

    sealed class Batch(ulong maxBytes, TxDecoder txDecoder) : IDisposable
    {
        private readonly ulong _maxBytes = maxBytes;
        private ulong _length;

        public ArrayPoolList<Transaction> Transactions { get; } = ArrayPoolList<Transaction>.Empty();

        public bool TryAddTx(Transaction tx)
        {
            ulong estimatedLength = EstimateTxLength(tx);
            if (_length + estimatedLength < _maxBytes)
            {
                Transactions.Add(tx);
                _length += estimatedLength;
                return true;
            }

            return false;
        }

        public ulong GetCompressedTxsLength()
        {
            int contentLength = Transactions.Sum(tx => txDecoder.GetLength(tx, RlpBehaviors.None));
            byte[] data = ArrayPool<byte>.Shared.Rent(contentLength);

            try
            {
                RlpStream rlpStream = new(data);

                rlpStream.StartSequence(contentLength);
                foreach (Transaction tx in Transactions)
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

        private ulong EstimateTxLength(Transaction tx)
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

        public void Dispose()
        {
            Transactions.Dispose();
        }
    }
}
