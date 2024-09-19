// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Microsoft.IO;
using Nethermind.Core.Resettables;
using System.Buffers;
using Nethermind.Core.Collections;

namespace Nethermind.Taiko.Rpc;

public class TaikoRpcModule : EthRpcModule, ITaikoRpcModule, ITaikoAuthRpcModule
{
    private readonly ISyncConfig _syncConfig;
    private readonly IL1OriginStore _l1OriginStore;
    private readonly IReadOnlyTxProcessorSource _readonlyTxProcessingEnv;

    public TaikoRpcModule(
        IJsonRpcConfig rpcConfig,
        IBlockchainBridge blockchainBridge,
        IBlockFinder blockFinder,
        IReceiptFinder receiptFinder,
        IStateReader stateReader,
        ITxPool txPool,
        ITxSender txSender,
        IWallet wallet,
        ILogManager logManager,
        ISpecProvider specProvider,
        IGasPriceOracle gasPriceOracle,
        IEthSyncingInfo ethSyncingInfo,
        IFeeHistoryOracle feeHistoryOracle,
        ulong? secondsPerSlot,
        ISyncConfig syncConfig,
        IL1OriginStore l1OriginStore,
        IReadOnlyTxProcessorSource readonlyTxProcessingEnv) : base(
       rpcConfig,
       blockchainBridge,
       blockFinder,
       receiptFinder,
       stateReader,
       txPool,
       txSender,
       wallet,
       logManager,
       specProvider,
       gasPriceOracle,
       ethSyncingInfo,
       feeHistoryOracle,
       secondsPerSlot)
    {
        _syncConfig = syncConfig;
        _l1OriginStore = l1OriginStore;
        _readonlyTxProcessingEnv = readonlyTxProcessingEnv;
    }

    public Task<ResultWrapper<string>> taiko_getSyncMode() => ResultWrapper<string>.Success(_syncConfig switch
    {
        { SnapSync: true } => "snap",
        _ => "full",
    });

    public Task<ResultWrapper<L1Origin?>> taiko_headL1Origin()
    {
        UInt256? head = _l1OriginStore.ReadHeadL1Origin();
        if (head is null)
        {
            return ResultWrapper<L1Origin?>.Fail("not found");
        }

        L1Origin? origin = _l1OriginStore.ReadL1Origin(head.Value);

        return origin is null ? ResultWrapper<L1Origin?>.Fail("not found") : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<L1Origin?>> taiko_l1OriginByID(UInt256 blockId)
    {
        L1Origin? origin = _l1OriginStore.ReadL1Origin(blockId);

        return origin is null ? ResultWrapper<L1Origin?>.Fail("not found") : ResultWrapper<L1Origin?>.Success(origin);
    }

    public ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit,
        ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists)
    {
        IEnumerable<KeyValuePair<AddressAsKey, Transaction[]>> pendingTxs =
            _txPool.GetPendingTransactionsBySender();

        if (localAccounts is not null)
        {
            pendingTxs = pendingTxs.OrderBy(txs => !localAccounts.Contains(txs.Key.Value));
        }

        Transaction[] txQueue = pendingTxs.SelectMany(txs => txs.Value).Where(tx => !tx.SupportsBlobs && tx.CanPayBaseFee(baseFee)).ToArray();

        BlockHeader? head = _blockFinder.Head?.Header;

        if (txQueue.Length is 0 || head is null)
        {
            return ResultWrapper<PreBuiltTxList[]?>.Success([]);
        }

        using IReadOnlyTxProcessingScope scope = _readonlyTxProcessingEnv.Build(Keccak.EmptyTreeHash);

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
