// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

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

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory : ModuleFactoryBase<IProofRpcModule>
    {
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IReceiptFinder _receiptFinder;
        private readonly ISpecProvider _specProvider;
        private readonly ILogManager _logManager;
        private readonly IReadOnlyBlockTree _blockTree;
        private readonly IWorldStateManager _worldStateManager;
        private readonly IReadOnlyTxProcessorSource _txProcessorSource;

        public ProofModuleFactory(
            IWorldStateManager worldStateManager,
            IBlockTree blockTree,
            IBlockPreprocessorStep recoveryStep,
            IReceiptFinder receiptFinder,
            ISpecProvider specProvider,
            IReadOnlyTxProcessorSource txProcessorSource,
            ILogManager logManager)
        {
            _worldStateManager = worldStateManager ?? throw new ArgumentNullException(nameof(worldStateManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _txProcessorSource = txProcessorSource ?? throw new ArgumentNullException(nameof(txProcessorSource));
            _blockTree = blockTree.AsReadOnly();
        }

        public override IProofRpcModule Create()
        {
            IReadOnlyTxProcessingScope scope = _txProcessorSource.Build(Keccak.EmptyTreeHash);

            RpcBlockTransactionsExecutor traceExecutor = new(scope.TransactionProcessor, scope.WorldState);

            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                scope,
                Always.Valid,
                _recoveryStep,
                NoBlockRewards.Instance,
                new InMemoryReceiptStorage(),
                _specProvider,
                _blockTree,
                _worldStateManager.GlobalStateReader,
                _logManager,
                traceExecutor);

            Tracer tracer = new(
                scope.WorldState,
                chainProcessingEnv.ChainProcessor,
                chainProcessingEnv.ChainProcessor);

            return new ProofRpcModule(tracer, _blockTree, _receiptFinder, _specProvider, _logManager);
        }
    }
}
