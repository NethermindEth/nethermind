// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Core.Timers;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class MergeModule(ITxPoolConfig txPoolConfig, IMergeConfig mergeConfig, IBlocksConfig blocksConfig) : Module
{
    public MergeModule(IConfigProvider configProvider) : this(
        configProvider.GetConfig<ITxPoolConfig>(),
        configProvider.GetConfig<IMergeConfig>(),
        configProvider.GetConfig<IBlocksConfig>()
    )
    {
    }

    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new MergePluginModule())

            .AddSingleton<IBlockFinalizationManager, ManualBlockFinalizationManager>()
            .OnActivate<MainBlockProcessingContext>(((context, componentContext) =>
            {
                componentContext.Resolve<InvalidChainTracker>().SetupBlockchainProcessorInterceptor(context.BlockchainProcessor);
            }))

            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()

            // Validators
            .AddDecorator<ISealValidator, MergeSealValidator>()
            .AddDecorator<ISealValidator, InvalidHeaderSealInterceptor>()

            .AddDecorator<IGossipPolicy, MergeGossipPolicy>()
            .AddSingleton<IBlockPreprocessorStep, MergeProcessingRecoveryStep>()

            .AddDecorator<IHealthHintService, MergeHealthHintService>()
            .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()
            .AddDecorator<IBlockFinalizationManager, MergeFinalizationManager>()

            // Block production related.
            .AddScoped<PostMergeBlockProducer>()
            .AddScoped<IBlockImprovementContextFactory, IBlockProducer>(blockProducer => new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(blocksConfig.SecondsPerSlot)))
            .AddDecorator<IBlockProducer>((ctx, currentBlockProducer) =>
            {
                PostMergeBlockProducer postMerge = ctx.Resolve<PostMergeBlockProducer>();
                IPoSSwitcher posSwitcher = ctx.Resolve<IPoSSwitcher>();
                return new MergeBlockProducer(currentBlockProducer, postMerge, posSwitcher);
            })
            .AddDecorator<ISealEngine, MergeSealEngine>()
            .AddSingleton<IPayloadPreparationService, BlockProducerContext>((producerContext) =>
            {
                ILifetimeScope ctx = producerContext.LifetimeScope;
                return new PayloadPreparationService(
                    ctx.Resolve<PostMergeBlockProducer>(),
                    ctx.Resolve<IBlockImprovementContextFactory>(),
                    ctx.Resolve<ITimerFactory>(),
                    ctx.Resolve<ILogManager>(),
                    TimeSpan.FromSeconds(blocksConfig.SecondsPerSlot));
            })
            ;

        if (txPoolConfig.BlobsSupport.SupportsReorgs())
        {
            builder
                .AddSingleton<ProcessedTransactionsDbCleaner>()
                .ResolveOnServiceActivation<ProcessedTransactionsDbCleaner, IBlockFinalizationManager>();
        }

        if (!string.IsNullOrEmpty(mergeConfig.BuilderRelayUrl))
        {
            builder
                .AddSingleton<IBlockImprovementContextFactory>(CreateBoostBlockImprovementContextFactory);
        }
    }

    IBlockImprovementContextFactory CreateBoostBlockImprovementContextFactory(IComponentContext ctx)
    {
        IJsonSerializer jsonSerializer = ctx.Resolve<IJsonSerializer>();
        ILogManager logManager = ctx.Resolve<ILogManager>();
        IBlockProducer blockProducer = ctx.Resolve<IBlockProducer>();
        IStateReader stateReader = ctx.Resolve<IStateReader>();

        DefaultHttpClient httpClient = new(new HttpClient(), jsonSerializer, logManager, retryDelayMilliseconds: 100);
        IBoostRelay boostRelay = new BoostRelay(httpClient, mergeConfig.BuilderRelayUrl!);
        return new BoostBlockImprovementContextFactory(blockProducer!, TimeSpan.FromSeconds(blocksConfig.SecondsPerSlot), boostRelay, stateReader);
    }
}
