// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;

namespace Nethermind.Taiko.Rpc;

public class TaikoRpcModule(
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
    IProtocolsManager protocolsManager,
    ulong? secondsPerSlot,
    ISyncConfig syncConfig,
    IL1OriginStore l1OriginStore) : EthRpcModule(
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
   protocolsManager,
   secondsPerSlot), ITaikoRpcModule
{
    private static readonly ResultWrapper<L1Origin?> NotFound = ResultWrapper<L1Origin?>.Fail("not found");

    public Task<ResultWrapper<string>> taiko_getSyncMode() => ResultWrapper<string>.Success(syncConfig switch
    {
        { SnapSync: true } => "snap",
        _ => "full",
    });

    public Task<ResultWrapper<L1Origin?>> taiko_headL1Origin()
    {
        UInt256? head = l1OriginStore.ReadHeadL1Origin();
        if (head is null)
        {
            return NotFound;
        }

        L1Origin? origin = l1OriginStore.ReadL1Origin(head.Value);

        return origin is null ? NotFound : ResultWrapper<L1Origin?>.Success(origin);
    }

    public Task<ResultWrapper<L1Origin?>> taiko_l1OriginByID(UInt256 blockId)
    {
        L1Origin? origin = l1OriginStore.ReadL1Origin(blockId);

        return origin is null ? NotFound : ResultWrapper<L1Origin?>.Success(origin);
    }
}
