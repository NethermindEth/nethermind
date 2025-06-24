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
using Nethermind.Optimism.Cl.Rpc;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.Derivation;

namespace Nethermind.Optimism;

public class OptimismPlugin(ChainSpec chainSpec) : IConsensusPlugin
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
    private OptimismPayloadPreparationService? _payloadPreparationService;

    private OptimismCL? _cl;
    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    public IEnumerable<StepInfo> GetSteps()
    {
        yield return typeof(InitializeBlockchainOptimism);
    }

    #region IConsensusPlugin

    public string SealEngineType => Core.SealEngineType.Optimism;

    public IBlockProductionTrigger DefaultBlockProductionTrigger => NeverProduceTrigger.Instance;

    public IBlockProducer InitBlockProducer()
    {
        StepDependencyException.ThrowIfNull(_api);

        OptimismGasLimitCalculator gasLimitCalculator = new OptimismGasLimitCalculator();

        BlockProducerEnv producerEnv = _api.BlockProducerEnvFactory.Create();

        return new OptimismPostMergeBlockProducer(
            new OptimismPayloadTxSource(),
            producerEnv.TxSource,
            producerEnv.ChainProcessor,
            producerEnv.BlockTree,
            producerEnv.ReadOnlyStateProvider,
            gasLimitCalculator,
            _api.SealEngine,
            new ManualTimestamper(),
            _api.SpecProvider,
            _api.SpecHelper,
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
        _invalidChainTracker = _api.Context.Resolve<InvalidChainTracker>();
        _api.FinalizationManager = _blockFinalizationManager = new ManualBlockFinalizationManager();

        _api.GossipPolicy = ShouldNotGossip.Instance;

        _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_api.Context.Resolve<IPoSSwitcher>()));

        return Task.CompletedTask;
    }

    public Task InitRpcModules()
    {
        if (_api is null)
            return Task.CompletedTask;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
        ArgumentNullException.ThrowIfNull(_api.SyncModeSelector);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockProducer);
        ArgumentNullException.ThrowIfNull(_api.TxPool);
        ArgumentNullException.ThrowIfNull(_api.EngineRequestsTracker);

        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_invalidChainTracker);
        ArgumentNullException.ThrowIfNull(_blockFinalizationManager);

        // Single block shouldn't take a full slot to run
        // We can improve the blocks until requested, but the single block still needs to be run in a timely manner
        double maxSingleImprovementTimePerSlot = _blocksConfig.SecondsPerSlot * _blocksConfig.SingleBlockImprovementOfSlot;
        BlockImprovementContextFactory improvementContextFactory = new(
            _api.BlockProducer,
            TimeSpan.FromSeconds(maxSingleImprovementTimePerSlot));

        OptimismPayloadPreparationService payloadPreparationService = new(
            _api.SpecProvider,
            (PostMergeBlockProducer)_api.BlockProducer,
            improvementContextFactory,
            _api.TimerFactory,
            _api.LogManager,
            TimeSpan.FromSeconds(_blocksConfig.SecondsPerSlot));
        _payloadPreparationService = payloadPreparationService;

        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        var posSwitcher = _api.Context.Resolve<IPoSSwitcher>();
        var beaconPivot = _api.Context.Resolve<IBeaconPivot>();
        var beaconSync = _api.Context.Resolve<BeaconSync>();

        IPeerRefresher peerRefresher = _api.Context.Resolve<IPeerRefresher>();
        IInitConfig initConfig = _api.Config<IInitConfig>();

        NewPayloadHandler newPayloadHandler = new(
            _api.BlockValidator,
            _api.BlockTree,
            posSwitcher,
            beaconSync,
            beaconPivot,
            _blockCacheService,
            _api.BlockProcessingQueue,
            _invalidChainTracker,
            beaconSync,
            _api.LogManager,
            TimeSpan.FromSeconds(_mergeConfig.NewPayloadTimeout),
            _api.Config<IReceiptConfig>().StoreReceipts);
        bool simulateBlockProduction = _api.Config<IMergeConfig>().SimulateBlockProduction;
        if (simulateBlockProduction)
        {
            newPayloadHandler.NewPayloadForParentReceived += payloadPreparationService.CancelBlockProductionForParent;
        }

        IEngineRpcModule engineRpcModule = new EngineRpcModule(
            new GetPayloadV1Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV2Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager, _api.CensorshipDetector),
            new GetPayloadV4Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager, _api.CensorshipDetector),
            new GetPayloadV5Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager, _api.CensorshipDetector),
            newPayloadHandler,
            new ForkchoiceUpdatedHandler(
                _api.BlockTree,
                _blockFinalizationManager,
                posSwitcher,
                payloadPreparationService,
                _api.BlockProcessingQueue,
                _blockCacheService,
                _invalidChainTracker,
                beaconSync,
                beaconPivot,
                peerRefresher,
                _api.SpecProvider,
                _api.SyncPeerPool!,
                _api.LogManager,
                simulateBlockProduction),
            new GetPayloadBodiesByHashV1Handler(_api.BlockTree, _api.LogManager),
            new GetPayloadBodiesByRangeV1Handler(_api.BlockTree, _api.LogManager),
            new ExchangeTransitionConfigurationV1Handler(posSwitcher, _api.LogManager),
            new ExchangeCapabilitiesHandler(_api.RpcCapabilitiesProvider, _api.LogManager),
            new GetBlobsHandler(_api.TxPool),
            new GetBlobsHandlerV2(_api.TxPool),
            _api.EngineRequestsTracker,
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
        StepDependencyException.ThrowIfNull(_api.IpResolver);

        IOptimismConfig config = _api.Config<IOptimismConfig>();
        if (config.ClEnabled)
        {
            ArgumentNullException.ThrowIfNull(config.L1BeaconApiEndpoint);
            ArgumentNullException.ThrowIfNull(config.L1EthApiEndpoint);

            CLChainSpecEngineParameters clParameters = _api.ChainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<CLChainSpecEngineParameters>();
            OptimismChainSpecEngineParameters engineParameters = chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<OptimismChainSpecEngineParameters>();

            ArgumentNullException.ThrowIfNull(clParameters.UnsafeBlockSigner);
            ArgumentNullException.ThrowIfNull(clParameters.Nodes);
            ArgumentNullException.ThrowIfNull(clParameters.SystemConfigProxy);
            ArgumentNullException.ThrowIfNull(clParameters.L2BlockTime);

            IEthApi ethApi = new EthereumEthApi(config.L1EthApiEndpoint, _api.EthereumJsonSerializer, _api.LogManager);
            IBeaconApi beaconApi = new EthereumBeaconApi(new Uri(config.L1BeaconApiEndpoint), _api.EthereumJsonSerializer, _api.EthereumEcdsa, _api.LogManager);

            IDecodingPipeline decodingPipeline = new DecodingPipeline(_api.LogManager);
            IL1Bridge l1Bridge = new EthereumL1Bridge(ethApi, beaconApi, clParameters, _api.LogManager);
            IL1ConfigValidator l1ConfigValidator = new L1ConfigValidator(ethApi, _api.LogManager);

            ISystemConfigDeriver systemConfigDeriver = new SystemConfigDeriver(clParameters.SystemConfigProxy);
            IL2Api l2Api = new L2Api(_api.Context.Resolve<IRpcModuleFactory<IOptimismEthRpcModule>>().Create(), opEngine, systemConfigDeriver, _api.LogManager);
            IExecutionEngineManager executionEngineManager = new ExecutionEngineManager(l2Api, _api.LogManager);

            _cl = new OptimismCL(
                decodingPipeline,
                l1Bridge,
                l1ConfigValidator,
                l2Api,
                executionEngineManager,
                _api.Timestamper,
                // Configs
                config,
                clParameters,
                _api.IpResolver.ExternalIp,
                _api.SpecProvider.ChainId,
                _api.ChainSpec.Genesis.Timestamp,
                // Logging
                _api.LogManager
            );
            _ = _cl.Start(); // NOTE: Fire and forget, exception handling must be done inside `Start`
            _api.DisposeStack.Push(_cl);

            IOptimismOptimismRpcModule optimismRpcModule = new OptimismOptimismRpcModule(
                ethApi,
                l2Api,
                executionEngineManager,
                decodingPipeline,
                clParameters,
                engineParameters,
                _api.ChainSpec);
            _api.RpcModuleProvider.RegisterSingle(optimismRpcModule);
        }

        if (_logger.IsInfo) _logger.Info("Optimism Engine Module has been enabled");
        return Task.CompletedTask;
    }

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer blockProducer)
    {
        return new StandardBlockProducerRunner(
            DefaultBlockProductionTrigger,
            _api!.BlockTree!,
            blockProducer);
    }

    public ValueTask DisposeAsync()
    {
        _payloadPreparationService?.Dispose();
        return ValueTask.CompletedTask;
    }

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
            .AddModule(new BaseMergePluginModule())
            .AddModule(new OptimismSynchronizerModule(chainSpec))

            .AddSingleton(chainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<OptimismChainSpecEngineParameters>())
            .AddSingleton<IOptimismSpecHelper, OptimismSpecHelper>()
            .AddSingleton<ICostHelper, OptimismCostHelper>()

            .AddSingleton<IPoSSwitcher, OptimismPoSSwitcher>()
            .AddSingleton<StartingSyncPivotUpdater, UnsafeStartingSyncPivotUpdater>()

            // Validators
            .AddSingleton<IBlockValidator, OptimismBlockValidator>()
            .AddSingleton<IHeaderValidator, OptimismHeaderValidator>()
            .AddSingleton<IUnclesValidator>(Always.Valid)

            // Block processing
            .AddScoped<ITransactionProcessor, OptimismTransactionProcessor>()
            .AddScoped<IBlockProcessor, OptimismBlockProcessor>()
            .AddScoped<IWithdrawalProcessor, OptimismWithdrawalProcessor>()
            .AddScoped<Create2DeployerContractRewriter>()

            .AddDecorator<IEthereumEcdsa, OptimismEthereumEcdsa>()
            .AddSingleton<IBlockProducerEnvFactory, OptimismBlockProducerEnvFactory>()
            .AddDecorator<IBlockProducerTxSourceFactory, OptimismBlockProducerTxSourceFactory>()

            .AddDecorator<IEthereumEcdsa, OptimismEthereumEcdsa>()
            .AddSingleton<ISimulateTransactionProcessorFactory, SimulateOptimismTransactionProcessorFactory>()

            // Rpcs
            .AddSingleton<IHealthHintService, IBlocksConfig>((blocksConfig) =>
                new ManualHealthHintService(blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint))

            .AddSingleton<OptimismEthModuleFactory>()
                .Bind<IRpcModuleFactory<IOptimismEthRpcModule>, OptimismEthModuleFactory>()
                .Bind<IRpcModuleFactory<IEthRpcModule>, OptimismEthModuleFactory>()
            ;

    }
}
