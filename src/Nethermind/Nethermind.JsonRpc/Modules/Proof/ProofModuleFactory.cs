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
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory : ModuleFactoryBase<IProofRpcModule>
    {
        private readonly IBlockPreprocessorStep _recoveryStep;
        private readonly IReceiptFinder _receiptFinder;
        protected readonly ISpecProvider SpecProvider;
        protected readonly ILogManager LogManager;
        protected readonly IReadOnlyBlockTree BlockTree;
        protected readonly IWorldStateManager WorldStateManager;

        public ProofModuleFactory(
            IWorldStateManager worldStateManager,
            IBlockTree blockTree,
            IBlockPreprocessorStep recoveryStep,
            IReceiptFinder receiptFinder,
            ISpecProvider specProvider,
            ILogManager logManager)
        {
            WorldStateManager = worldStateManager ?? throw new ArgumentNullException(nameof(worldStateManager));
            LogManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            SpecProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            BlockTree = blockTree.AsReadOnly();
        }

        protected virtual ReadOnlyTxProcessingEnv CreateTxProcessingEnv()
        {
            return new ReadOnlyTxProcessingEnv(WorldStateManager, BlockTree, SpecProvider, LogManager);
        }

        protected virtual IBlockProcessor.IBlockTransactionsExecutor CreateBlockTransactionsExecutor(IReadOnlyTxProcessingScope scope)
        {
            return new BlockProcessor.BlockValidationTransactionsExecutor(scope.TransactionProcessor, scope.WorldState);
        }

        public override IProofRpcModule Create()
        {
            ReadOnlyTxProcessingEnv txProcessingEnv = CreateTxProcessingEnv();

            IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

            IBlockProcessor.IBlockTransactionsExecutor traceExecutor = CreateBlockTransactionsExecutor(scope);

            ReadOnlyChainProcessingEnv chainProcessingEnv = new(
                scope,
                Always.Valid,
                _recoveryStep,
                NoBlockRewards.Instance,
                new InMemoryReceiptStorage(),
                SpecProvider,
                BlockTree,
                WorldStateManager.GlobalStateReader,
                LogManager,
                traceExecutor);

            Tracer tracer = new(
                scope.WorldState,
                chainProcessingEnv.ChainProcessor,
                chainProcessingEnv.ChainProcessor);

            return new ProofRpcModule(tracer, BlockTree, _receiptFinder, SpecProvider, LogManager);
        }
    }
}
