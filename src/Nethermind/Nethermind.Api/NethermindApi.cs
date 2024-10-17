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
using Nethermind.Blockchain.Find;
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
using Nethermind.Evm.TransactionProcessing;
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
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Nethermind.Sockets;
using Nethermind.Trie;
using Nethermind.Consensus.Processing.CensorshipDetector;
using Nethermind.Core.Container;
using Nethermind.Facade.Find;

namespace Nethermind.Api
{
    public class NethermindApi : INethermindApi
    {
        public NethermindApi(ILifetimeScope lifetimeScope)
        {
            BaseContainer = lifetimeScope;
        }

        public ILifetimeScope BaseContainer { get; set; }

        public IBlockchainBridge CreateBlockchainBridge()
        {
            ReadOnlyBlockTree readOnlyTree = BlockTree!.AsReadOnly();

            // TODO: reuse the same trie cache here
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = new(
                WorldStateManager!,
                readOnlyTree,
                SpecProvider,
                LogManager);

            SimulateReadOnlyBlocksProcessingEnvFactory simulateReadOnlyBlocksProcessingEnvFactory =
                new SimulateReadOnlyBlocksProcessingEnvFactory(
                    WorldStateManager!,
                    readOnlyTree,
                    DbProvider!,
                    SpecProvider!,
                    LogManager);

            IMiningConfig miningConfig = ConfigProvider.GetConfig<IMiningConfig>();
            IBlocksConfig blocksConfig = ConfigProvider.GetConfig<IBlocksConfig>();

            return new BlockchainBridge(
                readOnlyTxProcessingEnv,
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
        public IBlockchainProcessor? BlockchainProcessor { get; set; }
        public CompositeBlockPreprocessorStep BlockPreprocessor { get; } = new();
        public IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        public IBlockProcessor? MainBlockProcessor { get; set; }
        public IBlockProducer? BlockProducer { get; set; }
        public IBlockProducerRunner? BlockProducerRunner { get; set; }
        public IBlockTree? BlockTree { get; set; }
        public IBlockValidator? BlockValidator { get; set; }
        public IBloomStorage? BloomStorage { get; set; }
        public IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        public IConfigProvider ConfigProvider => BaseContainer.Resolve<IConfigProvider>();
        public ICryptoRandom CryptoRandom => BaseContainer.Resolve<ICryptoRandom>();
        public IDbProvider? DbProvider { get; set; }
        public IDbFactory? DbFactory { get; set; }

        public IDiscoveryApp? DiscoveryApp => BaseContainer.ResolveOptional<IDiscoveryApp>();
        public ISigner? EngineSigner { get; set; }
        public ISignerStore? EngineSignerStore { get; set; }
        public IEnode? Enode { get; set; }
        public IEthereumEcdsa? EthereumEcdsa { get; set; }
        public IFileSystem FileSystem { get; set; } = new FileSystem();
        public IFilterStore? FilterStore { get; set; }
        public IFilterManager? FilterManager { get; set; }
        public IUnclesValidator? UnclesValidator { get; set; }
        public IGrpcServer? GrpcServer { get; set; }
        public IHeaderValidator? HeaderValidator { get; set; }

        public IManualBlockProductionTrigger ManualBlockProductionTrigger { get; set; } =
            new BuildBlocksWhenRequested();

        public IIPResolver? IpResolver { get; set; }
        public IJsonSerializer EthereumJsonSerializer => BaseContainer.Resolve<IJsonSerializer>();
        public IKeyStore? KeyStore { get; set; }
        public IPasswordProvider? PasswordProvider { get; set; }
        public ILogFinder? LogFinder { get; set; }
        public ILogManager LogManager => BaseContainer.Resolve<ILogManager>();
        public IKeyValueStoreWithBatching? MainStateDbWithCache { get; set; }
        public IMessageSerializationService MessageSerializationService { get; } = new MessageSerializationService();
        public IGossipPolicy GossipPolicy { get; set; } = Policy.FullGossip;
        public IMonitoringService MonitoringService { get; set; } = NullMonitoringService.Instance;
        public INodeStatsManager? NodeStatsManager => BaseContainer.ResolveOptional<INodeStatsManager>();
        public IPeerManager? PeerManager => BaseContainer.ResolveOptional<IPeerManager>();
        public IPeerPool? PeerPool => BaseContainer.ResolveOptional<IPeerPool>();
        public IReceiptStorage? ReceiptStorage { get; set; }
        public IReceiptFinder? ReceiptFinder { get; set; }
        public IReceiptMonitor? ReceiptMonitor { get; set; }
        public IRewardCalculatorSource? RewardCalculatorSource { get; set; } = NoBlockRewards.Instance;
        public IRlpxHost? RlpxPeer => BaseContainer.ResolveOptional<IRlpxHost>();
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

        public ISessionMonitor? SessionMonitor => BaseContainer.ResolveOptional<ISessionMonitor>();
        public ISpecProvider? SpecProvider => BaseContainer.Resolve<ISpecProvider>();
        public IPoSSwitcher PoSSwitcher { get; set; } = NoPoS.Instance;
        public ISyncModeSelector SyncModeSelector => BaseContainer.ResolveOptional<ISyncModeSelector>()!;
        public IBetterPeerStrategy? BetterPeerStrategy => BaseContainer.ResolveOptional<IBetterPeerStrategy>();
        public ISyncPeerPool? SyncPeerPool => BaseContainer.ResolveOptional<ISyncPeerPool>();
        public IPeerDifficultyRefreshPool? PeerDifficultyRefreshPool => BaseContainer.ResolveOptional<IPeerDifficultyRefreshPool>();
        public ISynchronizer? Synchronizer => BaseContainer.ResolveOptional<ISynchronizer>();
        public ISyncServer? SyncServer => BaseContainer.ResolveOptional<ISyncServer>();

        [SkipServiceCollection]
        public IWorldState? WorldState { get; set; }
        public IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        public IWorldStateManager? WorldStateManager { get; set; }
        public IStateReader? StateReader { get; set; }
        public IStaticNodesManager? StaticNodesManager => BaseContainer.ResolveOptional<IStaticNodesManager>();
        public ITimestamper Timestamper { get; } = Core.Timestamper.Default;
        public ITimerFactory TimerFactory { get; } = Core.Timers.TimerFactory.Default;
        public ITransactionProcessor? TransactionProcessor { get; set; }
        public ITrieStore? TrieStore { get; set; }
        public ITxSender? TxSender { get; set; }
        public INonceManager? NonceManager { get; set; }
        public ITxPool? TxPool { get; set; }
        public ITxPoolInfoProvider? TxPoolInfoProvider { get; set; }
        public IHealthHintService? HealthHintService { get; set; }
        public IRpcCapabilitiesProvider? RpcCapabilitiesProvider { get; set; }
        public TxValidator? TxValidator { get; set; }
        public IBlockFinalizationManager? FinalizationManager { get; set; }
        public IGasLimitCalculator? GasLimitCalculator { get; set; }

        public IBlockProducerEnvFactory? BlockProducerEnvFactory { get; set; }
        public IBlockImprovementContextFactory? BlockImprovementContextFactory { get; set; }
        public IGasPriceOracle? GasPriceOracle { get; set; }

        public IEthSyncingInfo? EthSyncingInfo => BaseContainer.ResolveOptional<IEthSyncingInfo>();
        public IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        public INodeStorageFactory NodeStorageFactory { get; set; } = null!;
        public IBackgroundTaskScheduler BackgroundTaskScheduler { get; set; } = null!;
        public CensorshipDetector CensorshipDetector { get; set; } = null!;
        public IWallet? Wallet { get; set; }
        public IBlockStore? BadBlocksStore { get; set; }
        public ITransactionComparerProvider? TransactionComparerProvider { get; set; }
        public IWebSocketsManager WebSocketsManager { get; set; } = new WebSocketsManager();

        public ISubscriptionFactory? SubscriptionFactory { get; set; }
        public ProtectedPrivateKey? NodeKey { get; set; }

        /// <summary>
        /// Key used for signing blocks. Original as its loaded on startup. This can later be changed via RPC in <see cref="Signer"/>.
        /// </summary>
        public ProtectedPrivateKey? OriginalSignerKey { get; set; }

        public ChainSpec ChainSpec => BaseContainer.Resolve<ChainSpec>();
        public DisposableStack DisposeStack { get; } = new();
        public IReadOnlyList<INethermindPlugin> Plugins => BaseContainer.Resolve<IReadOnlyList<INethermindPlugin>>();
        public IList<IPublisher> Publishers { get; } = new List<IPublisher>(); // this should be called publishers
        public CompositePruningTrigger PruningTrigger { get; } = new();
        public IProcessExitSource? ProcessExit => BaseContainer.Resolve<IProcessExitSource>();
        public CompositeTxGossipPolicy TxGossipPolicy { get; } = new();
    }
}
