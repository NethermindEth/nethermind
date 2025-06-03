// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.TxPool;

namespace Nethermind.Init.Steps;

public static class NettyMemoryEstimator
{
    private const uint PageSize = 8192;

    public static void SetPageSize()
    {
        // For some reason needs to be half page size to get page size
        Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", (PageSize / 2).ToString((IFormatProvider?)null));
    }

    public static long Estimate(uint arenaCount, int arenaOrder)
    {
        return arenaCount * (1L << arenaOrder) * PageSize;
    }
}

[RunnerStepDependencies(
    typeof(LoadGenesisBlock),
    typeof(SetupKeyStore),
    typeof(ResolveIps),
    typeof(InitializePlugins),
    typeof(EraStep),
    typeof(InitializeBlockchain))]
public class InitializeNetwork : IStep
{
    private readonly IApiWithNetwork _api;
    private readonly INodeStatsManager _nodeStatsManager;
    private readonly ISynchronizer _synchronizer;
    private readonly ISyncPeerPool _syncPeerPool;
    private readonly NodeSourceToDiscV4Feeder _enrDiscoveryAppFeeder;
    private readonly INetworkStorage _peerStorage;
    private readonly IDiscoveryApp _discoveryApp;
    private readonly Lazy<IPeerPool> _peerPool;

    private readonly INetworkConfig _networkConfig;
    private readonly ISyncConfig _syncConfig;
    private readonly IInitConfig _initConfig;

    private readonly ILogger _logger;

    public InitializeNetwork(
        INethermindApi api,
        INodeStatsManager nodeStatsManager,
        ISyncServer _, // Need to be resolved at least once
        ISynchronizer synchronizer,
        ISyncPeerPool syncPeerPool,
        NodeSourceToDiscV4Feeder enrDiscoveryAppFeeder,
        IDiscoveryApp discoveryApp,
        Lazy<IPeerPool> peerPool, // Require IRlpxPeer to be created first, hence, lazy.
        [KeyFilter(DbNames.PeersDb)] INetworkStorage peerStorage,
        INetworkConfig networkConfig,
        ISyncConfig syncConfig,
        IInitConfig initConfig,
        ILogManager logManager
    )
    {
        _api = api;
        _nodeStatsManager = nodeStatsManager;
        _synchronizer = synchronizer;
        _syncPeerPool = syncPeerPool;
        _enrDiscoveryAppFeeder = enrDiscoveryAppFeeder;
        _discoveryApp = discoveryApp;
        _peerPool = peerPool;
        _peerStorage = peerStorage;
        _networkConfig = networkConfig;
        _syncConfig = syncConfig;
        _initConfig = initConfig;

        _logger = logManager.GetClassLogger();
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        await Initialize(cancellationToken);
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

        if (_networkConfig.DiagTracerEnabled)
        {
            NetworkDiagTracer.IsEnabled = true;
        }

        if (NetworkDiagTracer.IsEnabled)
        {
            NetworkDiagTracer.Start(_api.LogManager);
        }

        int maxPeersCount = _networkConfig.ActivePeersMaxCount;
        Network.Metrics.PeerLimit = maxPeersCount;

        _api.TxGossipPolicy.Policies.Add(new SyncedTxGossipPolicy(_api.SyncModeSelector));

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

        ProductInfo.InitializePublicClientId(_networkConfig.PublicClientIdFormat);

        ThisNodeInfo.AddInfo("Ethereum     :", $"tcp://{_api.Enode.HostIp}:{_api.Enode.Port}");
        ThisNodeInfo.AddInfo("Client id    :", ProductInfo.ClientId);
        ThisNodeInfo.AddInfo("Public id    :", ProductInfo.PublicClientId);
        ThisNodeInfo.AddInfo("This node    :", $"{_api.Enode.Info}");
        ThisNodeInfo.AddInfo("Node address :", $"{_api.Enode.Address} (do not use as an account)");
    }

    private Task StartDiscovery()
    {
        if (!_initConfig.DiscoveryEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping discovery init due to {nameof(IInitConfig.DiscoveryEnabled)} set to false");
            return Task.CompletedTask;
        }

        if (_logger.IsDebug) _logger.Debug("Starting discovery process.");
        _ = _discoveryApp.StartAsync();
        if (_logger.IsDebug) _logger.Debug("Discovery process started.");
        return Task.CompletedTask;
    }

    private void StartPeer()
    {
        if (_api.PeerManager is null) throw new StepDependencyException(nameof(_api.PeerManager));
        if (_api.SessionMonitor is null) throw new StepDependencyException(nameof(_api.SessionMonitor));

        if (!_api.Config<IInitConfig>().PeerManagerEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false");
        }

        if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
        _peerPool.Value.Start();
        _api.PeerManager.Start();
        _api.SessionMonitor.Start();
        if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
    }

    private Task StartSync()
    {
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

        if (_syncConfig.NetworkingEnabled)
        {
            _syncPeerPool.Start();

            if (_syncConfig.SynchronizationEnabled)
            {
                if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_api.BlockTree.Head?.Header.ToString(BlockHeader.Format.Short)}.");
                _synchronizer.Start();
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
        if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.SpecProvider is null) throw new StepDependencyException(nameof(_api.SpecProvider));
        if (_api.TxPool is null) throw new StepDependencyException(nameof(_api.TxPool));

        await _api.RlpxPeer.Init();

        await _api.StaticNodesManager.InitAsync();

        await _api.TrustedNodesManager.InitAsync();

        ISyncServer syncServer = _api.SyncServer!;
        ForkInfo forkInfo = new(_api.SpecProvider!, syncServer.Genesis.Hash!);

        ProtocolValidator protocolValidator = new(
            _nodeStatsManager!,
            _api.BlockTree,
            forkInfo,
            _api.PeerManager!,
            _networkConfig,
            _api.LogManager);
        PooledTxsRequestor pooledTxsRequestor = new(_api.TxPool!, _api.Config<ITxPoolConfig>(), _api.SpecProvider);

        _api.ProtocolsManager = new ProtocolsManager(
            _api.SyncPeerPool!,
            syncServer,
            _api.BackgroundTaskScheduler,
            _api.TxPool,
            pooledTxsRequestor,
            _discoveryApp,
            _api.MessageSerializationService,
            _api.RlpxPeer,
            _nodeStatsManager,
            protocolValidator,
            _peerStorage,
            forkInfo,
            _api.GossipPolicy,
            _api.WorldStateManager!,
            _api.LogManager,
            _api.TxGossipPolicy);

        if (_syncConfig.SnapServingEnabled == true)
        {
            _api.ProtocolsManager!.AddSupportedCapability(new Capability(Protocol.Snap, 1));
        }
        if (_api.WorldStateManager!.HashServer is null)
        {
            _api.ProtocolsManager!.RemoveSupportedCapability(new Capability(Protocol.NodeData, 1));
        }

        _api.ProtocolValidator = protocolValidator;

        if (!_networkConfig.DisableDiscV4DnsFeeder)
        {
            // Feed some nodes into discoveryApp in case all bootnodes is faulty.
            _ = _enrDiscoveryAppFeeder.Run();
        }

        foreach (INethermindPlugin plugin in _api.Plugins)
        {
            await plugin.InitNetworkProtocol();
        }
    }
}
