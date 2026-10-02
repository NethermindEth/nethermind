// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;

namespace Nethermind.Network.Libp2p;

/// <summary>Dials every static peer the gossip router holds no connection to and opens gossipsub on the session.</summary>
/// <remarks>The router redials a peer only while its reconnection is not suppressed, and a peer it disconnected for an invalid RPC stays
/// suppressed; discovering a known peer again does nothing, so a lost static peer such as the sequencer is dialed here.</remarks>
public sealed class StaticPeerKeeper : IDisposable
{
    private readonly ILocalPeer _localPeer;
    private readonly IRoutingStateContainer _router;
    private readonly IReadOnlyList<Multiaddress> _staticPeers;
    private readonly ILogger _logger;
    private readonly Func<ISession, CancellationToken, Task> _openGossip;

    public StaticPeerKeeper(ILocalPeer localPeer, IRoutingStateContainer router, IReadOnlyList<Multiaddress> staticPeers, ILogger logger)
        : this(localPeer, router, staticPeers, logger, openGossip: null)
    {
    }

    /// <param name="openGossip">Opens gossipsub on a session and completes when that channel ends; gossipsub v1.1 when null.</param>
    internal StaticPeerKeeper(ILocalPeer localPeer, IRoutingStateContainer router, IReadOnlyList<Multiaddress> staticPeers, ILogger logger,
        Func<ISession, CancellationToken, Task>? openGossip)
    {
        _localPeer = localPeer;
        _router = router;
        _staticPeers = staticPeers;
        _logger = logger;
        _openGossip = openGossip ?? (static (session, token) => session.DialAsync<GossipsubProtocolV11>(token));
    }

    /// <summary>Whether a dial can reach <paramref name="address"/>: an IP address or DNS name, a TCP port and a peer id, or a dnsaddr name and a peer id.</summary>
    public static bool CanDial(Multiaddress address) => address.Protocols is [IP4 or IP6 or DNS or DNS4 or DNS6, TCP, P2P] or [DnsAddr, P2P];

    /// <summary>Long enough not to race the router's own reconnect, short enough to win back a lost peer within a minute.</summary>
    public static TimeSpan CheckInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Checks <paramref name="staticPeers"/> at once and then every <paramref name="interval"/> until <paramref name="token"/> is cancelled.</summary>
    public static async Task RunAsync(ILocalPeer localPeer, IRoutingStateContainer router, IReadOnlyList<Multiaddress> staticPeers, TimeSpan interval, ILogger logger, CancellationToken token)
    {
        using StaticPeerKeeper keeper = new(localPeer, router, staticPeers, logger);
        using PeriodicTimer timer = new(interval);
        try
        {
            do
            {
                await keeper.CheckAsync(token);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    // At most one gossip dial per peer: it is the live channel once connected, and is abandoned if the next check still finds the peer unconnected.
    private readonly Dictionary<PeerId, CancellationTokenSource> _gossipDials = [];

    /// <summary>Runs one check; calls must not overlap.</summary>
    public async Task CheckAsync(CancellationToken token)
    {
        foreach (Multiaddress address in _staticPeers)
        {
            if (address.GetPeerId() is not { } peerId || _router.ConnectedPeers.Contains(peerId))
            {
                continue;
            }

            Abandon(peerId);
            try
            {
                ISession session = await _localPeer.DialAsync(address, token);
                // A connect the router made meanwhile wins, so the peer gets no second gossip channel.
                if (_router.ConnectedPeers.Contains(peerId))
                {
                    continue;
                }

                CancellationTokenSource dial = CancellationTokenSource.CreateLinkedTokenSource(token);
                _gossipDials[peerId] = dial;
                // The gossip channel lives as long as the connection, so its end is only observed.
                _ = _openGossip(session, dial.Token).ContinueWith(
                    static (t, state) => { if (t.IsFaulted && ((ILogger)state!).IsDebug) ((ILogger)state!).Debug($"Static peer gossip ended: {t.Exception!.InnerException?.Message}"); },
                    _logger, TaskScheduler.Default);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (_logger.IsDebug) _logger.Debug($"Reconnecting static peer {address} failed: {e.Message}");
            }
        }
    }

    private void Abandon(PeerId peerId)
    {
        if (_gossipDials.Remove(peerId, out CancellationTokenSource? dial))
        {
            // Cancelling the dial closes its channel, whether it stalled in negotiation or ended with the connection.
            dial.Cancel();
            dial.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource dial in _gossipDials.Values)
        {
            dial.Cancel();
            dial.Dispose();
        }

        _gossipDials.Clear();
    }
}
