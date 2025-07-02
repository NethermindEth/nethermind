// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Proxy;
using Nethermind.HealthChecks;
using Nethermind.Init.Steps.Migrations;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.BlockProduction.Boost;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin;

public partial class MergePlugin(ChainSpec chainSpec, IMergeConfig mergeConfig) : IConsensusWrapperPlugin, ISynchronizationPlugin
{
    protected INethermindApi _api = null!;
    private ILogger _logger;
    private ISyncConfig _syncConfig = null!;
    protected IBlocksConfig _blocksConfig = null!;
    protected ITxPoolConfig _txPoolConfig = null!;
    protected IPoSSwitcher _poSSwitcher = NoPoS.Instance;
    private IBeaconPivot? _beaconPivot;
    private BeaconSync? _beaconSync;
    private IBlockCacheService _blockCacheService = null!;
    private InvalidChainTracker.InvalidChainTracker _invalidChainTracker = null!;
    private IPeerRefresher _peerRefresher = null!;
    private IJsonRpcConfig _jsonRpcConfig = null!;


    protected ManualBlockFinalizationManager _blockFinalizationManager = null!;
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
        _jsonRpcConfig = nethermindApi.Config<IJsonRpcConfig>();

        MigrateSecondsPerSlot(_blocksConfig, mergeConfig);

        _logger = _api.LogManager.GetClassLogger();

        EnsureNotConflictingSettings();

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
                mergeConfig,
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

        if (!_jsonRpcConfig.Enabled)
        {
            if (_logger.IsInfo)
                _logger.Info("JsonRpc not enabled. Turning on JsonRpc URL with engine API.");

            _jsonRpcConfig.Enabled = true;

            EnsureEngineModuleIsConfigured();

            if (!_jsonRpcConfig.EnabledModules.Contains(ModuleType.Engine, StringComparison.OrdinalIgnoreCase))
            {
                // Disable it
                _jsonRpcConfig.EnabledModules = [];
            }

            _jsonRpcConfig.AdditionalRpcUrls = _jsonRpcConfig.AdditionalRpcUrls
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
                "Engine module wasn't configured on any port. Nethermind can't work without engine port configured. Verify your RPC configuration. You can find examples in our docs: https://docs.nethermind.io/nethermind/ethereum-client/engine-jsonrpc-configuration-examples",
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
            _api.FinalizationManager = InitializeMergeFinilizationManager();

            // Need to do it here because blockprocessor is not available in init
            _invalidChainTracker.SetupBlockchainProcessorInterceptor(_api.MainProcessingContext!.BlockchainProcessor!);
        }

