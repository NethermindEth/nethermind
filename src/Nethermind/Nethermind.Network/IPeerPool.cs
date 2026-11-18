// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.ServiceStopper;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public interface IPeerPool : IStoppableService
{
    ConcurrentDictionary<PublicKeyAsKey, Peer> Peers { get; }
    ConcurrentDictionary<PublicKeyAsKey, Peer> ActivePeers { get; }

    IEnumerable<Peer> StaticPeers { get; }
    IEnumerable<Peer> NonStaticPeers { get; }

    int PeerCount { get; }
    int ActivePeerCount { get; }

    public Peer GetOrAdd(NetworkNode networkNode)
    {
        Node node = new(networkNode);
        return GetOrAdd(node);
    }

    Peer GetOrAdd(Node node);
    /// <summary>Attempts to publish a candidate with retained statistics ownership.</summary>
    /// <remarks>Decorators must forward inbound candidate attribution to the wrapped pool.</remarks>
    bool TryGetOrAdd(Node node, [NotNullWhen(true)] out Peer? peer, bool inboundCandidate = false)
    {
        GetOrAdd(node);
        if (Peers.TryGetValue(node.Id, out peer)) return true;
        peer = null;
        return false;
    }
    /// <summary>Reserves node statistics for an operator-requested admission.</summary>
    /// <remarks>The default imposes no capacity limit or lease. Bounded pools and their decorators must override or forward this method.</remarks>
    bool TryReserveStats(Node node, out IDisposable? lease)
    {
        lease = null;
        return true;
    }

    bool TryGet(PublicKey id, out Peer peer);
    bool TryRemove(PublicKey id, out Peer removed);
    Peer Replace(ISession session);

    event EventHandler<PeerEventArgs> PeerAdded;
    event EventHandler<PeerEventArgs> PeerRemoved;

    void Start();
}
