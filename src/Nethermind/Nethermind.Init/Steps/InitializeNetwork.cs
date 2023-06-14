// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Discovery.Serializers;
using Nethermind.Network.Dns;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.LesSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;

namespace Nethermind.Init.Steps;

public static class NettyMemoryEstimator
{
    // Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", "8192");
    private const uint PageSize = 8192;

    public static long Estimate(uint arenaCount, int arenaOrder)
    {
        return arenaCount * (1L << arenaOrder) * PageSize;
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
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));

        if (_networkConfig.DiagTracerEnabled)
        {
            NetworkDiagTracer.IsEnabled = true;
        }

        if (NetworkDiagTracer.IsEnabled)
        {
            NetworkDiagTracer.Start(_api.LogManager);
        }

        CanonicalHashTrie cht = new CanonicalHashTrie(_api.DbProvider!.ChtDb);

        ProgressTracker progressTracker = new(_api.BlockTree!, _api.DbProvider.StateDb, _api.LogManager, _syncConfig.SnapSyncAccountRangePartitionCount);
        _api.SnapProvider = new SnapProvider(progressTracker, _api.DbProvider, _api.LogManager);

        SyncProgressResolver syncProgressResolver = new(
            _api.BlockTree!,
            _api.ReceiptStorage!,
            _api.DbProvider.StateDb,
            _api.ReadOnlyTrieStore!,
            progressTracker,
            _syncConfig,
            _api.LogManager);

        _api.SyncProgressResolver = syncProgressResolver;
        _api.BetterPeerStrategy = new TotalDifficultyBetterPeerStrategy(_api.LogManager);

        int maxPeersCount = _networkConfig.ActivePeersMaxCount;
        int maxPriorityPeersCount = _networkConfig.PriorityPeersMaxCount;
        Network.Metrics.PeerLimit = maxPeersCount;
        SyncPeerPool apiSyncPeerPool = new(_api.BlockTree!, _api.NodeStatsManager!, _api.BetterPeerStrategy, _api.LogManager, maxPeersCount, maxPriorityPeersCount);

        _api.SyncPeerPool = apiSyncPeerPool;
        _api.PeerDifficultyRefreshPool = apiSyncPeerPool;
        _api.DisposeStack.Push(_api.SyncPeerPool);

        IEnumerable<ISynchronizationPlugin> synchronizationPlugins = _api.GetSynchronizationPlugins();
        foreach (ISynchronizationPlugin plugin in synchronizationPlugins)
        {
            await plugin.InitSynchronization();
        }

        _api.SyncModeSelector ??= CreateMultiSyncModeSelector(syncProgressResolver);
        _api.EthSyncingInfo = new EthSyncingInfo(_api.BlockTree!, _api.ReceiptStorage!, _syncConfig, _api.SyncModeSelector, _api.LogManager);
        _api.DisposeStack.Push(_api.SyncModeSelector);

        _api.Pivot ??= new Pivot(_syncConfig);

        if (_api.BlockDownloaderFactory is null || _api.Synchronizer is null)
        {
            SyncReport syncReport = new(_api.SyncPeerPool!, _api.NodeStatsManager!, _api.SyncModeSelector, _syncConfig, _api.Pivot, _api.LogManager);

            _api.BlockDownloaderFactory ??= new BlockDownloaderFactory(
                _api.SpecProvider!,
                _api.BlockTree!,
                _api.ReceiptStorage!,
                _api.BlockValidator!,
                _api.SealValidator!,
                _api.SyncPeerPool!,
                _api.BetterPeerStrategy!,
                syncReport,
                _api.LogManager);
            _api.Synchronizer ??= new Synchronizer(
                _api.DbProvider,
                _api.SpecProvider!,
                _api.BlockTree!,
                _api.ReceiptStorage!,
                _api.SyncPeerPool,
                _api.NodeStatsManager!,
                _api.SyncModeSelector,
                _syncConfig,
                _api.SnapProvider,
                _api.BlockDownloaderFactory,
                _api.Pivot,
                syncReport,
                _api.LogManager);
        }

        _api.DisposeStack.Push(_api.Synchronizer);

        ISyncServer syncServer = _api.SyncServer = new SyncServer(
            _api.TrieStore!,
            _api.DbProvider.CodeDb,
            _api.BlockTree!,
            _api.ReceiptStorage!,
            _api.BlockValidator!,
            _api.SealValidator!,
            _api.SyncPeerPool,
            _api.SyncModeSelector,
            _api.Config<ISyncConfig>(),
            _api.WitnessRepository,
            _api.GossipPolicy,
            _api.SpecProvider!,
            _api.LogManager,
            cht);

        _ = syncServer.BuildCHT();
        _api.DisposeStack.Push(syncServer);

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

        bool stateSyncFinished = _api.SyncProgressResolver.FindBestFullState() != 0;

        if (_syncConfig.SnapSync || stateSyncFinished || !_syncConfig.FastSync)
        {
            // we can't add eth67 capability as default, because it needs snap protocol for syncing (GetNodeData is
            // no longer available). Eth67 should be added if snap is enabled OR sync is finished OR in archive nodes (no state sync)
            _api.ProtocolsManager!.AddSupportedCapability(new Capability(Protocol.Eth, 67));
            _api.ProtocolsManager!.AddSupportedCapability(new Capability(Protocol.Eth, 68));
        }
        else if (_logger.IsDebug) _logger.Debug("Skipped enabling eth67 & eth68 capabilities");

        if (_syncConfig.SnapSync && !stateSyncFinished)
        {
            SnapCapabilitySwitcher snapCapabilitySwitcher = new(_api.ProtocolsManager, _api.SyncModeSelector, _api.LogManager);
            snapCapabilitySwitcher.EnableSnapCapabilityUntilSynced();
        }
        else if (_logger.IsDebug) _logger.Debug("Skipped enabling snap capability");

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

        if (_api.Enode is null)
        {
            throw new InvalidOperationException("Cannot initialize network without knowing own enode");
        }

        ThisNodeInfo.AddInfo("Ethereum     :", $"tcp://{_api.Enode.HostIp}:{_api.Enode.Port}");
        ThisNodeInfo.AddInfo("Client id    :", ProductInfo.ClientId);
        ThisNodeInfo.AddInfo("This node    :", $"{_api.Enode.Info}");
        ThisNodeInfo.AddInfo("Node address :", $"{_api.Enode.Address} (do not use as an account)");
    }

    protected virtual MultiSyncModeSelector CreateMultiSyncModeSelector(SyncProgressResolver syncProgressResolver)
        => new(syncProgressResolver, _api.SyncPeerPool!, _syncConfig, No.BeaconSync, _api.BetterPeerStrategy!, _api.LogManager, _api.ChainSpec?.SealEngineType == SealEngineType.Clique);

    private Task StartDiscovery()
    {
        if (_api.DiscoveryApp is null) throw new StepDependencyException(nameof(_api.DiscoveryApp));

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
        if (_api.PeerManager is null) throw new StepDependencyException(nameof(_api.PeerManager));
        if (_api.SessionMonitor is null) throw new StepDependencyException(nameof(_api.SessionMonitor));
        if (_api.PeerPool is null) throw new StepDependencyException(nameof(_api.PeerPool));

        if (!_api.Config<IInitConfig>().PeerManagerEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false");
        }

        if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
        _api.PeerPool.Start();
        _api.PeerManager.Start();
        _api.SessionMonitor.Start();
        if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
    }

    private void InitDiscovery()
    {
        if (_api.NodeStatsManager is null) throw new StepDependencyException(nameof(_api.NodeStatsManager));
        if (_api.Timestamper is null) throw new StepDependencyException(nameof(_api.Timestamper));
        if (_api.NodeKey is null) throw new StepDependencyException(nameof(_api.NodeKey));
        if (_api.CryptoRandom is null) throw new StepDependencyException(nameof(_api.CryptoRandom));
        if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));

        if (!_api.Config<IInitConfig>().DiscoveryEnabled)
        {
            _api.DiscoveryApp = new NullDiscoveryApp();
            return;
        }

        IDiscoveryConfig discoveryConfig = _api.Config<IDiscoveryConfig>();

        SameKeyGenerator privateKeyProvider = new(_api.NodeKey.Unprotect());
        NodeIdResolver nodeIdResolver = new(_api.EthereumEcdsa);

        NodeRecord selfNodeRecord = PrepareNodeRecord(privateKeyProvider);
        IDiscoveryMsgSerializersProvider msgSerializersProvider = new DiscoveryMsgSerializersProvider(
            _api.MessageSerializationService,
            _api.EthereumEcdsa,
            privateKeyProvider,
            nodeIdResolver);

        msgSerializersProvider.RegisterDiscoverySerializers();

        NodeDistanceCalculator nodeDistanceCalculator = new(discoveryConfig);

        NodeTable nodeTable = new(nodeDistanceCalculator, discoveryConfig, _networkConfig, _api.LogManager);
        EvictionManager evictionManager = new(nodeTable, _api.LogManager);

        NodeLifecycleManagerFactory nodeLifeCycleFactory = new(
            nodeTable,
            evictionManager,
            _api.NodeStatsManager,
            selfNodeRecord,
            discoveryConfig,
            _api.Timestamper,
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
            _api.LogManager
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

    private NodeRecord PrepareNodeRecord(SameKeyGenerator privateKeyProvider)
    {
        NodeRecord selfNodeRecord = new();
        selfNodeRecord.SetEntry(IdEntry.Instance);
        selfNodeRecord.SetEntry(new IpEntry(_api.IpResolver!.ExternalIp));
        selfNodeRecord.SetEntry(new TcpEntry(_networkConfig.P2PPort));
        selfNodeRecord.SetEntry(new UdpEntry(_networkConfig.DiscoveryPort));
        selfNodeRecord.SetEntry(new Secp256K1Entry(_api.NodeKey!.CompressedPublicKey));
        selfNodeRecord.EnrSequence = 1;
        NodeRecordSigner enrSigner = new(_api.EthereumEcdsa, privateKeyProvider.Generate());
        enrSigner.Sign(selfNodeRecord);
        if (!enrSigner.Verify(selfNodeRecord))
        {
            throw new NetworkingException("Self ENR initialization failed", NetworkExceptionType.Discovery);
        }

        return selfNodeRecord;
    }

    private Task StartSync()
    {
        if (_api.SyncPeerPool is null) throw new StepDependencyException(nameof(_api.SyncPeerPool));
        if (_api.Synchronizer is null) throw new StepDependencyException(nameof(_api.Synchronizer));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

        ISyncConfig syncConfig = _api.Config<ISyncConfig>();
        if (syncConfig.NetworkingEnabled)
        {
            _api.SyncPeerPool!.Start();

            if (syncConfig.SynchronizationEnabled)
            {
                if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_api.BlockTree.Head?.Header.ToString(BlockHeader.Format.Short)}.");
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
        if (_api.DbProvider is null) throw new StepDependencyException(nameof(_api.DbProvider));
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.ReceiptStorage is null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
        if (_api.BlockValidator is null) throw new StepDependencyException(nameof(_api.BlockValidator));
        if (_api.SyncPeerPool is null) throw new StepDependencyException(nameof(_api.SyncPeerPool));
        if (_api.Synchronizer is null) throw new StepDependencyException(nameof(_api.Synchronizer));
        if (_api.Enode is null) throw new StepDependencyException(nameof(_api.Enode));
        if (_api.NodeKey is null) throw new StepDependencyException(nameof(_api.NodeKey));
        if (_api.MainBlockProcessor is null) throw new StepDependencyException(nameof(_api.MainBlockProcessor));
        if (_api.NodeStatsManager is null) throw new StepDependencyException(nameof(_api.NodeStatsManager));
        if (_api.KeyStore is null) throw new StepDependencyException(nameof(_api.KeyStore));
        if (_api.Wallet is null) throw new StepDependencyException(nameof(_api.Wallet));
        if (_api.EthereumEcdsa is null) throw new StepDependencyException(nameof(_api.EthereumEcdsa));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.TxPool is null) throw new StepDependencyException(nameof(_api.TxPool));
        if (_api.TxSender is null) throw new StepDependencyException(nameof(_api.TxSender));
        if (_api.EthereumJsonSerializer is null) throw new StepDependencyException(nameof(_api.EthereumJsonSerializer));
        if (_api.DiscoveryApp is null) throw new StepDependencyException(nameof(_api.DiscoveryApp));

        /* rlpx */
        EciesCipher eciesCipher = new(_api.CryptoRandom);
        Eip8MessagePad eip8Pad = new(_api.CryptoRandom);
        _api.MessageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
        _api.MessageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
        _api.MessageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer))!);
        ReceiptsMessageSerializer receiptsMessageSerializer = new(_api.SpecProvider);
        _api.MessageSerializationService.Register(receiptsMessageSerializer);
        _api.MessageSerializationService.Register(new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessageSerializer(receiptsMessageSerializer));

        HandshakeService encryptionHandshakeServiceA = new(
            _api.MessageSerializationService,
            eciesCipher,
            _api.CryptoRandom,
            _api.EthereumEcdsa,
            _api.NodeKey.Unprotect(),
            _api.LogManager);

        IDiscoveryConfig discoveryConfig = _api.Config<IDiscoveryConfig>();
        // TODO: hack, but changing it in all the documentation would be a nightmare
        _networkConfig.Bootnodes = discoveryConfig.Bootnodes;

        IInitConfig initConfig = _api.Config<IInitConfig>();

        _api.DisconnectsAnalyzer = new MetricsDisconnectsAnalyzer();
        _api.SessionMonitor = new SessionMonitor(_networkConfig, _api.LogManager);
        _api.RlpxPeer = new RlpxHost(
            _api.MessageSerializationService,
            _api.NodeKey.PublicKey,
            _networkConfig.ProcessingThreadCount,
            _networkConfig.P2PPort,
            _networkConfig.LocalIp,
            _networkConfig.ConnectTimeoutMs,
            encryptionHandshakeServiceA,
            _api.SessionMonitor,
            _api.DisconnectsAnalyzer,
            _api.LogManager,
            TimeSpan.FromMilliseconds(_networkConfig.SimulateSendLatencyMs)
        );

        await _api.RlpxPeer.Init();

        _api.StaticNodesManager = new StaticNodesManager(initConfig.StaticNodesPath, _api.LogManager);
        await _api.StaticNodesManager.InitAsync();

        // ToDo: PeersDB is registered outside dbProvider
        string dbName = "PeersDB";
        IFullDb peersDb = initConfig.DiagnosticMode == DiagnosticMode.MemDb
            ? new MemDb(dbName)
            : new SimpleFilePublicKeyDb(dbName, PeersDbPath.GetApplicationResourcePath(initConfig.BaseDbPath),
                _api.LogManager);

        NetworkStorage peerStorage = new(peersDb, _api.LogManager);
        ISyncServer syncServer = _api.SyncServer!;
        ForkInfo forkInfo = new(_api.SpecProvider!, syncServer.Genesis.Hash!);

        ProtocolValidator protocolValidator = new(_api.NodeStatsManager!, _api.BlockTree!, forkInfo, _api.LogManager);
        PooledTxsRequestor pooledTxsRequestor = new(_api.TxPool!);
        _api.ProtocolsManager = new ProtocolsManager(
            _api.SyncPeerPool!,
            syncServer,
            _api.TxPool,
            pooledTxsRequestor,
            _api.DiscoveryApp!,
            _api.MessageSerializationService,
            _api.RlpxPeer,
            _api.NodeStatsManager,
            protocolValidator,
            peerStorage,
            forkInfo,
            _api.GossipPolicy,
            _api.LogManager);

        if (_syncConfig.WitnessProtocolEnabled)
        {
            _api.ProtocolsManager.AddSupportedCapability(new Capability(Protocol.Wit, 0));
        }

        _api.ProtocolValidator = protocolValidator;

        NodesLoader nodesLoader = new(_networkConfig, _api.NodeStatsManager, peerStorage, _api.RlpxPeer, _api.LogManager);

        // I do not use the key here -> API is broken - no sense to use the node signer here
        NodeRecordSigner nodeRecordSigner = new(_api.EthereumEcdsa, new PrivateKeyGenerator().Generate());
        EnrRecordParser enrRecordParser = new(nodeRecordSigner);
        EnrDiscovery enrDiscovery = new(enrRecordParser, _api.LogManager); // initialize with a proper network
        CompositeNodeSource nodeSources = new(_api.StaticNodesManager, nodesLoader, enrDiscovery, _api.DiscoveryApp);
        _api.PeerPool = new PeerPool(nodeSources, _api.NodeStatsManager, peerStorage, _networkConfig, _api.LogManager);
        _api.PeerManager = new PeerManager(
            _api.RlpxPeer,
            _api.PeerPool,
            _api.NodeStatsManager,
            _networkConfig,
            _api.LogManager);

        string chainName = BlockchainIds.GetBlockchainName(_api.ChainSpec!.NetworkId).ToLowerInvariant();
        string domain = _networkConfig.DiscoveryDns ?? $"all.{chainName}.ethdisco.net";
#pragma warning disable CS4014
        enrDiscovery.SearchTree(domain).ContinueWith(t =>
#pragma warning restore CS4014
        {
            if (t.IsFaulted)
            {
                _logger.Error($"ENR discovery failed: {t.Exception}");
            }
        });

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            await plugin.InitNetworkProtocol();
        }
    }
}
