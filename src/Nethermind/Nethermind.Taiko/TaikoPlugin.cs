// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Serialization.Json;
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
using Nethermind.State;
using Autofac;
using Nethermind.Synchronization;
using System.Linq;

namespace Nethermind.Taiko;

public class TaikoPlugin : IConsensusPlugin, ISynchronizationPlugin, IInitializationPlugin
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

    private BlockCacheService? _blockCacheService;
    private IPeerRefresher? _peerRefresher;
    private IBeaconPivot? _beaconPivot;
    private BeaconSync? _beaconSync;

    public Task Init(INethermindApi api)
    {
        if (!ShouldRunSteps(api))
            return Task.CompletedTask;

        _api = (TaikoNethermindApi)api;
        _mergeConfig = _api.Config<IMergeConfig>();
        _syncConfig = _api.Config<ISyncConfig>();
        _logger = _api.LogManager.GetClassLogger();

        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);

        _api.PoSSwitcher = AlwaysPoS.Instance;

        _blockCacheService = new BlockCacheService();
        _api.InvalidChainTracker = new InvalidChainTracker(
            _api.PoSSwitcher,
            _api.BlockTree,
            _blockCacheService,
            _api.LogManager);
        _api.DisposeStack.Push(_api.InvalidChainTracker);

        _api.FinalizationManager = new ManualBlockFinalizationManager();

        _api.RewardCalculatorSource = NoBlockRewards.Instance;
        _api.SealValidator = NullSealEngine.Instance;
        _api.GossipPolicy = ShouldNotGossip.Instance;

        _api.BlockPreprocessor.AddFirst(new MergeProcessingRecoveryStep(_api.PoSSwitcher));

        return Task.CompletedTask;
    }

    public void InitTxTypesAndRlpDecoders(INethermindApi api)
    {
        if (!ShouldRunSteps(api))
            return;

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
        if (_api is null || !ShouldRunSteps(_api))
            return;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.L1OriginStore);
        ArgumentNullException.ThrowIfNull(_api.BlockProcessingQueue);
        ArgumentNullException.ThrowIfNull(_api.SyncModeSelector);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.BlockValidator);
        ArgumentNullException.ThrowIfNull(_api.RpcModuleProvider);
        ArgumentNullException.ThrowIfNull(_api.ReceiptStorage);
        ArgumentNullException.ThrowIfNull(_api.StateReader);
        ArgumentNullException.ThrowIfNull(_api.TxPool);
        ArgumentNullException.ThrowIfNull(_api.TxSender);
        ArgumentNullException.ThrowIfNull(_api.Wallet);
        ArgumentNullException.ThrowIfNull(_api.GasPriceOracle);
        ArgumentNullException.ThrowIfNull(_api.EthSyncingInfo);
        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.TransactionProcessor);
        ArgumentNullException.ThrowIfNull(_api.FinalizationManager);
        ArgumentNullException.ThrowIfNull(_api.WorldStateManager);
        ArgumentNullException.ThrowIfNull(_api.InvalidChainTracker);
        ArgumentNullException.ThrowIfNull(_api.SyncPeerPool);
        ArgumentNullException.ThrowIfNull(_api.EthereumEcdsa);

        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_beaconPivot);
        ArgumentNullException.ThrowIfNull(_beaconSync);
        ArgumentNullException.ThrowIfNull(_peerRefresher);

        // Ugly temporary hack to not receive engine API messages before end of processing of all blocks after restart.
        // Then we will wait 5s more to ensure everything is processed
        while (!_api.BlockProcessingQueue.IsEmpty)
            await Task.Delay(100);
        await Task.Delay(5000);


        IInitConfig initConfig = _api.Config<IInitConfig>();

        ReadOnlyBlockTree readonlyBlockTree = _api.BlockTree.AsReadOnly();

        TaikoReadOnlyTxProcessingEnv txProcessingEnv =
            new(_api.WorldStateManager!.CreateOverridableWorldScope(), readonlyBlockTree, _api.SpecProvider, _api.LogManager);

        IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

        BlockProcessor blockProcessor =
            new(_api.SpecProvider,
                _api.BlockValidator,
                NoBlockRewards.Instance,
                new BlockInvalidTxExecutor(new BuildUpTransactionProcessorAdapter(scope.TransactionProcessor), scope.WorldState),
                scope.WorldState,
                _api.ReceiptStorage,
                _api.TransactionProcessor,
                new BeaconBlockRootHandler(_api.TransactionProcessor, _api.WorldStateManager.GlobalWorldState),
                new BlockhashStore(_api.SpecProvider, scope.WorldState),
                _api.LogManager,
                new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(scope.WorldState, _api.LogManager)));

        IBlockchainProcessor blockchainProcessor =
            new BlockchainProcessor(
                _api.BlockTree,
                blockProcessor,
                _api.BlockPreprocessor,
                txProcessingEnv.StateReader,
                _api.LogManager,
                BlockchainProcessor.Options.NoReceipts);

        OneTimeChainProcessor chainProcessor = new(
            scope.WorldState,
            blockchainProcessor);

        IRlpStreamDecoder<Transaction> txDecoder = Rlp.GetStreamDecoder<Transaction>() ?? throw new ArgumentNullException(nameof(IRlpStreamDecoder<Transaction>));

        TaikoPayloadPreparationService payloadPreparationService = new(
            chainProcessor,
            scope.WorldState,
            _api.L1OriginStore,
            _api.LogManager,
            txDecoder);

        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        ReadOnlyTxProcessingEnvFactory readonlyTxProcessingEnvFactory = new(_api.WorldStateManager, readonlyBlockTree, _api.SpecProvider, _api.LogManager);

        ITaikoEngineRpcModule engine = new TaikoEngineRpcModule(
            new GetPayloadV1Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV2Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV4Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new NewPayloadHandler(
                _api.BlockValidator,
                _api.BlockTree,
                _syncConfig,
                _api.PoSSwitcher,
                _beaconSync,
                _beaconPivot,
                _blockCacheService,
                _api.BlockProcessingQueue,
                _api.InvalidChainTracker!,
                _beaconSync,
                _api.LogManager,
                TimeSpan.FromSeconds(_mergeConfig.NewPayloadTimeout),
                _api.Config<IReceiptConfig>().StoreReceipts),
            new TaikoForkchoiceUpdatedHandler(
                _api.BlockTree,
                (ManualBlockFinalizationManager)_api.FinalizationManager,
                _api.PoSSwitcher,
                payloadPreparationService,
                _api.BlockProcessingQueue,
                _blockCacheService,
                _api.InvalidChainTracker,
                _beaconSync,
                _beaconPivot,
                _peerRefresher,
                _api.SpecProvider,
                _api.SyncPeerPool,
                _api.WorldStateManager.GlobalWorldState,
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
            _api.LogManager,
            _api.TxPool,
            readonlyBlockTree,
            readonlyTxProcessingEnvFactory,
            txDecoder
        );

        _api.RpcModuleProvider.RegisterSingle(engine);

        if (_logger.IsInfo) _logger.Info("Taiko Engine Module has been enabled");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public bool MustInitialize => true;

    // ISynchronizationPlugin
    public Task InitSynchronization()
    {
        if (_api is null || !ShouldRunSteps(_api))
            return Task.CompletedTask;

        ArgumentNullException.ThrowIfNull(_api.SpecProvider);
        ArgumentNullException.ThrowIfNull(_api.BlockTree);
        ArgumentNullException.ThrowIfNull(_api.DbProvider);
        ArgumentNullException.ThrowIfNull(_api.NodeStatsManager);
        ArgumentNullException.ThrowIfNull(_api.BlockchainProcessor);

        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_api.InvalidChainTracker);

        _api.InvalidChainTracker.SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor);

        _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.PoSSwitcher, _api.LogManager);
        _beaconSync = new BeaconSync(_beaconPivot, _api.BlockTree, _syncConfig, _blockCacheService, _api.PoSSwitcher, _api.LogManager);
        _api.BetterPeerStrategy = new MergeBetterPeerStrategy(null!, _api.PoSSwitcher, _beaconPivot, _api.LogManager);
        _api.Pivot = _beaconPivot;

        ContainerBuilder builder = new ContainerBuilder();

        ((INethermindApi)_api).ConfigureContainerBuilderFromApiWithNetwork(builder)
            .AddSingleton<IBeaconSyncStrategy>(_beaconSync)
            .AddSingleton<IBeaconPivot>(_beaconPivot)
            .AddSingleton(_mergeConfig)
            .AddSingleton<IInvalidChainTracker>(_api.InvalidChainTracker);

        builder.RegisterModule(new SynchronizerModule(_syncConfig));
        builder.RegisterModule(new MergeSynchronizerModule());

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
        _beaconSync.AllowBeaconHeaderSync();

        return Task.CompletedTask;
    }

    // IInitializationPlugin
    public bool ShouldRunSteps(INethermindApi api) => api.ChainSpec.SealEngineType == SealEngineType;

    // IConsensusPlugin

    public INethermindApi CreateApi(
        IConfigProvider configProvider,
        IJsonSerializer jsonSerializer,
        ILogManager logManager,
        ChainSpec chainSpec)
    {
        return new TaikoNethermindApi(configProvider, jsonSerializer, logManager, chainSpec);
    }

    public IBlockProducerRunner CreateBlockProducerRunner()
    {
        throw new NotSupportedException();
    }

    public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
    {
        throw new NotSupportedException();
    }

    public string SealEngineType => Core.SealEngineType.Taiko;
}
