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
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Wallet;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Trace
{
    public class TraceModuleFactory : ModuleFactoryBase<ITraceModule>
    {
        private readonly IBlockTree _blockTree;
        private readonly IDbProvider _dbProvider;
        private readonly IBlockValidator _blockValidator;
        private readonly IEthereumEcdsa _ethereumEcdsa;
        private readonly IRewardCalculator _rewardCalculator;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly ITxPool _txPool;
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private ILogger _logger;

        public TraceModuleFactory(
            IDbProvider dbProvider,
            ITxPool txPool,
            IBlockTree blockTree,
            IBlockValidator blockValidator,
            IEthereumEcdsa ethereumEcdsa,
            IBlockDataRecoveryStep recoveryStep,
            IRewardCalculator rewardCalculator,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockValidator = blockValidator ?? throw new ArgumentNullException(nameof(blockValidator));
            _ethereumEcdsa = ethereumEcdsa ?? throw new ArgumentNullException(nameof(ethereumEcdsa));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _rewardCalculator = rewardCalculator ?? throw new ArgumentNullException(nameof(rewardCalculator));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }
        
        public override ITraceModule Create()
        {
            ReadOnlyBlockTree readOnlyTree = new ReadOnlyBlockTree(_blockTree);
            IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            ReadOnlyTxProcessingEnv txEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, readOnlyTree, _specProvider, _logManager);
            
            var blockchainBridge = new BlockchainBridge(
                txEnv.StateReader,
                txEnv.StateProvider,
                txEnv.StorageProvider,
                txEnv.BlockTree,
                _txPool,
                _receiptStorage,
                NullFilterStore.Instance,
                NullFilterManager.Instance,
                NullWallet.Instance,
                txEnv.TransactionProcessor,
                _ethereumEcdsa);
            
            ReadOnlyChainProcessingEnv chainEnv = new ReadOnlyChainProcessingEnv(txEnv, _blockValidator, _recoveryStep, _rewardCalculator, _receiptStorage, readOnlyDbProvider, _specProvider, _logManager);
            ITracer tracer = new Tracer(chainEnv.Processor, _receiptStorage, new ReadOnlyBlockTree(_blockTree), _dbProvider.TraceDb);
            
            return new TraceModule(blockchainBridge, _logManager, tracer);
        }
        
        public static JsonConverter[] Converters = 
        {
            new ParityLikeTxTraceConverter(),
            new ParityAccountStateChangeConverter(),
            new ParityTraceActionConverter(),
            new ParityTraceResultConverter(),
            new ParityVmOperationTraceConverter(),
            new ParityVmTraceConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}