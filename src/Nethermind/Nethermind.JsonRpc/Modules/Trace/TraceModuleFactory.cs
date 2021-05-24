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
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceModuleFactory : ModuleFactoryBase<ITraceRpcModule>
    {
        private readonly ReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly IReadOnlyTrieStore _trieNodeResolver;
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private ILogger _logger;

        public TraceModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IReadOnlyTrieStore trieNodeResolver,
            IJsonRpcConfig jsonRpcConfig,
            IBlockPreprocessorStep recoveryStep,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptFinder,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _dbProvider = dbProvider.AsReadOnly(false);
            _blockTree = blockTree.AsReadOnly();
            _trieNodeResolver = trieNodeResolver;
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource =
                rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
            _receiptStorage = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }

        public override ITraceRpcModule Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv =
                new(_dbProvider, _trieNodeResolver, _blockTree, _specProvider, _logManager);
            
            IRewardCalculator rewardCalculator = _rewardCalculatorSource.Get(txProcessingEnv.TransactionProcessor);
            
            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                txProcessingEnv,
                Always.Valid,
                _recoveryStep,
                rewardCalculator,
                _receiptStorage,
                _dbProvider,
                _specProvider,
                _logManager);
            
            Tracer tracer = new(chainProcessingEnv.StateProvider, chainProcessingEnv.ChainProcessor);

            return new TraceRpcModule(_receiptStorage, tracer, _blockTree, _jsonRpcConfig);
        }

        public static JsonConverter[] Converters =
        {
            new ParityTxTraceFromReplayConverter(),
            new ParityAccountStateChangeConverter(),
            new ParityTraceActionConverter(),
            new ParityTraceResultConverter(),
            new ParityVmOperationTraceConverter(),
            new ParityVmTraceConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}
