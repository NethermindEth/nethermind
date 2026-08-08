// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Container;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Transactions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Exceptions;
using Nethermind.Facade.Proxy;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Network;
using Nethermind.Trie.Pruning;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

public class MergePlugin(ChainSpec chainSpec, IMergeConfig mergeConfig) : INethermindPlugin
{
    public virtual string Name => "Merge";
    public virtual string Description => "Merge plugin for ETH1-ETH2";
    public string Author => "Nethermind";

    protected virtual bool MergeEnabled => mergeConfig.Enabled &&
                                           chainSpec.SealEngineType is SealEngineType.BeaconChain or SealEngineType.Clique or SealEngineType.Ethash;

    public bool Enabled => MergeEnabled;

    internal static void MigrateSecondsPerSlot(IBlocksConfig blocksConfig, IMergeConfig mergeConfig)
    {
        ulong defaultValue = blocksConfig.GetDefaultValue<ulong>(nameof(IBlocksConfig.SecondsPerSlot));
        if (blocksConfig.SecondsPerSlot != mergeConfig.SecondsPerSlot)
        {
            if (blocksConfig.SecondsPerSlot == defaultValue)
            {
                blocksConfig.SecondsPerSlot = mergeConfig.SecondsPerSlot;
            }
            else if (mergeConfig.SecondsPerSlot == defaultValue)
            {
                mergeConfig.SecondsPerSlot = blocksConfig.SecondsPerSlot;
            }
            else
            {
                throw new InvalidConfigurationException($"Configuration mismatch at {nameof(IBlocksConfig.SecondsPerSlot)} " +
                                                            $"with conflicting values {blocksConfig.SecondsPerSlot} and {mergeConfig.SecondsPerSlot}",
                        ExitCodes.ConflictingConfigurations);
            }
        }
    }

    public bool MustInitialize { get => true; }

    public virtual IModule Module => new MergePluginModule();
}

/// <summary>
/// Code for Ethereum. As in Mainnet. Block processing code should be here as other chain have a tendency to replace
/// them completely.
/// </summary>
public class MergePluginModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
            .AddStep(typeof(InitializeMergePlugin))

            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .AddDecorator<IUnclesValidator, MergeUnclesValidator>()

            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()
            .AddDecorator<ISealValidator, MergeSealValidator>()
            .AddDecorator<ISealer, MergeSealer>()

            .AddSingleton<ManualTimestamper>()
            .AddSingleton<PostMergeBlockProducerFactory, ISpecProvider, ISealEngine, ManualTimestamper, IBlocksConfig, ILogManager>(
                (specProvider, sealEngine, timestamper, blocksConfig, logManager) =>
                    new PostMergeBlockProducerFactory(specProvider, sealEngine, timestamper, blocksConfig, logManager))
            .AddDecorator<IBlockProducerFactory, MergeBlockProducerFactory>()
            .AddDecorator<IBlockProducerRunnerFactory, MergeBlockProducerRunnerFactory>()
            .AddDecorator<IBlockProductionPolicy, MergeBlockProductionPolicy>()

            .AddDecorator<IGossipPolicy, MergeGossipPolicy>()

            .AddLast<IP2PCapabilityResolver, MergeP2PCapabilityResolver>()

            .AddModule(new BaseMergePluginModule())

            .ResolveOnServiceActivation<ProcessedTransactionsDbCleaner, IBlockTree>();
}

/// <summary>
/// Common post merge code, also uses by some plugins. These are components generally needed for sync and engine api.
/// Note: Used by <see cref="OptimismModule"/>, <see cref="TaikoModule"/>, <see cref="AuRaMergeModule"/>
/// </summary>
public class BaseMergePluginModule : Module
{
    protected override void Load(ContainerBuilder builder) => builder
            // Sync related
            .AddModule(new MergeSynchronizerModule())

            .AddSingleton<BeaconSync>()
                .Bind<IBeaconSyncStrategy, BeaconSync>()
                .Bind<IMergeSyncController, BeaconSync>()
            .AddSingleton<IBlockCacheService, BlockCacheService>()
            .AddSingleton<IBeaconPivot, BeaconPivot>()
                .Bind<IPivot, IBeaconPivot>()
            .AddSingleton<InvalidChainTracker.InvalidChainTracker>()
                .Bind<IInvalidChainTracker, InvalidChainTracker.InvalidChainTracker>()
            .OnActivate<IMainProcessingContext>(((context, ctx) =>
            {
                ctx.Resolve<InvalidChainTracker.InvalidChainTracker>().SetupBlockchainProcessorInterceptor(context.BlockchainProcessor);
            }))

            .AddSingleton<IPoSSwitcher, PoSSwitcher>()

            .AddSingleton<ProcessedTransactionsDbCleaner>()

            // AddLast (not AddFirst) so RecoverSignatures stays ahead of it, matching the pre-DI ordering.
            .AddLast<IBlockPreprocessorStep, MergeProcessingRecoveryStep>()
            .AddDecorator<IBetterPeerStrategy, MergeBetterPeerStrategy>()

            .AddSingleton<IMainProcessingModule, IRpcCapabilitiesProvider>(static capabilitiesProvider =>
                new WitnessCapturingMainProcessingModule(IsWitnessCaptureEnabled(capabilitiesProvider)))
            .AddSingleton<WitnessRendezvous>()
            .AddSingleton<WitnessCapturingBlockProcessingEnv>()

            .AddSingleton<IPeerRefresher, PeerRefresher>()
            .ResolveOnServiceActivation<IPeerRefresher, ISynchronizer>()

