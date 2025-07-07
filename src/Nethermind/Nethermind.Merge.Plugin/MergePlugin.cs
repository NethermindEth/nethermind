// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Timers;
using Nethermind.Db;
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
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Network.Contract.P2P;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

public partial class MergePlugin(ChainSpec chainSpec, IMergeConfig mergeConfig) : IConsensusWrapperPlugin
{
    protected INethermindApi _api = null!;
    private ILogger _logger;
    private ISyncConfig _syncConfig = null!;
    protected IBlocksConfig _blocksConfig = null!;
    protected ITxPoolConfig _txPoolConfig = null!;
    protected IPoSSwitcher _poSSwitcher = NoPoS.Instance;
    private IBlockCacheService _blockCacheService = null!;
    private InvalidChainTracker.InvalidChainTracker _invalidChainTracker = null!;

    private IMergeBlockProductionPolicy? _mergeBlockProductionPolicy;

    public virtual string Name => "Merge";
    public virtual string Description => "Merge plugin for ETH1-ETH2";
    public string Author => "Nethermind";

    protected virtual bool MergeEnabled => mergeConfig.Enabled &&
                                           chainSpec.SealEngineType is SealEngineType.BeaconChain or SealEngineType.Clique or SealEngineType.Ethash;
    public int Priority => PluginPriorities.Merge;

    public virtual Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _syncConfig = nethermindApi.Config<ISyncConfig>();
        _blocksConfig = nethermindApi.Config<IBlocksConfig>();
        _txPoolConfig = nethermindApi.Config<ITxPoolConfig>();

        MigrateSecondsPerSlot(_blocksConfig, mergeConfig);

        _logger = _api.LogManager.GetClassLogger();

        EnsureNotConflictingSettings();

        if (MergeEnabled)
        {
            if (_api.DbProvider is null) throw new ArgumentException(nameof(_api.DbProvider));
            if (_api.SpecProvider is null) throw new ArgumentException(nameof(_api.SpecProvider));

            EnsureJsonRpcUrl();
            EnsureReceiptAvailable();

            _blockCacheService = _api.Context.Resolve<IBlockCacheService>();
            _poSSwitcher = _api.Context.Resolve<IPoSSwitcher>();
            _invalidChainTracker = _api.Context.Resolve<InvalidChainTracker.InvalidChainTracker>();
            if (_txPoolConfig.BlobsSupport.SupportsReorgs())
            {
                ProcessedTransactionsDbCleaner processedTransactionsDbCleaner = new(_api.Context.Resolve<IManualBlockFinalizationManager>(), _api.DbProvider.BlobTransactionsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs), _api.LogManager);
                _api.DisposeStack.Push(processedTransactionsDbCleaner);
            }

            _api.GossipPolicy = new MergeGossipPolicy(_api.GossipPolicy, _poSSwitcher, _blockCacheService);

