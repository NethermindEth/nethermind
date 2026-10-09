// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.BeaconChain;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.LightClient.Consensus;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using BeaconPeerManager = Nethermind.BeaconChain.P2P.PeerManager;

namespace Nethermind.LightClient;

/// <summary>Obtains light-client SSZ messages directly from connected beacon peers.</summary>
internal sealed class BeaconPeerTransport : IAsyncDisposable
{
    private readonly MemColumnsDb<BeaconChainDbColumns> _db = new();
    private readonly IPResolver _ipResolver;
    private readonly BeaconP2P _p2p;
    private readonly BeaconDiscovery _discovery;
    private readonly BeaconPeerManager _peers;
    private readonly Nethermind.Logging.ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task? _peerLoop;

    internal int PeerCount => _peers.PeerCount;

    internal BeaconPeerTransport(BeaconChainSpec spec, ILogManager logManager, int p2pPort = 9050, int discoveryPort = 9050)
    {
        _logger = logManager.GetClassLogger<BeaconPeerTransport>();
        BeaconChainConfig config = new()
        {
            P2PPort = p2pPort,
            Discv5Port = discoveryPort,
            MinPeerCount = 2,
            TargetPeerCount = 8,
            MaxPeerCount = 12,
        };
        BeaconChainStore store = new(_db, spec);
        ITimestamper clock = Timestamper.Default;
        BeaconChainStatusHolder status = new(spec, clock);
        _ipResolver = new IPResolver(new NetworkConfig { EnableExternalIpResolution = false }, logManager);
        _p2p = new BeaconP2P(config, spec, store, status, new LocalMetadataSource(),
            new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), logManager);
        _discovery = new BeaconDiscovery(config, spec, store, _ipResolver, clock, logManager);
        _peers = new BeaconPeerManager(_p2p, config, status, logManager, _discovery, clock);
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _p2p.StartAsync(cancellationToken);
        await _discovery.Start(cancellationToken);
        _peerLoop = Task.WhenAll(_peers.Run(_stop.Token),
            _peers.DialDiscoveredPeersAsync(_discovery.DiscoverPeers(_stop.Token), _stop.Token));
    }

    internal Task<LightClientBootstrap> BootstrapAsync(Hash256 root, Action<LightClientBootstrap> validate, CancellationToken token) =>
        RequestAsync(peer => peer.RequestLightClientBootstrapAsync(root, token), validate, token);

    internal Task<LightClientUpdate> UpdateAsync(ulong period, Action<LightClientUpdate> validate, CancellationToken token) =>
        RequestAsync(peer => peer.RequestLightClientUpdateAsync(period, token), validate, token);

    internal Task<LightClientFinalityUpdate> FinalityAsync(Action<LightClientFinalityUpdate> validate, CancellationToken token) =>
        RequestAsync(peer => peer.RequestLightClientFinalityAsync(token), validate, token);

    internal Task<LightClientOptimisticUpdate> OptimisticAsync(Action<LightClientOptimisticUpdate> validate, CancellationToken token) =>
        RequestAsync(peer => peer.RequestLightClientOptimisticAsync(token), validate, token);

    private async Task<T> RequestAsync<T>(Func<IBeaconSyncPeer, Task<T>> request, Action<T> validate, CancellationToken token) where T : class
    {
        long lastStatus = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            T? response = await RequestFromPeersAsync(_peers.GetBestPeers(0), request, validate, token);
            if (response is not null) return response;

            if (Stopwatch.GetElapsedTime(lastStatus) >= TimeSpan.FromSeconds(10))
            {
                if (_logger.IsInfo) _logger.Info($"Waiting for beacon light-client data; connected peers: {PeerCount}");
                lastStatus = Stopwatch.GetTimestamp();
            }

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    internal static async Task<T?> RequestFromPeersAsync<T>(IReadOnlyList<IBeaconSyncPeer> peers,
        Func<IBeaconSyncPeer, Task<T>> request, Action<T> validate, CancellationToken token) where T : class
    {
        foreach (IBeaconSyncPeer peer in peers)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                T response = await request(peer);
                try { validate(response); }
                catch (IrrelevantLightClientUpdateException) { continue; }
                catch (LightClientLocalStateException) { continue; }
                catch (InvalidDataException exception)
                {
                    peer.ReportFailure(PeerFailureReason.ProtocolViolation, exception.Message);
                    continue;
                }
                return response;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                peer.ReportFailure(PeerFailureReason.RequestFailed, exception.Message);
            }
        }
        return null;
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
        await _p2p.DisposeAsync();
        await _ipResolver.DisposeAsync();
        _db.Dispose();
        _stop.Dispose();
    }
}
