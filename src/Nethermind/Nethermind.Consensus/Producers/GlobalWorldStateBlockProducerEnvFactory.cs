// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
            .AddScoped<ITransactionProcessorAdapter, BuildUpTransactionProcessorAdapter>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
            .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<IBlockProducerEnv, BlockProducerEnv>();

        protected virtual IWorldStateScopeProvider CreateWorldState() => worldStateManager.GlobalWorldState;

        public IBlockProducerEnv CreatePersistent()
        {
            IEnvHandle handle = BeginScope();
            rootLifetime.Disposer.AddInstanceForAsyncDisposal(handle);
            return handle.Env;
        }

        public ScopedBlockProducerEnv CreateTransient()
        {
            IEnvHandle handle = BeginScope();
            return new ScopedBlockProducerEnv(handle.Env, handle);
        }

        private IEnvHandle BeginScope() =>
            new ProcessingEnvBuilder(rootLifetime)
                // May be the shared GlobalWorldState, which the environment must never dispose.
                .WithWorldState(CreateWorldState(), externallyOwned: true)
                .Configure(builder => ConfigureBuilder(builder))
                .BuildAs<IEnvHandle>();

        /// <summary>Owns the block-producer child scope; disposing it (async) disposes the scope.</summary>
        public interface IEnvHandle : IAsyncDisposable
        {
            IBlockProducerEnv Env { get; }
        }
    }
}
