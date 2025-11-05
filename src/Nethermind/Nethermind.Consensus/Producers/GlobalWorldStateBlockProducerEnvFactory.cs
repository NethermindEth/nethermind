// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Producers
{
    /// <summary>
    /// Block producer environment factory that uses the global world state and stores receipts by default.
    /// Combined with NonProcessingProducedBlockSuggester will add blocks to block tree
    /// </summary>
    /// <param name="rootLifetime"></param>
    /// <param name="worldStateManager"></param>
    /// <param name="txSourceFactory"></param>
    public class GlobalWorldStateBlockProducerEnvFactory(
        ILifetimeScope rootLifetime,
        IWorldStateManager worldStateManager,
        IBlockProducerTxSourceFactory txSourceFactory,
        IBlocksConfig blocksConfig)
        : IBlockProducerEnvFactory
    {
        protected virtual ContainerBuilder ConfigureBuilder(ContainerBuilder builder)
        {
            builder
            .AddScoped<ITxSource>(txSourceFactory.Create())
            .AddScoped(BlockchainProcessor.Options.Default)
            .AddScoped<ITransactionProcessorAdapter, BuildUpTransactionProcessorAdapter>()
                .AddScoped<IBlockProcessor.IBlockTransactionsExecutor,
                    BlockProcessor.BlockProductionTransactionsExecutor>()
            .AddDecorator<IWithdrawalProcessor, BlockProductionWithdrawalProcessor>()
            .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()

            .AddScoped<IBlockProducerEnv, BlockProducerEnv>();

            if (blocksConfig.PreWarmStateOnBlockBuilding)
            {
                builder
                    .AddScoped<PreBlockCaches>((worldStateManager.GlobalWorldState as IPreBlockCaches)!.Caches)
                    .AddScoped<IBlockCachePreWarmer, BlockCachePreWarmer>()
                    //.AddDecorator<ICodeInfoRepository>((ctx, originalCodeInfoRepository) =>
                    //{
                    //    PreBlockCaches preBlockCaches = ctx.Resolve<PreBlockCaches>();
                    //    // Note: The use of FrozenDictionary means that this cannot be used for other processing env also due to risk of memory leak.
                    //    return new CachedCodeInfoRepository(precompileProvider, originalCodeInfoRepository,
                    //        preBlockCaches?.PrecompileCache);
                    //})
                    ;
            }

            return builder;
        }

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
