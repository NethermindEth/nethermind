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
using Nethermind.Blockchain.Utils;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Facade.Eth;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.Dns;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.Trie;
using Nethermind.TxPool;

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
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

        if (_networkConfig.DiagTracerEnabled)
        {
            NetworkDiagTracer.IsEnabled = true;
        }

        if (NetworkDiagTracer.IsEnabled)
        {
            NetworkDiagTracer.Start(_api.LogManager);
        }

        _api.BetterPeerStrategy = new TotalDifficultyBetterPeerStrategy(_api.LogManager);

        int maxPeersCount = _networkConfig.ActivePeersMaxCount;
        int maxPriorityPeersCount = _networkConfig.PriorityPeersMaxCount;
        Network.Metrics.PeerLimit = maxPeersCount;
        SyncPeerPool apiSyncPeerPool = new(_api.BlockTree, _api.NodeStatsManager!, _api.BetterPeerStrategy, _api.LogManager, maxPeersCount, maxPriorityPeersCount);

        _api.SyncPeerPool = apiSyncPeerPool;
        _api.PeerDifficultyRefreshPool = apiSyncPeerPool;
        _api.DisposeStack.Push(_api.SyncPeerPool);

        IEnumerable<ISynchronizationPlugin> synchronizationPlugins = _api.GetSynchronizationPlugins();
        foreach (ISynchronizationPlugin plugin in synchronizationPlugins)
        {
            await plugin.InitSynchronization();
        }

        _api.Pivot ??= new Pivot(_syncConfig);

        if (_api.Synchronizer is null)
        {
            BlockDownloaderFactory blockDownloaderFactory = new BlockDownloaderFactory(
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.SealValidator!,
                _api.BetterPeerStrategy!,
                _api.LogManager);

            _api.Synchronizer ??= new Synchronizer(
                _api.DbProvider,
                _api.NodeStorageFactory.WrapKeyValueStore(_api.DbProvider.StateDb),
                _api.SpecProvider!,
                _api.BlockTree,
                _api.ReceiptStorage!,
                _api.SyncPeerPool,
                _api.NodeStatsManager!,
                _syncConfig,
                blockDownloaderFactory,
                _api.Pivot,
                _api.ProcessExit!,
                _api.BetterPeerStrategy,
                _api.ChainSpec,
                _api.StateReader!,
                _api.LogManager);
        }

        _api.SyncModeSelector = _api.Synchronizer.SyncModeSelector;
        _api.SyncProgressResolver = _api.Synchronizer.SyncProgressResolver;

        _api.EthSyncingInfo = new EthSyncingInfo(_api.BlockTree, _api.ReceiptStorage!, _syncConfig,
            _api.SyncModeSelector, _api.SyncProgressResolver, _api.LogManager);
        _api.TxGossipPolicy.Policies.Add(new SyncedTxGossipPolicy(_api.SyncModeSelector));
        _api.DisposeStack.Push(_api.SyncModeSelector);
        _api.DisposeStack.Push(_api.Synchronizer);

        ISyncServer syncServer = _api.SyncServer = new SyncServer(
            _api.TrieStore!.TrieNodeRlpStore,
            _api.DbProvider.CodeDb,
            _api.BlockTree,
            _api.ReceiptStorage!,
            _api.BlockValidator!,
            _api.SealValidator!,
            _api.SyncPeerPool,
            _api.SyncModeSelector,
            _api.Config<ISyncConfig>(),
            _api.GossipPolicy,
            _api.SpecProvider!,
            _api.LogManager);

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

        if (_syncConfig.SnapSync && _syncConfig.SnapServingEnabled != true)
        {
            SnapCapabilitySwitcher snapCapabilitySwitcher =
                new(_api.ProtocolsManager, _api.SyncModeSelector, _api.LogManager);
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

    private Task StartDiscovery()
    {
        if (_api.DiscoveryApp is null) throw new StepDependencyException(nameof(_api.DiscoveryApp));

        if (!_api.Config<IInitConfig>().DiscoveryEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to {nameof(IInitConfig.DiscoveryEnabled)} set to false");
            return Task.CompletedTask;
        }

        if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
        _ = _api.DiscoveryApp.StartAsync();
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

        _api.DiscoveryApp = new CompositeDiscoveryApp(_api.NodeKey,
            _networkConfig, _api.Config<IDiscoveryConfig>(), _api.Config<IInitConfig>(),
            _api.EthereumEcdsa, _api.MessageSerializationService,
            _api.LogManager, _api.Timestamper, _api.CryptoRandom,
            _api.NodeStatsManager, _api.IpResolver
        );

        _api.DiscoveryApp.Initialize(_api.NodeKey.PublicKey);
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

        ProtocolValidator protocolValidator = new(_api.NodeStatsManager!, _api.BlockTree, forkInfo, _api.LogManager);
        PooledTxsRequestor pooledTxsRequestor = new(_api.TxPool!, _api.Config<ITxPoolConfig>());

        ISnapServer? snapServer = null;
        if (_syncConfig.SnapServingEnabled == true)
        {
            // TODO: Add a proper config for the state persistence depth.
            snapServer = new SnapServer(_api.TrieStore!.AsReadOnly(), _api.DbProvider.CodeDb, new LastNStateRootTracker(_api.BlockTree, 128), _api.LogManager);
        }

        _api.ProtocolsManager = new ProtocolsManager(
            _api.SyncPeerPool!,
            syncServer,
            _api.BackgroundTaskScheduler,
            _api.TxPool,
            pooledTxsRequestor,
            _api.DiscoveryApp,
            _api.MessageSerializationService,
            _api.RlpxPeer,
            _api.NodeStatsManager,
            protocolValidator,
            peerStorage,
            forkInfo,
            _api.GossipPolicy,
            _networkConfig,
            snapServer,
            _api.LogManager,
            _api.TxGossipPolicy);

        if (_syncConfig.SnapServingEnabled == true)
        {
            _api.ProtocolsManager!.AddSupportedCapability(new Capability(Protocol.Snap, 1));
        }

        _api.ProtocolValidator = protocolValidator;

        NodesLoader nodesLoader = new(_networkConfig, _api.NodeStatsManager, peerStorage, _api.RlpxPeer, _api.LogManager);

        // I do not use the key here -> API is broken - no sense to use the node signer here
        NodeRecordSigner nodeRecordSigner = new(_api.EthereumEcdsa, new PrivateKeyGenerator().Generate());
        EnrRecordParser enrRecordParser = new(nodeRecordSigner);

        if (_networkConfig.DiscoveryDns == null)
        {
            string chainName = BlockchainIds.GetBlockchainName(_api.ChainSpec!.NetworkId).ToLowerInvariant();
            _networkConfig.DiscoveryDns = $"all.{chainName}.ethdisco.net";
        }

        EnrDiscovery enrDiscovery = new(enrRecordParser, _networkConfig, _api.LogManager); // initialize with a proper network

        if (!_networkConfig.DisableDiscV4DnsFeeder)
        {
            // Feed some nodes into discoveryApp in case all bootnodes is faulty.
            _ = new NodeSourceToDiscV4Feeder(enrDiscovery, _api.DiscoveryApp, 50).Run(_api.ProcessExit!.Token);
        }

        CompositeNodeSource nodeSources = _networkConfig.OnlyStaticPeers
            ? new(_api.StaticNodesManager, nodesLoader)
            : new(_api.StaticNodesManager, nodesLoader, enrDiscovery, _api.DiscoveryApp);
        _api.PeerPool = new PeerPool(nodeSources, _api.NodeStatsManager, peerStorage, _networkConfig, _api.LogManager);
        _api.PeerManager = new PeerManager(
            _api.RlpxPeer,
            _api.PeerPool,
            _api.NodeStatsManager,
            _networkConfig,
            _api.LogManager);

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            await plugin.InitNetworkProtocol();
        }
    }
}
