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

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public partial class PeerManager
{
    /// <summary>
    /// This class represents one of the peering events that needs to be handled in the peer manager main loop.
    /// This event represents a handshake completed event on a session.
    /// We are interested in this stage for all the outgoing sessions - when we know that our peer responded
    /// and we are ready to do more interesting things with them. 
    /// </summary>
    private class HandshakeCompleted : PeeringEvent
    {
        private readonly ISession _session;

        public override void Execute()
        {
            _stats.GetOrAdd(_session.Node);

            //In case of OUT connections and different RemoteNodeId we need to replace existing Active Peer with new peer 
            ManageNewRemoteNodeId();

            if (_logger.IsTrace)
                _logger.Trace($"|NetworkTrace| {_session} completed handshake - peer manager handling");

            //This is the first moment we get confirmed publicKey of remote node in case of incoming connections
            if (_session.Direction == ConnectionDirection.In)
            {
                ProcessIncomingConnection();
            }
            else
            {
                if (!_peerPool.ActivePeers.TryGetValue(_session.RemoteNodeId, out Peer peer))
                {
                    //Can happen when peer sent Disconnect message before handshake is done, it takes us a while to disconnect
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Initiated handshake (OUT) with a peer without adding it to the Active collection : {_session}");
                    return;
                }

                _stats.ReportHandshakeEvent(peer.Node, ConnectionDirection.Out);
            }

            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {_session} handshake initialized in peer manager");
        }

        private void ProcessIncomingConnection()
        {
            if (_logger.IsTrace) _logger.Trace($"INCOMING {_session}");

            // if we have already initiated connection before
            if (_peerPool.ActivePeers.TryGetValue(_session.RemoteNodeId, out Peer existingActivePeer))
            {
                AddSession(_session, existingActivePeer);
                return;
            }

            if (_peerPool.TryGet(_session.Node.Id, out Peer existingPeer))
            {
                // TODO: here the session.Node may not be equal peer.Node -> would be good to check if we can improve it
                _session.Node.IsStatic = existingPeer.Node.IsStatic;
            }

            if (!_session.Node.IsStatic && AvailableActivePeersCount <= 0)
            {
                _session.InitiateDisconnect(DisconnectReason.TooManyPeers,
                    $"Available active peers: {AvailableActivePeersCount}");
                return;
            }
            else if (AvailableActivePeersCount <= 0)
            {
                int initCount = 0;
                foreach (KeyValuePair<PublicKey, Peer> pair in _peerPool.ActivePeers)
                {
                    // we need to count initialized as we may have a list of active peers that is just being initialized
                    // and we do not know yet whether they are fine or not
                    if (pair.Value.InSession?.State == SessionState.Initialized ||
                        pair.Value.OutSession?.State == SessionState.Initialized)
                    {
                        initCount++;
                    }
                }

                if (initCount >= MaxActivePeers)
                {
                    if (_logger.IsTrace)
                        _logger.Trace(
                            $"Initiating disconnect with {_session} {DisconnectReason.TooManyPeers} {DisconnectType.Local}");
                    _session.InitiateDisconnect(DisconnectReason.TooManyPeers, $"{initCount}");
                    return;
                }
            }

            Peer peer = _peerPool.GetOrAdd(_session.Node);
            AddSession(_session, peer);
        }

        private void ManageNewRemoteNodeId()
        {
            if (_session.ObsoleteRemoteNodeId == null)
            {
                return;
            }

            Peer newPeer = _peerPool.Replace(_session);

            _peerManager.RemoveActivePeer(_session.ObsoleteRemoteNodeId,
                $"handshake difference old: {_session.ObsoleteRemoteNodeId}, new: {_session.RemoteNodeId}");
            _peerManager.AddActivePeer(_session.RemoteNodeId, newPeer,
                $"handshake difference old: {_session.ObsoleteRemoteNodeId}, new: {_session.RemoteNodeId}");

            if (_logger.IsTrace)
                _logger.Trace(
                    $"RemoteNodeId was updated due to handshake difference, old: {_session.ObsoleteRemoteNodeId}, new: {_session.RemoteNodeId}, new peer not present in candidate collection");
        }

        public HandshakeCompleted(PeerManager peerManager, ISession session) : base(peerManager)
        {
            _session = session;
        }

        public override string ToString()
        {
            return $"Handshake completed: {_session}";
        }
    }
}
