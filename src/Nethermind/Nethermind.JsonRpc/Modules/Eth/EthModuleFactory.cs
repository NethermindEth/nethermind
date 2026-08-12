// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Db.LogIndex;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Autofac.Features.AttributeFilters;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModuleFactory(
        ITxPool txPool,
        ITxSender txSender,
        IWallet wallet,
        IBlockTree blockTree,
        IJsonRpcConfig config,
        ILogManager logManager,
        IStateReader stateReader,
        IBlockchainBridgeFactory blockchainBridgeFactory,
        ISpecProvider specProvider,
        [KeyFilter(IReceiptFinder.RegenerableKey)] IReceiptFinder receiptFinder,
        IGasPriceOracle gasPriceOracle,
        IEthSyncingInfo ethSyncingInfo,
        IFeeHistoryOracle feeHistoryOracle,
        IProtocolsManager protocolsManager,
        IBlocksConfig blocksConfig,
        IForkInfo forkInfo,
        ILogIndexConfig logIndexConfig,
        IReceiptConfig receiptConfig,
        IEthCapabilitiesProvider capabilitiesProvider,
        IBlockForRpcFactory blockForRpcFactory)
        : ModuleFactoryBase<IEthRpcModule>
    {
        private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;
        private readonly IReadOnlyBlockTree _blockTree = blockTree.AsReadOnly();
        private readonly HeadBlockSignal _headBlockSignal = new(blockTree);
        // A single cache shared by all pooled module instances, so repeats hit regardless of which instance serves them.
        private readonly EthCallResponseCache? _ethCallCache = EthCallResponseCache.CreateIfEnabled(config);

        public override IEthRpcModule Create() => new EthRpcModule(
                config,
                blockchainBridgeFactory.CreateBlockchainBridge(),
                _blockTree,
                blockTree,
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
                forkInfo,
                logIndexConfig,
                receiptConfig,
                _secondsPerSlot,
                _headBlockSignal,
                capabilitiesProvider,
                blockForRpcFactory,
                _ethCallCache);
    }
}
