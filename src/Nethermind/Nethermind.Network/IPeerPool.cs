// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public interface IPeerPool
{
    ConcurrentDictionary<PublicKey, Peer> Peers { get; }
    ConcurrentDictionary<PublicKey, Peer> ActivePeers { get; }

    IEnumerable<Peer> StaticPeers { get; }
    IEnumerable<Peer> NonStaticPeers { get; }

    int PeerCount { get; }
    int ActivePeerCount { get; }
    int StaticPeerCount { get; }

    public Peer GetOrAdd(NetworkNode networkNode)
    {
        Node node = new(networkNode);
        return GetOrAdd(node);
    }

    Peer GetOrAdd(Node node);
    bool TryGet(PublicKey id, out Peer peer);
    bool TryRemove(PublicKey id, out Peer removed);
    Peer Replace(ISession session);

    event EventHandler<PeerEventArgs> PeerAdded;
    event EventHandler<PeerEventArgs> PeerRemoved;

    void Start();
    Task StopAsync();
}
