// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
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
        private readonly IWorldStateManager _worldStateManager;

        public TracerFactory(
            IBlockTree blockTree,
            IWorldStateManager worldStateManager,
            IBlockPreprocessorStep recoveryStep,
            ISpecProvider specProvider,
            ILogManager logManager,
            ProcessingOptions processingOptions = ProcessingOptions.Trace)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _processingOptions = processingOptions;
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _worldStateManager = worldStateManager ?? throw new ArgumentNullException(nameof(worldStateManager));
            _blockTree = blockTree.AsReadOnly();
        }

        public ITracer Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = new(
                _worldStateManager, _blockTree, _specProvider, _logManager);

            IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                scope,
                Always.Valid,
                _recoveryStep,
                NoBlockRewards.Instance,
                new InMemoryReceiptStorage(),
                _specProvider,
                _blockTree,
                _worldStateManager.GlobalStateReader,
                _logManager);

            return CreateTracer(scope, chainProcessingEnv);
        }

        protected virtual ITracer CreateTracer(IReadOnlyTxProcessingScope scope, ReadOnlyChainProcessingEnv chainProcessingEnv) =>
            new Tracer(scope.WorldState, chainProcessingEnv.ChainProcessor, chainProcessingEnv.ChainProcessor, _processingOptions);
    }
}
