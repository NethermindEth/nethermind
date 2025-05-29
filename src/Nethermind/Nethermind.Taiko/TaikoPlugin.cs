// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain;
using Nethermind.Taiko.Rpc;
using Nethermind.HealthChecks;
using Nethermind.Db;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Processing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Blockchain.Blocks;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Blockchain.Receipts;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Core;
using Autofac;
using Autofac.Core;
using Nethermind.Taiko.BlockTransactionExecutors;
using Nethermind.Api.Steps;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Taiko.TaikoSpec;

namespace Nethermind.Taiko;

public class TaikoPlugin(ChainSpec chainSpec) : IConsensusPlugin
{
    public const string Taiko = "Taiko";
    private const string L1OriginDbName = "L1Origin";
    public string Author => "Nethermind";
    public string Name => Taiko;
    public string Description => "Taiko support for Nethermind";

    private TaikoNethermindApi? _api;
    private ILogger _logger;

    private IMergeConfig _mergeConfig = null!;
    private ISyncConfig _syncConfig = null!;

    private IBlockCacheService? _blockCacheService;

    public bool Enabled => chainSpec.SealEngineType == SealEngineType;

    public Task Init(INethermindApi api)
    {
        _api = (TaikoNethermindApi)api;
        _mergeConfig = _api.Config<IMergeConfig>();
        _syncConfig = _api.Config<ISyncConfig>();
        _logger = _api.LogManager.GetClassLogger();

        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);

        _blockCacheService = _api.Context.Resolve<IBlockCacheService>();
        _api.FinalizationManager = new ManualBlockFinalizationManager();

        _api.RewardCalculatorSource = NoBlockRewards.Instance;
        _api.SealValidator = NullSealEngine.Instance;
        _api.GossipPolicy = ShouldNotGossip.Instance;

