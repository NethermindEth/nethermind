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
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Store;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory : ModuleFactoryBase<IProofModule>
    {
        private readonly IBlockDataRecoveryStep _recoveryStep;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ISpecProvider _specProvider;
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockTree _blockTree;

        public ProofModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IBlockDataRecoveryStep recoveryStep,
            IReceiptStorage receiptStorage,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _receiptStorage = receiptStorage ?? throw new ArgumentNullException(nameof(receiptStorage));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        }
        
        public override IProofModule Create()
        {
            ReadOnlyBlockTree readOnlyTree = new ReadOnlyBlockTree(_blockTree);
            IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = new ReadOnlyTxProcessingEnv(readOnlyDbProvider, readOnlyTree, _specProvider, _logManager);
            ReadOnlyChainProcessingEnv readOnlyChainProcessingEnv = new ReadOnlyChainProcessingEnv(readOnlyTxProcessingEnv, AlwaysValidBlockValidator.Instance, _recoveryStep, NoBlockRewards.Instance, new InMemoryReceiptStorage(), readOnlyDbProvider, _specProvider, _logManager);
            
            Tracer tracer = new Tracer(
                readOnlyTxProcessingEnv.StateProvider,
                readOnlyChainProcessingEnv.ChainProcessor);
            
            return new ProofModule(tracer, _blockTree, _receiptStorage, _specProvider, _logManager);
        }

        private static List<JsonConverter> _converters = new List<JsonConverter>
        {
            new ProofConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => _converters;
    }
}