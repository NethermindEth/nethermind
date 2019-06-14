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
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
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
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
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
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.StaticNodes;
using Nethermind.Runner.Config;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Wallet;
using Block = Nethermind.Core.Block;
using ISyncConfig = Nethermind.Blockchain.ISyncConfig;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IRunner
    {
        private static readonly bool HiveEnabled =
            Environment.GetEnvironmentVariable("NETHERMIND_HIVE_ENABLED")?.ToLowerInvariant() == "true";

        private static ILogManager _logManager;
        private static ILogger _logger;

        private IRpcModuleProvider _rpcModuleProvider;
        private IConfigProvider _configProvider;
        private ITxPoolConfig _txPoolConfig;
        private IInitConfig _initConfig;
        private INetworkHelper _networkHelper;

        private PrivateKey _nodeKey;
        private ChainSpec _chainSpec;
        private ICryptoRandom _cryptoRandom = new CryptoRandom();
        private IEcdsa _ecdsa = new Ecdsa();
        private IJsonSerializer _jsonSerializer = new UnforgivingJsonSerializer();
        private IJsonSerializer _ethereumJsonSerializer = new EthereumJsonSerializer();
        private CancellationTokenSource _runnerCancellation;

        private IBlockchainProcessor _blockchainProcessor;
        private IDiscoveryApp _discoveryApp;
        private IMessageSerializationService _messageSerializationService = new MessageSerializationService();
        private INodeStatsManager _nodeStatsManager;
        private IPerfService _perfService;
        private ITxPool _txPool;
        private ITxPoolInfoProvider _transactionPoolInfoProvider;
        private IReceiptStorage _receiptStorage;
        private IEthereumEcdsa _ethereumEcdsa;
        private IEthSyncPeerPool _syncPeerPool;
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
        private ISealer _sealer;
        private ISealValidator _sealValidator;
        private IBlockProducer _blockProducer;
        private ISnapshotManager _snapshotManager;
        private IRlpxPeer _rlpxPeer;
        private IDbProvider _dbProvider;
        private readonly ITimestamp _timestamp = new Timestamp();
        private IStateProvider _stateProvider;
        private IWallet _wallet;
        private IEnode _enode;
        private HiveRunner _hiveRunner;
        private ISessionMonitor _sessionMonitor;
        private ISyncConfig _syncConfig;
        public IEnode Enode => _enode;
        private IStaticNodesManager _staticNodesManager;
        public const string DiscoveryNodesDbPath = "discoveryNodes";
        public const string PeersDbPath = "peers";

        public EthereumRunner(IRpcModuleProvider rpcModuleProvider, IConfigProvider configurationProvider, ILogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = _logManager.GetClassLogger();

            InitRlp();
            _configProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
            _rpcModuleProvider = rpcModuleProvider ?? throw new ArgumentNullException(nameof(rpcModuleProvider));
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _txPoolConfig = configurationProvider.GetConfig<ITxPoolConfig>();
            _perfService = new PerfService(_logManager);
            _networkHelper = new NetworkHelper(_logger);
        }

        public async Task Start()
        {
            if (_logger.IsDebug) _logger.Debug("Initializing Ethereum");
            _runnerCancellation = new CancellationTokenSource();

            SetupKeyStore();
            LoadChainSpec();
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
                    _wallet = new DevWallet(_logManager);
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

            var ipVariable = Environment.GetEnvironmentVariable("NETHERMIND_ENODE_IPADDRESS");
            var localIp = string.IsNullOrWhiteSpace(ipVariable)
                ? _networkHelper.GetLocalIp()
                : IPAddress.Parse(ipVariable);

            _enode = new Enode(_nodeKey.PublicKey, localIp, _initConfig.P2PPort);
        }

        private void RegisterJsonRpcModules()
        {
            if (!_initConfig.JsonRpcEnabled)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Resolving CLI ({nameof(Cli.CliModuleLoader)})");

            IReadOnlyDbProvider rpcDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            AlternativeChain rpcChain = new AlternativeChain(_blockTree, _blockValidator, _rewardCalculator, _specProvider, rpcDbProvider, _recoveryStep, _logManager, _txPool, _receiptStorage);

            ITracer tracer = new Tracer(rpcChain.Processor, _receiptStorage, new ReadOnlyBlockTree(_blockTree), _dbProvider.TraceDb);
            IFilterStore filterStore = new FilterStore();
            IFilterManager filterManager = new FilterManager(filterStore, _blockProcessor, _txPool, _logManager);

            RpcState rpcState = new RpcState(_blockTree, _specProvider, rpcDbProvider, _logManager);

            //creating blockchain bridge
            var blockchainBridge = new BlockchainBridge(
                rpcState.StateReader,
                rpcState.StateProvider,
                rpcState.StorageProvider,
                rpcState.BlockTree,
                _txPool,
                _transactionPoolInfoProvider,
                _receiptStorage,
                filterStore,
                filterManager,
                _wallet,
                rpcState.TransactionProcessor);

            AlternativeChain debugChain = new AlternativeChain(_blockTree, _blockValidator, _rewardCalculator, _specProvider, rpcDbProvider, _recoveryStep, _logManager, NullTxPool.Instance, NullReceiptStorage.Instance);
            IReadOnlyDbProvider debugDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
            var debugBridge = new DebugBridge(_configProvider, debugDbProvider, tracer, debugChain.Processor);

            EthModule module = new EthModule(_logManager, blockchainBridge);
            _rpcModuleProvider.Register<IEthModule>(module);

            DebugModule debugModule = new DebugModule(_logManager, debugBridge);
            _rpcModuleProvider.Register<IDebugModule>(debugModule);

            if (_sealValidator is CliqueSealValidator)
            {
                CliqueModule cliqueModule = new CliqueModule(_logManager, new CliqueBridge(_blockProducer as ICliqueBlockProducer, _snapshotManager, _blockTree));
                _rpcModuleProvider.Register<ICliqueModule>(cliqueModule);
            }

            if (_initConfig.EnableUnsecuredDevWallet)
            {
                PersonalBridge personalBridge = new PersonalBridge(_wallet);
                PersonalModule personalModule = new PersonalModule(personalBridge, _logManager);
                _rpcModuleProvider.Register<IPersonalModule>(personalModule);
            }

            AdminModule adminModule = new AdminModule(_logManager, _peerManager, _staticNodesManager);
            _rpcModuleProvider.Register<IAdminModule>(adminModule);

            TxPoolModule txPoolModule = new TxPoolModule(_logManager, blockchainBridge);
            _rpcModuleProvider.Register<ITxPoolModule>(txPoolModule);

            NetModule netModule = new NetModule(_logManager, new NetBridge(_enode, _syncServer, _peerManager));
            _rpcModuleProvider.Register<INetModule>(netModule);

            TraceModule traceModule = new TraceModule(_logManager, tracer);
            _rpcModuleProvider.Register<ITraceModule>(traceModule);
        }

        private void UpdateDiscoveryConfig()
        {
            var localHost = _networkHelper.GetLocalIp()?.ToString() ?? "127.0.0.1";
            var discoveryConfig = _configProvider.GetConfig<IDiscoveryConfig>();
            discoveryConfig.MasterExternalIp = localHost;
            discoveryConfig.MasterHost = localHost;
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
            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private void LoadChainSpec()
        {
            if(_logger.IsInfo) _logger.Info($"Loading chain spec from {_initConfig.ChainSpecPath}");

            IChainSpecLoader loader = string.Equals(_initConfig.ChainSpecFormat, "ChainSpec", StringComparison.InvariantCultureIgnoreCase)
                ? (IChainSpecLoader) new ChainSpecLoader(_ethereumJsonSerializer)
                : new GenesisFileLoader(_ethereumJsonSerializer);

            if (HiveEnabled)
            {
                if(_logger.IsInfo) _logger.Info($"HIVE chainspec:{Environment.NewLine}{File.ReadAllText(_initConfig.ChainSpecPath)}");
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

            _ethereumEcdsa = new EthereumEcdsa(_specProvider, _logManager);
            _txPool = new TxPool(
                new PersistentTxStorage(_dbProvider.PendingTxsDb, _specProvider),
                new PendingTxThresholdValidator(_txPoolConfig.ObsoletePendingTransactionInterval,
                    _txPoolConfig.RemovePendingTransactionInterval), new Timestamp(),
                _ethereumEcdsa, _specProvider, _logManager, _txPoolConfig.RemovePendingTransactionInterval,
                _txPoolConfig.PeerNotificationThreshold);
            _receiptStorage = new PersistentReceiptStorage(_dbProvider.ReceiptsDb, _specProvider, _logManager);

//            IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_dbBasePath, "debug"), dbConfig);
//            _dbProvider = new RpcDbProvider(_jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.NethVm1, _jsonSerializer, _logManager), _logManager, debugRecorder);

//            IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_dbBasePath, "debug"), dbConfig));
//            _dbProvider = debugReader;

            _blockTree = new BlockTree(
                _dbProvider.BlocksDb,
                _dbProvider.HeadersDb,
                _dbProvider.BlockInfosDb,
                _specProvider,
                _txPool,
                _syncConfig,
                _logManager);

            _recoveryStep = new TxSignaturesRecoveryStep(_ethereumEcdsa, _txPool, _logManager);

            CliqueConfig cliqueConfig = null;
            _snapshotManager = null;
            switch (_chainSpec.SealEngineType)
            {
                case SealEngineType.None:
                    _sealer = NullSealEngine.Instance;
                    _sealValidator = NullSealEngine.Instance;
                    _rewardCalculator = NoBlockRewards.Instance;
                    break;
                case SealEngineType.Clique:
                    _rewardCalculator = NoBlockRewards.Instance;
                    cliqueConfig = new CliqueConfig();
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
                default:
                    throw new NotSupportedException($"Seal engine type {_chainSpec.SealEngineType} is not supported in Nethermind");
            }

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

            var stateProvider = new StateProvider(
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                _logManager);

            _stateProvider = stateProvider;

            var storageProvider = new StorageProvider(
                _dbProvider.StateDb,
                stateProvider,
                _logManager);

            _transactionPoolInfoProvider = new TxPoolInfoProvider(stateProvider);

            /* blockchain processing */
            var blockhashProvider = new BlockhashProvider(
                _blockTree, _logManager);

            var virtualMachine = new VirtualMachine(
                stateProvider,
                storageProvider,
                blockhashProvider,
                _logManager);

            var transactionProcessor = new TransactionProcessor(
                _specProvider,
                stateProvider,
                storageProvider,
                virtualMachine,
                _logManager);

            _blockProcessor = new BlockProcessor(
                _specProvider,
                _blockValidator,
                _rewardCalculator,
                transactionProcessor,
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                _dbProvider.TraceDb,
                stateProvider,
                storageProvider,
                _txPool,
                _receiptStorage,
                _logManager);

            _blockchainProcessor = new BlockchainProcessor(
                _blockTree,
                _blockProcessor,
                _recoveryStep,
                _logManager,
                _initConfig.StoreReceipts,
                _initConfig.StoreTraces);

            // create shared objects between discovery and peer manager
            IStatsConfig statsConfig = _configProvider.GetConfig<IStatsConfig>();
            _nodeStatsManager = new NodeStatsManager(statsConfig, _logManager);

            if (_initConfig.IsMining)
            {
                IReadOnlyDbProvider minerDbProvider = new ReadOnlyDbProvider(_dbProvider, false);
                AlternativeChain producerChain = new AlternativeChain(_blockTree, _blockValidator, _rewardCalculator,
                    _specProvider, minerDbProvider, _recoveryStep, _logManager, _txPool, _receiptStorage);

                switch (_chainSpec.SealEngineType)
                {
                    case SealEngineType.Clique:
                    {
                        if (_logger.IsWarn) _logger.Warn("Starting Clique block producer & sealer");
                        _blockProducer = new CliqueBlockProducer(_txPool, producerChain.Processor,
                            _blockTree, _timestamp, _cryptoRandom, producerChain.StateProvider, _snapshotManager, (CliqueSealer) _sealer, _nodeKey.Address, cliqueConfig, _logManager);
                        break;
                    }

                    case SealEngineType.NethDev:
                    {
                        if (_logger.IsWarn) _logger.Warn("Starting Dev block producer & sealer");
                        _blockProducer = new DevBlockProducer(_txPool, producerChain.Processor, _blockTree,
                            _timestamp, _logManager);
                        break;
                    }

                    default:
                        throw new NotSupportedException($"Mining in {_chainSpec.SealEngineType} mode is not supported");
                }

                _blockProducer.Start();
            }

            _blockchainProcessor.Start();
            LoadGenesisBlock(_chainSpec, string.IsNullOrWhiteSpace(_initConfig.GenesisHash) ? null : new Keccak(_initConfig.GenesisHash), _blockTree, stateProvider, _specProvider);
            if (_initConfig.ProcessingEnabled)
            {
#pragma warning disable 4014
                LoadBlocksFromDb();
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

            await InitializeNetwork();
        }

        private async Task LoadBlocksFromDb()
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
        }

        private async Task InitializeNetwork()
        {
            var maxPeersCount = _configProvider.GetConfig<INetworkConfig>().ActivePeersMaxCount;
            _syncPeerPool = new EthSyncPeerPool(_blockTree, _nodeStatsManager, _syncConfig, maxPeersCount, _logManager);
            NodeDataFeed feed = new NodeDataFeed(_dbProvider.CodeDb, _dbProvider.StateDb, _logManager);
            NodeDataDownloader nodeDataDownloader = new NodeDataDownloader(_syncPeerPool, feed, _logManager);
            _synchronizer = new Synchronizer(_specProvider, _blockTree, _receiptStorage, _blockValidator, _sealValidator, _syncPeerPool, _syncConfig, nodeDataDownloader, _logManager);

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

            if (_logger.IsInfo) _logger.Info($"Ethereum     : tcp://{_enode.IpAddress}:{_enode.P2PPort}");
            if (_logger.IsInfo) _logger.Info($"Version      : {ClientVersion.Description}");
            if (_logger.IsInfo) _logger.Info($"This node    : {_enode.Info}");
            if (_logger.IsInfo) _logger.Info($"Node address : {_enode.Address} (do not use as an account)");
        }

        private static void LoadGenesisBlock(
            ChainSpec chainSpec,
            Keccak expectedGenesisHash,
            IBlockTree blockTree,
            IStateProvider stateProvider,
            ISpecProvider specProvider)
        {
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (blockTree.Genesis != null)
            {
                return;
            }

            foreach ((Address address, (UInt256 balance, byte[] code)) in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(address, balance);
                if (code != null)
                {
                    Keccak codeHash = stateProvider.UpdateCode(code);
                    stateProvider.UpdateCodeHash(address, codeHash, specProvider.GenesisSpec);
                }
            }

            stateProvider.Commit(specProvider.GenesisSpec);

            Block genesis = chainSpec.Genesis;
            genesis.StateRoot = stateProvider.StateRoot;
            genesis.Hash = BlockHeader.CalculateHash(genesis.Header);

            ManualResetEventSlim genesisProcessedEvent = new ManualResetEventSlim(false);

            bool genesisLoaded = false;

            void GenesisProcessed(object sender, BlockEventArgs args)
            {
                genesisLoaded = true;
                blockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            blockTree.NewHeadBlock += GenesisProcessed;
            blockTree.SuggestBlock(genesis);
            genesisProcessedEvent.Wait(TimeSpan.FromSeconds(5));
            if (!genesisLoaded)
            {
                throw new BlockchainException("Genesis block processing failure");
            }

            // if expectedGenesisHash is null here then it means that we do not care about the exact value in advance (e.g. in test scenarios)
            if (expectedGenesisHash != null && blockTree.Genesis.Hash != expectedGenesisHash)
            {
                throw new Exception($"Unexpected genesis hash, expected {expectedGenesisHash}, but was {blockTree.Genesis.Hash}");
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

            var encryptionHandshakeServiceA = new EncryptionHandshakeService(_messageSerializationService, eciesCipher,
                _cryptoRandom, new Ecdsa(), _nodeKey, _logManager);

            var networkConfig = _configProvider.GetConfig<INetworkConfig>();
            var discoveryConfig = _configProvider.GetConfig<IDiscoveryConfig>();

            _sessionMonitor = new SessionMonitor(networkConfig, _logManager);
            _rlpxPeer = new RlpxPeer(
                _nodeKey.PublicKey,
                _initConfig.P2PPort,
                encryptionHandshakeServiceA,
                _logManager,
                _sessionMonitor);

            await _rlpxPeer.Init();

            _staticNodesManager = new StaticNodesManager(_initConfig.StaticNodesPath, _logManager);
            await _staticNodesManager.InitAsync();

            var peersDb = new SimpleFilePublicKeyDb(PeersDbPath, _logManager);
            var peerStorage = new NetworkStorage(peersDb, _logManager);

            ProtocolValidator protocolValidator = new ProtocolValidator(_nodeStatsManager, _blockTree, _logManager);
            _protocolsManager = new ProtocolsManager(_syncPeerPool, _syncServer, _txPool, _discoveryApp, _messageSerializationService, _rlpxPeer, _nodeStatsManager, protocolValidator, peerStorage, _perfService, _logManager);
            PeerLoader peerLoader = new PeerLoader(networkConfig, discoveryConfig, _nodeStatsManager, peerStorage, _logManager);
            _peerManager = new PeerManager(_rlpxPeer, _discoveryApp, _nodeStatsManager, peerStorage, peerLoader, networkConfig, _logManager, _staticNodesManager);
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
            discoveryConfig.MasterPort = _initConfig.DiscoveryPort;

            var privateKeyProvider = new SameKeyGenerator(_nodeKey);
            var discoveryMessageFactory = new DiscoveryMessageFactory(_timestamp);
            var nodeIdResolver = new NodeIdResolver(_ecdsa);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _messageSerializationService,
                _ecdsa,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver);

            msgSerializersProvider.RegisterDiscoverySerializers();

            var nodeDistanceCalculator = new NodeDistanceCalculator(discoveryConfig);

            var nodeTable = new NodeTable(nodeDistanceCalculator,
                discoveryConfig,
                _logManager);

            var evictionManager = new EvictionManager(
                nodeTable,
                _logManager);

            var nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _nodeStatsManager,
                discoveryConfig,
                _logManager);

            var discoveryDb = new SimpleFilePublicKeyDb(DiscoveryNodesDbPath, _logManager);
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
                discoveryConfig,
                _timestamp,
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
            var port = _configProvider.GetConfig<IInitConfig>().P2PPort;
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