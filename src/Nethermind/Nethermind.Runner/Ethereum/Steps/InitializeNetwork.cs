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
using Nethermind.Core.Crypto;
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
using Nethermind.Store.BeamSyncStore;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependency(typeof(InitializeBlockchain), typeof(UpdateDiscoveryConfig), typeof(LoadChainspec), typeof(SetupKeyStore))]
    public class InitializeNetwork : IStep
    {
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

            var maxPeersCount = _context.NetworkConfig.ActivePeersMaxCount;
            _context._syncPeerPool = new EthSyncPeerPool(_context.BlockTree, _context._nodeStatsManager, _context._syncConfig, maxPeersCount, _context.LogManager);
            NodeDataFeed feed = new NodeDataFeed(_context._dbProvider.CodeDb, _context._dbProvider.StateDb, _context.LogManager);
            NodeDataDownloader nodeDataDownloader = new NodeDataDownloader(_context._syncPeerPool, feed, NullDataConsumer.Instance, _context.LogManager);
            _context._synchronizer = new Synchronizer(_context.SpecProvider, _context.BlockTree, _context._receiptStorage, _context._blockValidator, _context._sealValidator, _context._syncPeerPool, _context._syncConfig, nodeDataDownloader, _context._nodeStatsManager, _context.LogManager);

            _context._syncServer = new SyncServer(
                _context._dbProvider.StateDb,
                _context._dbProvider.CodeDb,
                _context.BlockTree,
                _context._receiptStorage,
                _context._blockValidator,
                _context._sealValidator,
                _context._syncPeerPool,
                _context._synchronizer,
                _context._syncConfig,
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

            if (_context.Logger.IsInfo) _context.Logger.Info($"Ethereum     : tcp://{_context._enode.HostIp}:{_context._enode.Port}");
            if (_context.Logger.IsInfo) _context.Logger.Info($"Version      : {ClientVersion.Description}");
            if (_context.Logger.IsInfo) _context.Logger.Info($"This node    : {_context._enode.Info}");
            if (_context.Logger.IsInfo) _context.Logger.Info($"Node address : {_context._enode.Address} (do not use as an account)");
        }
        
        private Task StartDiscovery()
        {
            if (!_context._initConfig.DiscoveryEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping discovery init due to ({nameof(IInitConfig.DiscoveryEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug("Starting discovery process.");
            _context._discoveryApp.Start();
            if (_context.Logger.IsDebug) _context.Logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }
        
        private void StartPeer()
        {
            if (!_context._initConfig.PeerManagerEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping peer manager init due to {nameof(_context._initConfig.PeerManagerEnabled)} set to false)");
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug("Initializing peer manager");
            _context.PeerManager.Start();
            _context._sessionMonitor.Start();
            if (_context.Logger.IsDebug) _context.Logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            if (!_context._initConfig.DiscoveryEnabled)
            {
                _context._discoveryApp = new NullDiscoveryApp();
                return;
            }

            IDiscoveryConfig discoveryConfig = _context._configProvider.GetConfig<IDiscoveryConfig>();

            var privateKeyProvider = new SameKeyGenerator(_context._nodeKey);
            var discoveryMessageFactory = new DiscoveryMessageFactory(_context._timestamper);
            var nodeIdResolver = new NodeIdResolver(_context._ethereumEcdsa);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _context._messageSerializationService,
                _context._ethereumEcdsa,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver);

            msgSerializersProvider.RegisterDiscoverySerializers();

            var nodeDistanceCalculator = new NodeDistanceCalculator(discoveryConfig);

            var nodeTable = new NodeTable(nodeDistanceCalculator, discoveryConfig, _context.NetworkConfig, _context.LogManager);
            var evictionManager = new EvictionManager(nodeTable, _context.LogManager);

            var nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _context._nodeStatsManager,
                discoveryConfig,
                _context.LogManager);

            var discoveryDb = new SimpleFilePublicKeyDb("DiscoveryDB", _context.DiscoveryNodesDbPath.GetApplicationResourcePath(_context._initConfig.BaseDbPath), _context.LogManager);
            var discoveryStorage = new NetworkStorage(
                discoveryDb,
                _context.LogManager);

            var discoveryManager = new DiscoveryManager(
                nodeLifeCycleFactory,
                nodeTable,
                discoveryStorage,
                discoveryConfig,
                _context.LogManager);

            var nodesLocator = new NodesLocator(
                nodeTable,
                discoveryManager,
                discoveryConfig,
                _context.LogManager);

            _context._discoveryApp = new DiscoveryApp(
                nodesLocator,
                discoveryManager,
                nodeTable,
                _context._messageSerializationService,
                _context._cryptoRandom,
                discoveryStorage,
                _context.NetworkConfig,
                discoveryConfig,
                _context._timestamper,
                _context.LogManager);

            _context._discoveryApp.Initialize(_context._nodeKey.PublicKey);
        }
        
          private Task StartSync()
        {
            if (!_context._syncConfig.SynchronizationEnabled)
            {
                if (_context.Logger.IsWarn) _context.Logger.Warn($"Skipping blockchain synchronization init due to ({nameof(ISyncConfig.SynchronizationEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_context.Logger.IsDebug) _context.Logger.Debug($"Starting synchronization from block {_context.BlockTree.Head.ToString(BlockHeader.Format.Short)}.");

            _context._syncPeerPool.Start();
            _context._synchronizer.Start();
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            /* rlpx */
            var eciesCipher = new EciesCipher(_context._cryptoRandom);
            var eip8Pad = new Eip8MessagePad(_context._cryptoRandom);
            _context._messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _context._messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            _context._messageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer)));
            _context._messageSerializationService.Register(new ReceiptsMessageSerializer(_context.SpecProvider));

            var encryptionHandshakeServiceA = new HandshakeService(_context._messageSerializationService, eciesCipher,
                _context._cryptoRandom, new Ecdsa(), _context._nodeKey, _context.LogManager);

            _context._messageSerializationService.Register(Assembly.GetAssembly(typeof(HiMessageSerializer)));

            var discoveryConfig = _context._configProvider.GetConfig<IDiscoveryConfig>();

            _context._sessionMonitor = new SessionMonitor(_context.NetworkConfig, _context.LogManager);
            _context._rlpxPeer = new RlpxPeer(
                _context._messageSerializationService,
                _context._nodeKey.PublicKey,
                _context.NetworkConfig.P2PPort,
                encryptionHandshakeServiceA,
                _context.LogManager,
                _context._sessionMonitor);

            await _context._rlpxPeer.Init();

            _context._staticNodesManager = new StaticNodesManager(_context._initConfig.StaticNodesPath, _context.LogManager);
            await _context._staticNodesManager.InitAsync();

            var peersDb = new SimpleFilePublicKeyDb("PeersDB", _context.PeersDbPath.GetApplicationResourcePath(_context._initConfig.BaseDbPath), _context.LogManager);
            var peerStorage = new NetworkStorage(peersDb, _context.LogManager);

            ProtocolValidator protocolValidator = new ProtocolValidator(_context._nodeStatsManager, _context.BlockTree, _context.LogManager);
            _context._protocolsManager = new ProtocolsManager(_context._syncPeerPool, _context._syncServer, _context._txPool, _context._discoveryApp, _context._messageSerializationService, _context._rlpxPeer, _context._nodeStatsManager, protocolValidator, peerStorage, _context.LogManager);

            if (!(_context._ndmInitializer is null))
            {
                if (_context.Logger.IsInfo) _context.Logger.Info($"Initializing NDM...");
                _context._httpClient = new DefaultHttpClient(new HttpClient(), _context._ethereumJsonSerializer, _context.LogManager);
                var ndmConfig = _context._configProvider.GetConfig<INdmConfig>();
                if (ndmConfig.ProxyEnabled)
                {
                    _context._jsonRpcClientProxy = new JsonRpcClientProxy(_context._httpClient, ndmConfig.JsonRpcUrlProxies,
                        _context.LogManager);
                    _context._ethJsonRpcClientProxy = new EthJsonRpcClientProxy(_context._jsonRpcClientProxy);
                }

                FilterStore filterStore = new FilterStore();
                FilterManager filterManager = new FilterManager(filterStore, _context._blockProcessor, _context._txPool, _context.LogManager);
                INdmCapabilityConnector capabilityConnector = await _context._ndmInitializer.InitAsync(_context._configProvider, _context._dbProvider,
                    _context._initConfig.BaseDbPath, _context.BlockTree, _context._txPool, _context.SpecProvider, _context._receiptStorage, _context._wallet, filterStore,
                    filterManager, _context._timestamper, _context._ethereumEcdsa, _context._rpcModuleProvider, _context._keyStore, _context._ethereumJsonSerializer,
                    _context._cryptoRandom, _context._enode, _context._ndmConsumerChannelManager, _context._ndmDataPublisher, _context._grpcServer,
                    _context._nodeStatsManager, _context._protocolsManager, protocolValidator, _context._messageSerializationService,
                    _context._initConfig.EnableUnsecuredDevWallet, _context._webSocketsManager, _context.LogManager, _context._blockProcessor,
                    _context._jsonRpcClientProxy, _context._ethJsonRpcClientProxy, _context._httpClient, _context._monitoringService);
                capabilityConnector.Init();
                if (_context.Logger.IsInfo) _context.Logger.Info($"NDM initialized.");
            }

            PeerLoader peerLoader = new PeerLoader(_context.NetworkConfig, discoveryConfig, _context._nodeStatsManager, peerStorage, _context.LogManager);
            _context.PeerManager = new PeerManager(_context._rlpxPeer, _context._discoveryApp, _context._nodeStatsManager, peerStorage, peerLoader, _context.NetworkConfig, _context.LogManager, _context._staticNodesManager);
            _context.PeerManager.Init();
        }
    }
}