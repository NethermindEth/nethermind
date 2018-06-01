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
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.ChainSpec;
using Nethermind.Db;
using Nethermind.Discovery;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.Messages;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Serializers;
using Nethermind.Evm;
using Nethermind.KeyStore;
using Nethermind.Network;
using Nethermind.Network.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Runner.Data;
using Nethermind.Store;
using PingMessageSerializer = Nethermind.Network.P2P.PingMessageSerializer;
using PongMessageSerializer = Nethermind.Network.P2P.PongMessageSerializer;

namespace Nethermind.Runner.Runners
{
    public class EthereumRunner : IEthereumRunner
    {
        private IBlockchainProcessor _blockchainProcessor;
        private IRlpxPeer _localPeer;
        private IPeerManager _peerManager;
        private PrivateKey _privateKey;
        private ISynchronizationManager _syncManager;
        private ITransactionTracer _tracer;
        private IDiscoveryApp _discoveryApp;
        private IDiscoveryConfigurationProvider _discoveryConfigurationProvider;
        private IMessageSerializationService _messageSerializationService;
        private ISigner _signer;
        private ICryptoRandom _cryptoRandom;
        private CancellationTokenSource _runnerCancellation;

        private static ILogger _defaultLogger = new NLogLogger("default");
        private static ILogger _evmLogger = new NLogLogger("evm");
        private static ILogger _stateLogger = new NLogLogger("state");
        private static ILogger _chainLogger = new NLogLogger("chain");
        private static ILogger _networkLogger = new NLogLogger("net");
        private static ILogger _discoveryLogger = new NLogLogger("discovery");

        public EthereumRunner(IDiscoveryConfigurationProvider configurationProvider)
        {
            _discoveryConfigurationProvider = configurationProvider;
        }

        public async Task Start(InitParams initParams)
        {
            _runnerCancellation = new CancellationTokenSource();
            _defaultLogger = new NLogLogger(initParams.LogFileName, "default");
            _evmLogger = new NLogLogger(initParams.LogFileName, "evm");
            _stateLogger = new NLogLogger(initParams.LogFileName, "state");
            _chainLogger = new NLogLogger(initParams.LogFileName, "chain");
            _networkLogger = new NLogLogger(initParams.LogFileName, "net");

            _defaultLogger.Info("Initializing Ethereum");
            _privateKey = new PrivateKey(initParams.TestNodeKey);
            _dbBasePath = initParams.BaseDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "db");

