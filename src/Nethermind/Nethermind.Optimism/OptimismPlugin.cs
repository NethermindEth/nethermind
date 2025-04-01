// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
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
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Rlp;
using Nethermind.Optimism.Rpc;
using Nethermind.Optimism.ProtocolVersion;

namespace Nethermind.Optimism;

public class OptimismPlugin(ChainSpec chainSpec) : IConsensusPlugin, ISynchronizationPlugin
{
    public string Author => "Nethermind";
    public string Name => "Optimism";
    public string Description => "Optimism support for Nethermind";

    private OptimismNethermindApi? _api;
    private ILogger _logger;
    private IMergeConfig _mergeConfig = null!;
    private ISyncConfig _syncConfig = null!;
    private IBlocksConfig _blocksConfig = null!;
    private IBlockCacheService? _blockCacheService;
    private InvalidChainTracker? _invalidChainTracker;
    private ManualBlockFinalizationManager? _blockFinalizationManager;
    private IPeerRefresher? _peerRefresher;
    private IBeaconPivot? _beaconPivot;
    private BeaconSync? _beaconSync;

    private OptimismCL? _cl;
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    public IEnumerable<StepInfo> GetSteps()
    {
        yield return typeof(InitializeBlockchainOptimism);
        yield return typeof(RegisterOptimismRpcModules);
    }

    #region IConsensusPlugin

    public string SealEngineType => Core.SealEngineType.Optimism;

    public IBlockProductionTrigger DefaultBlockProductionTrigger => NeverProduceTrigger.Instance;

    public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
    {
        if (additionalTxSource is not null)
            throw new ArgumentException(
                "Optimism does not support additional tx source");

        StepDependencyException.ThrowIfNull(_api);
        StepDependencyException.ThrowIfNull(_api.WorldStateManager);
        StepDependencyException.ThrowIfNull(_api.BlockTree);
        StepDependencyException.ThrowIfNull(_api.SpecProvider);
        StepDependencyException.ThrowIfNull(_api.BlockValidator);
        StepDependencyException.ThrowIfNull(_api.RewardCalculatorSource);
        StepDependencyException.ThrowIfNull(_api.ReceiptStorage);
        StepDependencyException.ThrowIfNull(_api.TxPool);
        StepDependencyException.ThrowIfNull(_api.TransactionComparerProvider);
        StepDependencyException.ThrowIfNull(_api.SpecHelper);
        StepDependencyException.ThrowIfNull(_api.L1CostHelper);

        _api.BlockProducerEnvFactory = new OptimismBlockProducerEnvFactory(
            _api.WorldStateManager,
            _api.BlockTree,
            _api.SpecProvider,
            _api.BlockValidator,
            _api.RewardCalculatorSource,
            _api.ReceiptStorage,
            _api.BlockPreprocessor,
            _api.TxPool,
            _api.TransactionComparerProvider,
            _api.Config<IBlocksConfig>(),
            _api.SpecHelper,
            _api.L1CostHelper,
            _api.LogManager);

        OptimismGasLimitCalculator gasLimitCalculator = new OptimismGasLimitCalculator();

        BlockProducerEnv producerEnv = _api.BlockProducerEnvFactory.Create();

        return new OptimismPostMergeBlockProducer(
            new OptimismPayloadTxSource(),
            producerEnv.TxSource,
            producerEnv.ChainProcessor,
            producerEnv.BlockTree,
            producerEnv.ReadOnlyStateProvider,
            gasLimitCalculator,
            NullSealEngine.Instance,
            new ManualTimestamper(),
            _api.SpecProvider,
            _api.LogManager,
            _api.Config<IBlocksConfig>());
    }

    #endregion

