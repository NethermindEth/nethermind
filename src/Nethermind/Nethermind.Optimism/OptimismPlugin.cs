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
using Nethermind.Core;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Blockchain;
using Nethermind.Consensus.Rewards;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.ParallelSync;
using System.Threading;
using Nethermind.HealthChecks;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Optimism;

public class OptimismPlugin : IConsensusPlugin, ISynchronizationPlugin, IInitializationPlugin
{
    public string Author => "Nethermind";
    public string Name => "Optimism";
    public string Description => "Optimism support for Nethermind";

    private OptimismNethermindApi _api = null!;
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
    private IManualBlockProductionTrigger? _blockProductionTrigger;
    private OptimismPostMergeBlockProducer? _blockProducer;

    public bool ShouldRunSteps(INethermindApi api) => api.ChainSpec.SealEngineType == SealEngineType;

    #region IConsensusPlugin

    public string SealEngineType => Core.SealEngineType.Optimism;

    public IBlockProductionTrigger DefaultBlockProductionTrigger => throw new NotImplementedException();

    public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null,
        ITxSource? additionalTxSource = null)
    {
        if (blockProductionTrigger is not null || additionalTxSource is not null)
            throw new ArgumentException(
                "Optimism does not support custom block production trigger or additional tx source");

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.ReadOnlyTrieStore);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);
        ArgumentNullException.ThrowIfNull(_api.RewardCalculatorSource);
        ArgumentNullException.ThrowIfNull(_api.ReceiptStorage);
        ArgumentNullException.ThrowIfNull(_api.TxPool);
        ArgumentNullException.ThrowIfNull(_api.TransactionComparerProvider);

        _api.BlockProducerEnvFactory = new OptimismBlockProducerEnvFactory(
            _api.ChainSpec,
            _api.DbProvider,
            _api.BlockTree,
            _api.ReadOnlyTrieStore,
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.BlockPreprocessor,
            _api.TxPool,
            _api.TransactionComparerProvider,
            _blocksConfig,
            _api.LogManager);

        _api.GasLimitCalculator = new OptimismGasLimitCalculator();

        _blockProductionTrigger = new BuildBlocksWhenRequested();

        BlockProducerEnv producerEnv = _api.BlockProducerEnvFactory.Create();

        _api.BlockProducer = _blockProducer = new OptimismPostMergeBlockProducer(
            new OptimismPayloadTxSource(),
            producerEnv.TxSource,
            producerEnv.ChainProcessor,
            producerEnv.BlockTree,
            _blockProductionTrigger,
            producerEnv.ReadOnlyStateProvider,
            _api.GasLimitCalculator,
            NullSealEngine.Instance,
            new ManualTimestamper(),
            _api.SpecProvider,
            _api.LogManager,
            _api.Config<IBlocksConfig>());

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

        _api.PoSSwitcher = AlwaysPoS.Instance;

        _blockCacheService = new BlockCacheService();
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
        if (!ShouldRunSteps(_api))
            return Task.CompletedTask;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.SnapProvider);
        ArgumentNullException.ThrowIfNull(_api.PeerDifficultyRefreshPool);
        ArgumentNullException.ThrowIfNull(_api.SyncPeerPool);
        ArgumentNullException.ThrowIfNull(_api.NodeStatsManager);
        ArgumentNullException.ThrowIfNull(_api.SyncProgressResolver);

        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_invalidChainTracker);

        _peerRefresher = new PeerRefresher(_api.PeerDifficultyRefreshPool, _api.TimerFactory, _api.LogManager);
        _api.DisposeStack.Push((PeerRefresher)_peerRefresher);

        _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.LogManager);
        _beaconSync = new BeaconSync(_beaconPivot, _api.BlockTree, _syncConfig, _blockCacheService, _api.LogManager);
        _api.BetterPeerStrategy = new MergeBetterPeerStrategy(null!, _api.PoSSwitcher, _beaconPivot, _api.LogManager);

        _api.SyncModeSelector = new MultiSyncModeSelector(
            _api.SyncProgressResolver,
            _api.SyncPeerPool,
            _syncConfig,
            _beaconSync,
            _api.BetterPeerStrategy!,
            _api.LogManager);
        _api.Pivot = _beaconPivot;

        SyncReport syncReport = new(
            _api.SyncPeerPool,
            _api.NodeStatsManager,
            _api.SyncModeSelector,
            _syncConfig,
            _beaconPivot,
            _api.LogManager);

        _api.BlockDownloaderFactory = new MergeBlockDownloaderFactory(
            _api.PoSSwitcher,
            _beaconPivot,
            _api.SpecProvider,
            _api.BlockTree,
            _api.ReceiptStorage!,
            _api.BlockValidator!,
            _api.SealValidator!,
            _api.SyncPeerPool,
            _syncConfig,
            _api.BetterPeerStrategy!,
            syncReport,
            _api.SyncProgressResolver,
            _api.LogManager);
        _api.Synchronizer = new MergeSynchronizer(
            _api.DbProvider,
            _api.SpecProvider!,
            _api.BlockTree!,
            _api.ReceiptStorage!,
            _api.SyncPeerPool,
            _api.NodeStatsManager!,
            _api.SyncModeSelector,
            _syncConfig,
            _api.SnapProvider,
            _api.BlockDownloaderFactory,
            _api.Pivot,
            _api.PoSSwitcher,
            _mergeConfig,
            _invalidChainTracker,
            _api.ProcessExit!,
            _api.LogManager,
            syncReport);

        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (!ShouldRunSteps(_api))
            return Task.CompletedTask;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
        ArgumentNullException.ThrowIfNull(_api.SyncModeSelector);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);

        ArgumentNullException.ThrowIfNull(_blockProducer);
        ArgumentNullException.ThrowIfNull(_blockProductionTrigger);
        ArgumentNullException.ThrowIfNull(_beaconSync);
        ArgumentNullException.ThrowIfNull(_beaconPivot);
        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_invalidChainTracker);
        ArgumentNullException.ThrowIfNull(_blockFinalizationManager);
        ArgumentNullException.ThrowIfNull(_peerRefresher);

        // Ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart.
        // Then we will wait 5s more to ensure everything is processed
        while (!_api.BlockProcessingQueue.IsEmpty)
            Thread.Sleep(100);
        Thread.Sleep(5000);

        BlockImprovementContextFactory improvementContextFactory = new(
            _blockProductionTrigger,
            TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));

        OptimismPayloadPreparationService payloadPreparationService = new(
            _blockProducer,
            improvementContextFactory,
            _api.TimerFactory,
            _api.LogManager,
            TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));

        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        IEngineRpcModule engineRpcModule = new EngineRpcModule(
            new GetPayloadV1Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV2Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new NewPayloadHandler(
                _api.BlockValidator,
                _api.BlockTree,
                _api.Config<IInitConfig>(),
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
                _api.LogManager),
            new GetPayloadBodiesByHashV1Handler(_api.BlockTree, _api.LogManager),
            new GetPayloadBodiesByRangeV1Handler(_api.BlockTree, _api.LogManager),
            new ExchangeTransitionConfigurationV1Handler(_api.PoSSwitcher, _api.LogManager),
            new ExchangeCapabilitiesHandler(_api.RpcCapabilitiesProvider, _api.LogManager),
            _api.SpecProvider,
            new GCKeeper(new NoSyncGcRegionStrategy(_api.SyncModeSelector, _mergeConfig), _api.LogManager),
            _api.LogManager);

        IOptimismEngineRpcModule opEngine = new OptimismEngineRpcModule(engineRpcModule);

        _api.RpcModuleProvider.RegisterSingle(opEngine);

        if (_logger.IsInfo) _logger.Info("Optimism Engine Module has been enabled");

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;
}
