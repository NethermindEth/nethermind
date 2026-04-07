// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
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
        : IBlockProducerEnvFactory
    {
        protected IWorldStateManager WorldStateManager => worldStateManager;

        protected virtual ContainerBuilder ConfigureBuilder(ContainerBuilder builder) => builder
            .AddScoped(txSourceFactory.Create())
            .AddScoped(BlockchainProcessor.Options.Default)
            .AddScoped<ITransactionProcessorAdapter, BuildUpTransactionProcessorAdapter>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
            .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<IBlockProducerEnv, BlockProducerEnv>();

        protected virtual IWorldStateScopeProvider CreateWorldState() =>
            worldStateManager.GlobalWorldState;

        public IBlockProducerEnv Create(BlockProducerEnvLifetime lifetime = BlockProducerEnvLifetime.Persistent)
        {
            IWorldStateScopeProvider worldState = CreateWorldState();
            ILifetimeScope lifetimeScope = rootLifetime.BeginLifetimeScope(builder => ConfigureBuilder(builder).AddScoped(worldState));
            IBlockProducerEnv env = lifetimeScope.Resolve<IBlockProducerEnv>();

            if (lifetime is BlockProducerEnvLifetime.Transient)
                return new ScopedBlockProducerEnv(env, lifetimeScope);

            rootLifetime.Disposer.AddInstanceForAsyncDisposal(lifetimeScope);
            return env;
        }
    }
}
