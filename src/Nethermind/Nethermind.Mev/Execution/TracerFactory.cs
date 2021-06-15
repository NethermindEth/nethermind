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
// 

using System;
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

namespace Nethermind.Mev.Execution
{
    public class TracerFactory : ITracerFactory
    {
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly ProcessingOptions _processingOptions;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly ReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyTrieStore _trieStore;

        public TracerFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IReadOnlyTrieStore trieStore,
            IBlockPreprocessorStep recoveryStep,
            ISpecProvider specProvider,
            ILogManager logManager,
            ProcessingOptions processingOptions = ProcessingOptions.Trace)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _processingOptions = processingOptions;
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _dbProvider = dbProvider.AsReadOnly(false);
            _blockTree = blockTree.AsReadOnly();
            _trieStore = trieStore;
        }
        
        public ITracer Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _dbProvider, _trieStore, _blockTree, _specProvider, _logManager);
            
            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                txProcessingEnv, Always.Valid, _recoveryStep, NoBlockRewards.Instance, new InMemoryReceiptStorage(), _dbProvider, _specProvider, _logManager);

            return CreateTracer(txProcessingEnv, chainProcessingEnv);
        }

        protected virtual ITracer CreateTracer(ReadOnlyTxProcessingEnv txProcessingEnv, ReadOnlyChainProcessingEnv chainProcessingEnv) =>
            new Tracer(txProcessingEnv.StateProvider, chainProcessingEnv.ChainProcessor, _processingOptions);
    }
}
