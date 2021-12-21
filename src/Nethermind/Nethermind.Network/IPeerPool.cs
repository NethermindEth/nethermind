//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
    
    List<Peer> StaticPeers { get; }
    List<Peer> NonStaticPeers { get; }
    
    int PeerCount { get; }
    int ActivePeerCount { get; }
    int StaticPeerCount { get; }

    public Peer GetOrAdd(NetworkNode networkNode)
    {
        Node node = new (networkNode);
        return GetOrAdd(node);
    }
    
    Peer GetOrAdd(Node node, string member = null);
    bool TryGet(PublicKey id, out Peer peer);
    bool TryRemove(PublicKey id, out Peer removed);
    Peer Replace(ISession session);

    event EventHandler<PeerEventArgs> PeerAdded;
    event EventHandler<PeerEventArgs> PeerRemoved;
    
    void Start();
    Task StopAsync();
}