            _tracer = initParams.TransactionTracingEnabled ? new TransactionTracer(initParams.BaseTracingPath, new UnforgivingJsonSerializer()) : NullTracer.Instance; 
            
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
            var loader = new ChainSpecLoader(new UnforgivingJsonSerializer());
            if (!Path.IsPathRooted(chainSpecFile))
            {
                chainSpecFile = Path.Combine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, chainSpecFile));
            }

            var chainSpec = loader.Load(File.ReadAllBytes(chainSpecFile));
            return chainSpec;
        }

        private static string _dbBasePath;

        private async Task InitBlockchain(ChainSpec chainSpec, bool isMining, int miningDelay, bool shouldSynchronize, int listenPort, InitParams initParams)
        {
            /* spec */
            var blockMiningTime = TimeSpan.FromMilliseconds(miningDelay);
            var transactionDelay = TimeSpan.FromMilliseconds(miningDelay / 4);
            var specProvider = RopstenSpecProvider.Instance;
            var difficultyCalculator = new DifficultyCalculator(specProvider);
            // var sealEngine = new EthashSealEngine(new Ethash());
            var sealEngine = new FakeSealEngine(blockMiningTime, false);

            /* sync */
            var transactionStore = new TransactionStore();
            var blocksDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.BlocksDbPath));
            var blockInfosDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.BlockInfosDbPath));
            var receiptsDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.ReceiptsDbPath));

            var blockTree = new BlockTree(blocksDb, blockInfosDb, receiptsDb, RopstenSpecProvider.Instance, _chainLogger);

            /* validation */
            var headerValidator = new HeaderValidator(difficultyCalculator, blockTree, sealEngine, specProvider, _chainLogger);
            var ommersValidator = new OmmersValidator(blockTree, headerValidator, _chainLogger);
            var txValidator = new TransactionValidator(new SignatureValidator(ChainId.Ropsten));
            var blockValidator = new BlockValidator(txValidator, headerValidator, ommersValidator, specProvider, _chainLogger);

            /* state */
            var dbProvider = new RocksDbProvider(_dbBasePath, _stateLogger);
            var codeDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.CodeDbPath));
            var stateDb = new DbOnTheRocks(Path.Combine(_dbBasePath, DbOnTheRocks.StateDbPath));
            var stateTree = new StateTree(stateDb);
            var stateProvider = new StateProvider(stateTree, _stateLogger, codeDb);
            var storageProvider = new StorageProvider(dbProvider, stateProvider, _stateLogger);

            /* blockchain */
            var ethereumSigner = new EthereumSigner(specProvider, _chainLogger);

            /* blockchain processing */
            var blockhashProvider = new BlockhashProvider(blockTree);
            var virtualMachine = new VirtualMachine(specProvider, stateProvider, storageProvider, blockhashProvider, _evmLogger);
            var transactionProcessor = new TransactionProcessor(specProvider, stateProvider, storageProvider, virtualMachine, ethereumSigner, _tracer, _chainLogger);
            var rewardCalculator = new RewardCalculator(specProvider);
            var blockProcessor = new BlockProcessor(specProvider, blockValidator, rewardCalculator, transactionProcessor, dbProvider, stateProvider, storageProvider, transactionStore, _chainLogger);
            _blockchainProcessor = new BlockchainProcessor(blockTree, sealEngine, transactionStore, difficultyCalculator, blockProcessor, _chainLogger);
            
            var expectedGenesisHash = "0x41941023680923e0fe4d74a34bdac8141f2540e3ae90623718e47d66d1ca4a2d";
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
                
                if (blockTree.Genesis == null)
                {
                    blockTree.SuggestBlock(genesis);
                }
                
                // we are adding test transactions account so the state root will change (not an actual ropsten at the moment)
                if (chainSpec.Genesis.Hash != new Keccak(expectedGenesisHash))
                {
                    throw new Exception($"Unexpected genesis hash for Ropsten, expected {expectedGenesisHash}, but was {chainSpec.Genesis.Hash}");
                }
            }
            else
            {
                if (blockTree.Genesis.Hash != new Keccak(expectedGenesisHash))
                {
                    throw new Exception($"Unexpected genesis hash for Ropsten, expected {expectedGenesisHash}, but was {chainSpec.Genesis.Hash}");
                }
            }

            if (isMining)
            {
                var testTransactionsGenerator = new TestTransactionsGenerator(transactionStore, ethereumSigner, transactionDelay, _chainLogger);
//                stateProvider.CreateAccount(testTransactionsGenerator.SenderAddress, 1000.Ether());
//                stateProvider.Commit(specProvider.GenesisSpec);
                testTransactionsGenerator.Start();
            }

            /* start test processing */
            sealEngine.IsMining = isMining;

            _blockchainProcessor.Start();
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
                        await InitNet(chainSpec, listenPort);
                        // TODO: only start sync manager after queued blocks are processed
                        _syncManager = new SynchronizationManager(
                            blockTree,
                            blockValidator,
                            headerValidator,
                            transactionStore,
                            txValidator,
                            _networkLogger);

                        _syncManager.Start();

                        if (initParams.DiscoveryEnabled)
                        {
                            await InitDiscovery(initParams);
                        }
                    }
                }
            });
        }
        
        private async Task InitNet(ChainSpec chainSpec, int listenPort)
        {
            /* tools */
            _messageSerializationService = new MessageSerializationService();
            _cryptoRandom = new CryptoRandom();
            _signer = new Signer();

            /* rlpx */
            var eciesCipher = new EciesCipher(_cryptoRandom);
            var eip8Pad = new Eip8MessagePad(_cryptoRandom);
            _messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            var encryptionHandshakeServiceA = new EncryptionHandshakeService(_messageSerializationService, eciesCipher, _cryptoRandom, _signer, _privateKey, _networkLogger);

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

            // https://stackoverflow.com/questions/6803073/get-local-ip-address?utm_medium=organic&utm_source=google_rich_qa&utm_campaign=google_rich_qa
            string localIp;
            //TODO you can use NetworkHelper for that - I use it in the same way in discovery - i changed it to lazy load impl so we can find it once and share between discovery and peer
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                Debug.Assert(endPoint != null, "null endpoint when connecting a UDP socket");
                localIp = endPoint.Address.ToString();
            }

            _networkLogger.Info($"Node is up and listening on {localIp}:{listenPort}... press ENTER to exit");
            _networkLogger.Info($"enode://{_privateKey.PublicKey.ToString(false)}@{localIp}:{listenPort}");

            //foreach (NetworkNode bootnode in chainSpec.NetworkNodes)
            //{
            //    bootnode.Host = bootnode.Host == "127.0.0.1" ? localIp : bootnode.Host;
            //    _networkLogger.Info($"Connecting to {bootnode.Description}@{bootnode.Host}:{bootnode.Port}");
            //    await _localPeer.ConnectAsync(bootnode.PublicKey, bootnode.Host, bootnode.Port).ContinueWith(
            //        t =>
            //        {
            //            if (t.IsFaulted)
            //            {
            //                _networkLogger.Error($"Connection to {bootnode.Description}@{bootnode.Host}:{bootnode.Port} failed.", t.Exception);
            //            }
            //            else
            //            {
            //                _networkLogger.Info($"Established connection with {bootnode.Description}@{bootnode.Host}:{bootnode.Port}");
            //            }
            //        });

            //    _networkLogger.Info("Testnet connected...");
            //}
        }

        private Task InitDiscovery(InitParams initParams)
        {            
            _discoveryLogger.Info("Initializing Discovery");
            if (initParams.DiscoveryPort.HasValue)
            {
                _discoveryConfigurationProvider.MasterPort = initParams.DiscoveryPort.Value;
            }

            var privateKeyProvider = new PrivateKeyProvider(_privateKey);
            var discoveryMessageFactory = new DiscoveryMessageFactory(_discoveryConfigurationProvider);
            var nodeIdResolver = new NodeIdResolver(_signer);
            var nodeFactory = new NodeFactory();
            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(_messageSerializationService, _signer, privateKeyProvider, discoveryMessageFactory, nodeIdResolver, nodeFactory);
            msgSerializersProvider.RegisterDiscoverySerializers();

            var configProvider = new ConfigurationProvider();
            var jsonSerializer = new JsonSerializer(_discoveryLogger);
            var encrypter = new AesEncrypter(configProvider, _discoveryLogger);
            var keyStore = new FileKeyStore(configProvider, jsonSerializer, encrypter, _cryptoRandom, _discoveryLogger);
            var nodeDistanceCalculator = new NodeDistanceCalculator(_discoveryConfigurationProvider);
            var nodeTable = new NodeTable(_discoveryConfigurationProvider, nodeFactory, keyStore, _discoveryLogger, nodeDistanceCalculator);

            var evictionManager = new EvictionManager(nodeTable, _discoveryLogger);
            var nodeLifeCycleFactory = new NodeLifecycleManagerFactory(nodeFactory, nodeTable, _discoveryLogger, _discoveryConfigurationProvider, discoveryMessageFactory, evictionManager);
            var discoveryManager = new DiscoveryManager(_discoveryLogger, _discoveryConfigurationProvider, nodeLifeCycleFactory, nodeFactory, nodeTable);

            var nodesLocator = new NodesLocator(nodeTable, discoveryManager, _discoveryConfigurationProvider, _discoveryLogger);
            var discoveryStorage = new DiscoveryStorage(_discoveryConfigurationProvider, nodeFactory);
            _discoveryApp = new DiscoveryApp(_discoveryConfigurationProvider, nodesLocator, _discoveryLogger, discoveryManager, nodeFactory, nodeTable, _messageSerializationService, _cryptoRandom, discoveryStorage);

            var peerManager = new PeerManager(_localPeer, discoveryManager, _discoveryLogger, nodeFactory, _discoveryConfigurationProvider, _syncManager);
            peerManager.StartPeerTimer();

            _discoveryApp.Start(_privateKey.PublicKey);

            _discoveryLogger.Info("Discovery initialization completed");

            return Task.CompletedTask;
        }

    }
}