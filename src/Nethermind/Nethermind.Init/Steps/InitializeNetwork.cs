//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.LesSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.Steps
{
    public static class NettyMemoryEstimator
    {
        // Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", "8192");
        private const uint PageSize = 8192;
        
        public static long Estimate(uint cpuCount, int arenaOrder)
        {
            // do not remember why there is 2 in front
            return 2L * cpuCount * (1L << arenaOrder) * PageSize;
        }
    }

    [RunnerStepDependencies(
        typeof(LoadGenesisBlock),
        typeof(UpdateDiscoveryConfig),
        typeof(SetupKeyStore),
        typeof(InitializeNodeStats),
        typeof(ResolveIps),
        typeof(InitializePlugins),
        typeof(InitializeBlockchain))]
    public class InitializeNetwork : IStep
    {
        private const string DiscoveryNodesDbPath = "discoveryNodes";
        private const string PeersDbPath = "peers";

        protected readonly IApiWithNetwork _api;
        private readonly ILogger _logger;
        private readonly INetworkConfig _networkConfig;
        protected readonly ISyncConfig _syncConfig;

        public InitializeNetwork(INethermindApi api)
        {
            _api = api;
            _logger = _api.LogManager.GetClassLogger();
            _networkConfig = _api.Config<INetworkConfig>();
            _syncConfig = _api.Config<ISyncConfig>();
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            await Initialize(cancellationToken);
        }

        private async Task Initialize(CancellationToken cancellationToken)
        {
            if (_networkConfig.DiagTracerEnabled)
            {
                NetworkDiagTracer.IsEnabled = true;
            }

            if (NetworkDiagTracer.IsEnabled)
            {
                NetworkDiagTracer.Start();
            }

            Environment.SetEnvironmentVariable("io.netty.allocator.maxOrder", _networkConfig.NettyArenaOrder.ToString());

            var cht = new CanonicalHashTrie(_api.DbProvider!.ChtDb);

            int maxPeersCount = _networkConfig.ActivePeersMaxCount;
            _api.SyncPeerPool = new SyncPeerPool(_api.BlockTree!, _api.NodeStatsManager!, maxPeersCount, _api.LogManager);
            _api.DisposeStack.Push(_api.SyncPeerPool);

            SyncProgressResolver syncProgressResolver = new(
                _api.BlockTree!,
                _api.ReceiptStorage!,
                _api.DbProvider.StateDb,
                _api.DbProvider.BeamTempDb,
                _api.ReadOnlyTrieStore!,
                _syncConfig,
                _api.LogManager);
            
            MultiSyncModeSelector syncModeSelector = CreateMultiSyncModeSelector(syncProgressResolver);
            if (_api.SyncModeSelector != null)
            {
                // this is really bad and is a result of lack of proper dependency management
                PendingSyncModeSelector pendingOne = (PendingSyncModeSelector) _api.SyncModeSelector;
                pendingOne.SetActual(syncModeSelector);
            }

            _api.SyncModeSelector = syncModeSelector;
            _api.DisposeStack.Push(syncModeSelector);

            _api.Synchronizer = new Synchronizer(
                _api.DbProvider,
                _api.SpecProvider!,
                _api.BlockTree!,
                _api.ReceiptStorage!,
                _api.BlockValidator!,
                _api.SealValidator!,
                _api.SyncPeerPool,
                _api.NodeStatsManager!,
                _api.SyncModeSelector,
                _syncConfig,
                _api.LogManager);
            _api.DisposeStack.Push(_api.Synchronizer);

            _api.SyncServer = new SyncServer(
                _api.DbProvider.StateDb,
                _api.DbProvider.CodeDb,
                _api.BlockTree!,
                _api.ReceiptStorage!,
                _api.BlockValidator!,
                _api.SealValidator!,
                _api.SyncPeerPool,
                _api.SyncModeSelector,
                _api.Config<ISyncConfig>(),
                _api.WitnessRepository,
                _api.LogManager,
                cht);

            _ = _api.SyncServer.BuildCHT();

            _api.DisposeStack.Push(_api.SyncServer);

            InitDiscovery();
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await InitPeer().ContinueWith(initPeerTask =>
            {
                if (initPeerTask.IsFaulted)
                {
                    _logger.Error("Unable to init the peer manager.", initPeerTask.Exception);
                }
            });

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await StartSync().ContinueWith(initNetTask =>
            {
                if (initNetTask.IsFaulted)
                {
                    _logger.Error("Unable to start the synchronizer.", initNetTask.Exception);
                }
            });

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await StartDiscovery().ContinueWith(initDiscoveryTask =>
            {
                if (initDiscoveryTask.IsFaulted)
                {
                    _logger.Error("Unable to start the discovery protocol.", initDiscoveryTask.Exception);
                }
            });

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                StartPeer();
            }
            catch (Exception e)
            {
                _logger.Error("Unable to start the peer manager.", e);
            }

            if (_api.Enode == null)
            {
                throw new InvalidOperationException("Cannot initialize network without knowing own enode");
            }

            ThisNodeInfo.AddInfo("Ethereum     :", $"tcp://{_api.Enode.HostIp}:{_api.Enode.Port}");
            ThisNodeInfo.AddInfo("Version      :", $"{ClientVersion.Description.Replace("Nethermind/v", string.Empty)}");
            ThisNodeInfo.AddInfo("This node    :", $"{_api.Enode.Info}");
            ThisNodeInfo.AddInfo("Node address :", $"{_api.Enode.Address} (do not use as an account)");
        }

        protected virtual MultiSyncModeSelector CreateMultiSyncModeSelector(SyncProgressResolver syncProgressResolver)
            => new(syncProgressResolver, _api.SyncPeerPool!, _syncConfig, _api.LogManager);

        private Task StartDiscovery()
        {
            if (_api.DiscoveryApp == null) throw new StepDependencyException(nameof(_api.DiscoveryApp));

            if (!_api.Config<IInitConfig>().DiscoveryEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to {nameof(IInitConfig.DiscoveryEnabled)} set to false");
                return Task.CompletedTask;
            }

            if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
            _api.DiscoveryApp.Start();
            if (_logger.IsDebug) _logger.Debug("Discovery process started.");
            return Task.CompletedTask;
        }

        private void StartPeer()
        {
            if (_api.PeerManager == null) throw new StepDependencyException(nameof(_api.PeerManager));
            if (_api.SessionMonitor == null) throw new StepDependencyException(nameof(_api.SessionMonitor));

            if (!_api.Config<IInitConfig>().PeerManagerEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false");
            }

            if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
            _api.PeerManager.Start();
            _api.SessionMonitor.Start();
            if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
        }

        private void InitDiscovery()
        {
            if (_api.NodeStatsManager == null) throw new StepDependencyException(nameof(_api.NodeStatsManager));
            if (_api.Timestamper == null) throw new StepDependencyException(nameof(_api.Timestamper));
            if (_api.NodeKey == null) throw new StepDependencyException(nameof(_api.NodeKey));
            if (_api.CryptoRandom == null) throw new StepDependencyException(nameof(_api.CryptoRandom));

            if (!_api.Config<IInitConfig>().DiscoveryEnabled)
            {
                _api.DiscoveryApp = new NullDiscoveryApp();
                return;
            }

            IDiscoveryConfig discoveryConfig = _api.Config<IDiscoveryConfig>();

            SameKeyGenerator privateKeyProvider = new(_api.NodeKey.Unprotect());
            DiscoveryMessageFactory discoveryMessageFactory = new(_api.Timestamper);
            NodeIdResolver nodeIdResolver = new(_api.EthereumEcdsa);
            IPResolver ipResolver = new(_networkConfig, _api.LogManager);

            IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
                _api.MessageSerializationService,
                _api.EthereumEcdsa,
                privateKeyProvider,
                discoveryMessageFactory,
                nodeIdResolver);

            msgSerializersProvider.RegisterDiscoverySerializers();

            NodeDistanceCalculator nodeDistanceCalculator = new(discoveryConfig);

            NodeTable nodeTable = new(nodeDistanceCalculator, discoveryConfig, _networkConfig, _api.LogManager);
            EvictionManager evictionManager = new(nodeTable, _api.LogManager);

            NodeLifecycleManagerFactory nodeLifeCycleFactory = new(
                nodeTable,
                discoveryMessageFactory,
                evictionManager,
                _api.NodeStatsManager,
                discoveryConfig,
                _api.LogManager);

            // ToDo: DiscoveryDB is registered outside dbProvider - bad
            SimpleFilePublicKeyDb discoveryDb = new(
                "DiscoveryDB",
                DiscoveryNodesDbPath.GetApplicationResourcePath(_api.Config<IInitConfig>().BaseDbPath),
                _api.LogManager);
            
            NetworkStorage discoveryStorage = new(
                discoveryDb,
                _api.LogManager);

            DiscoveryManager discoveryManager = new(
                nodeLifeCycleFactory,
                nodeTable,
                discoveryStorage,
                discoveryConfig,
                _api.LogManager,
                ipResolver
            );

            NodesLocator nodesLocator = new(
                nodeTable,
                discoveryManager,
                discoveryConfig,
                _api.LogManager);

            _api.DiscoveryApp = new DiscoveryApp(
                nodesLocator,
                discoveryManager,
                nodeTable,
                _api.MessageSerializationService,
                _api.CryptoRandom,
                discoveryStorage,
                _networkConfig,
                discoveryConfig,
                _api.Timestamper,
                _api.LogManager);

            _api.DiscoveryApp.Initialize(_api.NodeKey.PublicKey);
        }

        private Task StartSync()
        {
            if (_api.SyncPeerPool == null) throw new StepDependencyException(nameof(_api.SyncPeerPool));
            if (_api.Synchronizer == null) throw new StepDependencyException(nameof(_api.Synchronizer));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));

            ISyncConfig syncConfig = _api.Config<ISyncConfig>();
            if (syncConfig.NetworkingEnabled)
            {
                _api.SyncPeerPool!.Start();

                if (syncConfig.SynchronizationEnabled)
                {
                    if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_api.BlockTree.Head?.Header?.ToString(BlockHeader.Format.Short)}.");
                    _api.Synchronizer!.Start();
                }
                else
                {
                    if (_logger.IsWarn) _logger.Warn($"Skipping blockchain synchronization init due to {nameof(ISyncConfig.SynchronizationEnabled)} set to false");
                }
            }
            else if (_logger.IsWarn) _logger.Warn($"Skipping connecting to peers due to {nameof(ISyncConfig.NetworkingEnabled)} set to false");

            
            return Task.CompletedTask;
        }

        private async Task InitPeer()
        {
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.ReceiptStorage == null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
            if (_api.BlockValidator == null) throw new StepDependencyException(nameof(_api.BlockValidator));
            if (_api.SyncPeerPool == null) throw new StepDependencyException(nameof(_api.SyncPeerPool));
            if (_api.Synchronizer == null) throw new StepDependencyException(nameof(_api.Synchronizer));
            if (_api.Enode == null) throw new StepDependencyException(nameof(_api.Enode));
            if (_api.NodeKey == null) throw new StepDependencyException(nameof(_api.NodeKey));
            if (_api.MainBlockProcessor == null) throw new StepDependencyException(nameof(_api.MainBlockProcessor));
            if (_api.NodeStatsManager == null) throw new StepDependencyException(nameof(_api.NodeStatsManager));
            if (_api.KeyStore == null) throw new StepDependencyException(nameof(_api.KeyStore));
            if (_api.Wallet == null) throw new StepDependencyException(nameof(_api.Wallet));
            if (_api.EthereumEcdsa == null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));
            if (_api.TxSender == null) throw new StepDependencyException(nameof(_api.TxSender));
            if (_api.EthereumJsonSerializer == null) throw new StepDependencyException(nameof(_api.EthereumJsonSerializer));

            /* rlpx */
            EciesCipher eciesCipher = new(_api.CryptoRandom);
            Eip8MessagePad eip8Pad = new(_api.CryptoRandom);
            _api.MessageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
            _api.MessageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
            _api.MessageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer)));
            _api.MessageSerializationService.Register(new ReceiptsMessageSerializer(_api.SpecProvider));

            HandshakeService encryptionHandshakeServiceA = new(
                _api.MessageSerializationService,
                eciesCipher,
                _api.CryptoRandom,
                _api.EthereumEcdsa,
                _api.NodeKey.Unprotect(),
                _api.LogManager);

            IDiscoveryConfig discoveryConfig = _api.Config<IDiscoveryConfig>();
            IInitConfig initConfig = _api.Config<IInitConfig>();

            // _api.DisconnectsAnalyzer = new DisconnectsAnalyzer(_api.LogManager);
            _api.DisconnectsAnalyzer = new MetricsDisconnectsAnalyzer();
            _api.SessionMonitor = new SessionMonitor(_networkConfig, _api.LogManager);
            _api.RlpxPeer = new RlpxPeer(
                _api.MessageSerializationService,
                _api.NodeKey.PublicKey,
                _networkConfig.P2PPort,
                encryptionHandshakeServiceA,
                _api.SessionMonitor,
                _api.DisconnectsAnalyzer,
                _api.LogManager);

            await _api.RlpxPeer.Init();

            _api.StaticNodesManager = new StaticNodesManager(initConfig.StaticNodesPath, _api.LogManager);
            await _api.StaticNodesManager.InitAsync();

            // ToDo: PeersDB is register outside dbProvider - bad
            var dbName = "PeersDB";
            IFullDb peersDb = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                ? (IFullDb) new MemDb(dbName)
                : new SimpleFilePublicKeyDb(dbName, PeersDbPath.GetApplicationResourcePath(initConfig.BaseDbPath), _api.LogManager);

            NetworkStorage peerStorage = new(peersDb, _api.LogManager);

            ProtocolValidator protocolValidator = new(_api.NodeStatsManager, _api.BlockTree, _api.LogManager);
            PooledTxsRequestor pooledTxsRequestor = new(_api.TxPool);
            _api.ProtocolsManager = new ProtocolsManager(
                _api.SyncPeerPool,
                _api.SyncServer,
                _api.TxPool,
                pooledTxsRequestor,
                _api.DiscoveryApp,
                _api.MessageSerializationService,
                _api.RlpxPeer,
                _api.NodeStatsManager,
                protocolValidator,
                peerStorage,
                _api.SpecProvider,
                _api.LogManager);
            
            if (_syncConfig.WitnessProtocolEnabled)
            {
                _api.ProtocolsManager.AddSupportedCapability(new Capability(Protocol.Wit, 0));
            }
            
            _api.ProtocolValidator = protocolValidator;

            foreach (var plugin in _api.Plugins)
            {
                await plugin.InitNetworkProtocol();
            }

            PeerLoader peerLoader = new(_networkConfig, discoveryConfig, _api.NodeStatsManager, peerStorage, _api.LogManager);
            _api.PeerManager = new PeerManager(
                _api.RlpxPeer,
                _api.DiscoveryApp,
                _api.NodeStatsManager,
                peerStorage,
                peerLoader,
                _networkConfig,
                _api.LogManager,
                _api.StaticNodesManager);
            
            _api.PeerManager.Init();
        }
    }
}
