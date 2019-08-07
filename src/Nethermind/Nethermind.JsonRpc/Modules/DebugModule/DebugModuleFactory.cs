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
using System.Reflection;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Store;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    public class DebugModuleFactory : ModuleFactoryBase<IDebugModule>
    {
        private readonly IBlockTree _blockTree;
        private readonly IDbProvider _dbProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly IRewardCalculator _rewardCalculator;
        private readonly IReceiptStorage _receiptStorage;
        private readonly IConfigProvider _configProvider;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private ILogger _logger;

        public DebugModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IBlockDataRecoveryStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            IConfigProvider configProvider,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }

        public override IDebugModule Create()
        {
            IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            ReadOnlyBlockTree readOnlyTree = new ReadOnlyBlockTree(_blockTree);
            ReadOnlyTxProcessingEnv txEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, readOnlyTree, _specProvider, _logManager);
            ReadOnlyChainProcessingEnv readOnlyChainProcessingEnv = new ReadOnlyChainProcessingEnv(txEnv, _blockValidator, _recoveryStep, _rewardCalculator, _receiptStorage, readOnlyDbProvider, _specProvider, _logManager);
            ITracer tracer = new Tracer(readOnlyChainProcessingEnv.Processor, _receiptStorage, new ReadOnlyBlockTree(_blockTree), _dbProvider.TraceDb);

            DebugBridge debugBridge = new DebugBridge(_configProvider, readOnlyDbProvider, tracer, readOnlyChainProcessingEnv.Processor, readOnlyTree);
            return new DebugModule(_logManager, debugBridge);
        }

        public static JsonConverter[] Converters = {new GethLikeTxTraceConverter()};

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}