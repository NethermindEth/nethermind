// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Producers
{
    /// <summary>
    /// Block producer environment factory that uses the global world state and stores receipts by default.
    /// It will not trigger suggesting produced blocks.
    /// </summary>
    /// <param name="rootLifetime"></param>
    /// <param name="worldStateManager"></param>
    /// <param name="txSourceFactory"></param>
    public class GlobalWorldStateBlockProducerEnvFactory(
        ILifetimeScope rootLifetime,
        IWorldStateManager worldStateManager,
        IBlockProducerTxSourceFactory txSourceFactory)
        : IBlockProducerEnvFactory
    {
        protected virtual ContainerBuilder ConfigureBuilder(ContainerBuilder builder) => builder
            .AddScoped<ITxSource>(txSourceFactory.Create())
            .AddScoped(BlockchainProcessor.Options.Default)
            .AddScoped<ITransactionProcessorAdapter, BuildUpTransactionProcessorAdapter>()
            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockProductionTransactionsExecutor>()
            .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
            .AddScoped<IProducedBlockSuggester, NoOpProducedBlockSuggester>()

            .AddScoped<IBlockProducerEnv, BlockProducerEnv>();

        public virtual IBlockProducerEnv Create()
        {
            ILifetimeScope lifetimeScope = rootLifetime.BeginLifetimeScope(builder =>
                ConfigureBuilder(builder)
                    .AddScoped(worldStateManager.GlobalWorldState));

            rootLifetime.Disposer.AddInstanceForAsyncDisposal(lifetimeScope);
            return lifetimeScope.Resolve<IBlockProducerEnv>();
        }
    }
}
