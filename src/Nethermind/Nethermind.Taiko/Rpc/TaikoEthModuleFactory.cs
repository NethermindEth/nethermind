// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.State;
using Nethermind.Facade;
using Nethermind.Core.Specs;
using Nethermind.Blockchain.Receipts;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Facade.Eth;
using Nethermind.Blockchain.Find;
using Nethermind.Network;

namespace Nethermind.Taiko.Rpc;

class TaikoEthModuleFactory(IJsonRpcConfig jsonRpcConfig,
    IBlockchainBridgeFactory blockchainBridgeFactory,
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
    IL1OriginStore l1OriginStore
) : ModuleFactoryBase<ITaikoRpcModule>()
{
    public override ITaikoRpcModule Create()
    {
        return new TaikoRpcModule(
            jsonRpcConfig,
            blockchainBridgeFactory.CreateBlockchainBridge(),
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
            secondsPerSlot,
            syncConfig,
            l1OriginStore
        );
    }
}
