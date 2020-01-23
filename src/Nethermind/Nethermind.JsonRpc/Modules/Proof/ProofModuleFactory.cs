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
using Nethermind.Core.Specs;
using Nethermind.JsonRpc.Eip1186;
using Nethermind.Logging;
using Nethermind.Store;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory : ModuleFactoryBase<IProofModule>
    {
        private readonly IBlockProcessor _blockProcessor;
        private readonly ISpecProvider _specProvider;
        private readonly IDbProvider _dbProvider;
        private readonly ILogManager _logManager;
        private readonly IBlockTree _blockTree;

        public ProofModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IBlockProcessor blockProcessor,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
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
            
            // simplify blockchain bridge for this
            // var blockchainBridge = new BlockchainBridge(
            //     readOnlyTxProcessingEnv.StateReader,
            //     readOnlyTxProcessingEnv.StateProvider,
            //     readOnlyTxProcessingEnv.StorageProvider,
            //     readOnlyTxProcessingEnv.BlockTree,
            //     readOnlyTxProcessingEnv.TransactionProcessor);
            
            return new ProofModule(_logManager);
        }

        private static List<JsonConverter> Converters = new List<JsonConverter>
        {
            new ProofConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => Converters;
    }
}