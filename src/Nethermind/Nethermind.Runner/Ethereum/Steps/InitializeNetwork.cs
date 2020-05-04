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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Synchronization;
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
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Store;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.LesSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(LoadGenesisBlock), typeof(UpdateDiscoveryConfig), typeof(SetupKeyStore), typeof(InitializeNodeStats))]
    public class InitializeNetwork : IStep
    {
        private const string DiscoveryNodesDbPath = "discoveryNodes";
        private const string PeersDbPath = "peers";
        private const string ChtDbPath = "canonicalHashTrie";

        private readonly EthereumRunnerContext _ctx;
        private ILogger _logger;
        private INetworkConfig _networkConfig;
        private ISyncConfig _syncConfig;

        public InitializeNetwork(EthereumRunnerContext context)
        {
            _ctx = context;
            _logger = _ctx.LogManager.GetClassLogger();
            _networkConfig = _ctx.Config<INetworkConfig>();
            _syncConfig = _ctx.Config<ISyncConfig>();
        }

        public async Task Execute()
        {
            await Initialize();
        }

        private async Task Initialize()
        {
            if (_ctx.DbProvider == null) throw new StepDependencyException(nameof(_ctx.DbProvider));
            if (_ctx.BlockTree == null) throw new StepDependencyException(nameof(_ctx.BlockTree));
            if (_ctx.ReceiptStorage == null) throw new StepDependencyException(nameof(_ctx.ReceiptStorage));
            if (_ctx.BlockValidator == null) throw new StepDependencyException(nameof(_ctx.BlockValidator));
            if (_ctx.SealValidator == null) throw new StepDependencyException(nameof(_ctx.SealValidator));
            if (_ctx.Enode == null) throw new StepDependencyException(nameof(_ctx.Enode));

            if (_networkConfig.DiagTracerEnabled)
            {
                NetworkDiagTracer.IsEnabled = true;
                NetworkDiagTracer.Start();
            }

            // Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", "8192");
            ThisNodeInfo.AddInfo("Mem est netty:", $"{2 * Environment.ProcessorCount * (1 << _networkConfig.NettyArenaOrder) * 8192 / 1000 / 1000}MB".PadLeft(8));
            ThisNodeInfo.AddInfo("Mem est peers:", $"{_networkConfig.ActivePeersMaxCount}MB".PadLeft(8));
            Environment.SetEnvironmentVariable("io.netty.allocator.maxOrder", _networkConfig.NettyArenaOrder.ToString());

            var cht = new CanonicalHashTrie(_ctx.DbProvider.ChtDb);

            int maxPeersCount = _networkConfig.ActivePeersMaxCount;
            _ctx.SyncPeerPool = new SyncPeerPool(_ctx.BlockTree, _ctx.NodeStatsManager, maxPeersCount, _ctx.LogManager);
            _ctx.DisposeStack.Push(_ctx.SyncPeerPool);
            
            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(_ctx.BlockTree, _ctx.ReceiptStorage, _ctx.DbProvider.StateDb, _syncConfig, _ctx.LogManager);
            MultiSyncModeSelector syncModeSelector = new MultiSyncModeSelector(syncProgressResolver, _ctx.SyncPeerPool, _syncConfig, _ctx.LogManager);
            _ctx.SyncModeSelector = syncModeSelector;
            _ctx.DisposeStack.Push(syncModeSelector);
            
            _ctx.Synchronizer = new Synchronizer(
                _ctx.DbProvider,
                _ctx.SpecProvider,
                _ctx.BlockTree,
                _ctx.ReceiptStorage,
                _ctx.BlockValidator,
                _ctx.SealValidator,
                _ctx.SyncPeerPool,
                _ctx.NodeStatsManager,
                _ctx.SyncModeSelector,
                _syncConfig,
                _ctx.LogManager);
                
            _ctx.DisposeStack.Push(_ctx.Synchronizer);

            _ctx.SyncServer = new SyncServer(
                _ctx.DbProvider.StateDb,
                _ctx.DbProvider.CodeDb,
                _ctx.BlockTree,
                _ctx.ReceiptStorage,
                _ctx.BlockValidator,
                _ctx.SealValidator,
                _ctx.SyncPeerPool,
                _ctx.SyncModeSelector,
                _ctx.Synchronizer,
                _ctx.Config<ISyncConfig>(),
                _ctx.LogManager,
                cht);

            _ = _ctx.SyncServer.BuildCHT();

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

            ThisNodeInfo.AddInfo("Ethereum     :", $"tcp://{_ctx.Enode.HostIp}:{_ctx.Enode.Port}");
            ThisNodeInfo.AddInfo("Version      :", $"{ClientVersion.Description.Replace("Nethermind/v", string.Empty)}");
            ThisNodeInfo.AddInfo("This node    :", $"{_ctx.Enode.Info}");
            ThisNodeInfo.AddInfo("Node address :", $"{_ctx.Enode.Address} (do not use as an account)");
        }

        private Task StartDiscovery()
        {
            if (_ctx.DiscoveryApp == null) throw new StepDependencyException(nameof(_ctx.DiscoveryApp));

            if (!_ctx.Config<IInitConfig>().DiscoveryEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to ({nameof(IInitConfig.DiscoveryEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
            _ctx.DiscoveryApp.Start();
            if (_logger.IsDebug) _logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }

        private void StartPeer()
        {
            if (_ctx.PeerManager == null) throw new StepDependencyException(nameof(_ctx.PeerManager));
            if (_ctx.SessionMonitor == null) throw new StepDependencyException(nameof(_ctx.SessionMonitor));

            if (!_ctx.Config<IInitConfig>().PeerManagerEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false)");
            }

            if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
            _ctx.PeerManager.Start();
            _ctx.SessionMonitor.Start();
            if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            if (_ctx.NodeStatsManager == null) throw new StepDependencyException(nameof(_ctx.NodeStatsManager));
            if (_ctx.Timestamper == null) throw new StepDependencyException(nameof(_ctx.Timestamper));
            if (_ctx.NodeKey == null) throw new StepDependencyException(nameof(_ctx.NodeKey));
            if (_ctx.CryptoRandom == null) throw new StepDependencyException(nameof(_ctx.CryptoRandom));

            if (!_ctx.Config<IInitConfig>().DiscoveryEnabled)
            {
                _ctx.DiscoveryApp = new NullDiscoveryApp();
                return;
            }

            IDiscoveryConfig discoveryConfig = _ctx.Config<IDiscoveryConfig>();

            SameKeyGenerator privateKeyProvider = new SameKeyGenerator(_ctx.NodeKey);
            DiscoveryMessageFactory discoveryMessageFactory = new DiscoveryMessageFactory(_ctx.Timestamper);
            NodeIdResolver nodeIdResolver = new NodeIdResolver(_ctx.EthereumEcdsa);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _ctx._messageSerializationService,
                _ctx.EthereumEcdsa,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver);

            msgSerializersProvider.RegisterDiscoverySerializers();

            NodeDistanceCalculator nodeDistanceCalculator = new NodeDistanceCalculator(discoveryConfig);

            NodeTable nodeTable = new NodeTable(nodeDistanceCalculator, discoveryConfig, _networkConfig, _ctx.LogManager);
            EvictionManager evictionManager = new EvictionManager(nodeTable, _ctx.LogManager);

            NodeLifecycleManagerFactory nodeLifeCycleFactory = new NodeLifecycleManagerFactory(
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _ctx.NodeStatsManager,
                discoveryConfig,
                _ctx.LogManager);

            SimpleFilePublicKeyDb discoveryDb = new SimpleFilePublicKeyDb("DiscoveryDB", DiscoveryNodesDbPath.GetApplicationResourcePath(_ctx.Config<IInitConfig>().BaseDbPath), _ctx.LogManager);
            NetworkStorage discoveryStorage = new NetworkStorage(
                discoveryDb,
                _ctx.LogManager);

            DiscoveryManager discoveryManager = new DiscoveryManager(
                nodeLifeCycleFactory,
                nodeTable,
                discoveryStorage,
                discoveryConfig,
                _ctx.LogManager);

            NodesLocator nodesLocator = new NodesLocator(
                nodeTable,
                discoveryManager,
                discoveryConfig,
                _ctx.LogManager);

            _ctx.DiscoveryApp = new DiscoveryApp(
                nodesLocator,
                discoveryManager,
                nodeTable,
                _ctx._messageSerializationService,
                _ctx.CryptoRandom,
                discoveryStorage,
                _networkConfig,
                discoveryConfig,
                _ctx.Timestamper,
                _ctx.LogManager);

            _ctx.DiscoveryApp.Initialize(_ctx.NodeKey.PublicKey);
        }

        private Task StartSync()
        {
            if (_ctx.SyncPeerPool == null) throw new StepDependencyException(nameof(_ctx.SyncPeerPool));
            if (_ctx.Synchronizer == null) throw new StepDependencyException(nameof(_ctx.Synchronizer));
            if (_ctx.BlockTree == null) throw new StepDependencyException(nameof(_ctx.BlockTree));

            if (!_ctx.Config<ISyncConfig>().SynchronizationEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping blockchain synchronization init due to ({nameof(ISyncConfig.SynchronizationEnabled)} set to false)");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_ctx.BlockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}.");

            _ctx.SyncPeerPool.Start();
            _ctx.Synchronizer.Start();
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            if (_ctx.DbProvider == null) throw new StepDependencyException(nameof(_ctx.DbProvider));
            if (_ctx.BlockTree == null) throw new StepDependencyException(nameof(_ctx.BlockTree));
            if (_ctx.ReceiptStorage == null) throw new StepDependencyException(nameof(_ctx.ReceiptStorage));
            if (_ctx.BlockValidator == null) throw new StepDependencyException(nameof(_ctx.BlockValidator));
            if (_ctx.SyncPeerPool == null) throw new StepDependencyException(nameof(_ctx.SyncPeerPool));
            if (_ctx.Synchronizer == null) throw new StepDependencyException(nameof(_ctx.Synchronizer));
            if (_ctx.Enode == null) throw new StepDependencyException(nameof(_ctx.Enode));
            if (_ctx.NodeKey == null) throw new StepDependencyException(nameof(_ctx.NodeKey));
            if (_ctx.MainBlockProcessor == null) throw new StepDependencyException(nameof(_ctx.MainBlockProcessor));
            if (_ctx.NodeStatsManager == null) throw new StepDependencyException(nameof(_ctx.NodeStatsManager));
            if (_ctx.KeyStore == null) throw new StepDependencyException(nameof(_ctx.KeyStore));
            if (_ctx.RpcModuleProvider == null) throw new StepDependencyException(nameof(_ctx.RpcModuleProvider));
            if (_ctx.Wallet == null) throw new StepDependencyException(nameof(_ctx.Wallet));
            if (_ctx.EthereumEcdsa == null) throw new StepDependencyException(nameof(_ctx.EthereumEcdsa));
            if (_ctx.SpecProvider == null) throw new StepDependencyException(nameof(_ctx.SpecProvider));
            if (_ctx.TxPool == null) throw new StepDependencyException(nameof(_ctx.TxPool));
            if (_ctx.EthereumJsonSerializer == null) throw new StepDependencyException(nameof(_ctx.EthereumJsonSerializer));

            /* rlpx */
            EciesCipher eciesCipher = new EciesCipher(_ctx.CryptoRandom);
            Eip8MessagePad eip8Pad = new Eip8MessagePad(_ctx.CryptoRandom);
            _ctx._messageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _ctx._messageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            _ctx._messageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer)));
            _ctx._messageSerializationService.Register(new ReceiptsMessageSerializer(_ctx.SpecProvider));

            HandshakeService encryptionHandshakeServiceA = new HandshakeService(_ctx._messageSerializationService, eciesCipher,
                _ctx.CryptoRandom, new Ecdsa(), _ctx.NodeKey, _ctx.LogManager);

            _ctx._messageSerializationService.Register(Assembly.GetAssembly(typeof(HiMessageSerializer)));

            IDiscoveryConfig discoveryConfig = _ctx.Config<IDiscoveryConfig>();
            IInitConfig initConfig = _ctx.Config<IInitConfig>();

            _ctx.SessionMonitor = new SessionMonitor(_networkConfig, _ctx.LogManager);
            _ctx.RlpxPeer = new RlpxPeer(
                _ctx._messageSerializationService,
                _ctx.NodeKey.PublicKey,
                _networkConfig.P2PPort,
                encryptionHandshakeServiceA,
                _ctx.LogManager,
                _ctx.SessionMonitor);

            await _ctx.RlpxPeer.Init();

            _ctx.StaticNodesManager = new StaticNodesManager(initConfig.StaticNodesPath, _ctx.LogManager);
            await _ctx.StaticNodesManager.InitAsync();

            var dbName = "PeersDB";
            IFullDb peersDb = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                ? (IFullDb) new MemDb(dbName)
                : new SimpleFilePublicKeyDb(dbName, PeersDbPath.GetApplicationResourcePath(initConfig.BaseDbPath), _ctx.LogManager);

            NetworkStorage peerStorage = new NetworkStorage(peersDb, _ctx.LogManager);

            ProtocolValidator protocolValidator = new ProtocolValidator(_ctx.NodeStatsManager, _ctx.BlockTree, _ctx.LogManager);
            _ctx.ProtocolsManager = new ProtocolsManager(_ctx.SyncPeerPool, _ctx.SyncServer, _ctx.TxPool, _ctx.DiscoveryApp, _ctx._messageSerializationService, _ctx.RlpxPeer, _ctx.NodeStatsManager, protocolValidator, peerStorage, _ctx.SpecProvider, _ctx.LogManager);

            if (!(_ctx.NdmInitializer is null))
            {
                if (_ctx.WebSocketsManager == null) throw new StepDependencyException(nameof(_ctx.WebSocketsManager));
                if (_ctx.GrpcServer == null) throw new StepDependencyException(nameof(_ctx.GrpcServer));
                if (_ctx.NdmDataPublisher == null) throw new StepDependencyException(nameof(_ctx.NdmDataPublisher));
                if (_ctx.NdmConsumerChannelManager == null) throw new StepDependencyException(nameof(_ctx.NdmConsumerChannelManager));
                if (_ctx.BloomStorage == null) throw new StepDependencyException(nameof(_ctx.BloomStorage));
                if (_ctx.ReceiptFinder == null) throw new StepDependencyException(nameof(_ctx.ReceiptFinder));

                if (_logger.IsInfo) _logger.Info($"Initializing NDM...");
                _ctx.HttpClient = new DefaultHttpClient(new HttpClient(), _ctx.EthereumJsonSerializer, _ctx.LogManager);
                INdmConfig ndmConfig = _ctx.Config<INdmConfig>();
                if (ndmConfig.ProxyEnabled)
                {
                    _ctx.JsonRpcClientProxy = new JsonRpcClientProxy(_ctx.HttpClient, ndmConfig.JsonRpcUrlProxies,
                        _ctx.LogManager);
                    _ctx.EthJsonRpcClientProxy = new EthJsonRpcClientProxy(_ctx.JsonRpcClientProxy);
                }

                FilterStore filterStore = new FilterStore();
                FilterManager filterManager = new FilterManager(filterStore, _ctx.MainBlockProcessor, _ctx.TxPool, _ctx.LogManager);
                INdmCapabilityConnector capabilityConnector = await _ctx.NdmInitializer.InitAsync(
                    _ctx.ConfigProvider,
                    _ctx.DbProvider,
                    initConfig.BaseDbPath,
                    _ctx.BlockTree,
                    _ctx.TxPool,
                    _ctx.SpecProvider,
                    _ctx.ReceiptFinder,
                    _ctx.Wallet,
                    filterStore,
                    filterManager,
                    _ctx.Timestamper,
                    _ctx.EthereumEcdsa,
                    _ctx.RpcModuleProvider,
                    _ctx.KeyStore,
                    _ctx.EthereumJsonSerializer,
                    _ctx.CryptoRandom,
                    _ctx.Enode,
                    _ctx.NdmConsumerChannelManager,
                    _ctx.NdmDataPublisher,
                    _ctx.GrpcServer,
                    _ctx.NodeStatsManager,
                    _ctx.ProtocolsManager,
                    protocolValidator,
                    _ctx._messageSerializationService,
                    initConfig.EnableUnsecuredDevWallet,
                    _ctx.WebSocketsManager,
                    _ctx.LogManager,
                    _ctx.MainBlockProcessor,
                    _ctx.JsonRpcClientProxy,
                    _ctx.EthJsonRpcClientProxy,
                    _ctx.HttpClient,
                    _ctx.MonitoringService,
                    _ctx.BloomStorage);

                capabilityConnector.Init();
                if (_logger.IsInfo) _logger.Info($"NDM initialized.");
            }

            PeerLoader peerLoader = new PeerLoader(_networkConfig, discoveryConfig, _ctx.NodeStatsManager, peerStorage, _ctx.LogManager);
            _ctx.PeerManager = new PeerManager(_ctx.RlpxPeer, _ctx.DiscoveryApp, _ctx.NodeStatsManager, peerStorage, peerLoader, _networkConfig, _ctx.LogManager, _ctx.StaticNodesManager);
            _ctx.PeerManager.Init();
        }
    }
}