// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    ulong? secondsPerSlot,
    ISyncConfig syncConfig) : EthRpcModule(
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
   secondsPerSlot), ITaikoRpcModule, ITaikoAuthRpcModule
{
    public Task<ResultWrapper<string>> taiko_getSyncMode() => ResultWrapper<string>.Success(syncConfig switch
    {
        { SnapSync: true } => "snap",
        _ => "full",
    });

    public Task<ResultWrapper<L1Origin?>> taiko_headL1Origin()
    {
        return ResultWrapper<L1Origin?>.Fail("not found");
    }

    public Task<ResultWrapper<L1Origin?>> taiko_l1OriginByID(UInt256 blockId)
    {
        return ResultWrapper<L1Origin?>.Fail("not found");
    }

    public Task<ResultWrapper<PreBuiltTxList?>> taiko_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit, ulong maxBytesPerTxList, string[] locals, ulong maxTransactionsLists)
    {
        return ResultWrapper<PreBuiltTxList?>.Fail("not found");
    }
}
