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
using System.Net;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Db;
using Nethermind.Discovery;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Serializers;
using Nethermind.Discovery.Stats;
using Nethermind.Evm;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Network.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Store;
using PingMessageSerializer = Nethermind.Network.P2P.PingMessageSerializer;
using PongMessageSerializer = Nethermind.Network.P2P.PongMessageSerializer;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IEthereumRunner
    {
        private static ILogger _defaultLogger = new NLogLogger("default");
        private static ILogger _evmLogger = new NLogLogger("evm");
        private static ILogger _stateLogger = new NLogLogger("state");
        private static ILogger _chainLogger = new NLogLogger("chain");
        private static ILogger _networkLogger = new NLogLogger("net");
        private static ILogger _discoveryLogger = new NLogLogger("discovery");

        private static string _dbBasePath;
        private readonly IDiscoveryConfigurationProvider _discoveryConfigurationProvider;
        private readonly INetworkHelper _networkHelper;
        private IBlockchainProcessor _blockchainProcessor;
        private ICryptoRandom _cryptoRandom;
        private IDiscoveryApp _discoveryApp;
        private IDiscoveryManager _discoveryManager;
        private IRlpxPeer _localPeer;
        private IMessageSerializationService _messageSerializationService;
        private INodeFactory _nodeFactory;
        private INodeStatsProvider _nodeStatsProvider;
        private IPerfService _perfService;
        private PrivateKey _privateKey;
        private CancellationTokenSource _runnerCancellation;
        private ISigner _signer;
        private ISynchronizationManager _syncManager;
        private ITransactionTracer _tracer;

        public EthereumRunner(IDiscoveryConfigurationProvider configurationProvider, INetworkHelper networkHelper)
        {
            _discoveryConfigurationProvider = configurationProvider;
            _networkHelper = networkHelper;
        }

        public async Task Start(InitParams initParams)
        {
            _runnerCancellation = new CancellationTokenSource();
            _defaultLogger = new NLogLogger(initParams.LogFileName, "default");
            _evmLogger = new NLogLogger(initParams.LogFileName, "evm");
            _stateLogger = new NLogLogger(initParams.LogFileName, "state");
            _chainLogger = new NLogLogger(initParams.LogFileName, "chain");
            _networkLogger = new NLogLogger(initParams.LogFileName, "net");
            _discoveryLogger = new NLogLogger(initParams.LogFileName, "discovery");

            _defaultLogger.Info("Initializing Ethereum");
            _privateKey = new PrivateKey(initParams.TestNodeKey);
            _dbBasePath = initParams.BaseDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db");

            _tracer = initParams.TransactionTracingEnabled ? new TransactionTracer(initParams.BaseTracingPath, new UnforgivingJsonSerializer()) : NullTracer.Instance;
            _perfService = new PerfService(_defaultLogger);

            ChainSpec chainSpec = LoadChainSpec(initParams.ChainSpecPath);
            await InitBlockchain(chainSpec, initParams.IsMining ?? false, initParams.FakeMiningDelay ?? 12000, initParams.SynchronizationEnabled, initParams.P2PPort ?? 30303, initParams);
            _defaultLogger.Info("Ethereum initialization completed"); // TODO: this is not done very well, start should be async as well
        }

        public async Task StopAsync()
        {
            _networkLogger.Info("Shutting down...");
            _runnerCancellation.Cancel();
            _networkLogger.Info("Stopping sync manager...");
            await (_syncManager?.StopAsync() ?? Task.CompletedTask);
            _networkLogger.Info("Stopping blockchain processor...");
            await (_blockchainProcessor?.StopAsync() ?? Task.CompletedTask);
            _networkLogger.Info("Stopping local peer...");
            await (_localPeer?.Shutdown() ?? Task.CompletedTask);
            _networkLogger.Info("Goodbye...");
        }

        private ChainSpec LoadChainSpec(string chainSpecFile)
        {
            _defaultLogger.Info($"Loading ChainSpec from {chainSpecFile}");
            ChainSpecLoader loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            if (!Path.IsPathRooted(chainSpecFile))
            {
                chainSpecFile = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, chainSpecFile));
            }

            ChainSpec chainSpec = loader.Load(File.ReadAllBytes(chainSpecFile));
            return chainSpec;
        }

        private async Task InitBlockchain(ChainSpec chainSpec, bool isMining, int miningDelay, bool shouldSynchronize, int listenPort, InitParams initParams)
        {
            /* spec */
            TimeSpan blockMiningTime = TimeSpan.FromMilliseconds(miningDelay);
            TimeSpan transactionDelay = TimeSpan.FromMilliseconds(miningDelay / 4);
            
            // TODO: most likely we will end up with the chainspec approach here as well
            ISpecProvider specProvider;
            if (chainSpec.ChainId == RopstenSpecProvider.Instance.ChainId)
            {
                specProvider = RopstenSpecProvider.Instance;
            }
            else if(chainSpec.ChainId == MainNetSpecProvider.Instance.ChainId)
            {
                specProvider = MainNetSpecProvider.Instance;
            }
            else
            {
                throw new NotSupportedException($"Not yet tested, not yet supported ChainId {chainSpec.ChainId}");
            }

            DifficultyCalculator difficultyCalculator = new DifficultyCalculator(specProvider);
            // var sealEngine = new EthashSealEngine(new Ethash());
            FakeSealEngine sealEngine = new FakeSealEngine(blockMiningTime, false);

            /* sync */
            TransactionStore transactionStore = new TransactionStore();
            DbOnTheRocks blocksDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.BlocksDbPath));
            DbOnTheRocks blockInfosDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.BlockInfosDbPath));
            DbOnTheRocks receiptsDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.ReceiptsDbPath));

            BlockTree blockTree = new BlockTree(blocksDb, blockInfosDb, receiptsDb, specProvider, _chainLogger);

            /* validation */
            HeaderValidator headerValidator = new HeaderValidator(difficultyCalculator, blockTree, sealEngine, specProvider, _chainLogger);
            OmmersValidator ommersValidator = new OmmersValidator(blockTree, headerValidator, _chainLogger);
            TransactionValidator txValidator = new TransactionValidator(new SignatureValidator(specProvider.ChainId));
            BlockValidator blockValidator = new BlockValidator(txValidator, headerValidator, ommersValidator, specProvider, _chainLogger);

            /* state */
            RocksDbProvider dbProvider = new RocksDbProvider(_dbBasePath, _stateLogger);
            DbOnTheRocks codeDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.CodeDbPath));
            DbOnTheRocks stateDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.StateDbPath));
            StateTree stateTree = new StateTree(stateDb);
            StateProvider stateProvider = new StateProvider(stateTree, _stateLogger, codeDb);
            StorageProvider storageProvider = new StorageProvider(dbProvider, stateProvider, _stateLogger);

            /* blockchain */
            EthereumSigner ethereumSigner = new EthereumSigner(specProvider, _chainLogger);

            /* blockchain processing */
            BlockhashProvider blockhashProvider = new BlockhashProvider(blockTree);
            VirtualMachine virtualMachine = new VirtualMachine(stateProvider, storageProvider, blockhashProvider, _evmLogger);
            TransactionProcessor transactionProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, _tracer, _chainLogger);
            RewardCalculator rewardCalculator = new RewardCalculator(specProvider);
            BlockProcessor blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, transactionProcessor, dbProvider, stateProvider, storageProvider, transactionStore, _chainLogger);
            _blockchainProcessor = new BlockchainProcessor(blockTree, sealEngine, transactionStore, difficultyCalculator, blockProcessor, ethereumSigner, _chainLogger);
            _blockchainProcessor.Start();

            if (blockTree.Genesis == null)
            {
                /* genesis */
                foreach (KeyValuePair<Address, BigInteger> allocation in chainSpec.Allocations)
                {
                    stateProvider.CreateAccount(allocation.Key, allocation.Value);
                }

                stateProvider.Commit(specProvider.GenesisSpec);

                Block genesis = chainSpec.Genesis;
                genesis.Header.StateRoot = stateProvider.StateRoot;
                genesis.Header.Hash = BlockHeader.CalculateHash(genesis.Header);

                ManualResetEvent genesisProcessedEvent = new ManualResetEvent(false);
                if (blockTree.Genesis == null)
                {
                    void GenesisProcessed(object sender, BlockEventArgs args)
                    {
                        genesisProcessedEvent.Set();
                    }

                    blockTree.NewHeadBlock += GenesisProcessed;
                    blockTree.SuggestBlock(genesis);
                    genesisProcessedEvent.WaitOne();
                    blockTree.NewHeadBlock -= GenesisProcessed;
                }
            }

            if (!string.IsNullOrWhiteSpace(initParams.ExpectedGenesisHash) && blockTree.Genesis.Hash != new Keccak(initParams.ExpectedGenesisHash))
            {
                throw new Exception($"Unexpected genesis hash, expected {initParams.ExpectedGenesisHash}, but was {blockTree.Genesis.Hash}");
            }

            if (isMining)
            {
                TestTransactionsGenerator testTransactionsGenerator = new TestTransactionsGenerator(transactionStore, ethereumSigner, transactionDelay, _chainLogger);
//                stateProvider.CreateAccount(testTransactionsGenerator.SenderAddress, 1000.Ether());
//                stateProvider.Commit(specProvider.GenesisSpec);
                testTransactionsGenerator.Start();
            }

            /* start test processing */
            sealEngine.IsMining = isMining;

            await blockTree.LoadBlocksFromDb(_runnerCancellation.Token).ContinueWith(async t =>
            {
                if (t.IsFaulted)
                {
                    if (_chainLogger.IsErrorEnabled)
                    {
                        _chainLogger.Error("Loading blocks from DB failed.", t.Exception);
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_chainLogger.IsWarnEnabled)
                    {
                        _chainLogger.Warn("Loading blocks from DB cancelled.");
                    }
                }
                else
                {
                    if (_chainLogger.IsInfoEnabled)
                    {
                        _chainLogger.Info("Loaded all blocks from DB, starting sync manager.");
                    }

                    if (shouldSynchronize)
                    {
                        // TODO: only start sync manager after queued blocks are processed
                        _syncManager = new SynchronizationManager(
                            blockTree,
                            blockValidator,
                            headerValidator,
                            transactionStore,
                            txValidator,
                            _networkLogger);

                        _syncManager.Start();

                        await InitNet(listenPort);

                        // create shared objects between discovery and peer manager
                        _nodeFactory = new NodeFactory();
                        _nodeStatsProvider = new NodeStatsProvider(_discoveryConfigurationProvider);

                        if (initParams.DiscoveryEnabled)
                        {
                            await InitDiscovery(initParams);
                        }
                        else if (_discoveryLogger.IsInfoEnabled)
                        {
                            _discoveryLogger.Info("Discovery is disabled");
                        }

                        await InitPeerManager();
                    }
                }
            });
        }

        private async Task InitNet(int listenPort)
        {
            /* tools */
            _messageSerializationService = new MessageSerializationService();
            _cryptoRandom = new CryptoRandom();
            _signer = new Signer();

            /* rlpx */
            EciesCipher eciesCipher = new EciesCipher(_cryptoRandom);
            Eip8MessagePad eip8Pad = new Eip8MessagePad(_cryptoRandom);
            _messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            EncryptionHandshakeService encryptionHandshakeServiceA = new EncryptionHandshakeService(_messageSerializationService, eciesCipher, _cryptoRandom, _signer, _privateKey, _networkLogger);

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

            _networkLogger.Info("Initializing server...");
            _localPeer = new RlpxPeer(_privateKey.PublicKey, listenPort, encryptionHandshakeServiceA, _messageSerializationService, _syncManager, _networkLogger);
            await _localPeer.Init();

            IPAddress localIp = _networkHelper.GetLocalIp();
            _networkLogger.Info($"Node is up and listening on {localIp}:{listenPort}... press ENTER to exit");
            _networkLogger.Info($"enode://{_privateKey.PublicKey.ToString(false)}@{localIp}:{listenPort}");
        }

        private async Task InitPeerManager()
        {
            _networkLogger.Info("Initializing Peer Manager");

            PeerStorage peerStorage = new PeerStorage(_discoveryConfigurationProvider, _nodeFactory, _networkLogger, _perfService);
            PeerManager peerManager = new PeerManager(_localPeer, _discoveryManager, _networkLogger, _discoveryConfigurationProvider, _syncManager, _nodeStatsProvider, peerStorage, _perfService);
            await peerManager.Start();

            _networkLogger.Info("Peer Manager initialization completed");
        }

        private Task InitDiscovery(InitParams initParams)
        {
            _discoveryLogger.Info("Initializing Discovery");

            if (initParams.DiscoveryPort.HasValue)
            {
                _discoveryConfigurationProvider.MasterPort = initParams.DiscoveryPort.Value;
            }

            PrivateKeyProvider privateKeyProvider = new PrivateKeyProvider(_privateKey);
            DiscoveryMessageFactory discoveryMessageFactory = new DiscoveryMessageFactory(_discoveryConfigurationProvider);
            NodeIdResolver nodeIdResolver = new NodeIdResolver(_signer);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(_messageSerializationService, _signer, privateKeyProvider, discoveryMessageFactory, nodeIdResolver, _nodeFactory);
            msgSerializersProvider.RegisterDiscoverySerializers();

            ConfigurationProvider configProvider = new ConfigurationProvider();
            JsonSerializer jsonSerializer = new JsonSerializer(_discoveryLogger);
            AesEncrypter encrypter = new AesEncrypter(configProvider, _discoveryLogger);
            FileKeyStore keyStore = new FileKeyStore(configProvider, jsonSerializer, encrypter, _cryptoRandom, _discoveryLogger);
            NodeDistanceCalculator nodeDistanceCalculator = new NodeDistanceCalculator(_discoveryConfigurationProvider);
            NodeTable nodeTable = new NodeTable(_discoveryConfigurationProvider, _nodeFactory, keyStore, _discoveryLogger, nodeDistanceCalculator);

            EvictionManager evictionManager = new EvictionManager(nodeTable, _discoveryLogger);
            NodeLifecycleManagerFactory nodeLifeCycleFactory = new NodeLifecycleManagerFactory(_nodeFactory, nodeTable, _discoveryLogger, _discoveryConfigurationProvider, discoveryMessageFactory, evictionManager, _nodeStatsProvider);

            DiscoveryStorage discoveryStorage = new DiscoveryStorage(_discoveryConfigurationProvider, _nodeFactory, _discoveryLogger, _perfService);
            _discoveryManager = new DiscoveryManager(_discoveryLogger, _discoveryConfigurationProvider, nodeLifeCycleFactory, _nodeFactory, nodeTable, discoveryStorage);

            NodesLocator nodesLocator = new NodesLocator(nodeTable, _discoveryManager, _discoveryConfigurationProvider, _discoveryLogger);
            _discoveryApp = new DiscoveryApp(_discoveryConfigurationProvider, nodesLocator, _discoveryLogger, _discoveryManager, _nodeFactory, nodeTable, _messageSerializationService, _cryptoRandom, discoveryStorage);
            _discoveryApp.Start(_privateKey.PublicKey);

            _discoveryLogger.Info("Discovery initialization completed");

            return Task.CompletedTask;
        }
    }
}