// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
/// suppressed; discovering a known peer again does nothing, so a lost static peer such as the sequencer is dialed here.
/// A DNS name is resolved here on every check and the peer dialed by address: Nethermind.Libp2p 1.0.0 keeps a failed resolution as the
/// pending dial of that peer id for good, so no dial to the peer would succeed after one failed lookup.</remarks>
public sealed class StaticPeerKeeper : IDisposable
{
    private readonly ILocalPeer _localPeer;
    private readonly IRoutingStateContainer _router;
    private readonly IReadOnlyList<Multiaddress> _staticPeers;
    private readonly ILogger _logger;
    private readonly Func<ISession, CancellationToken, Task> _openGossip;
    private readonly Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> _resolveHost;

    public StaticPeerKeeper(ILocalPeer localPeer, IRoutingStateContainer router, IReadOnlyList<Multiaddress> staticPeers, ILogger logger)
        : this(localPeer, router, staticPeers, logger, openGossip: null, resolveHost: null)
    {
    }

    /// <param name="openGossip">Opens gossipsub on a session and completes when that channel ends; gossipsub v1.1 when null.</param>
    /// <param name="resolveHost">Resolves a DNS name to addresses of a family; the system resolver when null.</param>
    internal StaticPeerKeeper(ILocalPeer localPeer, IRoutingStateContainer router, IReadOnlyList<Multiaddress> staticPeers, ILogger logger,
        Func<ISession, CancellationToken, Task>? openGossip, Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>>? resolveHost)
    {
        _localPeer = localPeer;
        _router = router;
        _staticPeers = staticPeers;
        _logger = logger;
        _openGossip = openGossip ?? (static (session, token) => session.DialAsync<GossipsubProtocolV11>(token));
        _resolveHost = resolveHost ?? (static (host, family, token) => Dns.GetHostAddressesAsync(host, family, token));
    }

    /// <summary>Whether <paramref name="address"/> names its host by DNS; such a peer must reach the library only through the keeper.</summary>
    public static bool HasDnsName(Multiaddress address) => address.Has<DNS>() || address.Has<DNS4>() || address.Has<DNS6>();

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
                Multiaddress[] dialable = await DialableAsync(address, token);
                if (dialable.Length == 0)
                {
                    if (_logger.IsDebug) _logger.Debug($"Static peer {address} resolved to no address");
                    continue;
                }

                ISession session = await _localPeer.DialAsync(dialable, token);
                // A second gossip channel to one peer can leave the router a stale entry, so a connect the router made meanwhile wins.
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

    private async Task<Multiaddress[]> DialableAsync(Multiaddress address, CancellationToken token)
    {
        if (address.Has<DNS4>())
        {
            IPAddress[] ips = await _resolveHost(address.Get<DNS4>().ToString(), AddressFamily.InterNetwork, token);
            return [.. ips.Select(ip => address.Clone().Replace<DNS4, IP4>(ip))];
        }

        if (address.Has<DNS6>())
        {
            IPAddress[] ips = await _resolveHost(address.Get<DNS6>().ToString(), AddressFamily.InterNetworkV6, token);
            return [.. ips.Select(ip => address.Clone().Replace<DNS6, IP6>(ip))];
        }

        if (address.Has<DNS>())
        {
            IPAddress[] ips = await _resolveHost(address.Get<DNS>().ToString(), AddressFamily.Unspecified, token);
            return [.. ips.Select(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 ? address.Clone().Replace<DNS, IP6>(ip) : address.Clone().Replace<DNS, IP4>(ip))];
        }

        return [address];
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
