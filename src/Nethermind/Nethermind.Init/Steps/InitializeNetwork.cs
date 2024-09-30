// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Utils;
using Nethermind.Core;
using Nethermind.Core.Timers;
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
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.Trie;
using Module = Autofac.Module;

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
    typeof(ResolveIps),
    typeof(InitializePlugins),
    typeof(InitializeBlockchain))]
public class InitializeNetwork : IStep
{
    public const string PeersDbPath = "peers";

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
            NetworkDiagTracer.Start(_api.LogManager);
        }

        if (_api.ChainSpec.SealEngineType == SealEngineType.Clique)
            _syncConfig.NeedToWaitForHeader = true; // Should this be in chainspec itself?

        int maxPeersCount = _networkConfig.ActivePeersMaxCount;
        Network.Metrics.PeerLimit = maxPeersCount;

        ContainerBuilder builder = new ContainerBuilder();
        _api.ConfigureContainerBuilderFromApiWithNetwork(builder);
        builder.RegisterModule(new NetworkModule(_networkConfig, _syncConfig));

        ISynchronizationPlugin[] synchronizationPlugins = _api.GetSynchronizationPlugins().ToArray();

        foreach (ISynchronizationPlugin plugin in synchronizationPlugins)
        {
            plugin.ConfigureSynchronizationBuilder(builder);
        }

        IContainer container = builder.Build();
        _api.ApiWithNetworkServiceContainer = container;
        _api.DisposeStack.Append(container);

        foreach (ISynchronizationPlugin plugin in synchronizationPlugins)
        {
            await plugin.InitSynchronization(container);
        }

        // TODO: This whole thing can be injected into `InitializeNetwork`, but the container then
        // need to be put at a higher level.
        SyncedTxGossipPolicy txGossipPolicy = container.Resolve<SyncedTxGossipPolicy>();
        ISyncServer _ = container.Resolve<ISyncServer>();
        IDiscoveryApp discoveryApp = container.Resolve<IDiscoveryApp>();
        IPeerPool peerPool = container.Resolve<IPeerPool>();
        IPeerManager peerManager = container.Resolve<IPeerManager>();
        ISessionMonitor sessionMonitor = container.Resolve<ISessionMonitor>();
        IRlpxHost rlpxHost = container.Resolve<IRlpxHost>();
        IStaticNodesManager staticNodesManager = container.Resolve<IStaticNodesManager>();
        Func<NodeSourceToDiscV4Feeder> nodeSourceToDiscV4Feeder = container.Resolve<Func<NodeSourceToDiscV4Feeder>>();
        IProtocolsManager protocolsManager = container.Resolve<IProtocolsManager>();
        EnrDiscovery enrDiscover = container.Resolve<EnrDiscovery>();
        SnapCapabilitySwitcher snapCapabilitySwitcher = container.Resolve<SnapCapabilitySwitcher>();
        ISyncPeerPool syncPeerPool = container.Resolve<ISyncPeerPool>();
        ISynchronizer synchronizer = container.Resolve<ISynchronizer>();

        _api.TxGossipPolicy.Policies.Add(txGossipPolicy);

        if (_api.TrieStore is HealingTrieStore healingTrieStore)
        {
            healingTrieStore.InitializeNetwork(container.Resolve<GetNodeDataTrieNodeRecovery>());
        }

        if (_api.WorldState is HealingWorldState healingWorldState)
        {
            healingWorldState.InitializeNetwork(container.Resolve<SnapTrieNodeRecovery>());
        }

        InitDiscovery(discoveryApp);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await InitPeer(rlpxHost, staticNodesManager, nodeSourceToDiscV4Feeder, protocolsManager).ContinueWith(initPeerTask =>
        {
            if (initPeerTask.IsFaulted)
            {
                _logger.Error("Unable to init the peer manager.", initPeerTask.Exception);
            }
        });

        if (_syncConfig.SnapSync && _syncConfig.SnapServingEnabled != true)
        {
            snapCapabilitySwitcher.EnableSnapCapabilityUntilSynced();
        }

        else if (_logger.IsDebug) _logger.Debug("Skipped enabling snap capability");

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await StartSync(syncPeerPool, synchronizer).ContinueWith(initNetTask =>
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

        await StartDiscovery(discoveryApp).ContinueWith(initDiscoveryTask =>
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

            StartPeer(peerPool, peerManager, sessionMonitor);
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

    private Task StartDiscovery(IDiscoveryApp discoveryApp)
    {
        if (!_api.Config<IInitConfig>().DiscoveryEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to {nameof(IInitConfig.DiscoveryEnabled)} set to false");
            return Task.CompletedTask;
        }

        if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
        _ = discoveryApp.StartAsync();
        if (_logger.IsDebug) _logger.Debug("Discovery process started.");
        return Task.CompletedTask;
    }

    private void StartPeer(IPeerPool peerPool, IPeerManager peerManager, ISessionMonitor sessionMonitor)
    {
        if (!_api.Config<IInitConfig>().PeerManagerEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false");
        }

        if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
        peerPool.Start();
        peerManager.Start();
        sessionMonitor.Start();
        if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
    }

    private void InitDiscovery(IDiscoveryApp discoveryApp)
    {
        discoveryApp.Initialize(_api.NodeKey!.PublicKey);
    }

    private Task StartSync(ISyncPeerPool syncPeerPool, ISynchronizer synchronizer)
    {
        ISyncConfig syncConfig = _api.Config<ISyncConfig>();
        if (syncConfig.NetworkingEnabled)
        {
            syncPeerPool.Start();

            if (syncConfig.SynchronizationEnabled)
            {
                if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_api.BlockTree!.Head?.Header.ToString(BlockHeader.Format.Short)}.");
                synchronizer.Start();
            }
            else
            {
                if (_logger.IsWarn) _logger.Warn($"Skipping blockchain synchronization init due to {nameof(ISyncConfig.SynchronizationEnabled)} set to false");
            }
        }
        else if (_logger.IsWarn) _logger.Warn($"Skipping connecting to peers due to {nameof(ISyncConfig.NetworkingEnabled)} set to false");


        return Task.CompletedTask;
    }

    private async Task InitPeer(
        IRlpxHost rlpxHost,
        IStaticNodesManager staticNodesManager,
        Func<NodeSourceToDiscV4Feeder> nodeSourceToDiscV4Feeder,
        IProtocolsManager protocolsManager
    )
    {
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));

        /* rlpx */
        Eip8MessagePad eip8Pad = new(_api.CryptoRandom);
        _api.MessageSerializationService.Register(new AuthEip8MessageSerializer(eip8Pad));
        _api.MessageSerializationService.Register(new AckEip8MessageSerializer(eip8Pad));
        _api.MessageSerializationService.Register(Assembly.GetAssembly(typeof(HelloMessageSerializer))!);
        ReceiptsMessageSerializer receiptsMessageSerializer = new(_api.SpecProvider);
        _api.MessageSerializationService.Register(receiptsMessageSerializer);
        _api.MessageSerializationService.Register(new Network.P2P.Subprotocols.Eth.V66.Messages.ReceiptsMessageSerializer(receiptsMessageSerializer));

        IDiscoveryConfig discoveryConfig = _api.Config<IDiscoveryConfig>();
        // TODO: hack, but changing it in all the documentation would be a nightmare
        _networkConfig.Bootnodes = discoveryConfig.Bootnodes;

        await rlpxHost.Init();
        await staticNodesManager.InitAsync();

        if (_syncConfig.SnapServingEnabled == true)
        {
            protocolsManager.AddSupportedCapability(new Capability(Protocol.Snap, 1));
        }

        if (!_networkConfig.DisableDiscV4DnsFeeder)
        {
            // Feed some nodes into discoveryApp in case all bootnodes is faulty.
            _ = nodeSourceToDiscV4Feeder().Run(_api.ProcessExit!.Token);
        }

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            await plugin.InitNetworkProtocol();
        }
    }
}

