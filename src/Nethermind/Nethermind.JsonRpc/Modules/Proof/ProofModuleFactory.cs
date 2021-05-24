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
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory : ModuleFactoryBase<IProofRpcModule>
    {
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly ReadOnlyDbProvider _dbProvider;
        private readonly IReadOnlyTrieStore _trieStore;

        public ProofModuleFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IReadOnlyTrieStore trieStore,
            IBlockPreprocessorStep recoveryStep,
            IReceiptFinder receiptFinder,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _dbProvider = dbProvider.AsReadOnly(false);
            _blockTree = blockTree.AsReadOnly();
            _trieStore = trieStore;
        }

        public override IProofRpcModule Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _dbProvider, _trieStore, _blockTree, _specProvider, _logManager);
            
            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                txProcessingEnv, Always.Valid, _recoveryStep, NoBlockRewards.Instance, new InMemoryReceiptStorage(), _dbProvider, _specProvider, _logManager);

            Tracer tracer = new(
                txProcessingEnv.StateProvider,
                chainProcessingEnv.ChainProcessor);

            return new ProofRpcModule(tracer, _blockTree, _receiptFinder, _specProvider, _logManager);
        }

        private static readonly List<JsonConverter> _converters = new()
        {
            new ProofConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters() => _converters;
    }
}
