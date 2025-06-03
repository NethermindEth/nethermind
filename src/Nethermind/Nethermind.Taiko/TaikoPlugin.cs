// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Serialization.Rlp;
using Autofac;
using Autofac.Core;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Serialization.Json;
using Nethermind.Taiko.BlockTransactionExecutors;
using Nethermind.Taiko.Config;
using Nethermind.Taiko.Rpc;
using Nethermind.Taiko.TaikoSpec;

namespace Nethermind.Taiko;

public class TaikoPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public const string Taiko = "Taiko";
    public string Author => "Nethermind";
    public string Name => Taiko;
    public string Description => "Taiko support for Nethermind";

    private TaikoNethermindApi? _api;
    private ILogger _logger;

    private IMergeConfig _mergeConfig = null!;

    private IBlockCacheService? _blockCacheService;

    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    public Task Init(INethermindApi api)
    {
        _api = (TaikoNethermindApi)api;
        _mergeConfig = _api.Config<IMergeConfig>();
        _logger = _api.LogManager.GetClassLogger();

        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);

        _blockCacheService = _api.Context.Resolve<IBlockCacheService>();
        _api.FinalizationManager = new ManualBlockFinalizationManager();

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
        ArgumentNullException.ThrowIfNull(_api.ReceiptStorage);
        ArgumentNullException.ThrowIfNull(_api.StateReader);
        ArgumentNullException.ThrowIfNull(_api.TxPool);
        ArgumentNullException.ThrowIfNull(_api.FinalizationManager);
        ArgumentNullException.ThrowIfNull(_api.WorldStateManager);
        ArgumentNullException.ThrowIfNull(_api.SyncPeerPool);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);
        ArgumentNullException.ThrowIfNull(_api.EngineRequestsTracker);

        ArgumentNullException.ThrowIfNull(_blockCacheService);

        IInitConfig initConfig = _api.Config<IInitConfig>();

        ReadOnlyBlockTree readonlyBlockTree = _api.BlockTree.AsReadOnly();
        IRlpStreamDecoder<Transaction> txDecoder = Rlp.GetStreamDecoder<Transaction>() ?? throw new ArgumentNullException(nameof(IRlpStreamDecoder<Transaction>));
        IPayloadPreparationService payloadPreparationService = _api.Context.Resolve<IPayloadPreparationService>();

        IPoSSwitcher poSSwitcher = _api.Context.Resolve<IPoSSwitcher>();
        IInvalidChainTracker invalidChainTracker = _api.Context.Resolve<IInvalidChainTracker>();
        IPeerRefresher peerRefresher = _api.Context.Resolve<IPeerRefresher>();
        IBeaconPivot beaconPivot = _api.Context.Resolve<IBeaconPivot>();
        BeaconSync beaconSync = _api.Context.Resolve<BeaconSync>();

        ITaikoEngineRpcModule engine = new TaikoEngineRpcModule(
            new GetPayloadV1Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV2Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV4Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV5Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new NewPayloadHandler(
                payloadPreparationService,
                _api.BlockValidator,
                _api.BlockTree,
                poSSwitcher,
                beaconSync,
                beaconPivot,
                _blockCacheService,
                _api.BlockProcessingQueue,
                invalidChainTracker,
                beaconSync,
                _mergeConfig,
                _api.Config<IReceiptConfig>(),
                _api.LogManager),
            new TaikoForkchoiceUpdatedHandler(
                _api.BlockTree,
                (ManualBlockFinalizationManager)_api.FinalizationManager,
                poSSwitcher,
                payloadPreparationService,
                _api.BlockProcessingQueue,
                _blockCacheService,
                invalidChainTracker,
                beaconSync,
                beaconPivot,
                peerRefresher,
                _api.SpecProvider,
                _api.SyncPeerPool,
                _mergeConfig,
                _api.LogManager),
            new GetPayloadBodiesByHashV1Handler(_api.BlockTree, _api.LogManager),
            new GetPayloadBodiesByRangeV1Handler(_api.BlockTree, _api.LogManager),
            new ExchangeTransitionConfigurationV1Handler(poSSwitcher, _api.LogManager),
            new ExchangeCapabilitiesHandler(_api.RpcCapabilitiesProvider, _api.LogManager),
            new GetBlobsHandler(_api.TxPool),
            new GetBlobsHandlerV2(_api.TxPool),
            _api.EngineRequestsTracker,
            _api.SpecProvider,
            new GCKeeper(
                initConfig.DisableGcOnNewPayload
                    ? NoGCStrategy.Instance
                    : new NoSyncGcRegionStrategy(_api.SyncModeSelector, _mergeConfig), _api.LogManager),
            _api.LogManager,
            _api.TxPool,
            readonlyBlockTree,
            _api.ReadOnlyTxProcessingEnvFactory,
            txDecoder,
            _api.L1OriginStore
        );

        _api.RpcModuleProvider.RegisterSingle(engine);

        if (_logger.IsInfo) _logger.Info("Taiko Engine Module has been enabled");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;

    // IConsensusPlugin

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer _)
    {
        throw new NotSupportedException();
    }

    public IBlockProducer InitBlockProducer()
    {
        throw new NotSupportedException();
    }

    public string SealEngineType => Core.SealEngineType.Taiko;

    public IModule Module => new TaikoModule();

    public Type ApiType => typeof(TaikoNethermindApi);
}

