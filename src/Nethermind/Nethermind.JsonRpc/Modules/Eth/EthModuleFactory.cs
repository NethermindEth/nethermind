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
        IReceiptStorage receiptStorage,
        IGasPriceOracle gasPriceOracle,
        IEthSyncingInfo ethSyncingInfo,
        IFeeHistoryOracle feeHistoryOracle,
        IProtocolsManager protocolsManager,
        IBlocksConfig blocksConfig,
        IForkInfo forkInfo,
        ILogIndexConfig logIndexConfig)
        : ModuleFactoryBase<IEthRpcModule>
    {
        private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;
        private readonly IReadOnlyBlockTree _blockTree = blockTree.AsReadOnly();

        public override IEthRpcModule Create()
        {
            return new EthRpcModule(
                config,
                blockchainBridgeFactory.CreateBlockchainBridge(),
                _blockTree,
                receiptStorage,
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
                _secondsPerSlot);
        }
    }
}