            _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_poSSwitcher));
        }

        return Task.CompletedTask;
    }

    private void EnsureNotConflictingSettings()
    {
        if (!mergeConfig.Enabled && mergeConfig.TerminalTotalDifficulty is not null)
        {
            throw new InvalidConfigurationException(
                $"{nameof(MergeConfig)}.{nameof(MergeConfig.TerminalTotalDifficulty)} cannot be set when {nameof(MergeConfig)}.{nameof(MergeConfig.Enabled)} is false.",
                ExitCodes.ConflictingConfigurations);
        }
    }

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

    private void EnsureReceiptAvailable()
    {
        if (HasTtd() == false) // by default we have Merge.Enabled = true, for chains that are not post-merge, we can skip this check, but we can still working with MergePlugin
            return;

        if (_syncConfig.FastSync)
        {
            if (!_syncConfig.NonValidatorNode && (!_syncConfig.DownloadReceiptsInFastSync || !_syncConfig.DownloadBodiesInFastSync))
            {
                throw new InvalidConfigurationException(
                    "Receipt and body must be available for merge to function. The following configs values should be set to true: Sync.DownloadReceiptsInFastSync, Sync.DownloadBodiesInFastSync",
                    ExitCodes.NoDownloadOldReceiptsOrBlocks);
            }
        }
    }

    private void EnsureJsonRpcUrl()
    {
        if (HasTtd() == false) // by default we have Merge.Enabled = true, for chains that are not post-merge, wwe can skip this check, but we can still working with MergePlugin
            return;

        IJsonRpcConfig jsonRpcConfig = _api.Config<IJsonRpcConfig>();
        if (!jsonRpcConfig.Enabled)
        {
            if (_logger.IsInfo)
                _logger.Info("JsonRpc not enabled. Turning on JsonRpc URL with engine API.");

            jsonRpcConfig.Enabled = true;

            EnsureEngineModuleIsConfigured();

            if (!jsonRpcConfig.EnabledModules.Contains(ModuleType.Engine, StringComparison.OrdinalIgnoreCase))
            {
                // Disable it
                jsonRpcConfig.EnabledModules = [];
            }

            jsonRpcConfig.AdditionalRpcUrls = jsonRpcConfig.AdditionalRpcUrls
                .Where(static (url) => JsonRpcUrl.Parse(url).EnabledModules.Contains(ModuleType.Engine, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            EnsureEngineModuleIsConfigured();
        }
    }

    private void EnsureEngineModuleIsConfigured()
    {
        JsonRpcUrlCollection urlCollection = new(_api.LogManager, _api.Config<IJsonRpcConfig>(), false);
        bool hasEngineApiConfigured = urlCollection
            .Values
            .Any(static rpcUrl => rpcUrl.EnabledModules.Contains(ModuleType.Engine, StringComparison.OrdinalIgnoreCase));

        if (!hasEngineApiConfigured)
        {
            throw new InvalidConfigurationException(
                "Engine module wasn't configured on any port. Nethermind can't work without engine port configured. Verify your RPC configuration. You can find examples in our docs: https://docs.nethermind.io/interacting/json-rpc-server/#engine-api",
                ExitCodes.NoEngineModule);
        }
    }

    private bool HasTtd()
    {
        return _api.SpecProvider?.TerminalTotalDifficulty is not null || mergeConfig.TerminalTotalDifficulty is not null;
    }

    public Task InitNetworkProtocol()
    {
        if (MergeEnabled)
        {
            ArgumentNullException.ThrowIfNull(_api.SpecProvider);
            ArgumentNullException.ThrowIfNull(_api.ProtocolsManager);
            if (_api.BlockProductionPolicy is null) throw new ArgumentException(nameof(_api.BlockProductionPolicy));

            _mergeBlockProductionPolicy = new MergeBlockProductionPolicy(_api.BlockProductionPolicy);
            _api.BlockProductionPolicy = _mergeBlockProductionPolicy;
            _api.FinalizationManager = InitializeMergeFinilizationManager();

            // Need to do it here because blockprocessor is not available in init
            _invalidChainTracker.SetupBlockchainProcessorInterceptor(_api.MainProcessingContext!.BlockchainProcessor!);

            if (_poSSwitcher.TransitionFinished)
            {
                AddEth69();
            }
            else
            {
                if (_logger.IsDebug) _logger.Debug("Delayed adding eth/69 capability until terminal block reached");
                _poSSwitcher.TerminalBlockReached += (_, _) => AddEth69();
            }
        }

        return Task.CompletedTask;
    }

    private void AddEth69()
    {
        if (_logger.IsInfo) _logger.Info("Adding eth/69 capability");
        _api.ProtocolsManager!.AddSupportedCapability(new(Protocol.Eth, 69));
    }

    protected virtual IBlockFinalizationManager InitializeMergeFinilizationManager()
    {
        return new MergeFinalizationManager(_api.Context.Resolve<IManualBlockFinalizationManager>(), _api.FinalizationManager, _poSSwitcher);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize { get => true; }

    public virtual IModule Module => new MergePluginModule();
}

/// <summary>
/// Code for Ethereum. As in Mainnet. Block processing code should be here as other chain have a tendency to replace
/// them completely.
/// </summary>
public class MergePluginModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .AddDecorator<IUnclesValidator, MergeUnclesValidator>()

            .AddDecorator<IRewardCalculatorSource, MergeRewardCalculatorSource>()
            .AddDecorator<ISealValidator, MergeSealValidator>()
            .AddDecorator<ISealer, MergeSealer>()

            .AddModule(new BaseMergePluginModule());
    }
}

