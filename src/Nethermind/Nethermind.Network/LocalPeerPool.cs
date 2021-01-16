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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    internal class LocalPeerPool
    {
        private readonly ILogger _logger;
        private ConcurrentDictionary<PublicKey, Peer> _staticPeers = new ConcurrentDictionary<PublicKey, Peer>();
        public ConcurrentDictionary<PublicKey, Peer> AllPeers { get; } = new ConcurrentDictionary<PublicKey, Peer>();
        public IEnumerable<Peer> CandidatePeers => AllPeers.Values;
        public List<Peer> NonStaticCandidatePeers => AllPeers.Values.Where(p => !p.Node.IsStatic).ToList();
        public List<Peer> StaticPeers => _staticPeers.Values.ToList();
        public int CandidatePeerCount => AllPeers.Count;
        public int StaticPeerCount => _staticPeers.Count;

        public LocalPeerPool(ILogger logger)
        {
            _logger = logger;
        }

        public Peer GetOrAdd(NetworkNode node, bool isStatic)
        {
            static Peer CreateNew(PublicKey key, (NetworkNode Node, bool IsStatic, ConcurrentDictionary<PublicKey, Peer> Statics) arg)
            {
                Peer peer = new Peer(new Node(arg.Node.NodeId, arg.Node.Host, arg.Node.Port, arg.IsStatic));
                if (arg.IsStatic)
                {
                    arg.Statics.TryAdd(arg.Node.NodeId, peer);
                }

                return peer;
            }

            return AllPeers.GetOrAdd(node.NodeId, CreateNew, (node, isStatic, _staticPeers));
        }

        public Peer GetOrAdd(Node node)
        {
            if (node.IsBootnode || node.IsStatic)
            {
                if (_logger.IsDebug) _logger.Debug($"Adding a {(node.IsBootnode ? "bootnode" : "stored")} candidate peer {node:s}");
            }

            static Peer CreateNew(PublicKey key, (Node Node, ConcurrentDictionary<PublicKey, Peer> Statics) arg)
            {
                Peer peer = new Peer(arg.Node);
                if (arg.Node.IsStatic)
                {
                    arg.Statics.TryAdd(arg.Node.Id, peer);
                }

                return peer;
            }

            return AllPeers.GetOrAdd(node.Id, CreateNew, (node, _staticPeers));
        }

        public bool TryRemove(PublicKey id, out Peer peer)
        {
            if (AllPeers.TryRemove(id, out peer))
            {
                _staticPeers.TryRemove(id, out _);
                peer.InSession?.MarkDisconnected(DisconnectReason.DisconnectRequested, DisconnectType.Local, "admin_removePeer");
                peer.OutSession?.MarkDisconnected(DisconnectReason.DisconnectRequested, DisconnectType.Local, "admin_removePeer");
                peer.InSession = null;
                peer.OutSession = null;
                return true;
            }

            return false;
        }

        public Peer Replace(ISession session)
        {
            if (AllPeers.TryGetValue(session.ObsoleteRemoteNodeId, out Peer previousPeer))
            {
                // this should happen
                if (previousPeer.InSession == session || previousPeer.OutSession == session)
                {
                    if (previousPeer.Node.IsStatic)
                    {
                        session.Node.IsStatic = true;
                    }

                    // (what with the other session?)

                    _staticPeers.TryRemove(session.ObsoleteRemoteNodeId, out _);
                    AllPeers.TryRemove(session.ObsoleteRemoteNodeId, out Peer oldPeer);
                    if (oldPeer != null)
                    {
                        oldPeer.InSession = null;
                        oldPeer.OutSession = null;
                    }
                }
                else
                {
                    _logger.Error("Trying to update node ID on an unknown peer - requires investigation, please report the issue.");
                }
            }

            Peer newPeer = GetOrAdd(session.Node);
            newPeer.InSession = session.Direction == ConnectionDirection.In ? session : null;
            newPeer.OutSession = session.Direction == ConnectionDirection.Out ? session : null;
            return newPeer;
        }
    }
}
