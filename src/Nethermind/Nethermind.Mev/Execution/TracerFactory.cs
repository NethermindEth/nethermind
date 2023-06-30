// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
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
        private readonly IReadOnlyTrieStore _storageTrieStore;

        public TracerFactory(
            IDbProvider dbProvider,
            IBlockTree blockTree,
            IReadOnlyTrieStore trieStore,
            IReadOnlyTrieStore storageTrieStore,
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
            _storageTrieStore = storageTrieStore;
        }

        public ITracer Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _dbProvider, _trieStore, _storageTrieStore, _blockTree, _specProvider, _logManager);

            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                txProcessingEnv, Always.Valid, _recoveryStep, NoBlockRewards.Instance, new InMemoryReceiptStorage(), _dbProvider, _specProvider, _logManager);

            return CreateTracer(txProcessingEnv, chainProcessingEnv);
        }

        protected virtual ITracer CreateTracer(ReadOnlyTxProcessingEnv txProcessingEnv, ReadOnlyChainProcessingEnv chainProcessingEnv) =>
            new Tracer(txProcessingEnv.StateProvider, chainProcessingEnv.ChainProcessor, _processingOptions);
    }
}
