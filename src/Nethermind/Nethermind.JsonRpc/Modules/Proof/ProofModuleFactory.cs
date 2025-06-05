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
    public class ProofModuleFactory(
        IWorldStateManager worldStateManager,
        IReadOnlyTxProcessingEnvFactory txProcessingEnvFactory,
        IBlockTree blockTree,
        IBlockPreprocessorStep recoveryStep,
        IReceiptFinder receiptFinder,
        ISpecProvider specProvider,
        ILogManager logManager)
        : ModuleFactoryBase<IProofRpcModule>
    {
        private readonly IBlockPreprocessorStep _recoveryStep = recoveryStep ?? throw new ArgumentNullException(nameof(recoveryStep));
        private readonly IReceiptFinder _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
        protected readonly ISpecProvider SpecProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        protected readonly ILogManager LogManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
        protected readonly IReadOnlyBlockTree BlockTree = blockTree.AsReadOnly();
        protected readonly IWorldStateManager WorldStateManager = worldStateManager ?? throw new ArgumentNullException(nameof(worldStateManager));

        protected virtual IBlockProcessor.IBlockTransactionsExecutor CreateRpcBlockTransactionsExecutor(IReadOnlyTxProcessingScope scope)
        {
            return new RpcBlockTransactionsExecutor(scope.TransactionProcessor, scope.WorldState);
        }

        public override IProofRpcModule Create()
        {
            IReadOnlyTxProcessorSource txProcessingEnv = txProcessingEnvFactory.Create();

            IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

            IBlockProcessor.IBlockTransactionsExecutor traceExecutor = CreateRpcBlockTransactionsExecutor(scope);

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
                scope,
                chainProcessingEnv.ChainProcessor,
                chainProcessingEnv.ChainProcessor);

            return new ProofRpcModule(tracer, BlockTree, _receiptFinder, SpecProvider, LogManager);
        }
    }
}
