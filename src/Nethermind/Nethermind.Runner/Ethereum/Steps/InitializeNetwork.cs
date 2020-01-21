//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Synchronization.FastSync;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Initializers;
using Nethermind.DataMarketplace.Subprotocols.Serializers;
using Nethermind.Db;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;
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
using Nethermind.Store.BeamSync;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(LoadGenesisBlock), typeof(UpdateDiscoveryConfig), typeof(LoadChainspec), typeof(SetupKeyStore))]
    public class InitializeNetwork : IStep
    {
        private const string DiscoveryNodesDbPath = "discoveryNodes";
        private const string PeersDbPath = "peers";
    
        private readonly EthereumRunnerContext _context;

        public InitializeNetwork(EthereumRunnerContext context)
        {
            _context = context;
        }

        public async Task Execute()
        {
            await Initialize();
        }
        
        private async Task Initialize()
        {
            if (_context.NetworkConfig.DiagTracerEnabled)
            {
                NetworkDiagTracer.IsEnabled = true;
                NetworkDiagTracer.Start();
            }

            int maxPeersCount = _context.NetworkConfig.ActivePeersMaxCount;
            _context.SyncPeerPool = new EthSyncPeerPool(_context.BlockTree, _context.NodeStatsManager, _context.Config<ISyncConfig>(), maxPeersCount, _context.LogManager);
            NodeDataFeed feed = new NodeDataFeed(_context.DbProvider.CodeDb, _context.DbProvider.StateDb, _context.LogManager);
            NodeDataDownloader nodeDataDownloader = new NodeDataDownloader(_context.SyncPeerPool, feed, NullDataConsumer.Instance, _context.LogManager);
            _context.Synchronizer = new Synchronizer(_context.SpecProvider, _context.BlockTree, _context.ReceiptStorage, _context.BlockValidator, _context.SealValidator, _context.SyncPeerPool, _context.Config<ISyncConfig>(), nodeDataDownloader, _context.NodeStatsManager, _context.LogManager);
            _context.DisposeStack.Push(_context.Synchronizer);

            _context.SyncServer = new SyncServer(
                _context.DbProvider.StateDb,
                _context.DbProvider.CodeDb,
                _context.BlockTree,
                _context.ReceiptStorage,
                _context.BlockValidator,
                _context.SealValidator,
                _context.SyncPeerPool,
                _context.Synchronizer,
                _context.Config<ISyncConfig>(),
                _context.LogManager);

            InitDiscovery();
            await InitPeer().ContinueWith(initPeerTask =>
            {
                if (initPeerTask.IsFaulted)
                {
                    _context.Logger.Error("Unable to init the peer manager.", initPeerTask.Exception);
                }
            });

            await StartSync().ContinueWith(initNetTask =>
            {
                if (initNetTask.IsFaulted)
                {
                    _context.Logger.Error("Unable to start the synchronizer.", initNetTask.Exception);
                }
            });

            await StartDiscovery().ContinueWith(initDiscoveryTask =>
            {
                if (initDiscoveryTask.IsFaulted)
                {
                    _context.Logger.Error("Unable to start the discovery protocol.", initDiscoveryTask.Exception);
                }
            });

            try
            {
                StartPeer();
            }
            catch (Exception e)
            {
                _context.Logger.Error("Unable to start the peer manager.", e);
            }

            ThisNodeInfo.AddInfo("Ethereum     :", $"tcp://{_context.Enode.HostIp}:{_context.Enode.Port}");
            ThisNodeInfo.AddInfo("Version      :", $"{ClientVersion.Description}");
            ThisNodeInfo.AddInfo("This node    :", $"{_context.Enode.Info}");
            ThisNodeInfo.AddInfo("Node address :", $"{_context.Enode.Address} (do not use as an account)");
        }
        
        private Task StartDiscovery()
        {
            if (!_context.Config<IInitConfig>().DiscoveryEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping discovery init due to ({nameof(IInitConfig.DiscoveryEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug("Starting discovery process.");
            _context.DiscoveryApp.Start();
            if (_context.Logger.IsDebug) _context.Logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }
        
        private void StartPeer()
        {
            if (!_context.Config<IInitConfig>().PeerManagerEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false)");
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug("Initializing peer manager");
            _context.PeerManager.Start();
            _context.SessionMonitor.Start();
            if (_context.Logger.IsDebug) _context.Logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            if (!_context.Config<IInitConfig>().DiscoveryEnabled)
            {
                _context.DiscoveryApp = new NullDiscoveryApp();
                return;
            }

            IDiscoveryConfig discoveryConfig = _context.Config<IDiscoveryConfig>();

            SameKeyGenerator privateKeyProvider = new SameKeyGenerator(_context.NodeKey);
            DiscoveryMessageFactory discoveryMessageFactory = new DiscoveryMessageFactory(_context.Timestamper);
            NodeIdResolver nodeIdResolver = new NodeIdResolver(_context.EthereumEcdsa);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _context._messageSerializationService,
                _context.EthereumEcdsa,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver);

            msgSerializersProvider.RegisterDiscoverySerializers();

            NodeDistanceCalculator nodeDistanceCalculator = new NodeDistanceCalculator(discoveryConfig);

            NodeTable nodeTable = new NodeTable(nodeDistanceCalculator, discoveryConfig, _context.NetworkConfig, _context.LogManager);
            EvictionManager evictionManager = new EvictionManager(nodeTable, _context.LogManager);

            NodeLifecycleManagerFactory nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _context.NodeStatsManager,
                discoveryConfig,
                _context.LogManager);

            SimpleFilePublicKeyDb discoveryDb = new SimpleFilePublicKeyDb("DiscoveryDB", DiscoveryNodesDbPath.GetApplicationResourcePath(_context.Config<IInitConfig>().BaseDbPath), _context.LogManager);
            NetworkStorage discoveryStorage = new NetworkStorage(
                discoveryDb,
                _context.LogManager);

            DiscoveryManager discoveryManager = new DiscoveryManager(
                nodeLifeCycleFactory,
                nodeTable,
                discoveryStorage,
                discoveryConfig,
                _context.LogManager);

            NodesLocator nodesLocator = new NodesLocator(
                nodeTable,
                discoveryManager,
                discoveryConfig,
                _context.LogManager);

            _context.DiscoveryApp = new DiscoveryApp(
                nodesLocator,
                discoveryManager,
                nodeTable,
                _context._messageSerializationService,
                _context.CryptoRandom,
                discoveryStorage,
                _context.NetworkConfig,
                discoveryConfig,
                _context.Timestamper,
                _context.LogManager);

            _context.DiscoveryApp.Initialize(_context.NodeKey.PublicKey);
        }
        
          private Task StartSync()
        {
            if (!_context.Config<ISyncConfig>().SynchronizationEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping blockchain synchronization init due to ({nameof(ISyncConfig.SynchronizationEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug($"Starting synchronization from block {_context.BlockTree.Head.ToString(BlockHeader.Format.Short)}.");

            _context.SyncPeerPool.Start();
            _context.Synchronizer.Start();
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            /* rlpx */
            EciesCipher eciesCipher = new EciesCipher(_context.CryptoRandom);
            Eip8MessagePad eip8Pad = new Eip8MessagePad(_context.CryptoRandom);
            _context._messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _context._messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            _context._messageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer)));
            _context._messageSerializationService.Register(new ReceiptsMessageSerializer(_context.SpecProvider));

            HandshakeService encryptionHandshakeServiceA = new HandshakeService(_context._messageSerializationService, eciesCipher,
                _context.CryptoRandom, new Ecdsa(), _context.NodeKey, _context.LogManager);

            _context._messageSerializationService.Register(Assembly.GetAssembly(typeof(HiMessageSerializer)));

            IDiscoveryConfig discoveryConfig = _context.Config<IDiscoveryConfig>();
            IInitConfig initConfig = _context.Config<IInitConfig>();

            _context.SessionMonitor = new SessionMonitor(_context.NetworkConfig, _context.LogManager);
            _context.RlpxPeer = new RlpxPeer(
                _context._messageSerializationService,
                _context.NodeKey.PublicKey,
                _context.NetworkConfig.P2PPort,
                encryptionHandshakeServiceA,
                _context.LogManager,
                _context.SessionMonitor);

            await _context.RlpxPeer.Init();

            _context.StaticNodesManager = new StaticNodesManager(initConfig.StaticNodesPath, _context.LogManager);
            await _context.StaticNodesManager.InitAsync();

            SimpleFilePublicKeyDb peersDb = new SimpleFilePublicKeyDb("PeersDB", PeersDbPath.GetApplicationResourcePath(initConfig.BaseDbPath), _context.LogManager);
            NetworkStorage peerStorage = new NetworkStorage(peersDb, _context.LogManager);

            ProtocolValidator protocolValidator = new ProtocolValidator(_context.NodeStatsManager, _context.BlockTree, _context.LogManager);
            _context.ProtocolsManager = new ProtocolsManager(_context.SyncPeerPool, _context.SyncServer, _context.TxPool, _context.DiscoveryApp, _context._messageSerializationService, _context.RlpxPeer, _context.NodeStatsManager, protocolValidator, peerStorage, _context.LogManager);

            if (!(_context.NdmInitializer is null))
            {
                if (_context.Logger.IsInfo) _context.Logger.Info($"Initializing NDM...");
                _context.HttpClient = new DefaultHttpClient(new HttpClient(), _context.EthereumJsonSerializer, _context.LogManager);
                INdmConfig ndmConfig = _context.Config<INdmConfig>();
                if (ndmConfig.ProxyEnabled)
                {
                    _context.JsonRpcClientProxy = new JsonRpcClientProxy(_context.HttpClient, ndmConfig.JsonRpcUrlProxies,
                        _context.LogManager);
                    _context.EthJsonRpcClientProxy = new EthJsonRpcClientProxy(_context.JsonRpcClientProxy);
                }

                FilterStore filterStore = new FilterStore();
                FilterManager filterManager = new FilterManager(filterStore, _context.BlockProcessor, _context.TxPool, _context.LogManager);
                INdmCapabilityConnector capabilityConnector = await _context.NdmInitializer.InitAsync(_context.ConfigProvider, _context.DbProvider,
                    initConfig.BaseDbPath, _context.BlockTree, _context.TxPool, _context.SpecProvider, _context.ReceiptStorage, _context.Wallet, filterStore,
                    filterManager, _context.Timestamper, _context.EthereumEcdsa, _context.RpcModuleProvider, _context.KeyStore, _context.EthereumJsonSerializer,
                    _context.CryptoRandom, _context.Enode, _context.NdmConsumerChannelManager, _context.NdmDataPublisher, _context.GrpcServer,
                    _context.NodeStatsManager, _context.ProtocolsManager, protocolValidator, _context._messageSerializationService,
                    initConfig.EnableUnsecuredDevWallet, _context.WebSocketsManager, _context.LogManager, _context.BlockProcessor,
                    _context.JsonRpcClientProxy, _context.EthJsonRpcClientProxy, _context.HttpClient, _context.MonitoringService);
                capabilityConnector.Init();
                if (_context.Logger.IsInfo) _context.Logger.Info($"NDM initialized.");
            }

            PeerLoader peerLoader = new PeerLoader(_context.NetworkConfig, discoveryConfig, _context.NodeStatsManager, peerStorage, _context.LogManager);
            _context.PeerManager = new PeerManager(_context.RlpxPeer, _context.DiscoveryApp, _context.NodeStatsManager, peerStorage, peerLoader, _context.NetworkConfig, _context.LogManager, _context.StaticNodesManager);
            _context.PeerManager.Init();
        }
    }
}