    public void InitTxTypesAndRlpDecoders(INethermindApi api)
    {
        api.RegisterTxType<DepositTransactionForRpc>(new OptimismTxDecoder<Transaction>(), Always.Valid);
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

        _blockCacheService = _api.Context.Resolve<IBlockCacheService>();
        _api.EthereumEcdsa = new OptimismEthereumEcdsa(_api.EthereumEcdsa);
        _invalidChainTracker = _api.Context.Resolve<InvalidChainTracker>();
        _api.FinalizationManager = _blockFinalizationManager = new ManualBlockFinalizationManager();

        _api.RewardCalculatorSource = NoBlockRewards.Instance;
        _api.SealValidator = NullSealEngine.Instance;
        _api.GossipPolicy = ShouldNotGossip.Instance;

        _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_api.Context.Resolve<IPoSSwitcher>()));

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

        ArgumentNullException.ThrowIfNull(_blockCacheService);

        _api.Context.Resolve<InvalidChainTracker>().SetupBlockchainProcessorInterceptor(_api.MainProcessingContext!.BlockchainProcessor);

        _beaconPivot = _api.Context.Resolve<IBeaconPivot>();
        _beaconSync = _api.Context.Resolve<BeaconSync>();

        _peerRefresher = new PeerRefresher(_api.PeerDifficultyRefreshPool!, _api.TimerFactory, _api.LogManager);
        _api.DisposeStack.Push((PeerRefresher)_peerRefresher);

        _ = new UnsafeStartingSyncPivotUpdater(
            _api.BlockTree,
            _api.SyncModeSelector,
            _api.SyncPeerPool!,
            _syncConfig,
            _blockCacheService,
            _beaconSync,
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

        var posSwitcher = _api.Context.Resolve<IPoSSwitcher>();

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
                posSwitcher,
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
                posSwitcher,
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
                _api.HistoryPruner,
                _api.Config<IBlocksConfig>().SecondsPerSlot,
                _api.Config<IMergeConfig>().SimulateBlockProduction),
            new GetPayloadBodiesByHashV1Handler(_api.BlockTree, _api.LogManager),
            new GetPayloadBodiesByRangeV1Handler(_api.BlockTree, _api.LogManager),
            new ExchangeTransitionConfigurationV1Handler(posSwitcher, _api.LogManager),
            new ExchangeCapabilitiesHandler(_api.RpcCapabilitiesProvider, _api.LogManager),
            new GetBlobsHandler(_api.TxPool),
            _api.SpecProvider,
            new GCKeeper(
                initConfig.DisableGcOnNewPayload
                    ? NoGCStrategy.Instance
                    : new NoSyncGcRegionStrategy(_api.SyncModeSelector, _mergeConfig), _api.LogManager),
            _api.LogManager);

        IOptimismSignalSuperchainV1Handler signalHandler = new LoggingOptimismSignalSuperchainV1Handler(
            OptimismConstants.CurrentProtocolVersion,
            _api.LogManager);

        IOptimismEngineRpcModule opEngine = new OptimismEngineRpcModule(engineRpcModule, signalHandler);

        _api.RpcModuleProvider.RegisterSingle(opEngine);

        StepDependencyException.ThrowIfNull(_api.EthereumEcdsa);

        ICLConfig clConfig = _api.Config<ICLConfig>();
        if (clConfig.Enabled)
        {
            CLChainSpecEngineParameters chainSpecEngineParameters = _api.ChainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<CLChainSpecEngineParameters>();
            _cl = new OptimismCL(_api.SpecProvider, chainSpecEngineParameters, clConfig, _api.EthereumJsonSerializer,
                _api.EthereumEcdsa, _api.Timestamper, _api!.LogManager, opEngine);
            await _cl.Start();
        }

        if (_logger.IsInfo) _logger.Info("Optimism Engine Module has been enabled");
    }

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        return new StandardBlockProducerRunner(
            DefaultBlockProductionTrigger,
            _api!.BlockTree!,
            blockProducer);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;

    public Type ApiType => typeof(OptimismNethermindApi);

    public IModule Module => new OptimismModule(chainSpec);
}

public class OptimismModule(ChainSpec chainSpec) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<NethermindApi, OptimismNethermindApi>()
            .AddModule(new MergePluginModule())
            .AddModule(new OptimismSynchronizerModule(chainSpec))

            .AddSingleton<OptimismChainSpecEngineParameters>(chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<OptimismChainSpecEngineParameters>())

            .AddSingleton<IPoSSwitcher, OptimismPoSSwitcher>()
            ;

    }
}