        return Task.CompletedTask;
    }

    protected virtual IBlockFinalizationManager InitializeMergeFinilizationManager()
    {
        return new MergeFinalizationManager(_blockFinalizationManager, _api.FinalizationManager, _poSSwitcher);
    }

    public Task InitRpcModules()
    {
        if (MergeEnabled)
        {
            if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
            if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
            if (_api.EthSyncingInfo is null) throw new ArgumentNullException(nameof(_api.EthSyncingInfo));
            if (_api.Sealer is null) throw new ArgumentNullException(nameof(_api.Sealer));
            if (_api.BlockValidator is null) throw new ArgumentNullException(nameof(_api.BlockValidator));
            if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
            if (_api.TxPool is null) throw new ArgumentNullException(nameof(_api.TxPool));
            if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
            if (_api.StateReader is null) throw new ArgumentNullException(nameof(_api.StateReader));
            if (_beaconPivot is null) throw new ArgumentNullException(nameof(_beaconPivot));
            if (_beaconSync is null) throw new ArgumentNullException(nameof(_beaconSync));
            if (_peerRefresher is null) throw new ArgumentNullException(nameof(_peerRefresher));
            if (_postMergeBlockProducer is null) throw new ArgumentNullException(nameof(_postMergeBlockProducer));

            // ToDo: ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart. Then we will wait 5s more to ensure everything is processed
            while (!_api.BlockProcessingQueue.IsEmpty)
            {
                Thread.Sleep(100);
            }
            Thread.Sleep(5000);

            IBlockImprovementContextFactory CreateBlockImprovementContextFactory()
            {
                if (string.IsNullOrEmpty(mergeConfig.BuilderRelayUrl))
                {
                    return new BlockImprovementContextFactory(_api.BlockProducer!, TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));
                }

                DefaultHttpClient httpClient = new(new HttpClient(), _api.EthereumJsonSerializer, _api.LogManager, retryDelayMilliseconds: 100);
                IBoostRelay boostRelay = new BoostRelay(httpClient, mergeConfig.BuilderRelayUrl);
                return new BoostBlockImprovementContextFactory(_api.BlockProducer!, TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot), boostRelay, _api.StateReader);
            }

            IBlockImprovementContextFactory improvementContextFactory = _api.BlockImprovementContextFactory ??= CreateBlockImprovementContextFactory();

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
                new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager, _api.CensorshipDetector),
                new GetPayloadV4Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager, _api.CensorshipDetector),
                new NewPayloadHandler(
                    _api.BlockValidator,
                    _api.BlockTree,
                    _syncConfig,
                    _poSSwitcher,
                    _beaconSync,
                    _beaconPivot,
                    _blockCacheService,
                    _api.BlockProcessingQueue,
                    _invalidChainTracker,
                    _beaconSync,
                    _api.LogManager,
                    TimeSpan.FromSeconds(mergeConfig.NewPayloadTimeout),
                    _api.Config<IReceiptConfig>().StoreReceipts),
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
                new GetBlobsHandler(_api.TxPool),
                _api.SpecProvider,
                new GCKeeper(new NoSyncGcRegionStrategy(_api.SyncModeSelector, mergeConfig), _api.LogManager),
                _api.LogManager);

            RegisterEngineRpcModule(engineRpcModule);

            if (_logger.IsInfo) _logger.Info("Engine Module has been enabled");
        }

        if (!_jsonRpcConfig.EnabledModules.Contains(ModuleType.Debug, StringComparison.OrdinalIgnoreCase))
        {
            // Register debug module for merge
            IOverridableWorldScope worldStateManager = _api.WorldStateManager!.CreateOverridableWorldScope();
            OverridableTxProcessingEnv txEnv = new(worldStateManager, _api.BlockTree!.AsReadOnly(), _api.SpecProvider!, _api.LogManager);

            IReadOnlyTxProcessingScope scope = txEnv.Build(Keccak.EmptyTreeHash);

            ChangeableTransactionProcessorAdapter transactionProcessorAdapter = new(scope.TransactionProcessor);
            IBlockProcessor.IBlockTransactionsExecutor transactionsExecutor = new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessorAdapter, scope.WorldState);
            ReadOnlyChainProcessingEnv chainProcessingEnv = new ReadOnlyChainProcessingEnv(
                scope,
                _api.BlockValidator!,
                _api.BlockPreprocessor,
                _api.RewardCalculatorSource!.Get(scope.TransactionProcessor),
                _api.ReceiptStorage!,
                _api.SpecProvider!,
                _api.BlockTree!,
                worldStateManager.GlobalStateReader,
                _api.LogManager,
                transactionsExecutor);

            GethStyleTracer tracer = new(
                chainProcessingEnv.ChainProcessor,
                scope.WorldState,
                _api.ReceiptStorage!,
                _api.BlockTree!,
                _api.BadBlocksStore!,
                _api.SpecProvider!,
                transactionProcessorAdapter,
                _api.FileSystem,
                txEnv);

            MergeDebugBridge debugBridge = new(
                _api.ConfigProvider,
                _api.DbProvider!.AsReadOnly(true),
                tracer,
                _api.BlockTree!,
                _api.ReceiptStorage!,
                new ReceiptMigration(_api),
                _api.SpecProvider!,
                _api.SyncModeSelector,
                _api.BadBlocksStore!,
                _api.BlockProducer!);

            var debugModule = new MergeDebugRpcModule(_api.LogManager, debugBridge, _jsonRpcConfig, _api.SpecProvider!);
            RegisterDebugRpcModule(debugModule);
        }

        return Task.CompletedTask;
    }

    protected virtual void RegisterEngineRpcModule(IEngineRpcModule engineRpcModule)
    {
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        _api.RpcModuleProvider.RegisterSingle(engineRpcModule);
    }
    protected virtual void RegisterDebugRpcModule(IMergeDebugRpcModule debugRpcModule)
    {
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        _api.RpcModuleProvider.RegisterSingle(debugRpcModule);
    }

    public Task InitSynchronization()
    {
        if (MergeEnabled)
        {
            if (_api.SpecProvider is null) throw new ArgumentNullException(nameof(_api.SpecProvider));
            if (_api.BlockTree is null) throw new ArgumentNullException(nameof(_api.BlockTree));
            if (_api.DbProvider is null) throw new ArgumentNullException(nameof(_api.DbProvider));
            if (_api.BlockProcessingQueue is null) throw new ArgumentNullException(nameof(_api.BlockProcessingQueue));
            if (_blockCacheService is null) throw new ArgumentNullException(nameof(_blockCacheService));
            if (_api.BetterPeerStrategy is null) throw new ArgumentNullException(nameof(_api.BetterPeerStrategy));
            if (_api.SealValidator is null) throw new ArgumentNullException(nameof(_api.SealValidator));
            if (_api.UnclesValidator is null) throw new ArgumentNullException(nameof(_api.UnclesValidator));
            if (_api.NodeStatsManager is null) throw new ArgumentNullException(nameof(_api.NodeStatsManager));
            if (_api.HeaderValidator is null) throw new ArgumentNullException(nameof(_api.HeaderValidator));
            if (_api.StateReader is null) throw new ArgumentNullException(nameof(_api.StateReader));

            // ToDo strange place for validators initialization
            _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.PoSSwitcher, _api.LogManager);

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

            ContainerBuilder builder = new ContainerBuilder();

            _api.ConfigureContainerBuilderFromApiWithNetwork(builder)
                .AddSingleton<IBeaconSyncStrategy>(_beaconSync)
                .AddSingleton<IBeaconPivot>(_beaconPivot)
                .AddSingleton(mergeConfig)
                .AddSingleton<IInvalidChainTracker>(_invalidChainTracker);

            builder.RegisterModule(new SynchronizerModule(_syncConfig));
            builder.RegisterModule(new MergeSynchronizerModule());

            IContainer container = builder.Build();
            _api.ApiWithNetworkServiceContainer = container;
            _api.DisposeStack.Push((IAsyncDisposable)container);

            PeerRefresher peerRefresher = new(_api.PeerDifficultyRefreshPool!, _api.TimerFactory, _api.LogManager);
            _peerRefresher = peerRefresher;
            _api.DisposeStack.Push(peerRefresher);
            _ = new
            PivotUpdator(
                _api.BlockTree,
                _api.SyncModeSelector,
                _api.SyncPeerPool!,
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

    public virtual IEnumerable<StepInfo> GetSteps() => [];
}
