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
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class DebugModuleFactory : ModuleFactoryBase<IDebugRpcModule>
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IBlockValidator _blockValidator;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IReceiptsMigration _receiptsMigration;
        private readonly IReadOnlyTrieStore _trieStore;
        private readonly IConfigProvider _configProvider;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyBlockTree _blockTree;
        private ILogger _logger;

        public DebugModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IJsonRpcConfig jsonRpcConfig,
            IBlockValidator blockValidator,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculatorSource rewardCalculator,
            IReceiptStorage receiptStorage,
            IReceiptsMigration receiptsMigration,
            IReadOnlyTrieStore trieStore,
            IConfigProvider configProvider,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _dbProvider = dbProvider.AsReadOnly(false);
            _blockTree = blockTree.AsReadOnly();
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _receiptsMigration = receiptsMigration ?? throw new ArgumentNullException(nameof(receiptsMigration));
            _trieStore = (trieStore ?? throw new ArgumentNullException(nameof(trieStore)));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }

        public override IDebugRpcModule Create()
        {
            ReadOnlyTxProcessingEnv txEnv = new(
                _dbProvider,
                _trieStore,
                _blockTree,
                _specProvider,
                _logManager);

            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                txEnv,
                _blockValidator,
                _recoveryStep,
                _rewardCalculatorSource.Get(txEnv.TransactionProcessor),
                _receiptStorage,
                _dbProvider,
                _specProvider,
                _logManager);

            GethStyleTracer tracer = new(
                chainProcessingEnv.ChainProcessor,
                _receiptStorage,
                _blockTree);

            DebugBridge debugBridge = new(
                _configProvider,
                _dbProvider,
                tracer,
                _blockTree,
                _receiptStorage,
                _receiptsMigration,
                _specProvider);

            return new DebugRpcModule(_logManager, debugBridge, _jsonRpcConfig);
        }

        public static JsonConverter[] Converters = {new GethLikeTxTraceConverter()};

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}
