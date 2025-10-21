// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class TestMergeModule(ITxPoolConfig txPoolConfig) : Module
{
    public TestMergeModule(IConfigProvider configProvider) : this(configProvider.GetConfig<ITxPoolConfig>())
    {
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new MergePluginModule())

            .AddSingleton<IBlockFinalizationManager, ManualBlockFinalizationManager>()
            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()

            // Validators
            .AddDecorator<IGossipPolicy, MergeGossipPolicy>()
            .AddSingleton<IBlockPreprocessorStep, MergeProcessingRecoveryStep>()

            .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()
            .AddDecorator<IBlockFinalizationManager, MergeFinalizationManager>()

            // Block production related.
            .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()
            .AddScoped<PostMergeBlockProducerFactory>()
            .AddDecorator<IBlockProducerFactory, TestMergeBlockProducerFactory>()
            ;

        if (txPoolConfig.BlobsSupport.SupportsReorgs())
        {
            builder
                .AddSingleton<ProcessedTransactionsDbCleaner>()
                .ResolveOnServiceActivation<ProcessedTransactionsDbCleaner, IBlockFinalizationManager>();
        }
    }

    private class TestMergeBlockProducerFactory(
        IBlockProducerFactory baseBlockProducerFactory,
        IBlockProducerEnvFactory blockProducerEnvFactory,
        PostMergeBlockProducerFactory postMergeBlockProducerFactory,
        IPoSSwitcher poSSwitcher,
        IBlockProductionPolicy blockProductionPolicy) : IBlockProducerFactory
    {
        public IBlockProducer InitBlockProducer()
        {
            IMergeBlockProductionPolicy? mergeBlockProductionPolicy = blockProductionPolicy as IMergeBlockProductionPolicy;
            IBlockProducer? blockProducer = (mergeBlockProductionPolicy?.ShouldInitPreMergeBlockProduction() != false)
                ? baseBlockProducerFactory.InitBlockProducer()
                : null;

            IBlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create();

            PostMergeBlockProducer postMergeBlockProducer = postMergeBlockProducerFactory.Create(blockProducerEnv);
            return new MergeBlockProducer(blockProducer, postMergeBlockProducer, poSSwitcher);
        }
    }
}
