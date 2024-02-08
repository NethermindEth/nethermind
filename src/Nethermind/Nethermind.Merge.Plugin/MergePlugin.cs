// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.HealthChecks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin;

public partial class MergePlugin : IConsensusWrapperPlugin, ISynchronizationPlugin
{
    protected INethermindApi _api = null!;
    private ILogger _logger;
    protected IMergeConfig _mergeConfig = null!;
    private ISyncConfig _syncConfig = null!;
    protected IBlocksConfig _blocksConfig = null!;
    protected ITxPoolConfig _txPoolConfig = null!;
    protected IPoSSwitcher _poSSwitcher = NoPoS.Instance;
    private IBeaconPivot? _beaconPivot;
    private BeaconSync? _beaconSync;
    private IBlockCacheService _blockCacheService = null!;
    private InvalidChainTracker.InvalidChainTracker _invalidChainTracker = null!;
    private IPeerRefresher _peerRefresher = null!;

    private ManualBlockFinalizationManager _blockFinalizationManager = null!;
    private IMergeBlockProductionPolicy? _mergeBlockProductionPolicy;

    public virtual string Name => "Merge";
    public virtual string Description => "Merge plugin for ETH1-ETH2";
    public string Author => "Nethermind";

    protected virtual bool MergeEnabled => _mergeConfig.Enabled &&
                                           _api.ChainSpec.SealEngineType is SealEngineType.BeaconChain or SealEngineType.Clique or SealEngineType.Ethash;

    // Don't remove default constructor. It is used by reflection when we're loading plugins
    public MergePlugin() { }

