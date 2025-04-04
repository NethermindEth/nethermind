// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Abi;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.FullPruning;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Services;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.PubSub;
using Nethermind.Core.Specs;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Simulate;
using Nethermind.Grpc;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Network;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Sockets;
using Nethermind.Trie;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Facade.Find;

namespace Nethermind.Api
{
    public class NethermindApi : INethermindApi
    {
        public NethermindApi(Dependencies dependencies)
        {
            _dependencies = dependencies;
            CryptoRandom = new CryptoRandom();
            DisposeStack = new DisposableStack(dependencies.LogManager);
            DisposeStack.Push(CryptoRandom);
        }

        // A simple class to prevent having to modify subclass of NethermindApi many time
        public record Dependencies(
            IConfigProvider ConfigProvider,
            IJsonSerializer JsonSerializer,
            ILogManager LogManager,
            ChainSpec ChainSpec,
            ISpecProvider SpecProvider,
            IReadOnlyList<INethermindPlugin> Plugins,
            IProcessExitSource ProcessExitSource,
            ILifetimeScope Context
        );

        private Dependencies _dependencies;

        public IBlockchainBridge CreateBlockchainBridge()
        {
            ReadOnlyBlockTree readOnlyTree = BlockTree!.AsReadOnly();

            // TODO: reuse the same trie cache here
            OverridableTxProcessingEnv txProcessingEnv = new(
                WorldStateManager!.CreateOverridableWorldScope(),
                readOnlyTree,
                SpecProvider!,
                LogManager);

            SimulateReadOnlyBlocksProcessingEnvFactory simulateReadOnlyBlocksProcessingEnvFactory =
                new SimulateReadOnlyBlocksProcessingEnvFactory(
                    WorldStateManager!,
                    readOnlyTree,
                    DbProvider!,
                    SpecProvider!,
                    SimulateTransactionProcessorFactory,
                    LogManager);

            IMiningConfig miningConfig = ConfigProvider.GetConfig<IMiningConfig>();
            IBlocksConfig blocksConfig = ConfigProvider.GetConfig<IBlocksConfig>();

            return new BlockchainBridge(
                txProcessingEnv,
                simulateReadOnlyBlocksProcessingEnvFactory,
                TxPool,
                ReceiptFinder,
                FilterStore,
                FilterManager,
                EthereumEcdsa,
                Timestamper,
                LogFinder,
                SpecProvider!,
                blocksConfig,
                miningConfig.Enabled
            );
        }

        public IAbiEncoder AbiEncoder { get; } = Nethermind.Abi.AbiEncoder.Instance;
        public IBlobTxStorage? BlobTxStorage { get; set; }
        public CompositeBlockPreprocessorStep BlockPreprocessor { get; } = new();
        public IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        public IBlockProducer? BlockProducer { get; set; }
        public IBlockProducerRunner? BlockProducerRunner { get; set; }
        public IBlockTree? BlockTree { get; set; }
        public IBlockValidator? BlockValidator => Context.Resolve<IBlockValidator>();
        public IBloomStorage? BloomStorage { get; set; }
        public IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        public IConfigProvider ConfigProvider => _dependencies.ConfigProvider;
        public ICryptoRandom CryptoRandom { get; }
        public IDbProvider? DbProvider { get; set; }
        public IDbFactory? DbFactory { get; set; }
        public IDisconnectsAnalyzer? DisconnectsAnalyzer { get; set; }
        public IDiscoveryApp? DiscoveryApp { get; set; }
        public ISigner? EngineSigner { get; set; }
        public ISignerStore? EngineSignerStore { get; set; }
        public IEnode? Enode { get; set; }
        public IEthereumEcdsa? EthereumEcdsa { get; set; }
        public IFileSystem FileSystem { get; set; } = new FileSystem();
        public IFilterStore? FilterStore { get; set; }
        public IFilterManager? FilterManager { get; set; }
        public IUnclesValidator? UnclesValidator => Context.Resolve<IUnclesValidator>();
        public IGrpcServer? GrpcServer { get; set; }
        public IHeaderValidator? HeaderValidator => Context.Resolve<IHeaderValidator>();

        public IManualBlockProductionTrigger ManualBlockProductionTrigger { get; set; } =
            new BuildBlocksWhenRequested();

