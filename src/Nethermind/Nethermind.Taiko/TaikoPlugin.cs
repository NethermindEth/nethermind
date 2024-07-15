// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc;
using Nethermind.HealthChecks;
using Nethermind.Db;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.ParallelSync;
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

namespace Nethermind.Taiko;

public class TaikoPlugin : IConsensusPlugin, ISynchronizationPlugin, IInitializationPlugin
{
    public string Author => "Nethermind";
    public string Name => "Taiko";
    public string Description => "Taiko support for Nethermind";

    private TaikoNethermindApi? _api;
    private ILogger _logger;

    private IMergeConfig _mergeConfig = null!;
    private ISyncConfig _syncConfig = null!;
    private ITaikoConfig? _taikoConfig;

    private BlockCacheService? _blockCacheService;
    private IPeerRefresher? _peerRefresher;
    private IBeaconPivot? _beaconPivot;
    private BeaconSync? _beaconSync;

    private const string L1OriginDbName = "L1Origin";

    public Task Init(INethermindApi api)
    {
        _taikoConfig = api.Config<ITaikoConfig>();

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

    public void InitRlpDecoders(INethermindApi api)
    {
        if (ShouldRunSteps(api))
        {
            Rlp.RegisterDecoder(typeof(L1Origin), new L1OriginDecoder());
        }
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
        // ArgumentNullException.ThrowIfNull(_api.BlockProducerEnvFactory);
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

        IDb db = _api.DbFactory!.CreateDb(new DbSettings(L1OriginDbName, L1OriginDbName.ToLower()));
        _api.DbProvider!.RegisterDb(L1OriginDbName, db);
        L1OriginStore l1OriginStore = new(db);

        IInitConfig initConfig = _api.Config<IInitConfig>();

        ReadOnlyBlockTree readonlyBlockTree = _api.BlockTree.AsReadOnly();

        TaikoReadOnlyTxProcessingEnv txProcessingEnv =
            new(_api.WorldStateManager, readonlyBlockTree, _api.SpecProvider, _api.LogManager);

        IReadOnlyTxProcessingScope scope = txProcessingEnv.Build(Keccak.EmptyTreeHash);

        BlockProcessor blockProcessor =
            new(_api.SpecProvider,
                _api.BlockValidator,
                NoBlockRewards.Instance,
                new BlockInvalidTxExecutor(new BuildUpTransactionProcessorAdapter(_api.TransactionProcessor), scope.WorldState, _api.EthereumEcdsa),
                scope.WorldState,
                _api.ReceiptStorage,
                new BlockhashStore(_api.SpecProvider, scope.WorldState),
                _api.LogManager,
                new BlockProductionWithdrawalProcessor(new WithdrawalProcessor(scope.WorldState, _api.LogManager)));

        IBlockchainProcessor blockchainProcessor =
            new BlockchainProcessor(
                readonlyBlockTree,
                blockProcessor,
                _api.BlockPreprocessor,
                txProcessingEnv.StateReader,
                _api.LogManager,
                BlockchainProcessor.Options.NoReceipts);

        OneTimeChainProcessor chainProcessor = new(
            scope.WorldState,
            blockchainProcessor);

        // BlockProducerEnv env = ((TaikoBlockProducerEnvFactory)_api.BlockProducerEnvFactory).CreateWithInvalidTxExecutor();

        TaikoSimplePayloadPreparationService payloadPreparationService = new(
            chainProcessor,
            // env.ChainProcessor,
            scope.WorldState,
            // env.ReadOnlyStateProvider,
            l1OriginStore,
            _api.LogManager);

        _api.RpcCapabilitiesProvider = new EngineRpcCapabilitiesProvider(_api.SpecProvider);

        IEngineRpcModule engineRpcModule = new EngineRpcModule(
            new GetPayloadV1Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV2Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
            new GetPayloadV3Handler(payloadPreparationService, _api.SpecProvider, _api.LogManager),
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
            new ForkchoiceUpdatedHandler(
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

        ITaikoEngineRpcModule engine = new TaikoEngineRpcModule(engineRpcModule);

        _api.RpcModuleProvider.RegisterSingle(engine);

        FeeHistoryOracle feeHistoryOracle = new(_api.BlockTree, _api.ReceiptStorage, _api.SpecProvider);
        _api.DisposeStack.Push(feeHistoryOracle);

        TaikoRpcModule taikoRpc = new(
            _api.Config<IJsonRpcConfig>(),
            _api.CreateBlockchainBridge(),
            _api.BlockTree.AsReadOnly(),
            _api.ReceiptStorage,
            _api.StateReader,
            _api.TxPool,
            _api.TxSender,
            _api.Wallet,
            _api.LogManager,
            _api.SpecProvider,
            _api.GasPriceOracle,
            _api.EthSyncingInfo,
            feeHistoryOracle,
            _api.Config<IBlocksConfig>().SecondsPerSlot,
            _api.Config<ISyncConfig>(),
            l1OriginStore
            );

        _api.RpcModuleProvider.RegisterSingle((ITaikoRpcModule)taikoRpc);
        _api.RpcModuleProvider.RegisterSingle((ITaikoAuthRpcModule)taikoRpc);

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
        ArgumentNullException.ThrowIfNull(_api.PeerDifficultyRefreshPool);
        ArgumentNullException.ThrowIfNull(_api.SyncPeerPool);
        ArgumentNullException.ThrowIfNull(_api.NodeStatsManager);
        ArgumentNullException.ThrowIfNull(_api.BlockchainProcessor);

        ArgumentNullException.ThrowIfNull(_blockCacheService);
        ArgumentNullException.ThrowIfNull(_api.InvalidChainTracker);

        _api.InvalidChainTracker.SetupBlockchainProcessorInterceptor(_api.BlockchainProcessor);

        _peerRefresher = new PeerRefresher(_api.PeerDifficultyRefreshPool, _api.TimerFactory, _api.LogManager);
        _api.DisposeStack.Push((PeerRefresher)_peerRefresher);

        _beaconPivot = new BeaconPivot(_syncConfig, _api.DbProvider.MetadataDb, _api.BlockTree, _api.PoSSwitcher, _api.LogManager);
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
            _api.NodeStorageFactory.WrapKeyValueStore(_api.DbProvider.StateDb),
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
            _api.InvalidChainTracker,
            _api.ProcessExit!,
            _api.BetterPeerStrategy,
            _api.ChainSpec,
            _beaconSync,
            _api.StateReader!,
            _api.LogManager
        );

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
        throw new NotImplementedException();
    }

    public IBlockProducer InitBlockProducer(ITxSource? additionalTxSource = null)
    {
        throw new NotImplementedException();
    }

    public string SealEngineType => Core.SealEngineType.Taiko;
}