        _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_api.Context.Resolve<IPoSSwitcher>()));

        return Task.CompletedTask;
    }

    public void InitTxTypesAndRlpDecoders(INethermindApi api)
    {
        _api = (TaikoNethermindApi)api;

        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.DbFactory);

        IRlpStreamDecoder<L1Origin> r1OriginDecoder = new L1OriginDecoder();
        Rlp.RegisterDecoder(typeof(L1Origin), r1OriginDecoder);


        IDb db = _api.DbFactory.CreateDb(new DbSettings(L1OriginDbName, L1OriginDbName.ToLower()));
        _api.DbProvider!.RegisterDb(L1OriginDbName, db);
        _api.L1OriginStore = new(_api.DbProvider.GetDb<IDb>(L1OriginDbName), r1OriginDecoder);
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
        ArgumentNullException.ThrowIfNull(_api.ReceiptStorage);
        ArgumentNullException.ThrowIfNull(_api.StateReader);
        ArgumentNullException.ThrowIfNull(_api.TxPool);
        ArgumentNullException.ThrowIfNull(_api.FinalizationManager);
        ArgumentNullException.ThrowIfNull(_api.WorldStateManager);
        ArgumentNullException.ThrowIfNull(_api.SyncPeerPool);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);
        ArgumentNullException.ThrowIfNull(_api.EngineRequestsTracker);

        ArgumentNullException.ThrowIfNull(_blockCacheService);

        // Ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart.
        // Then we will wait 5s more to ensure everything is processed
        while (!_api.BlockProcessingQueue.IsEmpty)
            await Task.Delay(100);
        await Task.Delay(5000);

        IInitConfig initConfig = _api.Config<IInitConfig>();

        ReadOnlyBlockTree readonlyBlockTree = _api.BlockTree.AsReadOnly();
        IRlpStreamDecoder<Transaction> txDecoder = Rlp.GetStreamDecoder<Transaction>() ?? throw new ArgumentNullException(nameof(IRlpStreamDecoder<Transaction>));
        TaikoPayloadPreparationService payloadPreparationService = CreatePayloadPreparationService(_api, txDecoder);
        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        var poSSwitcher = _api.Context.Resolve<IPoSSwitcher>();
        var invalidChainTracker = _api.Context.Resolve<IInvalidChainTracker>();
        var peerRefresher = _api.Context.Resolve<IPeerRefresher>();
        var beaconPivot = _api.Context.Resolve<IBeaconPivot>();
        var beaconSync = _api.Context.Resolve<BeaconSync>();

        ITaikoEngineRpcModule engine = new TaikoEngineRpcModule(
            new GetPayloadV1Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV2Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV4Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV5Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new NewPayloadHandler(
                _api.BlockValidator,
                _api.BlockTree,
                poSSwitcher,
                beaconSync,
                beaconPivot,
                _blockCacheService,
                _api.BlockProcessingQueue,
                invalidChainTracker,
                beaconSync,
                _api.LogManager,
                TimeSpan.FromSeconds(_mergeConfig.NewPayloadTimeout),
                _api.Config<IReceiptConfig>().StoreReceipts),
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
                _api.LogManager,
                _api.Config<IMergeConfig>().SimulateBlockProduction),
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
            txDecoder
        );

        _api.RpcModuleProvider.RegisterSingle(engine);

        if (_logger.IsInfo) _logger.Info("Taiko Engine Module has been enabled");
    }

    private static TaikoPayloadPreparationService CreatePayloadPreparationService(TaikoNethermindApi api, IRlpStreamDecoder<Transaction> txDecoder)
    {
        ArgumentNullException.ThrowIfNull(api.L1OriginStore);
        ArgumentNullException.ThrowIfNull(api.SpecProvider);

        IReadOnlyTxProcessorSource txProcessingEnv = api.ReadOnlyTxProcessingEnvFactory.Create();
        IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

        BlockProcessor blockProcessor = new BlockProcessor(
            api.SpecProvider,
            api.BlockValidator,
            NoBlockRewards.Instance,
            new BlockInvalidTxExecutor(new BuildUpTransactionProcessorAdapter(scope.TransactionProcessor), scope.WorldState),
            scope.WorldState,
            api.ReceiptStorage!,
            new BeaconBlockRootHandler(scope.TransactionProcessor, scope.WorldState),
            new BlockhashStore(api.SpecProvider, scope.WorldState),
            api.LogManager,
            new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(scope.WorldState, api.LogManager)),
            new ExecutionRequestsProcessor(scope.TransactionProcessor));

        IBlockchainProcessor blockchainProcessor =
            new BlockchainProcessor(
                api.BlockTree,
                blockProcessor,
                api.BlockPreprocessor,
                api.StateReader!,
                api.LogManager,
                BlockchainProcessor.Options.NoReceipts);

        OneTimeChainProcessor chainProcessor = new(
            scope.WorldState,
            blockchainProcessor);

        TaikoPayloadPreparationService payloadPreparationService = new(
            chainProcessor,
            scope.WorldState,
            api.L1OriginStore,
            api.LogManager,
            txDecoder);

        return payloadPreparationService;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;

    // IInitializationPlugin
    public IEnumerable<StepInfo> GetSteps()
    {
        yield return typeof(InitializeBlockchainTaiko);
        yield return typeof(RegisterTaikoRpcModules);
    }

    // IConsensusPlugin

    public IBlockProducerRunner InitBlockProducerRunner(IBlockProducer _)
    {
        throw new NotSupportedException();
    }

    public IBlockProducer InitBlockProducer(ITxSource? _ = null)
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

            .AddSingleton<IPoSSwitcher>(AlwaysPoS.Instance)
            .AddSingleton<StartingSyncPivotUpdater, UnsafeStartingSyncPivotUpdater>()

            .AddDecorator<BeaconSync>((_, strategy) =>
            {
                strategy.AllowBeaconHeaderSync();
                return strategy;
            })

            // Validators
            .AddSingleton<IBlockValidator, TaikoBlockValidator>()
            .AddSingleton<IHeaderValidator, TaikoHeaderValidator>()
            .AddSingleton<IUnclesValidator>(Always.Valid)

            .AddScoped<ITransactionProcessor, TaikoTransactionProcessor>()
            ;
    }
}
