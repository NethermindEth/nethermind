// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
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
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.HealthChecks;
using Nethermind.Init.Steps;
using Nethermind.Optimism.CL;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Rlp;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp.TxDecoders;
using Nethermind.Synchronization;

namespace Nethermind.Optimism;

public class OptimismPlugin(ChainSpec chainSpec) : IConsensusPlugin, ISynchronizationPlugin, IInitializationPlugin
{
    public string Author => "Nethermind";
    public string Name => "Optimism";
    public string Description => "Optimism support for Nethermind";

    private OptimismNethermindApi? _api;
    private OptimismChainSpecEngineParameters? _chainSpecParameters;
    private ILogger _logger;
    private IMergeConfig _mergeConfig = null!;
    private ISyncConfig _syncConfig = null!;
    private IBlocksConfig _blocksConfig = null!;
    private BlockCacheService? _blockCacheService;
    private InvalidChainTracker? _invalidChainTracker;
    private ManualBlockFinalizationManager? _blockFinalizationManager;
    private IPeerRefresher? _peerRefresher;
    private IBeaconPivot? _beaconPivot;
    private BeaconSync? _beaconSync;

    private OptimismCL? _cl;
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    public IEnumerable<StepInfo> GetSteps()
    {
        yield return new StepInfo(typeof(InitializeBlockchainOptimism));
        yield return new StepInfo(typeof(InitializeBlockProducerOptimism));
        yield return new StepInfo(typeof(RegisterOptimismRpcModules));
    }

    #region IConsensusPlugin

    public string SealEngineType => Core.SealEngineType.Optimism;

    public IBlockProductionTrigger DefaultBlockProductionTrigger => NeverProduceTrigger.Instance;

    public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
    {
        if (additionalTxSource is not null)
            throw new ArgumentException(
                "Optimism does not support additional tx source");

        ArgumentNullException.ThrowIfNull(_api);
        ArgumentNullException.ThrowIfNull(_api.BlockProducer);

        return _api.BlockProducer;
    }

    #endregion

    public INethermindApi CreateApi(IConfigProvider configProvider, IJsonSerializer jsonSerializer,
        ILogManager logManager, ChainSpec chainSpec) =>
        new OptimismNethermindApi(configProvider, jsonSerializer, logManager, chainSpec);

    public void InitTxTypesAndRlpDecoders(INethermindApi api)
    {
        api.RegisterTxType<OptimismTransactionForRpc>(new OptimismTxDecoder<Transaction>(), Always.Valid);
        api.RegisterTxType<LegacyTransactionForRpc>(new OptimismLegacyTxDecoder(), new OptimismLegacyTxValidator(api.SpecProvider!.ChainId));
        Rlp.RegisterDecoders(typeof(OptimismReceiptMessageDecoder).Assembly, true);
    }

    public Task Init(INethermindApi api)
    {
        _api = (OptimismNethermindApi)api;
        _mergeConfig = _api.Config<IMergeConfig>();
        _syncConfig = _api.Config<ISyncConfig>();
        _blocksConfig = _api.Config<IBlocksConfig>();
        _logger = _api.LogManager.GetClassLogger();

        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);

        _chainSpecParameters = _api.ChainSpec.EngineChainSpecParametersProvider
            .GetChainSpecParameters<OptimismChainSpecEngineParameters>();
        _api.PoSSwitcher = new OptimismPoSSwitcher(_api.SpecProvider, _chainSpecParameters.BedrockBlockNumber!.Value);

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

