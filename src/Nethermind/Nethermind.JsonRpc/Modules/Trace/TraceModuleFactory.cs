//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceModuleFactory : ModuleFactoryBase<ITraceModule>
    {
        private readonly IBlockTree _blockTree;
        private readonly IDbProvider _dbProvider;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private readonly IRewardCalculatorSource _rewardCalculatorSource;
        private ILogger _logger;

        public TraceModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IBlockDataRecoveryStep recoveryStep,
            IRewardCalculatorSource rewardCalculatorSource,
            IReceiptStorage receiptFinder,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculatorSource = rewardCalculatorSource ?? throw new ArgumentNullException(nameof(rewardCalculatorSource));
            _receiptStorage = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }
        
        public override ITraceModule Create()
        {
            var readOnlyTree = new ReadOnlyBlockTree(_blockTree);
            var readOnlyDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            var readOnlyTxProcessingEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, readOnlyTree, _specProvider, _logManager);
            var readOnlyChainProcessingEnv = new ReadOnlyChainProcessingEnv(readOnlyTxProcessingEnv, Always.Valid, _recoveryStep, _rewardCalculatorSource.Get(readOnlyTxProcessingEnv.TransactionProcessor), _receiptStorage, readOnlyDbProvider, _specProvider, _logManager);
            Tracer tracer = new Tracer(readOnlyChainProcessingEnv.StateProvider, readOnlyChainProcessingEnv.ChainProcessor);
            
            return new TraceModule(_receiptStorage, tracer, readOnlyTree);
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