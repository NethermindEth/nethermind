//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core.Specs;
using Nethermind.Facade;
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
            IReceiptStorage receiptStorage)
        {
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _rpcConfig = config ?? throw new ArgumentNullException(nameof(config));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _blockchainBridgeFactory = blockchainBridgeFactory ?? throw new ArgumentNullException(nameof(blockchainBridgeFactory));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
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
                new GasPriceOracle(_blockTree, _specProvider),
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
