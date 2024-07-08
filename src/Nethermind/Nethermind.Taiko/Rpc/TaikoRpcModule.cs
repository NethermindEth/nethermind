// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
    private readonly TxDecoder _txDecoder;

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
        IL1OriginStore l1OriginStore) : base(
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
        _txDecoder = Rlp.GetStreamDecoder<Transaction>() as TxDecoder ?? throw new NullReferenceException(nameof(_txDecoder));
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

    public Task<ResultWrapper<PreBuiltTxList[]?>> taikoAuth_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit, ulong maxBytesPerTxList, Address[] localAccounts, ulong maxTransactionsLists)
    {
        List<PreBuiltTxList> txLists = [];

        KeyValuePair<AddressAsKey, Queue<Transaction>>[] pendingTxs =
            _txPoolBridge.GetPendingTransactionsBySender()
                .ToDictionary(tx => tx.Key, tx => new Queue<Transaction>(tx.Value.Where(tx => !tx.SupportsBlobs && tx.CanPayBaseFee(baseFee))))
                .ToArray();

        KeyValuePair<AddressAsKey, Queue<Transaction>>[] localTxs = pendingTxs
            .Where(txPerAddr => localAccounts.Contains(txPerAddr.Key.Value) && txPerAddr.Value.Any())
            .ToArray();

        KeyValuePair<AddressAsKey, Queue<Transaction>>[] remoteTxs = pendingTxs
            .Where(txPerAddr => !localAccounts.Contains(txPerAddr.Key.Value) && txPerAddr.Value.Any())
            .ToArray();

        List<KeyValuePair<AddressAsKey, Queue<Transaction>>> source = [.. localTxs, .. remoteTxs];

        TxDecoder decoder = (Rlp.GetStreamDecoder<Transaction>() as TxDecoder)!;

        PreBuiltTxList PickTransactions()
        {
            List<Transaction> txs = [];
            ulong gasUsed = 0;

            byte[] lastCompressed = [];

            while (source.Count is not 0)
            {
                if (source.First().Value.Count is 0)
                {
                    source.RemoveAt(0);
                    continue;
                }

                Transaction nextTx = source.First().Value.Peek();

                if ((ulong)nextTx.GasLimit > (blockMaxGasLimit - gasUsed))
                {
                    break;
                }

                txs.Add(nextTx);

                byte[] compressed = EncodeAndCompress(txs.ToArray());

                if ((ulong)compressed.LongLength > maxBytesPerTxList)
                {
                    txs.RemoveAt(txs.Count - 1);
                    if (lastCompressed.Length is not 0)
                    {
                        break;
                    }
                }

                lastCompressed = compressed;
                gasUsed += (ulong)nextTx.GasLimit;
                source.First().Value.Dequeue();
            }

            return new PreBuiltTxList(lastCompressed, gasUsed, lastCompressed.LongLength);
        }

        for (ulong i = 0; i < maxTransactionsLists; i++)
        {
            PreBuiltTxList list = PickTransactions();
            if (list.Transactions.Length == 0)
            {
                break;
            }
            txLists.Add(list);
        }

        return ResultWrapper<PreBuiltTxList[]?>.Success([.. txLists]);
    }

    private byte[] EncodeAndCompress(Transaction[] txs)
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