    public virtual Task Init(INethermindApi nethermindApi)
    {
        _api = nethermindApi;
        _mergeConfig = nethermindApi.Config<IMergeConfig>();
        _syncConfig = nethermindApi.Config<ISyncConfig>();
        _blocksConfig = nethermindApi.Config<IBlocksConfig>();
        _txPoolConfig = nethermindApi.Config<ITxPoolConfig>();

        MigrateSecondsPerSlot(_blocksConfig, _mergeConfig);

        _logger = _api.LogManager.GetClassLogger();

        if (MergeEnabled)
        {
            if (_api.DbProvider is null) throw new ArgumentException(nameof(_api.DbProvider));
            if (_api.BlockTree is null) throw new ArgumentException(nameof(_api.BlockTree));
            if (_api.SpecProvider is null) throw new ArgumentException(nameof(_api.SpecProvider));
            if (_api.ChainSpec is null) throw new ArgumentException(nameof(_api.ChainSpec));
            if (_api.SealValidator is null) throw new ArgumentException(nameof(_api.SealValidator));

            EnsureJsonRpcUrl();
            EnsureReceiptAvailable();

            _blockCacheService = new BlockCacheService();
            _poSSwitcher = new PoSSwitcher(
                _mergeConfig,
                _syncConfig,
                _api.DbProvider.GetDb<IDb>(DbNames.Metadata),
                _api.BlockTree,
                _api.SpecProvider,
                _api.ChainSpec,
                _api.LogManager);
            _invalidChainTracker = new InvalidChainTracker.InvalidChainTracker(
                _poSSwitcher,
                _api.BlockTree,
                _blockCacheService,
                _api.LogManager);
            _api.PoSSwitcher = _poSSwitcher;
            _api.DisposeStack.Push(_invalidChainTracker);
            _blockFinalizationManager = new ManualBlockFinalizationManager();
            if (_txPoolConfig.BlobsSupport.SupportsReorgs())
            {
                ProcessedTransactionsDbCleaner processedTransactionsDbCleaner = new(_blockFinalizationManager, _api.DbProvider.BlobTransactionsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs), _api.LogManager);
                _api.DisposeStack.Push(processedTransactionsDbCleaner);
            }

            _api.RewardCalculatorSource = new MergeRewardCalculatorSource(
               _api.RewardCalculatorSource ?? NoBlockRewards.Instance, _poSSwitcher);
            _api.SealValidator = new InvalidHeaderSealInterceptor(
                new MergeSealValidator(_poSSwitcher, _api.SealValidator),
                _invalidChainTracker,
                _api.LogManager);

            _api.GossipPolicy = new MergeGossipPolicy(_api.GossipPolicy, _poSSwitcher, _blockCacheService);

            _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_poSSwitcher));
        }

        return Task.CompletedTask;
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

            if (!jsonRpcConfig.EnabledModules.Contains(ModuleType.Engine, StringComparison.InvariantCultureIgnoreCase))
            {
                // Disable it
                jsonRpcConfig.EnabledModules = Array.Empty<string>();
            }

            jsonRpcConfig.AdditionalRpcUrls = jsonRpcConfig.AdditionalRpcUrls
                .Where((url) => JsonRpcUrl.Parse(url).EnabledModules.Contains(ModuleType.Engine, StringComparison.InvariantCultureIgnoreCase))
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
            .Any(rpcUrl => rpcUrl.EnabledModules.Contains(ModuleType.Engine, StringComparison.InvariantCultureIgnoreCase));

        if (!hasEngineApiConfigured)
        {
            throw new InvalidConfigurationException(
                "Engine module wasn't configured on any port. Nethermind can't work without engine port configured. Verify your RPC configuration. You can find examples in our docs: https://docs.nethermind.io/nethermind/ethereum-client/engine-jsonrpc-configuration-examples",
                ExitCodes.NoEngineModule);
        }
    }

    private bool HasTtd()
    {
        return _api.SpecProvider?.TerminalTotalDifficulty is not null || _mergeConfig.TerminalTotalDifficulty is not null;
    }

    public Task InitNetworkProtocol()
    {
        if (MergeEnabled)
        {
            if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
            if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
            if (_api.UnclesValidator is null) throw new ArgumentNullException(nameof(_api.UnclesValidator));
            if (_api.BlockProductionPolicy is null) throw new ArgumentException(nameof(_api.BlockProductionPolicy));
            if (_api.SealValidator is null) throw new ArgumentException(nameof(_api.SealValidator));
            if (_api.HeaderValidator is null) throw new ArgumentException(nameof(_api.HeaderValidator));

            MergeHeaderValidator headerValidator = new(
                    _poSSwitcher,
                    _api.HeaderValidator,
                    _api.BlockTree,
                    _api.SpecProvider,
                    _api.SealValidator,
                    _api.LogManager);

            _api.HeaderValidator = new InvalidHeaderInterceptor(
                headerValidator,
                _invalidChainTracker,
                _api.LogManager);

            _api.UnclesValidator = new MergeUnclesValidator(_poSSwitcher, _api.UnclesValidator);
            _api.BlockValidator = new InvalidBlockInterceptor(
                new BlockValidator(_api.TxValidator, _api.HeaderValidator, _api.UnclesValidator,
                    _api.SpecProvider, _api.LogManager),
                _invalidChainTracker,
                _api.LogManager);
            _api.HealthHintService =
                new MergeHealthHintService(_api.HealthHintService, _poSSwitcher, _blocksConfig);
            _mergeBlockProductionPolicy = new MergeBlockProductionPolicy(_api.BlockProductionPolicy);
            _api.BlockProductionPolicy = _mergeBlockProductionPolicy;

            _api.FinalizationManager = new MergeFinalizationManager(_blockFinalizationManager, _api.FinalizationManager, _poSSwitcher);

            // Need to do it here because blockprocessor is not available in init
            _invalidChainTracker.SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor!);
        }

        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (MergeEnabled)
        {
            if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
            if (_api.BlockchainProcessor is null) throw new ArgumentNullException(nameof(_api.BlockchainProcessor));
            if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
            if (_api.EthSyncingInfo is null) throw new ArgumentNullException(nameof(_api.EthSyncingInfo));
            if (_api.Sealer is null) throw new ArgumentNullException(nameof(_api.Sealer));
            if (_api.BlockValidator is null) throw new ArgumentNullException(nameof(_api.BlockValidator));
            if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
            if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
            if (_api.StateReader is null) throw new ArgumentNullException(nameof(_api.StateReader));
            if (_beaconPivot is null) throw new ArgumentNullException(nameof(_beaconPivot));
            if (_beaconSync is null) throw new ArgumentNullException(nameof(_beaconSync));
            if (_blockProductionTrigger is null) throw new ArgumentNullException(nameof(_blockProductionTrigger));
            if (_peerRefresher is null) throw new ArgumentNullException(nameof(_peerRefresher));


            if (_postMergeBlockProducer is null) throw new ArgumentNullException(nameof(_postMergeBlockProducer));
            if (_blockProductionTrigger is null) throw new ArgumentNullException(nameof(_blockProductionTrigger));

            // ToDo: ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart. Then we will wait 5s more to ensure everything is processed
            while (!_api.BlockProcessingQueue.IsEmpty)
            {
                Thread.Sleep(100);
            }
            Thread.Sleep(5000);

            IBlockImprovementContextFactory improvementContextFactory;
            if (string.IsNullOrEmpty(_mergeConfig.BuilderRelayUrl))
            {
                improvementContextFactory = new BlockImprovementContextFactory(_blockProductionTrigger, TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));
            }
            else
            {
                DefaultHttpClient httpClient = new(new HttpClient(), _api.EthereumJsonSerializer, _api.LogManager, retryDelayMilliseconds: 100);
                IBoostRelay boostRelay = new BoostRelay(httpClient, _mergeConfig.BuilderRelayUrl);
                BoostBlockImprovementContextFactory boostBlockImprovementContextFactory = new(_blockProductionTrigger, TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot), boostRelay, _api.StateReader);
                improvementContextFactory = boostBlockImprovementContextFactory;
            }

            PayloadPreparationService payloadPreparationService = new(
                _postMergeBlockProducer,
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
                    _poSSwitcher,
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
                    _poSSwitcher,
                    payloadPreparationService,
                    _api.BlockProcessingQueue,
                    _blockCacheService,
                    _invalidChainTracker,
                    _beaconSync,
                    _beaconPivot,
                    _peerRefresher,
                    _api.SpecProvider,
                    _api.SyncPeerPool!,
                    _api.LogManager,
                    _api.Config<IBlocksConfig>().SecondsPerSlot,
                    _api.Config<IMergeConfig>().SimulateBlockProduction),
                new GetPayloadBodiesByHashV1Handler(_api.BlockTree, _api.LogManager),
                new GetPayloadBodiesByRangeV1Handler(_api.BlockTree, _api.LogManager),
                new ExchangeTransitionConfigurationV1Handler(_poSSwitcher, _api.LogManager),
                new ExchangeCapabilitiesHandler(_api.RpcCapabilitiesProvider, _api.LogManager),
                _api.SpecProvider,
                new GCKeeper(new NoSyncGcRegionStrategy(_api.SyncModeSelector, _mergeConfig), _api.LogManager),
                _api.LogManager);

            RegisterEngineRpcModule(engineRpcModule);

            if (_logger.IsInfo) _logger.Info("Engine Module has been enabled");
        }

        return Task.CompletedTask;
    }

    protected virtual void RegisterEngineRpcModule(IEngineRpcModule engineRpcModule)
    {
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        _api.RpcModuleProvider.RegisterSingle(engineRpcModule);
    }

    public Task InitSynchronization()
    {
        if (MergeEnabled)
        {
            if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
            if (_api.SyncPeerPool is null) throw new ArgumentNullException(nameof(_api.SyncPeerPool));
            if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
            if (_api.DbProvider is null) throw new ArgumentNullException(nameof(_api.DbProvider));
            if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
            if (_blockCacheService is null) throw new ArgumentNullException(nameof(_blockCacheService));
            if (_api.BetterPeerStrategy is null) throw new ArgumentNullException(nameof(_api.BetterPeerStrategy));
            if (_api.SealValidator is null) throw new ArgumentNullException(nameof(_api.SealValidator));
            if (_api.UnclesValidator is null) throw new ArgumentNullException(nameof(_api.UnclesValidator));
            if (_api.NodeStatsManager is null) throw new ArgumentNullException(nameof(_api.NodeStatsManager));
            if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
            if (_api.PeerDifficultyRefreshPool is null) throw new ArgumentNullException(nameof(_api.PeerDifficultyRefreshPool));
            if (_api.StateReader is null) throw new ArgumentNullException(nameof(_api.StateReader));

            // ToDo strange place for validators initialization
            PeerRefresher peerRefresher = new(_api.PeerDifficultyRefreshPool, _api.TimerFactory, _api.LogManager);
            _peerRefresher = peerRefresher;
            _api.DisposeStack.Push(peerRefresher);
            _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.LogManager);

            MergeHeaderValidator headerValidator = new(
                    _poSSwitcher,
                    _api.HeaderValidator,
                    _api.BlockTree,
                    _api.SpecProvider,
                    _api.SealValidator,
                    _api.LogManager);

            _api.HeaderValidator = new InvalidHeaderInterceptor(
                headerValidator,
                _invalidChainTracker,
                _api.LogManager);

            _api.UnclesValidator = new MergeUnclesValidator(_poSSwitcher, _api.UnclesValidator);
            _api.BlockValidator = new InvalidBlockInterceptor(
                new BlockValidator(
                    _api.TxValidator,
                    _api.HeaderValidator,
                    _api.UnclesValidator,
                    _api.SpecProvider,
                    _api.LogManager),
                _invalidChainTracker,
                _api.LogManager);
            _beaconSync = new BeaconSync(_beaconPivot, _api.BlockTree, _syncConfig, _blockCacheService, _poSSwitcher, _api.LogManager);

            _api.BetterPeerStrategy = new MergeBetterPeerStrategy(_api.BetterPeerStrategy, _poSSwitcher, _beaconPivot, _api.LogManager);

            _api.Pivot = _beaconPivot;

            MergeBlockDownloaderFactory blockDownloaderFactory = new MergeBlockDownloaderFactory(
                _poSSwitcher,
                _beaconPivot,
                _api.SpecProvider,
                _api.BlockValidator!,
                _api.SealValidator!,
                _syncConfig,
                _api.BetterPeerStrategy!,
                new FullStateFinder(_api.BlockTree, _api.StateReader),
                _api.LogManager);

            MergeSynchronizer synchronizer = new MergeSynchronizer(
                _api.DbProvider,
                _api.SpecProvider!,
                _api.BlockTree!,
                _api.ReceiptStorage!,
                _api.SyncPeerPool,
                _api.NodeStatsManager!,
                _syncConfig,
                blockDownloaderFactory,
                _beaconPivot,
                _poSSwitcher,
                _mergeConfig,
                _invalidChainTracker,
                _api.ProcessExit!,
                _api.BetterPeerStrategy,
                _api.ChainSpec,
                _beaconSync,
                _api.StateReader,
                _api.LogManager
            );
            _api.Synchronizer = synchronizer;

            PivotUpdator pivotUpdator = new(
                _api.BlockTree,
                synchronizer.SyncModeSelector,
                _api.SyncPeerPool,
                _syncConfig,
                _blockCacheService,
                _beaconSync,
                _api.DbProvider.MetadataDb,
                _api.LogManager);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize { get => true; }
}
