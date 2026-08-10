// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Producers
{
    /// <summary>
    /// Block producer environment factory that uses the global world state and stores receipts by default.
    /// Combined with NonProcessingProducedBlockSuggester will add blocks to block tree.
    /// </summary>
    public class GlobalWorldStateBlockProducerEnvFactory(
        ILifetimeScope rootLifetime,
        IWorldStateManager worldStateManager,
        IBlockProducerTxSourceFactory txSourceFactory)
        : IMainStateBlockProducerEnvFactory
    {
        protected IWorldStateManager WorldStateManager => worldStateManager;

        protected virtual ContainerBuilder ConfigureBuilder(ContainerBuilder builder) => builder
            .AddScoped(txSourceFactory.Create())
            .AddScoped(BlockchainProcessor.Options.Default)
            // Override the adapter factory (not ITransactionProcessorAdapter directly) so the EIP-7928 BAL pool's
            // per-worker adapters also build up rather than commit per tx during block production.
            .AddScoped<TransactionProcessorAdapterFactory>(CreateBuildUpAdapter)
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
            .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<IBlockProducerEnv, BlockProducerEnv>();

        private static ITransactionProcessorAdapter CreateBuildUpAdapter(ITransactionProcessor transactionProcessor)
            => new BuildUpTransactionProcessorAdapter(transactionProcessor);

        protected virtual IWorldStateScopeProvider CreateWorldState() => worldStateManager.GlobalWorldState;

        public IBlockProducerEnv CreatePersistent()
        {
            ILifetimeScope scope = BeginScope(out IBlockProducerEnv blockProducerEnv);
            rootLifetime.Disposer.AddInstanceForAsyncDisposal(scope);
            return blockProducerEnv;
        }

        public ScopedBlockProducerEnv CreateTransient()
        {
            ILifetimeScope scope = BeginScope(out IBlockProducerEnv blockProducerEnv);
            return new ScopedBlockProducerEnv(blockProducerEnv, scope);
        }

        private ILifetimeScope BeginScope(out IBlockProducerEnv blockProducerEnv)
        {
            IWorldStateScopeProvider worldState = CreateWorldState();
            ILifetimeScope scope = rootLifetime.BeginLifetimeScope(builder => ConfigureBuilder(builder).AddScoped(worldState));
            blockProducerEnv = scope.Resolve<IBlockProducerEnv>();
            return scope;
        }
    }
}