    public Task InitSynchronization()
    {
        if (_api is null)
            return Task.CompletedTask;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.NodeStatsManager);
        ArgumentNullException.ThrowIfNull(_api.BlockchainProcessor);
        ArgumentNullException.ThrowIfNull(_api.BetterPeerStrategy);

        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_invalidChainTracker);

        _invalidChainTracker.SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor);

        _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.PoSSwitcher, _api.LogManager);
        _beaconSync = new BeaconSync(_beaconPivot, _api.BlockTree, _syncConfig, _blockCacheService, _api.PoSSwitcher, _api.LogManager);
        _api.BetterPeerStrategy = new MergeBetterPeerStrategy(_api.BetterPeerStrategy, _api.PoSSwitcher, _beaconPivot, _api.LogManager);
        _api.Pivot = _beaconPivot;

        ContainerBuilder builder = new ContainerBuilder();
        ((INethermindApi)_api).ConfigureContainerBuilderFromApiWithNetwork(builder)
            .AddSingleton<IBeaconSyncStrategy>(_beaconSync)
            .AddSingleton(_beaconPivot)
            .AddSingleton(_api.PoSSwitcher)
            .AddSingleton(_mergeConfig)
            .AddSingleton<IInvalidChainTracker>(_invalidChainTracker);

        builder.RegisterModule(new SynchronizerModule(_syncConfig));
        builder.RegisterModule(new MergeSynchronizerModule());
        builder.RegisterModule(new OptimismSynchronizerModule(_chainSpecParameters!, _api.SpecProvider));

        IContainer container = builder.Build();

        _api.ApiWithNetworkServiceContainer = container;
        _api.DisposeStack.Push((IAsyncDisposable)container);

        _peerRefresher = new PeerRefresher(_api.PeerDifficultyRefreshPool!, _api.TimerFactory, _api.LogManager);
        _api.DisposeStack.Push((PeerRefresher)_peerRefresher);

        _ = new UnsafePivotUpdator(
            _api.BlockTree,
            _api.SyncModeSelector,
            _api.SyncPeerPool!,
            _syncConfig,
            _blockCacheService,
            _beaconSync,
            _api.DbProvider.MetadataDb,
            _api.LogManager);

        return Task.CompletedTask;
    }

    public async Task InitRpcModules()
    {
        if (_api is null)
            return;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
        ArgumentNullException.ThrowIfNull(_api.SyncModeSelector);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProducer);
        ArgumentNullException.ThrowIfNull(_api.TxPool);

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
            _api.BlockProducer,
            TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));

        OptimismPayloadPreparationService payloadPreparationService = new(
            _api.SpecProvider,
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
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager, _api.CensorshipDetector),
            new GetPayloadV4Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager, _api.CensorshipDetector),
            new NewPayloadHandler(
                _api.BlockValidator,
                _api.BlockTree,
                _syncConfig,
                _api.PoSSwitcher,
                _beaconSync,
                _beaconPivot,
                _blockCacheService,
                _api.BlockProcessingQueue,
                _invalidChainTracker,
                _beaconSync,
                _api.LogManager,
                TimeSpan.FromSeconds(_mergeConfig.NewPayloadTimeout),
                _api.Config<IReceiptConfig>().StoreReceipts),
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
                _api.SyncPeerPool!,
                _api.LogManager,
                _api.Config<IBlocksConfig>().SecondsPerSlot,
                _api.Config<IMergeConfig>().SimulateBlockProduction),
            new GetPayloadBodiesByHashV1Handler(_api.BlockTree, _api.LogManager),
            new GetPayloadBodiesByRangeV1Handler(_api.BlockTree, _api.LogManager),
            new ExchangeTransitionConfigurationV1Handler(_api.PoSSwitcher, _api.LogManager),
            new ExchangeCapabilitiesHandler(_api.RpcCapabilitiesProvider, _api.LogManager),
            new GetBlobsHandler(_api.TxPool),
            _api.SpecProvider,
            new GCKeeper(
                initConfig.DisableGcOnNewPayload
                    ? NoGCStrategy.Instance
                    : new NoSyncGcRegionStrategy(_api.SyncModeSelector, _mergeConfig), _api.LogManager),
            _api.LogManager);

        IOptimismEngineRpcModule opEngine = new OptimismEngineRpcModule(engineRpcModule);

        _api.RpcModuleProvider.RegisterSingle(opEngine);

        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);

        ICLConfig clConfig = _api.Config<ICLConfig>();
        if (clConfig.Enabled)
        {
            CLChainSpecEngineParameters chainSpecEngineParameters = _api.ChainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<CLChainSpecEngineParameters>();
            _cl = new OptimismCL(_api.SpecProvider, chainSpecEngineParameters, clConfig, _api.EthereumJsonSerializer,
                _api.EthereumEcdsa, _api.Timestamper, _api!.LogManager, opEngine);
            _cl.Start();
        }

        if (_logger.IsInfo) _logger.Info("Optimism Engine Module has been enabled");
    }

    public IBlockProducerRunner CreateBlockProducerRunner()
    {
        return new StandardBlockProducerRunner(
            DefaultBlockProductionTrigger,
            _api!.BlockTree!,
            _api.BlockProducer!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;
}
