// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Timers;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

public class MergeModule(ITxPoolConfig txPoolConfig, IMergeConfig mergeConfig, IBlocksConfig blocksConfig): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddModule(new MergeSynchronizerModule())

            .AddSingleton<IBlockCacheService, BlockCacheService>()
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddSingleton<IBlockFinalizationManager, ManualBlockFinalizationManager>()
            .AddSingleton<IInvalidChainTracker, InvalidChainTracker>()

            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()

            // Validators
            .AddDecorator<ISealValidator, MergeSealValidator>()
            .AddDecorator<ISealValidator, InvalidHeaderSealInterceptor>()
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .AddDecorator<IHeaderValidator, InvalidHeaderInterceptor>()
            .AddDecorator<IBlockValidator, InvalidBlockInterceptor>()
            .AddDecorator<IUnclesValidator, MergeUnclesValidator>()

            .AddDecorator<IGossipPolicy, MergeGossipPolicy>()
            .AddSingleton<IBlockPreprocessorStep, MergeProcessingRecoveryStep>()

            .AddDecorator<IHealthHintService, MergeHealthHintService>()
            .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()
            .AddDecorator<IBlockFinalizationManager, MergeFinalizationManager>()

            // Sync related
            .AddSingleton<BeaconSync>()
            .AddDecorator<IBetterPeerStrategy, MergeBetterPeerStrategy>()
            .AddSingleton<IBeaconPivot, BeaconPivot>()
            .Bind<IPivot, IBeaconPivot>()
            .Bind<IMergeSyncController, BeaconSync>()
            .Bind<IBeaconSyncStrategy, BeaconSync>()

            .AddSingleton<IPeerRefresher, PeerRefresher>()
            .AddSingleton<PivotUpdator>()

            // Block production related.
            .AddScoped<PostMergeBlockProducer>()
            .AddScoped<IBlockImprovementContextFactory, IBlockProducer>(blockProducer => new BlockImprovementContextFactory(blockProducer, TimeSpan.FromSeconds(blocksConfig.SecondsPerSlot)))
            .AddDecorator<IBlockProducer>((ctx, currentBlockProducer) =>
            {
                PostMergeBlockProducer postMerge = ctx.Resolve<PostMergeBlockProducer>();
                IPoSSwitcher posSwitcher = ctx.Resolve<IPoSSwitcher>();
                return new MergeBlockProducer(currentBlockProducer, postMerge, posSwitcher);
            })
            .AddDecorator<ISealEngine, SealEngine>()
            .AddSingleton<IPayloadPreparationService>((ctx) =>
            {
                var blockProducerContext = ctx.Resolve<BlockProducerContext>().LifetimeScope;

                return new PayloadPreparationService(
                    blockProducerContext.Resolve<PostMergeBlockProducer>(),
                    blockProducerContext.Resolve<IBlockImprovementContextFactory>(),
                    ctx.Resolve<ITimerFactory>(),
                    ctx.Resolve<ILogManager>(),
                    TimeSpan.FromSeconds(blocksConfig.SecondsPerSlot));
            })

            ;

        if (txPoolConfig.BlobsSupport.SupportsReorgs())
        {
            // Need to be resolved at sometime on start... probably.
            builder
                .AddSingleton<ProcessedTransactionsDbCleaner>();
        }

        // TODO: _invalidChainTracker.SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor!);

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
