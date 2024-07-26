// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
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

namespace Nethermind.Taiko.Rpc;

public class TaikoRpcModule : EthRpcModule, ITaikoRpcModule, ITaikoAuthRpcModule
{
    private readonly ISyncConfig _syncConfig;
    private readonly IL1OriginStore _l1OriginStore;
    private readonly ReadOnlyTxProcessingEnv _readonlyTxProcessingEnv;

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
        ReadOnlyTxProcessingEnv readonlyTxProcessingEnv) : base(
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

    public Task<ResultWrapper<PreBuiltTxList[]?>> taikoAuth_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit,
        ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists)
    {
        KeyValuePair<AddressAsKey, Queue<Transaction>>[] pendingTxs =
            _txPoolBridge.GetPendingTransactionsBySender()
                .ToDictionary(tx => tx.Key, tx => new Queue<Transaction>(tx.Value.Where(tx => !tx.SupportsBlobs && tx.CanPayBaseFee(baseFee))))
                .ToArray();

        if (localAccounts is not null)
        {
            KeyValuePair<AddressAsKey, Queue<Transaction>>[] localTxs = pendingTxs
                .Where(txPerAddr => localAccounts.Contains(txPerAddr.Key.Value) && txPerAddr.Value.Any())
                .ToArray();

            KeyValuePair<AddressAsKey, Queue<Transaction>>[] remoteTxs = pendingTxs
                .Where(txPerAddr => !localAccounts.Contains(txPerAddr.Key.Value) && txPerAddr.Value.Any())
                .ToArray();

            pendingTxs = [.. localTxs, .. remoteTxs];
        }



        BlockHeader? head = _blockFinder.Head?.Header;

        if (pendingTxs.Length is 0 || head is null)
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
        }, pendingTxs, maxTransactionsLists, maxBytesPerTxList));
    }


    private readonly TxDecoder _txDecoder = Rlp.GetStreamDecoder<Transaction>() as TxDecoder ?? throw new NullReferenceException(nameof(_txDecoder));

    public PreBuiltTxList[] ProcessTransactions(ITransactionProcessor txProcessor, IWorldState worldState, BlockHeader blockHeader, KeyValuePair<AddressAsKey, Queue<Transaction>>[] txSource, int maxBatchCount, ulong maxBytesPerTxList)
    {
        lock (worldState)
        {
            if (txSource.Length is 0 || blockHeader.StateRoot is null)
            {
                return [];
            }

            List<PreBuiltTxList> Batches = [];

            List<Transaction> currentBatch = [];

            void CommitBatch()
            {
                byte[] list = EncodeAndCompress(currentBatch.ToArray());
                Batches.Add(new PreBuiltTxList(currentBatch.Select(tx => new TransactionForRpc(tx)).ToArray(),
                                               (ulong)blockHeader.GasUsed,
                                               list.Length));
                currentBatch = [];
                blockHeader.GasUsed = 0;
            }

            bool TryAddToBatch(Transaction tx)
            {
                currentBatch.Add(tx);

                byte[] compressed = EncodeAndCompress(currentBatch.ToArray());

                if ((ulong)compressed.LongLength > maxBytesPerTxList)
                {
                    currentBatch.RemoveAt(currentBatch.Count - 1);
                    return false;
                }

                return true;
            }

            BlockExecutionContext blkCtx = new(blockHeader);
            worldState.StateRoot = blockHeader.StateRoot;

            for (int senderCounter = 0; senderCounter < txSource.Length; senderCounter++)
            {
                while (txSource[senderCounter].Value.Count != 0)
                {
                    Snapshot snapshot = worldState.TakeSnapshot(true);
                    Transaction tx = txSource[senderCounter].Value.Peek();

                    if (tx.Type == TxType.Blob)
                    {
                        txSource[senderCounter].Value.Clear();
                        break;
                    }

                    try
                    {
                        if (!txProcessor.Execute(tx, in blkCtx, NullTxTracer.Instance))
                        {
                            txSource[senderCounter].Value.Clear();
                            worldState.Restore(snapshot);
                            break;
                        }
                    }
                    catch
                    {
                        txSource[senderCounter].Value.Clear();
                        worldState.Restore(snapshot);
                        break;
                    }

                    if (TryAddToBatch(tx))
                    {
                        txSource[senderCounter].Value.Dequeue();
                    }
                    else
                    {
                        if (!currentBatch.Any())
                        {
                            txSource[senderCounter].Value.Clear();
                            worldState.Restore(snapshot);
                        }
                        else
                        {
                            CommitBatch();
                            worldState.Restore(snapshot);
                            if (maxBatchCount <= Batches.Count)
                            {
                                return Batches.ToArray();
                            }
                        }
                    }
                }
            }

            if (currentBatch.Any())
            {
                CommitBatch();
            }

            return Batches.ToArray();
        }
    }

    byte[] EncodeAndCompress(Transaction[] txs)
    {
        int contentLength = txs.Sum(tx => _txDecoder.GetLength(tx, RlpBehaviors.None));
        RlpStream rlpStream = new(Rlp.LengthOfSequence(contentLength));

        rlpStream.StartSequence(contentLength);
        foreach (Transaction tx in txs)
        {
            _txDecoder.Encode(rlpStream, tx);
        }

        using MemoryStream stream = new();
        using ZLibStream compressingStream = new(stream, CompressionMode.Compress, false);
        compressingStream.Write(rlpStream.Data);
        compressingStream.Close();
        return stream.ToArray();
    }
}
