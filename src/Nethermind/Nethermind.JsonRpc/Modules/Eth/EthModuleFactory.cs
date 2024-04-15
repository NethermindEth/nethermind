// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        IFeeHistoryOracle feeHistoryOracle)
        : ModuleFactoryBase<IEthRpcModule>
    {
        private readonly IReadOnlyBlockTree _blockTree = blockTree.AsReadOnly();
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        private readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        private readonly IBlockchainBridgeFactory _blockchainBridgeFactory = blockchainBridgeFactory ?? throw new ArgumentNullException(nameof(blockchainBridgeFactory));
        private readonly ITxPool _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
        private readonly ITxSender _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
        private readonly IWallet _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        private readonly IJsonRpcConfig _rpcConfig = config ?? throw new ArgumentNullException(nameof(config));
        private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        private readonly IReceiptStorage _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
        private readonly IGasPriceOracle _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
        private readonly IEthSyncingInfo _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
        private readonly IFeeHistoryOracle _feeHistoryOracle = feeHistoryOracle ?? throw new ArgumentNullException(nameof(feeHistoryOracle));

        public override IEthRpcModule Create()
        {
            return new EthRpcModule(
                _rpcConfig,
                _blockchainBridgeFactory.CreateBlockchainBridge(),
                _blockTree,
                _receiptStorage,
                _stateReader,
                _txPool,
                _txSender,
                _wallet,
                _logManager,
                _specProvider,
                _gasPriceOracle,
                _ethSyncingInfo,
                _feeHistoryOracle);
        }
    }
}
