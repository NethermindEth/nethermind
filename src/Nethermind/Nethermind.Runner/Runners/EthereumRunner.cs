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
using System.Reflection;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Validators;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.JsonRpc.Client;
using Nethermind.JsonRpc.Module;
using Nethermind.KeyStore;
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
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Runner.Config;
using Nethermind.Stats;
using Nethermind.Store;
using Nethermind.Store.Rpc;
using Nethermind.Wallet;
using PingMessageSerializer = Nethermind.Network.P2P.PingMessageSerializer;
using PongMessageSerializer = Nethermind.Network.P2P.PongMessageSerializer;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IEthereumRunner
    {
        private static ILogManager _logManager;
        private static ILogger _logger;

        private static string _dbBasePath;
        private readonly IConfigProvider _configProvider;
        private readonly IInitConfig _initConfig;
        private readonly INetworkHelper _networkHelper;

        private PrivateKey _nodeKey;
        private ICryptoRandom _cryptoRandom = new CryptoRandom();
        private IJsonSerializer _jsonSerializer = new UnforgivingJsonSerializer();
        private ISigner _signer = new Signer();

        private IBlockchainProcessor _blockchainProcessor;
        private IDiscoveryApp _discoveryApp;
        private IDiscoveryManager _discoveryManager;
        private IMessageSerializationService _messageSerializationService = new MessageSerializationService();
        private INodeFactory _nodeFactory;
        private INodeStatsProvider _nodeStatsProvider;
        private IPerfService _perfService;
        private CancellationTokenSource _runnerCancellation;
        private ISynchronizationManager _syncManager;
        private IKeyStore _keyStore;
        private IPeerManager _peerManager;
        private BlockTree _blockTree;
        private ISpecProvider _specProvider;

        public const string DiscoveryNodesDbPath = "discoveryNodes";
        public const string PeersDbPath = "peers";

        public EthereumRunner(IConfigProvider configurationProvider, INetworkHelper networkHelper,
            ILogManager logManager)
        {
            _configProvider = configurationProvider;
            _initConfig = configurationProvider.GetConfig<IInitConfig>();
            _networkHelper = networkHelper;
            _logManager = logManager;
        }

        public IBlockchainBridge BlockchainBridge { get; private set; }
        public IEthereumSigner EthereumSigner { get; private set; }

        public async Task Start()
        {
            ConfigureTools();
            await InitBlockchain();
            if (_logger.IsDebug) _logger.Debug("Ethereum initialization completed");
        }

        private const string UnsecuredNodeKeyFilePath = "node.key.plain";

        private void ConfigureTools()
        {
            _runnerCancellation = new CancellationTokenSource();
            _logger = _logManager.GetClassLogger();

            if (_logger.IsInfo) _logger.Info("Initializing Ethereum");
            if (_logger.IsDebug) _logger.Debug($"Server GC           : {System.Runtime.GCSettings.IsServerGC}");
            if (_logger.IsDebug) _logger.Debug($"GC latency mode     : {System.Runtime.GCSettings.LatencyMode}");
            if (_logger.IsDebug) _logger.Debug($"LOH compaction mode : {System.Runtime.GCSettings.LargeObjectHeapCompactionMode}");

            // this is not secure at all but this is just the node key, nothing critical so far, will use the key store here later and allow to manage by password when launching the node
            if (_initConfig.TestNodeKey == null)
            {
                if (!File.Exists(UnsecuredNodeKeyFilePath))
                {
                    if (_logger.IsInfo) _logger.Info("Generating private key for the node (no node key in configuration)");
                    _nodeKey = new PrivateKeyGenerator(_cryptoRandom).Generate();
                    File.WriteAllBytes(UnsecuredNodeKeyFilePath, _nodeKey.KeyBytes);
                }
                else
                {
                    _nodeKey = new PrivateKey(File.ReadAllBytes(UnsecuredNodeKeyFilePath));
                }
            }
            else
            {
                _nodeKey = new PrivateKey(_initConfig.TestNodeKey);
            }

            _dbBasePath = _initConfig.BaseDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db");
            _perfService = new PerfService(_logManager) {LogOnDebug = _initConfig.LogPerfStatsOnDebug};
        }

        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Shutting down...");
            _runnerCancellation.Cancel();

            if (_logger.IsInfo) _logger.Info("Stopping rlpx peer...");
            var rlpxPeerTask = (_rlpxPeer?.Shutdown() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping peer manager...");
            var peerManagerTask = (_peerManager?.StopAsync() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping sync manager...");
            var syncManagerTask = (_syncManager?.StopAsync() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping blockchain processor...");
            var blockchainProcessorTask = (_blockchainProcessor?.StopAsync() ?? Task.CompletedTask);

            if (_logger.IsInfo) _logger.Info("Stopping discovery app...");
            var discoveryStopTask = _discoveryApp?.StopAsync() ?? Task.CompletedTask;

            await Task.WhenAll(discoveryStopTask, rlpxPeerTask, peerManagerTask, syncManagerTask, blockchainProcessorTask);

            if (_logger.IsInfo) _logger.Info("Closing DBs...");
            _dbProvider.Dispose();
            if (_logger.IsInfo) _logger.Info("Ethereum shutdown complete... please wait for all components to close");
        }

        private ChainSpec LoadChainSpec(string chainSpecFile)
        {
            _logger.Info($"Loading chain spec from {chainSpecFile}");
            ChainSpecLoader loader = new ChainSpecLoader(_jsonSerializer);
            ChainSpec chainSpec = loader.LoadFromFile(chainSpecFile);
            return chainSpec;
        }

        private async Task InitBlockchain()
        {
            ChainSpec chainSpec = LoadChainSpec(_initConfig.ChainSpecPath);

            /* spec */
            // TODO: rebuild to use chainspec            
            if (chainSpec.ChainId == RopstenSpecProvider.Instance.ChainId)
            {
                _specProvider = RopstenSpecProvider.Instance;
            }
            else if (chainSpec.ChainId == MainNetSpecProvider.Instance.ChainId)
            {
                _specProvider = MainNetSpecProvider.Instance;
            }
            else
            {
                _specProvider = new SingleReleaseSpecProvider(LatestRelease.Instance, chainSpec.ChainId);
            }

            var ethereumSigner = new EthereumSigner(
                _specProvider,
                _logManager);

            /* sync */
            IDbConfig dbConfig = _configProvider.GetConfig<IDbConfig>();
            foreach (PropertyInfo propertyInfo in typeof(IDbConfig).GetProperties())
            {
                _logger.Info($"DB {propertyInfo.Name}: {propertyInfo.GetValue(dbConfig)}");
            }

            _dbProvider = new RocksDbProvider(_dbBasePath, dbConfig);

//            IDbProvider debugRecorder = new RocksDbProvider(Path.Combine(_dbBasePath, "debug"), dbConfig);
//            _dbProvider = new RpcDbProvider(_jsonSerializer, new BasicJsonRpcClient(KnownRpcUris.NethVm1, _jsonSerializer, _logManager), _logManager, debugRecorder);

//            IDbProvider debugReader = new ReadOnlyDbProvider(new RocksDbProvider(Path.Combine(_dbBasePath, "debug"), dbConfig));
//            _dbProvider = debugReader;

            var transactionStore = new TransactionStore(_dbProvider.ReceiptsDb, _specProvider);
            var sealEngine = ConfigureSealEngine();

            /* blockchain */
            _blockTree = new BlockTree(
                _dbProvider.BlocksDb,
                _dbProvider.BlockInfosDb,
                _specProvider,
                transactionStore,
                _logManager);

            /* validation */
            var headerValidator = new HeaderValidator(
                _blockTree,
                sealEngine,
                _specProvider,
                _logManager);

            var ommersValidator = new OmmersValidator(
                _blockTree,
                headerValidator,
                _logManager);

            var txValidator = new TransactionValidator(
                new SignatureValidator(_specProvider.ChainId));

            var blockValidator = new BlockValidator(
                txValidator,
                headerValidator,
                ommersValidator,
                _specProvider,
                _logManager);

            var stateTree = new StateTree(_dbProvider.StateDb);

            var stateProvider = new StateProvider(
                stateTree,
                _dbProvider.CodeDb,
                _logManager);

            var storageProvider = new StorageProvider(
                _dbProvider.StateDb,
                stateProvider,
                _logManager);

            /* blockchain processing */
            var blockhashProvider = new BlockhashProvider(
                _blockTree);

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

            var rewardCalculator = new RewardCalculator(
                _specProvider);

            var blockProcessor = new BlockProcessor(
                _specProvider,
                blockValidator,
                rewardCalculator,
                transactionProcessor,
                _dbProvider.StateDb,
                _dbProvider.CodeDb,
                stateProvider,
                storageProvider,
                transactionStore,
                _logManager);

            _blockchainProcessor = new BlockchainProcessor(
                _blockTree,
                blockProcessor,
                ethereumSigner,
                _logManager);
            
            ITxTracer txTracer = new TxTracer(_blockchainProcessor, transactionStore, _blockTree);

            // create shared objects between discovery and peer manager
            _nodeFactory = new NodeFactory();
            _nodeStatsProvider = new NodeStatsProvider(_configProvider.GetConfig<IStatsConfig>(), _nodeFactory, _logManager);

            var jsonSerializer = new JsonSerializer(
                _logManager);

            var encrypter = new AesEncrypter(
                _configProvider,
                _logManager);

            _keyStore = new FileKeyStore(
                _configProvider,
                jsonSerializer,
                encrypter,
                _cryptoRandom,
                _logManager);

            //creating blockchain bridge
            BlockchainBridge = new BlockchainBridge(
                ethereumSigner,
                stateProvider,
                _blockTree,
                _blockchainProcessor,
                txTracer,
                _dbProvider,
                transactionStore,
                new FilterStore(),
                new DevWallet(_logManager));

            EthereumSigner = ethereumSigner;

            if (_initConfig.IsMining)
            {
                var producer = new DevBlockProducer(transactionStore, _blockchainProcessor, _blockTree, _logManager);
                producer.Start();
            }
            
            _blockchainProcessor.Start();
            LoadGenesisBlock(chainSpec,
                string.IsNullOrWhiteSpace(_initConfig.GenesisHash) ? null : new Keccak(_initConfig.GenesisHash),
                _blockTree, stateProvider, _specProvider);

#pragma warning disable 4014
            LoadBlocksFromDb();
#pragma warning restore 4014

            await InitializeNetwork(
                transactionStore,
                blockValidator,
                headerValidator,
                txValidator);
        }

        private async Task LoadBlocksFromDb()
        {
            if (!_initConfig.SynchronizationEnabled)
            {
                return;
            }

            await _blockTree.LoadBlocksFromDb(_runnerCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsError) _logger.Error("Loading blocks from DB failed.", t.Exception);
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsWarn) _logger.Warn("Loading blocks from DB canceled.");
                }
                else
                {
                    if (_logger.IsInfo) _logger.Info("Loaded all blocks from DB");
                }
            });
        }

        private async Task InitializeNetwork(
            TransactionStore transactionStore,
            BlockValidator blockValidator,
            HeaderValidator headerValidator,
            TransactionValidator txValidator)
        {
            if (!_initConfig.NetworkEnabled)
            {
                if (_logger.IsInfo) _logger.Info($"Skipping blockchain synchronization init ({nameof(IInitConfig.NetworkEnabled)} = false)");
                return;
            }

            _syncManager = new SynchronizationManager(
                _blockTree,
                blockValidator,
                headerValidator,
                transactionStore,
                txValidator,
                _logManager,
                _configProvider.GetConfig<IBlockchainConfig>(), _perfService);

            InitDiscovery();
            await InitPeer();

            await StartSync().ContinueWith(initNetTask =>
            {
                if (initNetTask.IsFaulted)
                {
                    _logger.Error("Unable to start sync.", initNetTask.Exception);
                }
            });

            await StartDiscovery().ContinueWith(initDiscoveryTask =>
            {
                if (initDiscoveryTask.IsFaulted)
                {
                    _logger.Error("Unable to start discovery protocol.", initDiscoveryTask.Exception);
                }
            });

            await StartPeer().ContinueWith(initPeerManagerTask =>
            {
                if (initPeerManagerTask.IsFaulted)
                {
                    _logger.Error("Unable to start peer manager.", initPeerManagerTask.Exception);
                }
            });

            var localIp = _networkHelper.GetLocalIp();
            if (_logger.IsInfo) _logger.Info($"Node is up and listening on {localIp}:{_initConfig.P2PPort}");
            if (_logger.IsInfo) _logger.Info($"enode://{_nodeKey.PublicKey}@{localIp}:{_initConfig.P2PPort}");
        }

        private ISealEngine ConfigureSealEngine()
        {
//            var sealEngine = NullSealEngine.Instance;
            var difficultyCalculator = new DifficultyCalculator(_specProvider);
            var sealEngine = new EthashSealEngine(new Ethash(_logManager), difficultyCalculator, _logManager);

//            var blockMiningTime = TimeSpan.FromMilliseconds(_initConfig.FakeMiningDelay);
//            var sealEngine = new FakeSealEngine(blockMiningTime, false);
//            sealEngine.IsMining = _initConfig.IsMining;
//            if (sealEngine.IsMining)
//            {
//                var transactionDelay = TimeSpan.FromMilliseconds(_initConfig.FakeMiningDelay / 4);
//                TestTransactionsGenerator testTransactionsGenerator =
//                    new TestTransactionsGenerator(transactionStore, ethereumSigner, transactionDelay, _logManager);
//                // stateProvider.CreateAccount(testTransactionsGenerator.SenderAddress, 1000.Ether());
//                // stateProvider.Commit(specProvider.GenesisSpec);
//                testTransactionsGenerator.Start();
//            }
            return sealEngine;
        }

        private static void LoadGenesisBlock(
            ChainSpec chainSpec,
            Keccak expectedGenesisHash,
            BlockTree blockTree,
            StateProvider stateProvider,
            ISpecProvider specProvider)
        {
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (blockTree.Genesis != null)
            {
                return;
            }

            foreach (KeyValuePair<Address, UInt256> allocation in chainSpec.Allocations)
            {
                stateProvider.CreateAccount(allocation.Key, allocation.Value);
            }

            stateProvider.Commit(specProvider.GenesisSpec);

            Block genesis = chainSpec.Genesis;
            genesis.StateRoot = stateProvider.StateRoot;
            genesis.Hash = BlockHeader.CalculateHash(genesis.Header);

            ManualResetEvent genesisProcessedEvent = new ManualResetEvent(false);

            bool genesisLoaded = false;

            void GenesisProcessed(object sender, BlockEventArgs args)
            {
                genesisLoaded = true;
                blockTree.NewHeadBlock -= GenesisProcessed;
                genesisProcessedEvent.Set();
            }

            blockTree.NewHeadBlock += GenesisProcessed;
            blockTree.SuggestBlock(genesis);
            genesisProcessedEvent.WaitOne(TimeSpan.FromSeconds(5));
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

        private IRlpxPeer _rlpxPeer;
        private IDbProvider _dbProvider;

        private Task StartSync()
        {
            if (!_initConfig.SynchronizationEnabled)
            {
                if (_logger.IsInfo) _logger.Info($"Skipping blockchain synchronization init ({nameof(IInitConfig.SynchronizationEnabled)} = false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_blockTree.Head.ToString(BlockHeader.Format.Short)}.");

            _syncManager.Start();
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            /* rlpx */
            var eciesCipher = new EciesCipher(_cryptoRandom);
            var eip8Pad = new Eip8MessagePad(_cryptoRandom);
            _messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            var encryptionHandshakeServiceA = new EncryptionHandshakeService(_messageSerializationService, eciesCipher,
                _cryptoRandom, new Signer(), _nodeKey, _logManager);

            /* p2p */
            _messageSerializationService.Register(new HelloMessageSerializer());
            _messageSerializationService.Register(new DisconnectMessageSerializer());
            _messageSerializationService.Register(new PingMessageSerializer());
            _messageSerializationService.Register(new PongMessageSerializer());

            /* eth */
            _messageSerializationService.Register(new StatusMessageSerializer());
            _messageSerializationService.Register(new TransactionsMessageSerializer());
            _messageSerializationService.Register(new GetBlockHeadersMessageSerializer());
            _messageSerializationService.Register(new NewBlockHashesMessageSerializer());
            _messageSerializationService.Register(new GetBlockBodiesMessageSerializer());
            _messageSerializationService.Register(new BlockHeadersMessageSerializer());
            _messageSerializationService.Register(new BlockBodiesMessageSerializer());
            _messageSerializationService.Register(new NewBlockMessageSerializer());

            _rlpxPeer = new RlpxPeer(new NodeId(_nodeKey.PublicKey), _initConfig.P2PPort,
                _syncManager,
                _messageSerializationService,
                encryptionHandshakeServiceA,
                _nodeStatsProvider,
                _logManager, _perfService);

            await _rlpxPeer.Init();

            var peerStorage = new NetworkStorage(PeersDbPath, _configProvider.GetConfig<INetworkConfig>(), _logManager, _perfService);
            _peerManager = new PeerManager(_rlpxPeer, _discoveryApp, _syncManager, _nodeStatsProvider, peerStorage,
                _nodeFactory, _configProvider, _perfService, _logManager);
            _peerManager.Init(_initConfig.DiscoveryEnabled);
        }

        private async Task StartPeer()
        {
            if (!_initConfig.PeerManagerEnabled)
            {
                if (_logger.IsInfo) _logger.Info("Skipping peer manager init (PeerManagerEnabled} = false)");
                return;
            }

            if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
            await _peerManager.Start();
            if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            _configProvider.GetConfig<INetworkConfig>().MasterPort = _initConfig.DiscoveryPort;

            var privateKeyProvider = new SameKeyGenerator(_nodeKey);
            var discoveryMessageFactory = new DiscoveryMessageFactory(_configProvider);
            var nodeIdResolver = new NodeIdResolver(_signer);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _messageSerializationService,
                _signer,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver,
                _nodeFactory);

            msgSerializersProvider.RegisterDiscoverySerializers();

            var nodeDistanceCalculator = new NodeDistanceCalculator(_configProvider);

            var nodeTable = new NodeTable(
                _nodeFactory,
                _keyStore,
                nodeDistanceCalculator,
                _configProvider,
                _logManager);

            var evictionManager = new EvictionManager(
                nodeTable,
                _logManager);

            var nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                _nodeFactory,
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _nodeStatsProvider,
                _configProvider,
                _logManager);

            var discoveryStorage = new NetworkStorage(
                DiscoveryNodesDbPath,
                _configProvider.GetConfig<INetworkConfig>(),
                _logManager,
                _perfService);

            _discoveryManager = new DiscoveryManager(
                nodeLifeCycleFactory,
                _nodeFactory,
                nodeTable,
                discoveryStorage,
                _configProvider,
                _logManager);

            var nodesLocator = new NodesLocator(
                nodeTable,
                _discoveryManager,
                _configProvider,
                _logManager);

            _discoveryApp = new DiscoveryApp(
                nodesLocator,
                _discoveryManager,
                _nodeFactory,
                nodeTable,
                _messageSerializationService,
                _cryptoRandom,
                discoveryStorage,
                _configProvider,
                _logManager, _perfService);

            _discoveryApp.Initialize(_nodeKey.PublicKey);
        }

        private Task StartDiscovery()
        {
            if (!_initConfig.DiscoveryEnabled)
            {
                if (_logger.IsInfo) _logger.Info($"Skipping discovery init ({nameof(IInitConfig.DiscoveryEnabled)} = false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
            _discoveryApp.Start();
            if (_logger.IsDebug) _logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }
    }
}