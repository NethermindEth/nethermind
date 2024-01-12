// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.JsonRpc.Modules;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.HealthChecks;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OptimismPlugin : IConsensusPlugin, ISynchronizationPlugin, IInitializationPlugin
{
    public string Author => "Nethermind";
    public string Name => "Optimism";
    public string Description => "Optimism support for Nethermind";

    private OptimismNethermindApi? _api;
    private ILogger _logger = null!;
    private IMergeConfig _mergeConfig = null!;
    private ISyncConfig _syncConfig = null!;
    private IBlocksConfig _blocksConfig = null!;
    private BlockCacheService? _blockCacheService;
    private InvalidChainTracker? _invalidChainTracker;
    private ManualBlockFinalizationManager? _blockFinalizationManager;
    private IPeerRefresher? _peerRefresher;
    private IBeaconPivot? _beaconPivot;
    private BeaconSync? _beaconSync;

    public bool ShouldRunSteps(INethermindApi api) => api.ChainSpec.SealEngineType == SealEngineType;

    #region IConsensusPlugin

    public string SealEngineType => Core.SealEngineType.Optimism;

    public IBlockProductionTrigger DefaultBlockProductionTrigger => NeverProduceTrigger.Instance;

    public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null,
        ITxSource? additionalTxSource = null)
    {
        if (blockProductionTrigger is not null || additionalTxSource is not null)
            throw new ArgumentException(
                "Optimism does not support custom block production trigger or additional tx source");

        ArgumentNullException.ThrowIfNull(_api);
        ArgumentNullException.ThrowIfNull(_api.BlockProducer);

        return Task.FromResult(_api.BlockProducer);
    }

    #endregion

    public INethermindApi CreateApi(IConfigProvider configProvider, IJsonSerializer jsonSerializer,
        ILogManager logManager, ChainSpec chainSpec) =>
        new OptimismNethermindApi(configProvider, jsonSerializer, logManager, chainSpec);

    public Task Init(INethermindApi api)
    {
        if (!ShouldRunSteps(api))
            return Task.CompletedTask;

        _api = (OptimismNethermindApi)api;
        _mergeConfig = _api.Config<IMergeConfig>();
        _syncConfig = _api.Config<ISyncConfig>();
        _blocksConfig = _api.Config<IBlocksConfig>();
        _logger = _api.LogManager.GetClassLogger();

        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);

        _api.PoSSwitcher = AlwaysPoS.Instance;

        _blockCacheService = new BlockCacheService();
        _api.EthereumEcdsa = new OptimismEthereumEcdsa(_api.EthereumEcdsa);
        _api.InvalidChainTracker = _invalidChainTracker = new InvalidChainTracker(
            _api.PoSSwitcher,
            _api.BlockTree,
            _blockCacheService,
            _api.LogManager);
        _api.DisposeStack.Push(_invalidChainTracker);

        _api.FinalizationManager = _blockFinalizationManager = new ManualBlockFinalizationManager();

        _api.RewardCalculatorSource = NoBlockRewards.Instance;
        _api.SealValidator = NullSealEngine.Instance;
        _api.GossipPolicy = ShouldNotGossip.Instance;

        _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_api.PoSSwitcher));

        return Task.CompletedTask;
    }

    public Task InitNetworkProtocol() => Task.CompletedTask;

    public Task InitSynchronization()
    {
        if (_api is null || !ShouldRunSteps(_api))
            return Task.CompletedTask;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.PeerDifficultyRefreshPool);
        ArgumentNullException.ThrowIfNull(_api.SyncPeerPool);
        ArgumentNullException.ThrowIfNull(_api.NodeStatsManager);
        ArgumentNullException.ThrowIfNull(_api.BlockchainProcessor);

        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_invalidChainTracker);

        _invalidChainTracker.SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor);

        _peerRefresher = new PeerRefresher(_api.PeerDifficultyRefreshPool, _api.TimerFactory, _api.LogManager);
        _api.DisposeStack.Push((PeerRefresher)_peerRefresher);

        _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.LogManager);
        _beaconSync = new BeaconSync(_beaconPivot, _api.BlockTree, _syncConfig, _blockCacheService, _api.PoSSwitcher, _api.LogManager);
        _api.BetterPeerStrategy = new MergeBetterPeerStrategy(null!, _api.PoSSwitcher, _beaconPivot, _api.LogManager);
        _api.Pivot = _beaconPivot;

        MergeBlockDownloaderFactory blockDownloaderFactory = new MergeBlockDownloaderFactory(
            _api.PoSSwitcher,
            _beaconPivot,
            _api.SpecProvider,
            _api.BlockValidator!,
            _api.SealValidator!,
            _syncConfig,
            _api.BetterPeerStrategy!,
            new FullStateFinder(_api.BlockTree, _api.StateReader!),
            _api.LogManager);

        _api.Synchronizer = new MergeSynchronizer(
            _api.DbProvider,
            _api.SpecProvider!,
            _api.BlockTree!,
            _api.ReceiptStorage!,
            _api.SyncPeerPool,
            _api.NodeStatsManager!,
            _syncConfig,
            blockDownloaderFactory,
            _beaconPivot,
            _api.PoSSwitcher,
            _mergeConfig,
            _invalidChainTracker,
            _api.ProcessExit!,
            _api.BetterPeerStrategy,
            _api.ChainSpec,
            _beaconSync,
            _api.StateReader!,
            _api.LogManager
        );

        return Task.CompletedTask;
    }

    public async Task InitRpcModules()
    {
        if (_api is null || !ShouldRunSteps(_api))
            return;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
        ArgumentNullException.ThrowIfNull(_api.SyncModeSelector);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProducer);

        ArgumentNullException.ThrowIfNull(_beaconSync);
        ArgumentNullException.ThrowIfNull(_beaconPivot);
        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_invalidChainTracker);
        ArgumentNullException.ThrowIfNull(_blockFinalizationManager);
        ArgumentNullException.ThrowIfNull(_peerRefresher);

        // Ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart.
        // Then we will wait 5s more to ensure everything is processed
        while (!_api.BlockProcessingQueue.IsEmpty)
            await Task.Delay(100);
        await Task.Delay(5000);

        BlockImprovementContextFactory improvementContextFactory = new(
            _api.ManualBlockProductionTrigger,
            TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));

        OptimismPayloadPreparationService payloadPreparationService = new(
            (PostMergeBlockProducer)_api.BlockProducer,
            improvementContextFactory,
            _api.TimerFactory,
            _api.LogManager,
            TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));

        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        IInitConfig initConfig = _api.Config<IInitConfig>();
        IEngineRpcModule engineRpcModule = new EngineRpcModule(
            new GetPayloadV1Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV2Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new NewPayloadHandler(
                _api.BlockValidator,
                _api.BlockTree,
                initConfig,
                _syncConfig,
                _api.PoSSwitcher,
                _beaconSync,
                _beaconPivot,
                _blockCacheService,
                _api.BlockProcessingQueue,
                _invalidChainTracker,
                _beaconSync,
                _api.LogManager,
                TimeSpan.FromSeconds(_mergeConfig.NewPayloadTimeout)),
            new ForkchoiceUpdatedHandler(
                _api.BlockTree,
                _blockFinalizationManager,
                _api.PoSSwitcher,
                payloadPreparationService,
                _api.BlockProcessingQueue,
                _blockCacheService,
                _invalidChainTracker,
                _beaconSync,
                _beaconPivot,
                _peerRefresher,
                _api.SpecProvider,
                _api.LogManager,
                _api.Config<IBlocksConfig>().SecondsPerSlot,
                _api.Config<IMergeConfig>().SimulateBlockProduction),
            new GetPayloadBodiesByHashV1Handler(_api.BlockTree, _api.LogManager),
            new GetPayloadBodiesByRangeV1Handler(_api.BlockTree, _api.LogManager),
            new ExchangeTransitionConfigurationV1Handler(_api.PoSSwitcher, _api.LogManager),
            new ExchangeCapabilitiesHandler(_api.RpcCapabilitiesProvider, _api.LogManager),
            _api.SpecProvider,
            new GCKeeper(
                initConfig.DisableGcOnNewPayload
                    ? NoGCStrategy.Instance
                    : new NoSyncGcRegionStrategy(_api.SyncModeSelector, _mergeConfig), _api.LogManager),
            _api.LogManager);

        IOptimismEngineRpcModule opEngine = new OptimismEngineRpcModule(engineRpcModule);

        _api.RpcModuleProvider.RegisterSingle(opEngine);

        if (_logger.IsInfo) _logger.Info("Optimism Engine Module has been enabled");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;
}