public class TaikoModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<NethermindApi, TaikoNethermindApi>()
            .AddModule(new BaseMergePluginModule())

            .AddSingleton<ISpecProvider, TaikoChainSpecBasedSpecProvider>()
            .Map<TaikoChainSpecEngineParameters, ChainSpec>(chainSpec =>
                chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<TaikoChainSpecEngineParameters>())

            // Steps override
            .AddStep(typeof(InitializeBlockchainTaiko))

            // L1 origin store
            .AddSingleton<IRlpStreamDecoder<L1Origin>, L1OriginDecoder>()
            .AddKeyedSingleton<IDb>(L1OriginStore.L1OriginDbName, ctx => ctx
                .Resolve<IDbFactory>().CreateDb(new DbSettings(L1OriginStore.L1OriginDbName, L1OriginStore.L1OriginDbName.ToLower())))
            .AddSingleton<IL1OriginStore, L1OriginStore>()

            // Sync modification
            .AddSingleton<IPoSSwitcher>(AlwaysPoS.Instance)
            .AddSingleton<StartingSyncPivotUpdater, UnsafeStartingSyncPivotUpdater>()
            .AddDecorator<BeaconSync>((_, strategy) =>
            {
                // Normally not turned on at start because `StartingSyncPivotUpdater` waiting for pivot
                strategy.AllowBeaconHeaderSync();
                return strategy;
            })

            // Validators
            .AddSingleton<IBlockValidator, TaikoBlockValidator>()
            .AddSingleton<IHeaderValidator, TaikoHeaderValidator>()
            .AddSingleton<IUnclesValidator>(Always.Valid)

            // Blok processing
            .AddScoped<IValidationTransactionExecutor, TaikoBlockValidationTransactionExecutor>()
            .AddScoped<ITransactionProcessor, TaikoTransactionProcessor>()
            .AddScoped<IBlockProducerEnvFactory, TaikoBlockProductionEnvFactory>()

            .AddSingleton<IRlpStreamDecoder<Transaction>>((_) => Rlp.GetStreamDecoder<Transaction>()!)
            .AddSingleton<IPayloadPreparationService, IBlockProducerEnvFactory, L1OriginStore, IRlpStreamDecoder<Transaction>, ILogManager>(CreatePayloadPreparationService)
            .AddSingleton<IHealthHintService, IBlocksConfig>(blocksConfig =>
                new ManualHealthHintService(blocksConfig.SecondsPerSlot * 6, HealthHintConstants.InfinityHint))

            // Conditionally register SurgeGasPriceOracle if UseSurgeGasPriceOracle is enabled
            .AddDecorator<IGasPriceOracle>((ctx, defaultGasPriceOracle) =>
            {
                ISpecProvider specProvider = ctx.Resolve<ISpecProvider>();
                var taikoSpec = (TaikoReleaseSpec)specProvider.GenesisSpec;

                if (!taikoSpec.UseSurgeGasPriceOracle)
                    return defaultGasPriceOracle;

                ISurgeConfig surgeConfig = ctx.Resolve<ISurgeConfig>();

                if (string.IsNullOrEmpty(surgeConfig.L1EthApiEndpoint))
                {
                    throw new ArgumentException("L1EthApiEndpoint must be provided in the Surge configuration to compute the gas price");
                }

                if (string.IsNullOrEmpty(surgeConfig.TaikoInboxAddress))
                {
                    throw new ArgumentException("TaikoInboxAddress must be provided in the Surge configuration to compute the gas price");
                }

                var l1RpcClient = new BasicJsonRpcClient(
                    new Uri(surgeConfig.L1EthApiEndpoint),
                    ctx.Resolve<IJsonSerializer>(),
                    ctx.Resolve<ILogManager>());

                return new SurgeGasPriceOracle(
                    ctx.Resolve<IBlockFinder>(),
                    ctx.Resolve<ILogManager>(),
                    specProvider,
                    ctx.Resolve<IBlocksConfig>().MinGasPrice,
                    l1RpcClient,
                    surgeConfig);
            })

            // Rpc
            .RegisterSingletonJsonRpcModule<ITaikoExtendedEthRpcModule, TaikoExtendedEthModule>()
            .AddSingleton<IPayloadPreparationService, IBlockProducerEnvFactory, L1OriginStore, IRlpStreamDecoder<Transaction>, ILogManager>(CreatePayloadPreparationService)

            // Need to set the rlp globally
            .OnBuild(ctx =>
            {
                Rlp.RegisterDecoder(typeof(L1Origin), ctx.Resolve<IRlpStreamDecoder<L1Origin>>());
            })
            ;
    }

    private static IPayloadPreparationService CreatePayloadPreparationService(
        IBlockProducerEnvFactory blockProducerEnvFactory,
        L1OriginStore l1OriginStore,
        IRlpStreamDecoder<Transaction> txDecoder,
        ILogManager logManager)
    {
        IBlockProducerEnv blockProducerEnv = blockProducerEnvFactory.Create();

        TaikoPayloadPreparationService payloadPreparationService = new(
            blockProducerEnv.ChainProcessor,
            blockProducerEnv.ReadOnlyStateProvider,
            l1OriginStore,
            logManager,
            txDecoder);

        return payloadPreparationService;
    }

}
