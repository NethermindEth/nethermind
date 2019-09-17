/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.JsonRpc.Eip1186;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Wallet;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModuleFactory : ModuleFactoryBase<IEthModule>
    {
        private readonly IBlockTree _blockTree;
        private readonly IDbProvider _dbProvider;
        private readonly IEthereumEcdsa _ethereumEcdsa;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly ITxPool _txPool;
        private readonly IWallet _wallet;
        private readonly IFilterStore _filterStore;
        private readonly IFilterManager _filterManager;

        public EthModuleFactory(
            IDbProvider dbProvider,
            ITxPool txPool,
            IWallet wallet,
            IBlockTree blockTree,
            IEthereumEcdsa ethereumEcdsa,
            IBlockProcessor blockProcessor,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _ethereumEcdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            
            _filterStore = new FilterStore();
            _filterManager = new FilterManager(_filterStore, blockProcessor, _txPool, _logManager);
        }
        
        public override IEthModule Create()
        {
            ReadOnlyBlockTree readOnlyTree = new ReadOnlyBlockTree(_blockTree);
            IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, readOnlyTree, _specProvider, _logManager);
            
            var blockchainBridge = new BlockchainBridge(
                readOnlyTxProcessingEnv.StateReader,
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyTxProcessingEnv.StorageProvider,
                readOnlyTxProcessingEnv.BlockTree,
                _txPool,
                _receiptStorage,
                _filterStore,
                _filterManager,
                _wallet,
                readOnlyTxProcessingEnv.TransactionProcessor,
                _ethereumEcdsa);
            
            return new EthModule(_logManager, blockchainBridge);
        }
        
        
        public static List<JsonConverter> Converters = new List<JsonConverter>
        {
            new SyncingResultConverter(),
            new ProofConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}