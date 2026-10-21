// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net.Quic;
using Nethermind.BeaconChain;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.LightClient.Consensus;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using BeaconPeerManager = Nethermind.BeaconChain.P2P.PeerManager;

namespace Nethermind.LightClient;

/// <summary>Obtains light-client SSZ messages directly from connected beacon peers.</summary>
internal sealed class BeaconPeerTransport : IAsyncDisposable
{
    private static readonly TimeSpan PeerRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly IPResolver _ipResolver;
    private readonly BeaconP2P _p2p;
    private readonly BeaconDiscovery _discovery;
    private readonly BeaconPeerManager _peers;
    private readonly LightClientGossip _gossip;
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _peerLoop;

    internal int PeerCount => _peers.PeerCount;
    internal Task GossipCompletion => _gossip.Completion;

    internal BeaconPeerTransport(BeaconChainSpec spec, ILogManager logManager, IColumnsDb<BeaconChainDbColumns> db,
        INetworkStorage? peerStorage = null, int p2pPort = 9050, int discoveryPort = 9050,
        TimeProvider? time = null, int quicPort = 9051)
    {
        _logger = logManager.GetClassLogger<BeaconPeerTransport>();
        _gossip = new(spec, logManager, time);
        bool quicSupported = QuicListener.IsSupported && QuicConnection.IsSupported;
        if (!quicSupported && quicPort > 0 && _logger.IsWarn)
            _logger.Warn("QUIC is unavailable on this platform; beacon P2P will use TCP. Install the platform's MsQuic runtime to enable QUIC.");
        BeaconChainConfig config = new()
        {
            P2PPort = p2pPort,
            Discv5Port = discoveryPort,
            QuicPort = quicSupported ? quicPort : 0,
            MinPeerCount = 2,
            TargetPeerCount = 8,
            MaxPeerCount = 12,
        };
        BeaconChainStore store = new(db, spec);
        ITimestamper clock = Timestamper.Default;
        BeaconChainStatusHolder status = new(spec, clock);
        _ipResolver = new IPResolver(new NetworkConfig(), logManager);
        _p2p = new BeaconP2P(config, spec, store, status, new LocalMetadataSource(),
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), logManager,
            peerPool: new Lazy<IBeaconSyncPeerPool>(() => _peers!), configureGossip: _gossip.Configure);
        _discovery = new BeaconDiscovery(config, spec, store, _ipResolver, clock, logManager, peerStorage);
        _peers = new BeaconPeerManager(_p2p, config, status, logManager, _discovery, clock);
    }

    /// <summary>Opens durable beacon identity and ENR sequence storage owned by the caller.</summary>
    internal static ColumnsDb<BeaconChainDbColumns> OpenPeerDb(string dataDirectory, BeaconChainSpec spec, ILogManager logs)
    {
        string path = Path.Combine(dataDirectory, "cl", spec.ChainId.ToString(), "network");
        DbConfig config = new()
        {
            WriteAheadLogSync = true,
            MaxOpenFiles = 64,
            AdditionalRocksDbOptions = "write_buffer_size=262144;max_write_buffer_number=2;block_based_table_factory.block_cache=262144;target_file_size_base=262144;max_bytes_for_level_base=1048576;",
        };
        ColumnsDb<BeaconChainDbColumns> db = new(path,
            new DbSettings("lightClientBeacon", path) { PersistRepairMarkerUntilAcknowledged = true, SkipMetricsTracking = true },
            config, new RocksDbConfigFactory(config, new PruningConfig(), new HardwareInfo(), logs, validateConfig: false),
            logs, Enum.GetValues<BeaconChainDbColumns>());
        if (db.WasRepairedOnOpen)
        {
            db.Dispose();
            throw new InvalidDataException("Beacon networking storage was repaired; restore it before reusing its identity and ENR sequence.");
        }
        return db;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _p2p.StartAsync(cancellationToken);
        if (_logger.IsInfo) _logger.Info($"Beacon P2P identity: {_p2p.LocalPeerId}");
        await _discovery.Start(cancellationToken);
        _peerLoop = Task.WhenAll(_peers.Run(_stop.Token),
            _peers.DialDiscoveredPeersAsync(_discovery.DiscoverPeers(_stop.Token), _stop.Token));
    }

    internal void StartGossip(Func<LightClientFinalityUpdate, CancellationToken, Task<bool>> finality,
        Func<LightClientOptimisticUpdate, CancellationToken, Task<bool>> optimistic, CancellationToken token) =>
        _gossip.Start(_p2p.GetTopic, finality, optimistic, token);

    internal Task<LightClientBootstrap> BootstrapAsync(Hash256 root, Func<LightClientBootstrap, CancellationToken, Task> validate, CancellationToken token) =>
        RequestAsync((peer, peerToken) => peer.RequestLightClientBootstrapAsync(root, peerToken), validate, token);

    internal Task<LightClientUpdate> UpdateAsync(ulong period, Func<LightClientUpdate, CancellationToken, Task> validate, CancellationToken token) =>
        RequestAsync((peer, peerToken) => peer.RequestLightClientUpdateAsync(period, peerToken), validate, token);

    internal Task<LightClientFinalityUpdate?> PollFinalityAsync(Func<LightClientFinalityUpdate, CancellationToken, Task> validate, CancellationToken token) =>
        PollFromPeersAsync(() => _peers.GetBestPeers(0), (peer, peerToken) => peer.RequestLightClientFinalityAsync(peerToken), validate, token);

    internal Task<LightClientOptimisticUpdate?> PollOptimisticAsync(Func<LightClientOptimisticUpdate, CancellationToken, Task> validate, CancellationToken token) =>
        PollFromPeersAsync(() => _peers.GetBestPeers(0), (peer, peerToken) => peer.RequestLightClientOptimisticAsync(peerToken), validate, token);

    internal static async Task<T?> PollFromPeersAsync<T>(Func<IReadOnlyList<IBeaconSyncPeer>> getPeers,
        Func<IBeaconSyncPeer, CancellationToken, Task<T>> request, Func<T, CancellationToken, Task> validate, CancellationToken token,
        TimeProvider? timeProvider = null) where T : class
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<IBeaconSyncPeer> peers;
        while ((peers = getPeers()).Count == 0)
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider ?? TimeProvider.System, token);
        (T? response, bool sawInapplicable) = await RequestFromPeersAsync(peers, request, validate, token, timeProvider);
        if (response is null && !sawInapplicable) throw new IOException("No beacon peer supplied an applicable light-client update.");
        return response;
    }

    private async Task<T> RequestAsync<T>(Func<IBeaconSyncPeer, CancellationToken, Task<T>> request,
        Func<T, CancellationToken, Task> validate, CancellationToken token) where T : class
    {
        long lastStatus = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            (T? response, _) = await RequestFromPeersAsync(_peers.GetBestPeers(0), request, validate, token);
            if (response is not null) return response;

            if (Stopwatch.GetElapsedTime(lastStatus) >= TimeSpan.FromSeconds(10))
            {
                if (_logger.IsInfo) _logger.Info($"Waiting for beacon light-client data; connected peers: {PeerCount}");
                lastStatus = Stopwatch.GetTimestamp();
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    internal static async Task<(T? Response, bool SawInapplicable)> RequestFromPeersAsync<T>(IReadOnlyList<IBeaconSyncPeer> peers,
        Func<IBeaconSyncPeer, CancellationToken, Task<T>> request, Func<T, CancellationToken, Task> validate, CancellationToken token,
        TimeProvider? timeProvider = null) where T : class
    {
        bool sawInapplicable = false;
        foreach (IBeaconSyncPeer peer in peers)
        {
            token.ThrowIfCancellationRequested();
            using CancellationTokenSource peerTimeout = new(PeerRequestTimeout, timeProvider ?? TimeProvider.System);
            using CancellationTokenSource peerAttempt = CancellationTokenSource.CreateLinkedTokenSource(token, peerTimeout.Token);
            T response;
            try
            {
                response = await request(peer, peerAttempt.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (peerTimeout.IsCancellationRequested)
            {
                peer.ReportFailure(PeerFailureReason.RequestFailed, "Light-client request timed out.");
                continue;
            }
            catch (Exception exception)
            {
                peer.ReportFailure(PeerFailureReason.RequestFailed, exception.Message);
                continue;
            }
            try { await validate(response, token); }
            catch (IrrelevantLightClientUpdateException) { sawInapplicable = true; continue; }
            catch (LightClientLocalStateException) { sawInapplicable = true; continue; }
            catch (InvalidDataException exception)
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, exception.Message);
                continue;
            }
            return (response, false);
        }
        return (null, sawInapplicable);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_peerLoop is not null)
        {
            try { await _peerLoop; }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (_logger.IsWarn) _logger.Warn($"Beacon peer loop stopped with an error: {exception.Message}");
            }
        }
        await _discovery.DisposeAsync();
        await _gossip.DisposeAsync();
        await _p2p.DisposeAsync();
        await _ipResolver.DisposeAsync();
        _stop.Dispose();
    }
}