public class NetworkModule(INetworkConfig networkConfig, ISyncConfig syncConfig): Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder.RegisterModule(new SynchronizerModule(syncConfig));

        builder
            .Register(ctx =>
            {
                // Had to manually construct as the network config is not referrable by NodeStatsManagerv
                ITimerFactory timerFactory = ctx.Resolve<ITimerFactory>();
                ILogManager logManager = ctx.Resolve<ILogManager>();
                INetworkConfig config = ctx.Resolve<INetworkConfig>();
                return new NodeStatsManager(timerFactory, logManager, config.MaxCandidatePeerCount);
            })
            .As<INodeStatsManager>()
            .SingleInstance();

        builder
            .AddSingleton<IBetterPeerStrategy, TotalDifficultyBetterPeerStrategy>()
            .AddSingleton<IBeaconSyncStrategy>(No.BeaconSync)
            .AddSingleton<IPivot, Pivot>()
            .AddSingleton<IEthSyncingInfo, EthSyncingInfo>()
            .AddSingleton<SyncedTxGossipPolicy>()
            .AddSingleton<GetNodeDataTrieNodeRecovery>()
            .AddSingleton<SnapTrieNodeRecovery>()
            .AddSingleton<ISyncServer, SyncServer>()
            ;

        builder
            .RegisterType<SyncPeerPool>()
            .As<ISyncPeerPool>()
            .As<IPeerDifficultyRefreshPool>()
            .SingleInstance();


        builder
            .AddSingleton<CompositeDiscoveryApp>()
            .AddSingleton<NullDiscoveryApp>();

        builder
            .Register(ctx => (IDiscoveryApp)(ctx.Resolve<IInitConfig>().DiscoveryEnabled
                ? ctx.Resolve<CompositeDiscoveryApp>()
                : ctx.Resolve<NullDiscoveryApp>()))
            .As<IDiscoveryApp>()
            .SingleInstance();

        /* rlpx */
        builder
            .AddSingleton<IEciesCipher, EciesCipher>()
            .AddSingleton<IHandshakeService, HandshakeService>()
            .AddSingleton<IDisconnectsAnalyzer, MetricsDisconnectsAnalyzer>()
            .AddSingleton<ISessionMonitor, SessionMonitor>()
            .AddSingleton<IRlpxHost, RlpxHost>();

        builder
            .Register(ctx =>
            {
                // TOOD: Move StaticNodesPath to NetworkConfig.
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                ILogManager logManager = ctx.Resolve<ILogManager>();
                return new StaticNodesManager(initConfig.StaticNodesPath, logManager);
            })
            .As<IStaticNodesManager>()
            .SingleInstance();

        builder
            .Register(ctx =>
            {
                IInitConfig initConfig = ctx.Resolve<IInitConfig>();
                ILogManager logManager = ctx.Resolve<ILogManager>();

                // ToDo: PeersDB is registered outside dbProvider
                string dbName = "PeersDB";
                IFullDb peersDb = initConfig.DiagnosticMode == DiagnosticMode.MemDb
                    ? new MemDb(dbName)
                    : new SimpleFilePublicKeyDb(dbName,
                        InitializeNetwork.PeersDbPath.GetApplicationResourcePath(initConfig.BaseDbPath),
                        logManager);

                return peersDb;
            })
            .Named<IFullDb>(nameof(NetworkStorage));


        builder
            .AddSingleton<INetworkStorage, NetworkStorage>()
            .AddSingleton<ForkInfo>()
            .AddSingleton<IProtocolValidator, ProtocolValidator>()
            .AddSingleton<IPooledTxsRequestor, PooledTxsRequestor>()
            .AddSingleton<ISnapServer, SnapServer>()
            .AddSingleton<IProtocolsManager, ProtocolsManager>()
            .AddSingleton<NodesLoader>()
            .AddSingleton<NodeSourceToDiscV4Feeder>();

        builder
            .Register(ctx =>
            {
                IEthereumEcdsa ecdsa = ctx.Resolve<IEthereumEcdsa>();
                ILogManager logManager = ctx.Resolve<ILogManager>();
                ChainSpec chainSpec = ctx.Resolve<ChainSpec>();

                // I do not use the key here -> API is broken - no sense to use the node signer here
                NodeRecordSigner nodeRecordSigner = new(ecdsa, new PrivateKeyGenerator().Generate());
                EnrRecordParser enrRecordParser = new(nodeRecordSigner);

                if (networkConfig.DiscoveryDns == null)
                {
                    string chainName = BlockchainIds.GetBlockchainName(chainSpec.NetworkId).ToLowerInvariant();
                    networkConfig.DiscoveryDns = $"all.{chainName}.ethdisco.net";
                }

                EnrDiscovery enrDiscovery = new(enrRecordParser, networkConfig, logManager); // initialize with a proper network

                return enrDiscovery;
            })
            .AsSelf()
            .Named<INodeSource>(INodeSource.EnrSource)
            .SingleInstance();

        if (!networkConfig.OnlyStaticPeers)
        {
            builder
                .Bind<IDiscoveryApp, INodeSource>()
                .Bind<EnrDiscovery, INodeSource>();
        }
        else
        {
            builder
                .Bind<IStaticNodesManager, INodeSource>()
                .Bind<NodesLoader, INodeSource>()
                .Bind<IDiscoveryApp, INodeSource>()
                .Bind<EnrDiscovery, INodeSource>();
        }

        builder
            .RegisterComposite<CompositeNodeSource, INodeSource>();

        builder
            .AddSingleton<SnapCapabilitySwitcher>()
            .AddSingleton<IPeerPool, PeerPool>()
            .AddSingleton<IPeerManager, PeerManager>();
    }
}
