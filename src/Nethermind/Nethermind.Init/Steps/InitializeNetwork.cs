// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Init.Steps;

public static class NettyMemoryEstimator
{
    private const uint PageSize = 8192;

    public static void SetPageSize() =>
        // For some reason needs to be half page size to get page size
        Environment.SetEnvironmentVariable("io.netty.allocator.pageSize", (PageSize / 2).ToString((IFormatProvider?)null));

    public static long Estimate(uint arenaCount, int arenaOrder) => arenaCount * (1L << arenaOrder) * PageSize;
}

[RunnerStepDependencies(
    typeof(LoadGenesisBlock),
    typeof(SetupKeyStore),
    typeof(InitializePlugins),
    typeof(InitializeBlockchain))]
#pragma warning disable IDE0290 // Primary constructor would shadow discard `_` used in fire-and-forget patterns
public class InitializeNetwork : IStep
{
    protected readonly ISynchronizer _synchronizer;
    protected readonly ISyncPeerPool _syncPeerPool;
    protected readonly IDiscoveryApp _discoveryApp;
    protected readonly Lazy<IPeerPool> _peerPool;
    protected readonly INetworkConfig _networkConfig;

    private readonly IBlockTree _blockTree;
    private readonly IRlpxHost _rlpxPeer;
    private readonly IPeerManager _peerManager;
    private readonly ISessionMonitor _sessionMonitor;
    private readonly IStaticNodesManager _staticNodesManager;
    private readonly ITrustedNodesManager _trustedNodesManager;
    private readonly IEnode _enode;
    private readonly INethermindPlugin[] _plugins;
    private readonly Lazy<IProtocolsManager> _protocolsManager;
    private readonly Lazy<SnapCapabilitySwitcher> _snapCapabilitySwitcher;

    private readonly NodeSourceToDiscV4Feeder _enrDiscoveryAppFeeder;
    private readonly ISyncConfig _syncConfig;
    private readonly IInitConfig _initConfig;
    private readonly ILogManager _logManager;

    private readonly ILogger _logger;

    public InitializeNetwork(
        ISyncServer _, // Need to be resolved at least once
        ISynchronizer synchronizer,
        ISyncPeerPool syncPeerPool,
        NodeSourceToDiscV4Feeder enrDiscoveryAppFeeder,
        IDiscoveryApp discoveryApp,
        Lazy<IPeerPool> peerPool, // Require IRlpxPeer to be created first, hence, lazy.
        IBlockTree blockTree,
        IRlpxHost rlpxPeer,
        IPeerManager peerManager,
        ISessionMonitor sessionMonitor,
        IStaticNodesManager staticNodesManager,
        ITrustedNodesManager trustedNodesManager,
        IEnode enode,
        INethermindPlugin[] plugins,
        Lazy<IProtocolsManager> protocolsManager,
        Lazy<SnapCapabilitySwitcher> snapCapabilitySwitcher,
        INetworkConfig networkConfig,
        ISyncConfig syncConfig,
        IInitConfig initConfig,
        ILogManager logManager
    )
    {
        _synchronizer = synchronizer;
        _syncPeerPool = syncPeerPool;
        _enrDiscoveryAppFeeder = enrDiscoveryAppFeeder;
        _discoveryApp = discoveryApp;
        _peerPool = peerPool;
        _blockTree = blockTree;
        _rlpxPeer = rlpxPeer;
        _peerManager = peerManager;
        _sessionMonitor = sessionMonitor;
        _staticNodesManager = staticNodesManager;
        _trustedNodesManager = trustedNodesManager;
        _enode = enode;
        _plugins = plugins;
        _protocolsManager = protocolsManager;
        _snapCapabilitySwitcher = snapCapabilitySwitcher;
        _networkConfig = networkConfig;
        _syncConfig = syncConfig;
        _initConfig = initConfig;
        _logManager = logManager;

        _logger = logManager.GetClassLogger<InitializeNetwork>();
    }

    public virtual Task Execute(CancellationToken cancellationToken) => Initialize(cancellationToken);

    private async Task Initialize(CancellationToken cancellationToken)
    {
        if (_syncConfig.StaticSnapPivot)
        {
            if (!_syncConfig.SnapSync)
                throw new InvalidConfigurationException("Sync.StaticSnapPivot requires Sync.SnapSync to be enabled.", -1);
            if (_syncConfig.PivotNumber <= 0 || string.IsNullOrWhiteSpace(_syncConfig.PivotHash))
                throw new InvalidConfigurationException("Sync.StaticSnapPivot requires Sync.PivotNumber and Sync.PivotHash to be set to the target (frozen) pivot block.", -1);
        }

        if (_networkConfig.DiagTracerEnabled)
        {
            NetworkDiagTracer.IsEnabled = true;
        }

        if (NetworkDiagTracer.IsEnabled)
        {
            NetworkDiagTracer.Start(_logManager);
        }

        int maxPeersCount = _networkConfig.ActivePeersMaxCount;
        Network.Metrics.PeerLimit = maxPeersCount;

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
            _snapCapabilitySwitcher.Value.EnableSnapCapabilityUntilSynced();
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

        ProductInfo.InitializePublicClientId(_networkConfig.PublicClientIdFormat);

        ThisNodeInfo.AddInfo("Ethereum     :", $"tcp://{_enode.HostIp}:{_enode.Port} ");
        ThisNodeInfo.AddInfo("Client id    :", ProductInfo.ClientId);
        ThisNodeInfo.AddInfo("Public id    :", ProductInfo.PublicClientId);
        ThisNodeInfo.AddInfo("This node    :", $"{_enode.Info} ");
        ThisNodeInfo.AddInfo("Node address :", $"{_enode.Address} (do not use as an account)");
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
        if (!_initConfig.PeerManagerEnabled)
        {
            if (_logger.IsWarn) _logger.Warn($"Skipping peer manager init due to {nameof(IInitConfig.PeerManagerEnabled)} set to false");
        }

        if (_logger.IsDebug) _logger.Debug("Initializing peer manager");
        _peerPool.Value.Start();
        _peerManager.Start();
        _sessionMonitor.Start();
        if (_logger.IsDebug) _logger.Debug("Peer manager initialization completed");
    }

    private Task StartSync()
    {
        if (_syncConfig.NetworkingEnabled)
        {
            _syncPeerPool.Start();

            if (_syncConfig.SynchronizationEnabled)
            {
                if (_logger.IsDebug) _logger.Debug($"Starting synchronization from block {_blockTree.Head?.Header.ToString(BlockHeader.Format.Short)}.");
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

    protected virtual async Task InitPeer()
    {
        IProtocolsManager protocolsManager = _protocolsManager.Value;

        if (_syncConfig.SnapServingEnabled == true)
        {
            protocolsManager.AddSupportedCapability(new Capability(Protocol.Snap, 1));
        }
        if (!_networkConfig.DisableDiscV4DnsFeeder)
        {
            // Feed some nodes into discoveryApp in case all bootnodes is faulty.
            _ = _enrDiscoveryAppFeeder.Run();
        }

        foreach (INethermindPlugin plugin in _plugins)
        {
            await plugin.InitNetworkProtocol();
        }

        // Capabilities must be finalized before the RLPx listener accepts peers. Otherwise
        // early sessions can negotiate only the default ETH version and never upgrade.
        await _rlpxPeer.Init();

        await _staticNodesManager.InitAsync();

        await _trustedNodesManager.InitAsync();
    }
}
