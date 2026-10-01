// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.P2P;

/// <summary>Dials every static peer the gossip router holds no connection to and opens gossipsub on the session.</summary>
/// <remarks>The router redials a peer only while its reconnection is not suppressed, and a peer it disconnected for an invalid RPC stays
/// suppressed; discovering a known peer again does nothing, so a lost static peer such as the sequencer is dialed here.</remarks>
internal sealed class StaticPeerKeeper(ILocalPeer localPeer, IRoutingStateContainer router, IReadOnlyList<Multiaddress> staticPeers, ILogger logger) : IDisposable
{
    // At most one gossip dial per peer: it is the live stream once connected, and is abandoned if the next check still finds the peer unconnected.
    private readonly Dictionary<PeerId, CancellationTokenSource> _gossipDials = [];

    /// <summary>Runs one check; calls must not overlap.</summary>
    public async Task CheckAsync(CancellationToken token)
    {
        foreach (Multiaddress address in staticPeers)
        {
            if (address.GetPeerId() is not { } peerId || router.ConnectedPeers.Contains(peerId))
            {
                continue;
            }

            Abandon(peerId);
            try
            {
                ISession session = await localPeer.DialAsync(address, token);
                // A second gossip stream to one peer can leave the router a stale entry, so a connect the router made meanwhile wins.
                if (router.ConnectedPeers.Contains(peerId))
                {
                    continue;
                }

                CancellationTokenSource dial = CancellationTokenSource.CreateLinkedTokenSource(token);
                _gossipDials[peerId] = dial;
                // The gossip stream lives as long as the connection, so its end is only observed.
                _ = session.DialAsync<GossipsubProtocolV11>(dial.Token).ContinueWith(
                    static (t, state) => { if (t.IsFaulted && ((ILogger)state!).IsDebug) ((ILogger)state!).Debug($"Static peer gossip ended: {t.Exception!.InnerException?.Message}"); },
                    logger, TaskScheduler.Default);
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                if (logger.IsDebug) logger.Debug($"Reconnecting static peer {address} failed: {e.Message}");
            }
        }
    }

    /// <summary>Internal so a test can see that a stalled dial is replaced, not joined by another.</summary>
    internal int GossipDialCountForTest => _gossipDials.Count;

    private void Abandon(PeerId peerId)
    {
        if (_gossipDials.Remove(peerId, out CancellationTokenSource? dial))
        {
            // Cancelling the dial closes its stream, whether it stalled in negotiation or ended with the connection.
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