        public IIPResolver? IpResolver { get; set; }
        public IJsonSerializer EthereumJsonSerializer => _dependencies.JsonSerializer;
        public IKeyStore? KeyStore { get; set; }
        public IPasswordProvider? PasswordProvider { get; set; }
        public ILogFinder? LogFinder { get; set; }
        public ILogManager LogManager => _dependencies.LogManager;
        public IMessageSerializationService MessageSerializationService { get; } = new MessageSerializationService();
        public IGossipPolicy GossipPolicy { get; set; } = Policy.FullGossip;
        public IMonitoringService MonitoringService { get; set; } = NullMonitoringService.Instance;
        public INodeStatsManager? NodeStatsManager { get; set; }
        public IPeerManager? PeerManager { get; set; }
        public IPeerPool? PeerPool { get; set; }
        public IProtocolsManager? ProtocolsManager { get; set; }
        public IProtocolValidator? ProtocolValidator { get; set; }
        public IReceiptStorage? ReceiptStorage { get; set; }
        public IReceiptFinder? ReceiptFinder { get; set; }
        public IReceiptMonitor? ReceiptMonitor { get; set; }
        public IRewardCalculatorSource? RewardCalculatorSource { get; set; } = NoBlockRewards.Instance;
        public IRlpxHost? RlpxPeer { get; set; }
        public IRpcModuleProvider? RpcModuleProvider { get; set; } = NullModuleProvider.Instance;
        public IRpcAuthentication? RpcAuthentication { get; set; }
        public IJsonRpcLocalStats? JsonRpcLocalStats { get; set; }
        public ISealer? Sealer { get; set; } = NullSealEngine.Instance;
        public string SealEngineType => ChainSpec.SealEngineType;
        public ISealValidator? SealValidator { get; set; } = NullSealEngine.Instance;
        private ISealEngine? _sealEngine;

        public ISealEngine SealEngine
        {
            get
            {
                return _sealEngine ??= new SealEngine(Sealer, SealValidator);
            }

            set
            {
                _sealEngine = value;
            }
        }

        public ISessionMonitor? SessionMonitor { get; set; }
        public ISpecProvider SpecProvider => _dependencies.SpecProvider;
        public ISyncModeSelector SyncModeSelector => Context.Resolve<ISyncModeSelector>()!;

        public ISyncProgressResolver? SyncProgressResolver => Context.Resolve<ISyncProgressResolver>();
        public ISyncPointers? SyncPointers => Context.Resolve<ISyncPointers>();
        public ISyncPeerPool? SyncPeerPool => Context.Resolve<ISyncPeerPool>();
        public IPeerDifficultyRefreshPool? PeerDifficultyRefreshPool => Context.Resolve<IPeerDifficultyRefreshPool>();
        public ISynchronizer? Synchronizer => Context.Resolve<ISynchronizer>();
        public ISyncServer? SyncServer => Context.Resolve<ISyncServer>();
        public IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        public IWorldStateManager? WorldStateManager { get; set; }
        public INodeStorage? MainNodeStorage { get; set; }
        public CompositePruningTrigger? PruningTrigger { get; set; }
        public IVerifyTrieStarter? VerifyTrieStarter { get; set; }
        public IStateReader? StateReader { get; set; }
        public IStaticNodesManager? StaticNodesManager { get; set; }
        public ITrustedNodesManager? TrustedNodesManager { get; set; }
        public ITimestamper Timestamper { get; } = Core.Timestamper.Default;
        public ITimerFactory TimerFactory { get; } = Core.Timers.TimerFactory.Default;
        public IMainProcessingContext? MainProcessingContext { get; set; }
        public ITxSender? TxSender { get; set; }
        public INonceManager? NonceManager { get; set; }
        public ITxPool? TxPool { get; set; }
        public ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        public IHealthHintService? HealthHintService { get; set; }
        public IRpcCapabilitiesProvider? RpcCapabilitiesProvider { get; set; }
        public TxValidator? TxValidator => Context.Resolve<TxValidator>();
        public IBlockFinalizationManager? FinalizationManager { get; set; }

        public IBlockProducerEnvFactory? BlockProducerEnvFactory { get; set; }
        public IBlockImprovementContextFactory? BlockImprovementContextFactory { get; set; }
        public IGasPriceOracle? GasPriceOracle { get; set; }

        public IEthSyncingInfo? EthSyncingInfo { get; set; }
        public IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        public INodeStorageFactory NodeStorageFactory { get; set; } = null!;
        public BackgroundTaskScheduler BackgroundTaskScheduler { get; set; } = null!;
        public CensorshipDetector CensorshipDetector { get; set; } = null!;
        public IWallet? Wallet { get; set; }
        public IBadBlockStore? BadBlocksStore { get; set; }
        public ITransactionComparerProvider? TransactionComparerProvider { get; set; }
        public IWebSocketsManager WebSocketsManager { get; set; } = new WebSocketsManager();

        public ISubscriptionFactory? SubscriptionFactory { get; set; }
        public IProtectedPrivateKey? NodeKey { get; set; }

        /// <summary>
        /// Key used for signing blocks. Original as its loaded on startup. This can later be changed via RPC in <see cref="Signer"/>.
        /// </summary>
        public IProtectedPrivateKey? OriginalSignerKey { get; set; }

        public ChainSpec ChainSpec => _dependencies.ChainSpec;
        public DisposableStack DisposeStack { get; }
        public IReadOnlyList<INethermindPlugin> Plugins => _dependencies.Plugins;
        public IList<IPublisher> Publishers { get; } = new List<IPublisher>(); // this should be called publishers
        public IProcessExitSource ProcessExit => _dependencies.ProcessExitSource;
        public CompositeTxGossipPolicy TxGossipPolicy { get; } = new();
        public ISimulateTransactionProcessorFactory SimulateTransactionProcessorFactory { get; set; } = Nethermind.Facade.Simulate.SimulateTransactionProcessorFactory.Instance;
        public ILifetimeScope Context => _dependencies.Context;
    }
}
