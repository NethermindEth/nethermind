/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.AuRa;
using Nethermind.AuRa.Rewards;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.TxPools.Storages;
using Nethermind.Blockchain.Validators;
using Nethermind.Clique;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Json;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Core.Specs.GenesisFileStyle;
using Nethermind.DataMarketplace.Channels;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.DataMarketplace.Subprotocols.Serializers;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.EthStats;
using Nethermind.EthStats.Clients;
using Nethermind.EthStats.Integrations;
using Nethermind.EthStats.Senders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Grpc;
using Nethermind.Grpc.Producers;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Personal;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.TxPool;
using Nethermind.KeyStore;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Crypto;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.StaticNodes;
using Nethermind.PubSub;
using Nethermind.PubSub.Kafka;
using Nethermind.PubSub.Kafka.Avro;
using Nethermind.Runner.Config;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using Nethermind.Store.Rpc;
using Nethermind.Wallet;
using Nethermind.WebSockets;
using Block = Nethermind.Core.Block;
using ISyncConfig = Nethermind.Blockchain.ISyncConfig;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IRunner
    {
        private readonly Stack<IDisposable> _disposeStack = new Stack<IDisposable>();

        private static readonly bool HiveEnabled =
            Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";

        private readonly IGrpcServer _grpcServer;
        private static ILogManager _logManager;
        private readonly INdmConsumerChannelManager _ndmConsumerChannelManager;
        private readonly INdmDataPublisher _ndmDataPublisher;
        private readonly INdmInitializer _ndmInitializer;
        private readonly IWebSocketsManager _webSocketsManager;
        private static ILogger _logger;

        private IRpcModuleProvider _rpcModuleProvider;
        private IConfigProvider _configProvider;
        private ITxPoolConfig _txPoolConfig;
        private IInitConfig _initConfig;
        private IIpResolver _ipResolver;
        private PrivateKey _nodeKey;
        private ChainSpec _chainSpec;
        private ICryptoRandom _cryptoRandom = new CryptoRandom();
        private IJsonSerializer _jsonSerializer = new UnforgivingJsonSerializer();
        private IJsonSerializer _ethereumJsonSerializer;
        private CancellationTokenSource _runnerCancellation;
        private IBlockchainProcessor _blockchainProcessor;
        private IDiscoveryApp _discoveryApp;
        private IMessageSerializationService _messageSerializationService = new MessageSerializationService();
        private INodeStatsManager _nodeStatsManager;
        private IPerfService _perfService;
        private ITxPool _txPool;
        private IReceiptStorage _receiptStorage;
        private IEthereumEcdsa _ethereumEcdsa;
        private IEthSyncPeerPool _syncPeerPool;
        private ISyncReport _syncReport;
        private ISynchronizer _synchronizer;
        private ISyncServer _syncServer;
        private IKeyStore _keyStore;
        private IPeerManager _peerManager;
        private IProtocolsManager _protocolsManager;
        private IBlockTree _blockTree;
        private IBlockValidator _blockValidator;
        private IHeaderValidator _headerValidator;
        private IBlockDataRecoveryStep _recoveryStep;
        private IBlockProcessor _blockProcessor;
        private IRewardCalculator _rewardCalculator;
        private ISpecProvider _specProvider;
        private IStateProvider _stateProvider;
        private ISealer _sealer;
        private ISealValidator _sealValidator;
        private IBlockProducer _blockProducer;
        private ISnapshotManager _snapshotManager;
        private IRlpxPeer _rlpxPeer;
        private IDbProvider _dbProvider;
        private readonly ITimestamper _timestamper = new Timestamper();
        private IStorageProvider _storageProvider;
        private IWallet _wallet;
        private IEnode _enode;
        private HiveRunner _hiveRunner;
        private ISessionMonitor _sessionMonitor;
        private ISyncConfig _syncConfig;
        private IStaticNodesManager _staticNodesManager;
        private ITransactionProcessor _transactionProcessor;
        private ITxPoolInfoProvider _txPoolInfoProvider;
        private INetworkConfig _networkConfig;
        private ChainLevelInfoRepository _chainLevelInfoRepository;
        private IBlockFinalizationManager _finalizationManager;
        public const string DiscoveryNodesDbPath = "discoveryNodes";
        public const string PeersDbPath = "peers";

        public EthereumRunner(IRpcModuleProvider rpcModuleProvider, IConfigProvider configurationProvider,
            ILogManager logManager, IGrpcServer grpcServer, 
            INdmConsumerChannelManager ndmConsumerChannelManager, INdmDataPublisher ndmDataPublisher,
            INdmInitializer ndmInitializer, IWebSocketsManager webSocketsManager,
            IJsonSerializer ethereumJsonSerializer)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _grpcServer = grpcServer;
            _ndmConsumerChannelManager = ndmConsumerChannelManager;
            _ndmDataPublisher = ndmDataPublisher;
            _ndmInitializer = ndmInitializer;
            _webSocketsManager = webSocketsManager;
            _ethereumJsonSerializer = ethereumJsonSerializer;
            _logger = _logManager.GetClassLogger();

            _configProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _rpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _txPoolConfig = configurationProvider.GetConfig<ITxPoolConfig>();
            _perfService = new PerfService(_logManager);
            
            _networkConfig = _configProvider.GetConfig<INetworkConfig>();
            _ipResolver = new IpResolver(_networkConfig, _logManager);
            _networkConfig.ExternalIp = _ipResolver.ExternalIp.ToString();
            _networkConfig.LocalIp = _ipResolver.LocalIp.ToString();
        }

        public async Task Start()
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");
            _runnerCancellation = new CancellationTokenSource();

            SetupKeyStore();
            LoadChainSpec();
            InitRlp();
            UpdateDiscoveryConfig();
            await InitBlockchain();
            RegisterJsonRpcModules();
            InitEthStats();

            if (_logger.IsDebug) _logger.Debug("Ethereum initialization completed");
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        private void InitRlp()
        {
            Rlp.RegisterDecoders(Assembly.GetAssembly(typeof(ParityTraceDecoder)));
            Rlp.RegisterDecoders(Assembly.GetAssembly(typeof(NetworkNodeDecoder)));
            if (_chainSpec.SealEngineType == SealEngineType.AuRa)
            {
                Rlp.Decoders[typeof(BlockInfo)] = new BlockInfoDecoder(true);
            }
        }

        private void SetupKeyStore()
        {
            var encrypter = new AesEncrypter(
                _configProvider.GetConfig<IKeyStoreConfig>(),
                _logManager);

            _keyStore = new FileKeyStore(
                _configProvider.GetConfig<IKeyStoreConfig>(),
                _ethereumJsonSerializer,
                encrypter,
                _cryptoRandom,
                _logManager);

            switch (_initConfig)
            {
                case var _ when HiveEnabled:
                    // todo: use the keystore wallet here
                    _wallet = new HiveWallet();
                    break;
                case var config when config.EnableUnsecuredDevWallet && config.KeepDevWalletInMemory:
                    _wallet = new DevWallet(_configProvider.GetConfig<IWalletConfig>(), _logManager);
                    break;
                case var config when config.EnableUnsecuredDevWallet && !config.KeepDevWalletInMemory:
                    _wallet = new DevKeyStoreWallet(_keyStore, _logManager);
                    break;
                default:
                    _wallet = new NullWallet();
                    break;
            }

            INodeKeyManager nodeKeyManager = new NodeKeyManager(_cryptoRandom, _keyStore, _configProvider.GetConfig<IKeyStoreConfig>(), _logManager);
            _nodeKey = nodeKeyManager.LoadNodeKey();
            _enode = new Enode(_nodeKey.PublicKey, IPAddress.Parse(_networkConfig.ExternalIp), _networkConfig.P2PPort);
        }

        private void RegisterJsonRpcModules()
        {
            IJsonRpcConfig jsonRpcConfig = _configProvider.GetConfig<IJsonRpcConfig>();
            if (!jsonRpcConfig.Enabled)
            {
                return;
            }

            // the following line needs to be called in order to make sure that the CLI library is referenced from runner and built alongside
            if (_logger.IsDebug) _logger.Debug($"Resolving CLI ({nameof(Cli.CliModuleLoader)})");

            var ndmConfig = _configProvider.GetConfig<INdmConfig>();
            if (ndmConfig.Enabled && !(_ndmInitializer is null) && ndmConfig.ProxyEnabled)
            {
                if (_logger.IsInfo) _logger.Info("Enabled JSON RPC Proxy for NDM.");
                var proxyFactory = new EthModuleProxyFactory(ndmConfig.JsonRpcUrlProxies, _ethereumJsonSerializer,
                    _logManager);
                _rpcModuleProvider.Register(new SingletonModulePool<IEthModule>(proxyFactory, true));
            }
            else
            {
                EthModuleFactory ethModuleFactory = new EthModuleFactory(_dbProvider, _txPool, _wallet, _blockTree,
                    _ethereumEcdsa, _blockProcessor, _receiptStorage, _specProvider, _logManager);
                _rpcModuleProvider.Register(new BoundedModulePool<IEthModule>(8, ethModuleFactory));
            }

            DebugModuleFactory debugModuleFactory = new DebugModuleFactory(_dbProvider, _blockTree, _blockValidator, _recoveryStep, _rewardCalculator, _receiptStorage, _configProvider, _specProvider, _logManager);
            _rpcModuleProvider.Register(new BoundedModulePool<IDebugModule>(8, debugModuleFactory));
            
            TraceModuleFactory traceModuleFactory = new TraceModuleFactory(_dbProvider, _txPool, _blockTree, _blockValidator, _ethereumEcdsa, _recoveryStep, _rewardCalculator, _receiptStorage, _specProvider, _logManager);
            _rpcModuleProvider.Register(new BoundedModulePool<ITraceModule>(8, traceModuleFactory));

            if (_sealValidator is CliqueSealValidator)
            {
                CliqueModule cliqueModule = new CliqueModule(_logManager, new CliqueBridge(_blockProducer as ICliqueBlockProducer, _snapshotManager, _blockTree));
                _rpcModuleProvider.Register(new SingletonModulePool<ICliqueModule>(cliqueModule, true));
            }

            if (_initConfig.EnableUnsecuredDevWallet)
            {
                PersonalBridge personalBridge = new PersonalBridge(_ethereumEcdsa, _wallet);
                PersonalModule personalModule = new PersonalModule(personalBridge, _logManager);
                _rpcModuleProvider.Register(new SingletonModulePool<IPersonalModule>(personalModule, true));
            }

            AdminModule adminModule = new AdminModule(_logManager, _peerManager, _staticNodesManager);
            _rpcModuleProvider.Register(new SingletonModulePool<IAdminModule>(adminModule, true));
            
            TxPoolModule txPoolModule = new TxPoolModule(_logManager, _txPoolInfoProvider);
            _rpcModuleProvider.Register(new SingletonModulePool<ITxPoolModule>(txPoolModule, true));

            NetModule netModule = new NetModule(_logManager, new NetBridge(_enode, _syncServer, _peerManager));
            _rpcModuleProvider.Register(new SingletonModulePool<INetModule>(netModule, true));

            ParityModule parityModule = new ParityModule(_ethereumEcdsa, _txPool, _blockTree, _receiptStorage, _logManager);
            _rpcModuleProvider.Register(new SingletonModulePool<IParityModule>(parityModule, true));
        }

        private void UpdateDiscoveryConfig()
        {
            var discoveryConfig = _configProvider.GetConfig<IDiscoveryConfig>();
            if (discoveryConfig.Bootnodes != string.Empty)
            {
                if (_chainSpec.Bootnodes.Length != 0)
                {
                    discoveryConfig.Bootnodes += "," + string.Join(",", _chainSpec.Bootnodes.Select(bn => bn.ToString()));
                }
            }
            else
            {
                discoveryConfig.Bootnodes = string.Join(",", _chainSpec.Bootnodes.Select(bn => bn.ToString()));
            }
        }

        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Shutting down...");
            _runnerCancellation.Cancel();

            if (_logger.IsInfo) _logger.Info("Stopping rlpx peer...");
            var rlpxPeerTask = _rlpxPeer?.Shutdown() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping sesison monitor...");
            _sessionMonitor?.Stop();

            if (_logger.IsInfo) _logger.Info("Stopping peer manager...");
            var peerManagerTask = _peerManager?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping synchronizer...");
            var synchronizerTask = (_synchronizer?.StopAsync() ?? Task.CompletedTask)
                .ContinueWith(t => _synchronizer?.Dispose());

            if (_logger.IsInfo) _logger.Info("Stopping sync peer pool...");
            var peerPoolTask = _syncPeerPool?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping block producer...");
            var blockProducerTask = _blockProducer?.StopAsync() ?? Task.CompletedTask;

            if (_logger.IsInfo) _logger.Info("Stopping blockchain processor...");
            var blockchainProcessorTask = (_blockchainProcessor?.StopAsync() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping discovery app...");
            var discoveryStopTask = _discoveryApp?.StopAsync() ?? Task.CompletedTask;

            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, synchronizerTask, peerPoolTask, blockchainProcessorTask, blockProducerTask);

            if (_logger.IsInfo) _logger.Info("Closing DBs...");
            _dbProvider.Dispose();
            if (_logger.IsInfo) _logger.Info("All DBs closed.");
            
            while (_disposeStack.Count != 0)
            {
                var disposable = _disposeStack.Pop();
                if (_logger.IsDebug) _logger.Debug($"Disposing {disposable.GetType().Name}");
            }

            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private void LoadChainSpec()
        {
            if (_logger.IsInfo) _logger.Info($"Loading chain spec from {_initConfig.ChainSpecPath}");

            IChainSpecLoader loader = string.Equals(_initConfig.ChainSpecFormat, "ChainSpec", StringComparison.InvariantCultureIgnoreCase)
                ? (IChainSpecLoader) new ChainSpecLoader(_ethereumJsonSerializer)
                : new GenesisFileLoader(_ethereumJsonSerializer);

            if (HiveEnabled)
            {
                if (_logger.IsInfo) _logger.Info($"HIVE chainspec:{Environment.NewLine}{File.ReadAllText(_initConfig.ChainSpecPath)}");
            }

            _chainSpec = loader.LoadFromFile(_initConfig.ChainSpecPath);
            _chainSpec.Bootnodes = _chainSpec.Bootnodes?.Where(n => !n.NodeId?.Equals(_nodeKey.PublicKey) ?? false).ToArray() ?? new NetworkNode[0];
        }

        [Todo(Improve.Refactor, "Use chain spec for all chain configuration")]
        private async Task InitBlockchain()
        {
            _specProvider = new ChainSpecBasedSpecProvider(_chainSpec);

            Account.AccountStartNonce = _chainSpec.Parameters.AccountStartNonce;

            /* sync */
            IDbConfig dbConfig = _configProvider.GetConfig<IDbConfig>();
            _syncConfig = _configProvider.GetConfig<ISyncConfig>();

            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                if (_logger.IsDebug) _logger.Debug($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            _dbProvider = HiveEnabled
                ? (IDbProvider) new MemDbProvider()
                : new RocksDbProvider(_initConfig.BaseDbPath, dbConfig, _logManager, _initConfig.StoreTraces, _initConfig.StoreReceipts || _syncConfig.DownloadReceiptsInFastSync);
            
            // IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_initConfig.BaseDbPath, "debug"), dbConfig, _logManager, _initConfig.StoreTraces, _initConfig.StoreReceipts);
            // _dbProvider = new RpcDbProvider(_jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.Localhost, _jsonSerializer, _logManager), _logManager, debugRecorder);

            // IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_initConfig.BaseDbPath, "debug"), dbConfig, _logManager, _initConfig.StoreTraces, _initConfig.StoreReceipts), false);
            // _dbProvider = debugReader;

            _stateProvider = new StateProvider(
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                _logManager);
            
            _ethereumEcdsa = new EthereumEcdsa(_specProvider, _logManager);
            _txPool = new TxPool(
                new PersistentTxStorage(_dbProvider.PendingTxsDb, _specProvider),
                Timestamper.Default,
                _ethereumEcdsa,
                _specProvider,
                _txPoolConfig, _stateProvider, _logManager);
            var _rc7FixDb = _initConfig.EnableRc7Fix ? _dbProvider.HeadersDb : NullDb.Instance;
            _receiptStorage = new PersistentReceiptStorage(_dbProvider.ReceiptsDb, _rc7FixDb, _specProvider, _logManager);

            _chainLevelInfoRepository = new ChainLevelInfoRepository(_dbProvider.BlockInfosDb);
            
            _blockTree = new BlockTree(
                _dbProvider.BlocksDb,
                _dbProvider.HeadersDb,
                _dbProvider.BlockInfosDb,
                _chainLevelInfoRepository, 
                _specProvider,
                _txPool,
                _syncConfig,
                _logManager);

            // Init state if we need system calls before actual processing starts
            if (_blockTree.Head != null)
            {
                _stateProvider.StateRoot = _blockTree.Head.StateRoot;
            }

            _recoveryStep = new TxSignaturesRecoveryStep(_ethereumEcdsa, _txPool, _logManager);
            
            _snapshotManager = null;            

            
            _storageProvider = new StorageProvider(
                _dbProvider.StateDb,
                _stateProvider,
                _logManager);
            
            IList<IAdditionalBlockProcessor> blockPreProcessors = new List<IAdditionalBlockProcessor>();
            // blockchain processing
            var blockhashProvider = new BlockhashProvider(
                _blockTree, _logManager);

            var virtualMachine = new VirtualMachine(
                _stateProvider,
                _storageProvider,
                blockhashProvider,
                _specProvider,
                _logManager);

            _transactionProcessor = new TransactionProcessor(
                _specProvider,
                _stateProvider,
                _storageProvider,
                virtualMachine,
                _logManager);
            
            InitSealEngine(blockPreProcessors);

            /* validation */
            _headerValidator = new HeaderValidator(
                _blockTree,
                _sealValidator,
                _specProvider,
                _logManager);

            var ommersValidator = new OmmersValidator(
                _blockTree,
                _headerValidator,
                _logManager);

            var txValidator = new TxValidator(_specProvider.ChainId);

            _blockValidator = new BlockValidator(
                txValidator,
                _headerValidator,
                ommersValidator,
                _specProvider,
                _logManager);

            _txPoolInfoProvider = new TxPoolInfoProvider(_stateProvider, _txPool);

            _blockProcessor = new BlockProcessor(
                _specProvider,
                _blockValidator,
                _rewardCalculator,
                _transactionProcessor,
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                _dbProvider.TraceDb,
                _stateProvider,
                _storageProvider,
                _txPool,
                _receiptStorage,
                _logManager,
                blockPreProcessors);

            _blockchainProcessor = new BlockchainProcessor(
                _blockTree,
                _blockProcessor,
                _recoveryStep,
                _logManager,
                _initConfig.StoreReceipts,
                _initConfig.StoreTraces);

            _finalizationManager = InitFinalizationManager(blockPreProcessors);

            // create shared objects between discovery and peer manager
            IStatsConfig statsConfig = _configProvider.GetConfig<IStatsConfig>();
            _nodeStatsManager = new NodeStatsManager(statsConfig, _logManager);

            InitBlockProducers();

            _blockchainProcessor.Start();
            LoadGenesisBlock(string.IsNullOrWhiteSpace(_initConfig.GenesisHash) ? null : new Keccak(_initConfig.GenesisHash));
            if (_initConfig.ProcessingEnabled)
            {
#pragma warning disable 4014
                RunBlockTreeInitTasks();
#pragma warning restore 4014
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Shutting down the blockchain processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                await _blockchainProcessor.StopAsync();
            }

            if (HiveEnabled)
            {
                await InitHive();
            }

            var producers = new List<IProducer>();
            
            var kafkaConfig = _configProvider.GetConfig<IKafkaConfig>();
            if (kafkaConfig.Enabled)
            {
                var kafkaProducer = await PrepareKafkaProducer(_blockTree, _configProvider.GetConfig<IKafkaConfig>());
                producers.Add(kafkaProducer);
            }

            var grpcConfig = _configProvider.GetConfig<IGrpcConfig>();
            if (grpcConfig.Enabled && grpcConfig.ProducerEnabled)
            {
                var grpcProducer = new GrpcProducer(_grpcServer);
                producers.Add(grpcProducer);
            }

            ISubscription subscription;
            if (producers.Any())
            {
                subscription = new Subscription(producers, _blockProcessor, _logManager);
            }
            else
            {
                subscription = new EmptySubscription();
            }

            _disposeStack.Push(subscription);

            await InitializeNetwork();
        }

        private IBlockFinalizationManager InitFinalizationManager(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_chainSpec.SealEngineType)
            {
                case SealEngineType.AuRa:
                    return new AuRaBlockFinalizationManager(_blockTree, _chainLevelInfoRepository, _blockProcessor, 
                            blockPreProcessors.OfType<IAuRaValidator>().First(), _logManager);
                default:
                    return null;
            }
        }

        private void InitBlockProducers()
        {
            if (_initConfig.IsMining)
            {
                IReadOnlyDbProvider minerDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
                ReadOnlyBlockTree readOnlyBlockTree = new ReadOnlyBlockTree(_blockTree);
                ReadOnlyChain producerChain = new ReadOnlyChain(readOnlyBlockTree, _blockValidator, _rewardCalculator,
                    _specProvider, minerDbProvider, _recoveryStep, _logManager, _txPool, _receiptStorage);

                switch (_chainSpec.SealEngineType)
                {
                    case SealEngineType.Clique:
                    {
                        if (_logger.IsWarn) _logger.Warn("Starting Clique block producer & sealer");
                        CliqueConfig cliqueConfig = new CliqueConfig();
                        cliqueConfig.BlockPeriod = _chainSpec.Clique.Period;
                        cliqueConfig.Epoch = _chainSpec.Clique.Epoch;
                        _blockProducer = new CliqueBlockProducer(_txPool, producerChain.Processor,
                            _blockTree, _timestamper, _cryptoRandom, producerChain.ReadOnlyStateProvider, _snapshotManager, (CliqueSealer) _sealer, _nodeKey.Address, cliqueConfig, _logManager);
                        break;
                    }

                    case SealEngineType.NethDev:
                    {
                        if (_logger.IsWarn) _logger.Warn("Starting Dev block producer & sealer");
                        _blockProducer = new DevBlockProducer(_txPool, producerChain.Processor, _blockTree,
                            _timestamper, _logManager);
                        break;
                    }

                    default:
                        throw new NotSupportedException($"Mining in {_chainSpec.SealEngineType} mode is not supported");
                }

                _blockProducer.Start();
            }
        }

        private void InitSealEngine(IList<IAdditionalBlockProcessor> blockPreProcessors)
        {
            switch (_chainSpec.SealEngineType)
            {
                case SealEngineType.None:
                    _sealer = NullSealEngine.Instance;
                    _sealValidator = NullSealEngine.Instance;
                    _rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Clique:
                    _rewardCalculator = NoBlockRewards.Instance;
                    CliqueConfig cliqueConfig = new CliqueConfig();
                    cliqueConfig.BlockPeriod = _chainSpec.Clique.Period;
                    cliqueConfig.Epoch = _chainSpec.Clique.Epoch;
                    _snapshotManager = new SnapshotManager(cliqueConfig, _dbProvider.BlocksDb, _blockTree, _ethereumEcdsa, _logManager);
                    _sealValidator = new CliqueSealValidator(cliqueConfig, _snapshotManager, _logManager);
                    _recoveryStep = new CompositeDataRecoveryStep(_recoveryStep, new AuthorRecoveryStep(_snapshotManager));
                    if (_initConfig.IsMining)
                    {
                        _sealer = new CliqueSealer(new BasicWallet(_nodeKey), cliqueConfig, _snapshotManager, _nodeKey.Address, _logManager);
                    }
                    else
                    {
                        _sealer = NullSealEngine.Instance;
                    }

                    break;
                case SealEngineType.NethDev:
                    _sealer = NullSealEngine.Instance;
                    _sealValidator = NullSealEngine.Instance;
                    _rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Ethash:
                    _rewardCalculator = new RewardCalculator(_specProvider);
                    var difficultyCalculator = new DifficultyCalculator(_specProvider);
                    if (_initConfig.IsMining)
                    {
                        _sealer = new EthashSealer(new Ethash(_logManager), _logManager);
                    }
                    else
                    {
                        _sealer = NullSealEngine.Instance;
                    }

                    _sealValidator = new EthashSealValidator(_logManager, difficultyCalculator, new Ethash(_logManager));
                    break;
                case SealEngineType.AuRa:
                    var abiEncoder = new AbiEncoder();
                    var validatorProcessor = new AuRaAdditionalBlockProcessorFactory(_dbProvider.StateDb, _stateProvider, abiEncoder, _transactionProcessor, _blockTree, _logManager)
                        .CreateValidatorProcessor(_chainSpec.AuRa.Validators);
                        
                    _sealer = new AuRaSealer();
                    _sealValidator = new AuRaSealValidator(validatorProcessor, _ethereumEcdsa, _logManager);
                    _rewardCalculator = new AuRaRewardCalculator(_chainSpec.AuRa, abiEncoder, _transactionProcessor);
                    blockPreProcessors.Add(validatorProcessor);
                    break;
                default:
                    throw new NotSupportedException($"Seal engine type {_chainSpec.SealEngineType} is not supported in Nethermind");
            }
        }

        private async Task RunBlockTreeInitTasks()
        {
            if (!_initConfig.SynchronizationEnabled)
            {
                return;
            }

            if (!_syncConfig.FastSync)
            {
                await _blockTree.LoadBlocksFromDb(_runnerCancellation.Token, null).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Loading blocks from the DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsWarn) _logger.Warn("Loading blocks from the DB canceled.");
                    }
                });
            }
            else
            {
                await _blockTree.FixFastSyncGaps(_runnerCancellation.Token).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error("Fixing gaps in DB failed.", t.Exception);
                    }
                    else if (t.IsCanceled)
                    {
                        if (_logger.IsWarn) _logger.Warn("Fixing gaps in DB canceled.");
                    }
                });
            }
        }

        private async Task InitializeNetwork()
        {
            var maxPeersCount = _networkConfig.ActivePeersMaxCount;
            _syncPeerPool = new EthSyncPeerPool(_blockTree, _nodeStatsManager, _syncConfig, maxPeersCount, _logManager);
            NodeDataFeed feed = new NodeDataFeed(_dbProvider.CodeDb, _dbProvider.StateDb, _logManager);
            NodeDataDownloader nodeDataDownloader = new NodeDataDownloader(_syncPeerPool, feed, _logManager);
            _syncReport = new SyncReport(_syncPeerPool, _nodeStatsManager, _syncConfig, _logManager);
            _synchronizer = new Synchronizer(_specProvider, _blockTree, _receiptStorage, _blockValidator, _sealValidator, _syncPeerPool, _syncConfig, nodeDataDownloader, _syncReport, _logManager);

            _syncServer = new SyncServer(
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                _blockTree,
                _receiptStorage,
                _sealValidator,
                _syncPeerPool,
                _synchronizer,
                _syncConfig,
                _logManager);

            InitDiscovery();
            await InitPeer().ContinueWith(initPeerTask =>
            {
                if (initPeerTask.IsFaulted)
                {
                    _logger.Error("Unable to init the peer manager.", initPeerTask.Exception);
                }
            });

            await StartSync().ContinueWith(initNetTask =>
            {
                if (initNetTask.IsFaulted)
                {
                    _logger.Error("Unable to start the synchronizer.", initNetTask.Exception);
                }
            });

            await StartDiscovery().ContinueWith(initDiscoveryTask =>
            {
                if (initDiscoveryTask.IsFaulted)
                {
                    _logger.Error("Unable to start the discovery protocol.", initDiscoveryTask.Exception);
                }
            });

            try
            {
                StartPeer();
            }
            catch (Exception e)
            {
                _logger.Error("Unable to start the peer manager.", e);
            }

            if (_logger.IsInfo) _logger.Info($"Ethereum     : tcp://{_enode.HostIp}:{_enode.Port}");
            if (_logger.IsInfo) _logger.Info($"Version      : {ClientVersion.Description}");
            if (_logger.IsInfo) _logger.Info($"This node    : {_enode.Info}");
            if (_logger.IsInfo) _logger.Info($"Node address : {_enode.Address} (do not use as an account)");
        }

        private void LoadGenesisBlock(Keccak expectedGenesisHash)
        {
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (_blockTree.Genesis != null)
            {
                ValidateGenesisHash(expectedGenesisHash);
                return;
            }

            Block genesis = _chainSpec.Genesis;
            CreateSystemAccounts();
            
            foreach ((Address address, ChainSpecAllocation allocation) in _chainSpec.Allocations)
            {
                _stateProvider.CreateAccount(address, allocation.Balance);
                if (allocation.Code != null)
                {
                    Keccak codeHash = _stateProvider.UpdateCode(allocation.Code);
                    _stateProvider.UpdateCodeHash(address, codeHash, _specProvider.GenesisSpec);
                }

                if (allocation.Constructor != null)
                {
                    Transaction constructorTransaction = new Transaction(true)
                    {
                        SenderAddress = address,
                        Init = allocation.Constructor,
                        GasLimit = genesis.GasLimit
                    };
                    _transactionProcessor.Execute(constructorTransaction, genesis.Header, NullTxTracer.Instance);
                }
            }

            _storageProvider.Commit();
            _stateProvider.Commit(_specProvider.GenesisSpec);

            _storageProvider.CommitTrees();
            _stateProvider.CommitTree();
            
            _dbProvider.StateDb.Commit();
            _dbProvider.CodeDb.Commit();

            genesis.StateRoot = _stateProvider.StateRoot;
            genesis.Hash = BlockHeader.CalculateHash(genesis.Header);

            ManualResetEventSlim genesisProcessedEvent = new ManualResetEventSlim(false);

            bool genesisLoaded = false;

            void GenesisProcessed(object sender, BlockEventArgs args)
            {
                genesisLoaded = true;
                _blockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            _blockTree.NewHeadBlock += GenesisProcessed;
            _blockTree.SuggestBlock(genesis);
            genesisProcessedEvent.Wait(TimeSpan.FromSeconds(40));
            if (!genesisLoaded)
            {
                throw new BlockchainException("Genesis block processing failure");
            }
            
            ValidateGenesisHash(expectedGenesisHash);
        }

        private void CreateSystemAccounts()
        {
            var isAura = _chainSpec.SealEngineType == SealEngineType.AuRa;
            var hasConstructorAllocation = _chainSpec.Allocations.Values.Any(a => a.Constructor != null);
            if (isAura && hasConstructorAllocation)
            {
                _stateProvider.CreateAccount(Address.Zero, UInt256.Zero);
                _storageProvider.Commit();
                _stateProvider.Commit(Homestead.Instance);
            }
        }

        /// <summary>
        /// If <paramref name="expectedGenesisHash"/> is <value>null</value> then it means that we do not care about the genesis hash (e.g. in some quick testing of private chains)/>
        /// </summary>
        /// <param name="expectedGenesisHash"></param>
        private void ValidateGenesisHash(Keccak expectedGenesisHash)
        {
            if (expectedGenesisHash != null && _blockTree.Genesis.Hash != expectedGenesisHash)
            {
                if(_logger.IsWarn) _logger.Warn(_stateProvider.DumpState());
                if(_logger.IsWarn) _logger.Warn(_blockTree.Genesis.ToString(BlockHeader.Format.Full));
                if(_logger.IsError) _logger.Error($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {_blockTree.Genesis.Hash}");
            }
            else
            {
                if(_logger.IsInfo) _logger.Info($"Genesis hash :  {_blockTree.Genesis.Hash}");
            }
        }

        private Task StartSync()
        {
            if (!_initConfig.SynchronizationEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping blockchain synchronization init due to ({nameof(IInitConfig.SynchronizationEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_blockTree.Head.ToString(BlockHeader.Format.Short)}.");

            _syncPeerPool.Start();
            _synchronizer.Start();
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            /* rlpx */
            var eciesCipher = new EciesCipher(_cryptoRandom);
            var eip8Pad = new Eip8MessagePad(_cryptoRandom);
            _messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            _messageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer)));
            _messageSerializationService.Register(new ReceiptsMessageSerializer(_specProvider));

            var encryptionHandshakeServiceA = new HandshakeService(_messageSerializationService, eciesCipher,
                _cryptoRandom, new Ecdsa(), _nodeKey, _logManager);
            
            _messageSerializationService.Register(Assembly.GetAssembly(typeof(HiMessageSerializer)));
            
            var discoveryConfig = _configProvider.GetConfig<IDiscoveryConfig>();

            _sessionMonitor = new SessionMonitor(_networkConfig, _logManager);
            _rlpxPeer = new RlpxPeer(
                _messageSerializationService,
                _nodeKey.PublicKey,
                _networkConfig.P2PPort,
                encryptionHandshakeServiceA,
                _logManager,
                _sessionMonitor);

            await _rlpxPeer.Init();

            _staticNodesManager = new StaticNodesManager(_initConfig.StaticNodesPath, _logManager);
            await _staticNodesManager.InitAsync();

            var peersDb = new SimpleFilePublicKeyDb("PeersDB", PeersDbPath.GetApplicationResourcePath(_initConfig.BaseDbPath), _logManager);
            var peerStorage = new NetworkStorage(peersDb, _logManager);

            ProtocolValidator protocolValidator = new ProtocolValidator(_nodeStatsManager, _blockTree, _logManager);
            _protocolsManager = new ProtocolsManager(_syncPeerPool, _syncServer, _txPool, _discoveryApp, _messageSerializationService, _rlpxPeer, _nodeStatsManager, protocolValidator, peerStorage, _perfService, _logManager);

            if (!(_ndmInitializer is null))
            {
                if (_logger.IsInfo) _logger.Info($"Initializing NDM...");
                var filterStore = new FilterStore();
                var filterManager = new FilterManager(filterStore, _blockProcessor, _txPool, _logManager);
                var capabilityConnector = await _ndmInitializer.InitAsync(_configProvider, _dbProvider,
                    _initConfig.BaseDbPath, _blockTree, _txPool, _specProvider, _receiptStorage, _wallet, filterStore,
                    filterManager, _timestamper, _ethereumEcdsa, _rpcModuleProvider, _keyStore, _ethereumJsonSerializer,
                    _cryptoRandom, _enode, _ndmConsumerChannelManager, _ndmDataPublisher, _grpcServer,
                    _nodeStatsManager, _protocolsManager, protocolValidator, _messageSerializationService,
                    _initConfig.EnableUnsecuredDevWallet, _webSocketsManager, _logManager, _blockProcessor);
                capabilityConnector.Init();
                if (_logger.IsInfo) _logger.Info($"NDM initialized.");
            }

            PeerLoader peerLoader = new PeerLoader(_networkConfig, discoveryConfig, _nodeStatsManager, peerStorage, _logManager);
            _peerManager = new PeerManager(_rlpxPeer, _discoveryApp, _nodeStatsManager, peerStorage, peerLoader, _networkConfig, _logManager, _staticNodesManager);
            _peerManager.Init();
        }

        private void StartPeer()
        {
            if (!_initConfig.PeerManagerEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(_initConfig.PeerManagerEnabled)} set to false)");
            }

            if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
            _peerManager.Start();
            _sessionMonitor.Start();
            if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            if (!_initConfig.DiscoveryEnabled)
            {
                _discoveryApp = new NullDiscoveryApp();
                return;
            }

            IDiscoveryConfig discoveryConfig = _configProvider.GetConfig<IDiscoveryConfig>();

            var privateKeyProvider = new SameKeyGenerator(_nodeKey);
            var discoveryMessageFactory = new DiscoveryMessageFactory(_timestamper);
            var nodeIdResolver = new NodeIdResolver(_ethereumEcdsa);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _messageSerializationService,
                _ethereumEcdsa,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver);

            msgSerializersProvider.RegisterDiscoverySerializers();

            var nodeDistanceCalculator = new NodeDistanceCalculator(discoveryConfig);

            var nodeTable = new NodeTable(nodeDistanceCalculator, discoveryConfig, _networkConfig,  _logManager);
            var evictionManager = new EvictionManager(nodeTable, _logManager);

            var nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _nodeStatsManager,
                discoveryConfig,
                _logManager);

            var discoveryDb = new SimpleFilePublicKeyDb("DiscoveryDB", DiscoveryNodesDbPath.GetApplicationResourcePath(_initConfig.BaseDbPath), _logManager);
            var discoveryStorage = new NetworkStorage(
                discoveryDb,
                _logManager);

            var discoveryManager = new DiscoveryManager(
                nodeLifeCycleFactory,
                nodeTable,
                discoveryStorage,
                discoveryConfig,
                _logManager);

            var nodesLocator = new NodesLocator(
                nodeTable,
                discoveryManager,
                discoveryConfig,
                _logManager);

            _discoveryApp = new DiscoveryApp(
                nodesLocator,
                discoveryManager,
                nodeTable,
                _messageSerializationService,
                _cryptoRandom,
                discoveryStorage,
                _networkConfig,
                discoveryConfig,
                _timestamper,
                _logManager, _perfService);

            _discoveryApp.Initialize(_nodeKey.PublicKey);
        }

        private Task StartDiscovery()
        {
            if (!_initConfig.DiscoveryEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to ({nameof(IInitConfig.DiscoveryEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
            _discoveryApp.Start();
            if (_logger.IsDebug) _logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }

        private async Task<IProducer> PrepareKafkaProducer(IBlockTree blockTree, IKafkaConfig kafkaConfig)
        {
            var pubSubModelMapper = new PubSubModelMapper();
            var avroMapper = new AvroMapper(blockTree);
            var kafkaProducer = new KafkaProducer(kafkaConfig, pubSubModelMapper, avroMapper, _logManager);
            await kafkaProducer.InitAsync().ContinueWith(x =>
            {
                if (x.IsFaulted && _logger.IsError) _logger.Error("Error during Kafka initialization", x.Exception);
            });

            return kafkaProducer;
        }

        private async Task InitHive()
        {
            if (_logger.IsInfo) _logger.Info("Initializing Hive");
            _hiveRunner = new HiveRunner(_blockTree as BlockTree, _wallet as HiveWallet, _jsonSerializer, _configProvider, _logger);
            await _hiveRunner.Start();
        }

        private void InitEthStats()
        {
            var config = _configProvider.GetConfig<IEthStatsConfig>();
            if (!config.Enabled)
            {
                if (_logger.IsInfo) _logger.Info($"ETH Stats integration is disabled.");
                return;
            }

            var instanceId = $"{config.Name}-{Keccak.Compute(_enode.Info)}";
            if (_logger.IsInfo) _logger.Info($"Initializing ETH Stats for the instance: {instanceId}, server: {config.Server}");
            var sender = new MessageSender(instanceId, _logManager);
            const int reconnectionInterval = 5000;
            const string api = "no";
            const string client = "0.1.1";
            const bool canUpdateHistory = false;
            var node = ClientVersion.Description;
            var port = _networkConfig.P2PPort;
            var network = _specProvider.ChainId.ToString();
            var protocol = _syncConfig.FastSync ? "eth/63" : "eth/62";
            var ethStatsClient = new EthStatsClient(config.Server, reconnectionInterval, sender, _logManager);
            var ethStatsIntegration = new EthStatsIntegration(config.Name, node, port, network, protocol, api, client,
                config.Contact, canUpdateHistory, config.Secret, ethStatsClient, sender, _blockTree, _peerManager,
                _logManager);
            Task.Run(() => ethStatsIntegration.InitAsync());
        }
    }
}