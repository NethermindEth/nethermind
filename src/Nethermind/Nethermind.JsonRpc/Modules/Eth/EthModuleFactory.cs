// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus;
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
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModuleFactory : ModuleFactoryBase<IEthRpcModule>
    {
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly ILogManager _logManager;
        private readonly IStateReader _stateReader;
        private readonly IBlockchainBridgeFactory _blockchainBridgeFactory;
        private readonly ITxPool _txPool;
        private readonly ITxSender _txSender;
        private readonly IWallet _wallet;
        private readonly IJsonRpcConfig _rpcConfig;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IGasPriceOracle _gasPriceOracle;
        private readonly IEthSyncingInfo _ethSyncingInfo;

        public EthModuleFactory(
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
            IEthSyncingInfo ethSyncingInfo)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _rpcConfig = config ?? throw new ArgumentNullException(nameof(config));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _blockchainBridgeFactory = blockchainBridgeFactory ?? throw new ArgumentNullException(nameof(blockchainBridgeFactory));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
            _blockTree = blockTree.AsReadOnly();
        }

        public override IEthRpcModule Create()
        {
            return new EthRpcModule(
                _rpcConfig,
                _blockchainBridgeFactory.CreateBlockchainBridge(),
                _blockTree,
                _stateReader,
                _txPool,
                _txSender,
                _wallet,
                _logManager,
                _specProvider,
                _gasPriceOracle,
                _ethSyncingInfo,
                 new FeeHistoryOracle(_blockTree, _receiptStorage, _specProvider));
        }

        public static List<JsonConverter> Converters = new()
        {
            new SyncingResultConverter(),
            new ProofConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}
