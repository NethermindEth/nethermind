// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using Nethermind.Abi;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
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
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.Blocks;

namespace Nethermind.Api
{
    public class NethermindApi : INethermindApi
    {
        public NethermindApi(IConfigProvider configProvider, IJsonSerializer jsonSerializer, ILogManager logManager, ChainSpec chainSpec)
        {
            ConfigProvider = configProvider;
            EthereumJsonSerializer = jsonSerializer;
            LogManager = logManager;
            ChainSpec = chainSpec;
            CryptoRandom = new CryptoRandom();
            DisposeStack.Push(CryptoRandom);
        }

        private IReadOnlyDbProvider? _readOnlyDbProvider;

        public IBlockchainBridge CreateBlockchainBridge()
        {
            ReadOnlyBlockTree readOnlyTree = BlockTree!.AsReadOnly();
            LazyInitializer.EnsureInitialized(ref _readOnlyDbProvider, () => new ReadOnlyDbProvider(DbProvider, false));

            // TODO: reuse the same trie cache here
            ReadOnlyTxProcessingEnv readOnlyTxProcessingEnv = new(
                _readOnlyDbProvider,
                ReadOnlyTrieStore,
                readOnlyTree,
                SpecProvider,
                LogManager);

            IMiningConfig miningConfig = ConfigProvider.GetConfig<IMiningConfig>();
            IBlocksConfig blocksConfig = ConfigProvider.GetConfig<IBlocksConfig>();

            return new BlockchainBridge(
                readOnlyTxProcessingEnv,
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
        public IBlockchainProcessor? BlockchainProcessor { get; set; }
        public CompositeBlockPreprocessorStep BlockPreprocessor { get; } = new();
        public IBlockProcessingQueue? BlockProcessingQueue { get; set; }
        public IBlockProcessor? MainBlockProcessor { get; set; }
        public IBlockProducer? BlockProducer { get; set; }
        public IBlockTree? BlockTree { get; set; }
        public IBlockValidator? BlockValidator { get; set; }
        public IBloomStorage? BloomStorage { get; set; }
        public IChainLevelInfoRepository? ChainLevelInfoRepository { get; set; }
        public IConfigProvider ConfigProvider { get; set; }
        public ICryptoRandom CryptoRandom { get; }
        public IDbProvider? DbProvider { get; set; }
        public IRocksDbFactory? RocksDbFactory { get; set; }
        public IMemDbFactory? MemDbFactory { get; set; }
        public IDisconnectsAnalyzer? DisconnectsAnalyzer { get; set; }
        public IDiscoveryApp? DiscoveryApp { get; set; }
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
        public IJsonSerializer EthereumJsonSerializer { get; set; }
        public IKeyStore? KeyStore { get; set; }
        public IPasswordProvider? PasswordProvider { get; set; }
        public ILogFinder? LogFinder { get; set; }
        public ILogManager LogManager { get; set; }
        public IKeyValueStoreWithBatching? MainStateDbWithCache { get; set; }
        public IMessageSerializationService MessageSerializationService { get; } = new MessageSerializationService();
        public IGossipPolicy GossipPolicy { get; set; } = Policy.FullGossip;
        public IMonitoringService MonitoringService { get; set; } = NullMonitoringService.Instance;
        public INodeStatsManager? NodeStatsManager { get; set; }
        public IPeerManager? PeerManager { get; set; }
        public IPeerPool? PeerPool { get; set; }
        public IProtocolsManager? ProtocolsManager { get; set; }
        public IProtocolValidator? ProtocolValidator { get; set; }
        public IReceiptStorage? ReceiptStorage { get; set; }
        public IWitnessCollector? WitnessCollector { get; set; }
        public IWitnessRepository? WitnessRepository { get; set; }
        public IReceiptFinder? ReceiptFinder { get; set; }
        public IReceiptMonitor? ReceiptMonitor { get; set; }
        public IRewardCalculatorSource? RewardCalculatorSource { get; set; } = NoBlockRewards.Instance;
        public IRlpxHost? RlpxPeer { get; set; }
        public IRpcModuleProvider? RpcModuleProvider { get; set; } = NullModuleProvider.Instance;
        public IRpcAuthentication? RpcAuthentication { get; set; }
        public IJsonRpcLocalStats? JsonRpcLocalStats { get; set; }
        public ISealer? Sealer { get; set; } = NullSealEngine.Instance;
        public string SealEngineType { get; set; } = Nethermind.Core.SealEngineType.None;
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
        public ISpecProvider? SpecProvider { get; set; }
        public IPoSSwitcher PoSSwitcher { get; set; } = NoPoS.Instance;
        public ISyncModeSelector? SyncModeSelector { get; set; }

        public ISyncProgressResolver? SyncProgressResolver { get; set; }
        public IBetterPeerStrategy? BetterPeerStrategy { get; set; }
        public IBlockDownloaderFactory? BlockDownloaderFactory { get; set; }
        public IPivot? Pivot { get; set; }
        public ISyncPeerPool? SyncPeerPool { get; set; }
        public IPeerDifficultyRefreshPool? PeerDifficultyRefreshPool { get; set; }
        public ISynchronizer? Synchronizer { get; set; }
        public ISyncServer? SyncServer { get; set; }
        public IWorldState? WorldState { get; set; }
        public IReadOnlyStateProvider? ChainHeadStateProvider { get; set; }
        public IStateReader? StateReader { get; set; }
        public IStaticNodesManager? StaticNodesManager { get; set; }
        public ITimestamper Timestamper { get; } = Core.Timestamper.Default;
        public ITimerFactory TimerFactory { get; } = Core.Timers.TimerFactory.Default;
        public ITransactionProcessor? TransactionProcessor { get; set; }
        public ITrieStore? TrieStore { get; set; }
        public IReadOnlyTrieStore? ReadOnlyTrieStore { get; set; }
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
        public IGasPriceOracle? GasPriceOracle { get; set; }

        public IEthSyncingInfo? EthSyncingInfo { get; set; }
        public IBlockProductionPolicy? BlockProductionPolicy { get; set; }
        public IWallet? Wallet { get; set; }
        public ITransactionComparerProvider? TransactionComparerProvider { get; set; }
        public IWebSocketsManager WebSocketsManager { get; set; } = new WebSocketsManager();

        public ISubscriptionFactory? SubscriptionFactory { get; set; }
        public ProtectedPrivateKey? NodeKey { get; set; }

        /// <summary>
        /// Key used for signing blocks. Original as its loaded on startup. This can later be changed via RPC in <see cref="Signer"/>.
        /// </summary>
        public ProtectedPrivateKey? OriginalSignerKey { get; set; }

        public ChainSpec ChainSpec { get; set; }
        public DisposableStack DisposeStack { get; } = new();
        public IReadOnlyList<INethermindPlugin> Plugins { get; } = new List<INethermindPlugin>();
        public IList<IPublisher> Publishers { get; } = new List<IPublisher>(); // this should be called publishers
        public CompositePruningTrigger PruningTrigger { get; } = new();
        public ISnapProvider? SnapProvider { get; set; }
        public IProcessExitSource? ProcessExit { get; set; }
    }
}
