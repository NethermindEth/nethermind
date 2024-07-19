// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Taiko.Rpc;

public class TaikoRpcModule : EthRpcModule, ITaikoRpcModule, ITaikoAuthRpcModule
{
    private readonly ISyncConfig _syncConfig;
    private readonly IL1OriginStore _l1OriginStore;
    private readonly IBlockchainProcessor _chainProcessor;

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
        IBlockchainProcessor chainProcessor) : base(
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
        _chainProcessor = chainProcessor;
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
        ulong maxBytesPerTxList, Address[] localAccounts, int maxTransactionsLists)
    {
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


        BlockHeader? head = _blockFinder.Head?.Header;

        if (source.Count is 0 || head is null)
        {
            return ResultWrapper<PreBuiltTxList[]?>.Success([]);
        }

        var block = new TxListBlock(new BlockHeader(
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
        }, source, maxTransactionsLists, maxBytesPerTxList);


        return ResultWrapper<PreBuiltTxList[]?>.Success(_chainProcessor.Process(block, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance) is not TxListBlock processed ?
            [] :
            processed.Batches.ToArray());
    }
}