/// <summary>
/// Common post merge code, also uses by some plugins. These are components generally needed for sync and engine api.
/// Note: Used by <see cref="OptimismModule"/>, <see cref="TaikoModule"/>, <see cref="AuRaMergeModule"/>
/// </summary>
public class BaseMergePluginModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
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
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddDecorator<IBetterPeerStrategy, MergeBetterPeerStrategy>()

            .AddSingleton<IPeerRefresher, PeerRefresher>()
            .ResolveOnServiceActivation<IPeerRefresher, ISynchronizer>()

            .AddSingleton<StartingSyncPivotUpdater>()
            .ResolveOnServiceActivation<StartingSyncPivotUpdater, ISyncModeSelector>()
            .AddSingleton<IManualBlockFinalizationManager, ManualBlockFinalizationManager>()

            // Invalid chain tracker wrapper should be after other validator.
            .AddDecorator<IHeaderValidator, InvalidHeaderInterceptor>()
            .AddDecorator<IBlockValidator, InvalidBlockInterceptor>()
            .AddDecorator<ISealValidator, InvalidHeaderSealInterceptor>()

            .AddDecorator<IHealthHintService, MergeHealthHintService>()

            // Engine rpc related
            .RegisterSingletonJsonRpcModule<IEngineRpcModule, EngineRpcModule>()
                .AddSingleton<IPayloadPreparationService, PayloadPreparationService>()
                    .AddSingleton<IBlockImprovementContextFactory>(CreateBlockImprovementContextFactory)

                .AddSingleton<IAsyncHandler<byte[], ExecutionPayload?>, GetPayloadV1Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV2Result?>, GetPayloadV2Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV3Result?>, GetPayloadV3Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV4Result?>, GetPayloadV4Handler>()
                .AddSingleton<IAsyncHandler<byte[], GetPayloadV5Result?>, GetPayloadV5Handler>()
                .AddSingleton<IAsyncHandler<ExecutionPayload, PayloadStatusV1>, NewPayloadHandler>()
                .AddSingleton<IForkchoiceUpdatedHandler, ForkchoiceUpdatedHandler>()
                .AddSingleton<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>, GetPayloadBodiesByHashV1Handler>()
                .AddSingleton<IGetPayloadBodiesByRangeV1Handler, GetPayloadBodiesByRangeV1Handler>()
                .AddSingleton<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>, ExchangeTransitionConfigurationV1Handler>()
                .AddSingleton<IHandler<IEnumerable<string>, IEnumerable<string>>, ExchangeCapabilitiesHandler>()
                    .AddSingleton<IRpcCapabilitiesProvider, EngineRpcCapabilitiesProvider>()
                .AddSingleton<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>, GetBlobsHandler>()
                .AddSingleton<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>?>, GetBlobsHandlerV2>()

                .AddSingleton<NoSyncGcRegionStrategy>()
                .AddSingleton<GCKeeper>((ctx) =>
                {
                    IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                    return new GCKeeper(
                        initConfig.DisableGcOnNewPayload
                            ? NoGCStrategy.Instance
                            : ctx.Resolve<NoSyncGcRegionStrategy>(),
                        ctx.Resolve<ILogManager>());
                })
            ;
    }

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

        ILogManager logManager = ctx.Resolve<ILogManager>();
        IStateReader stateReader = ctx.Resolve<IStateReader>();
        IJsonSerializer jsonSerializer = ctx.Resolve<IJsonSerializer>();

        DefaultHttpClient httpClient = new(new HttpClient(), jsonSerializer, logManager, retryDelayMilliseconds: 100);
        IBoostRelay boostRelay = new BoostRelay(httpClient, mergeConfig.BuilderRelayUrl);
        return new BoostBlockImprovementContextFactory(blockProducer!, TimeSpan.FromSeconds(maxSingleImprovementTimePerSlot), boostRelay, stateReader);
    }
}