            .AddSingleton<StartingSyncPivotUpdater>()
            .Bind<ISyncPivotResolver, StartingSyncPivotUpdater>()

            // Invalid chain tracker wrapper should be after other validator.
            .AddDecorator<IHeaderValidator, InvalidHeaderInterceptor>()
            .AddDecorator<IBlockValidator, InvalidBlockInterceptor>()
            .AddDecorator<ISealValidator, InvalidHeaderSealInterceptor>()

            .AddDecorator<IHealthHintService, MergeHealthHintService>()

            .AddDecorator<IFinalizedStateProvider, MergeFinalizedStateProvider>()

            .AddKeyedSingleton<ITxValidator>(ITxValidator.HeadTxValidatorKey, new HeadTxValidator())

            // Engine rpc related
            .AddComposite<IBuilderOverridePolicy, CompositeBuilderOverridePolicy>()
            .RegisterSingletonJsonRpcModule<IEngineRpcModule, EngineRpcModule>()
                .AddSingleton<IPayloadPreparationService, PayloadPreparationService>()
                    .AddSingleton<IBlockImprovementContextFactory>(CreateBlockImprovementContextFactory)

                .AddSingleton<IAsyncHandler<byte[], ExecutionPayload?>, GetPayloadV1Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV2Result?>, GetPayloadV2Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV3Result?>, GetPayloadV3Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV4Result?>, GetPayloadV4Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV5Result?>, GetPayloadV5Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV6Result?>, GetPayloadV6Handler>()
                .AddSingleton<IAsyncHandler<ExecutionPayload, PayloadStatusV1>, NewPayloadHandler>()
                .AddSingleton<IForkchoiceUpdatedHandler, ForkchoiceUpdatedHandler>()
                .AddSingleton<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>>, GetPayloadBodiesByHashV1Handler>()
                .AddSingleton<IGetPayloadBodiesByRangeV1Handler, GetPayloadBodiesByRangeV1Handler>()
                .AddSingleton<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>, ExchangeTransitionConfigurationV1Handler>()
                .AddSingleton<IHandler<HashSet<string>, IReadOnlyList<string>>, ExchangeCapabilitiesHandler>()
                    .AddSingleton<IRpcCapabilitiesProvider, EngineRpcCapabilitiesProvider>()
                .AddSingleton<IAsyncHandler<byte[][], IReadOnlyList<BlobAndProofV1?>>, GetBlobsHandler>()
                .AddSingleton<IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>, GetBlobsHandlerV2>()
                .AddSingleton<IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>, GetBlobsHandlerV4>()
                .AddSingleton<IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>, GetPayloadBodiesByHashV2Handler>()
                .AddSingleton<IGetPayloadBodiesByRangeV2Handler, GetPayloadBodiesByRangeV2Handler>()
                .AddSingleton<NewPayloadWithWitnessHandler>()
                .Bind<IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV3>, NewPayloadWithWitnessV1Result>, NewPayloadWithWitnessHandler>()
                .Bind<IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV4>, NewPayloadWithWitnessV1Result>, NewPayloadWithWitnessHandler>()

                .AddSingleton<InclusionListTxSource>()
                .AddDecorator<IBlockProducerTxSourceFactory, InclusionListBlockProducerTxSourceFactory>()
                .AddSingleton<IHandler<InclusionListBytes>, GetInclusionListTransactionsHandler>()

                .AddSingleton<NoSyncGcRegionStrategy>()
                .AddSingleton<GCKeeper>((ctx) =>
                {
                    IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                    return new GCKeeper(
                        initConfig.DisableGcOnNewPayload
                            ? ctx.Resolve<NoSyncGcRegionStrategy>()
                            : NoGCStrategy.Instance,
                        ctx.Resolve<ILogManager>());
                })
                .AddSingleton<IHttpClient, DefaultHttpClient>()
                .AddSingleton<IGasLimitCalculator, TargetAdjustedGasLimitCalculator>()
                .AddSingleton<IJsonRpcServiceConfigurer, SszMiddlewareConfigurer>()

            // Testing rpc
            .RegisterSingletonJsonRpcModule<ITestingRpcModule, TestingRpcModule>()
            ;

    private static bool IsWitnessCaptureEnabled(IRpcCapabilitiesProvider capabilitiesProvider) =>
        capabilitiesProvider.GetEngineCapabilities().TryGetValue(
            nameof(IEngineRpcModule.engine_newPayloadWithWitnessV4), out RpcCapabilityOptions options)
        && options.IsEnabled();

    IBlockImprovementContextFactory CreateBlockImprovementContextFactory(IComponentContext ctx)
    {
        IMergeConfig mergeConfig = ctx.Resolve<IMergeConfig>();
        IBlocksConfig blocksConfig = ctx.Resolve<IBlocksConfig>();
        IBlockProducer blockProducer = ctx.Resolve<IBlockProducer>();

        double maxSingleImprovementTimePerSlot = blocksConfig.SecondsPerSlot * blocksConfig.SingleBlockImprovementOfSlot;
        if (string.IsNullOrEmpty(mergeConfig.BuilderRelayUrl))
        {
            return new BlockImprovementContextFactory(blockProducer!, TimeSpan.FromSeconds(maxSingleImprovementTimePerSlot));
        }

        IStateReader stateReader = ctx.Resolve<IStateReader>();
        IHttpClient httpClient = ctx.Resolve<IHttpClient>();
        IBoostRelay boostRelay = new BoostRelay(httpClient, mergeConfig.BuilderRelayUrl);
        return new BoostBlockImprovementContextFactory(blockProducer!, TimeSpan.FromSeconds(maxSingleImprovementTimePerSlot), boostRelay, stateReader);
    }
}
