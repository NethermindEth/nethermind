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
using System.ComponentModel;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public partial class PeerManager
{
    private class SessionDisconnected : PeeringEvent
    {
        private readonly ISession _session;
        private readonly DisconnectType _disconnectType;
        private readonly DisconnectReason _reason;

        public SessionDisconnected(
            PeerManager peerManager,
            ISession session,
            DisconnectType disconnectType,
            DisconnectReason reason) : base(peerManager)
        {
            _session = session;
            _disconnectType = disconnectType;
            _reason = reason;
        }
        
        public override void Execute()
        {
            if (_session.State != SessionState.Disconnected)
            {
                throw new InvalidAsynchronousStateException(
                    $"Invalid session state in {nameof(OnDisconnected)} - {_session.State}");
            }

            bool resolved = _peerPool.TryGetOrAdd(_session.Node, out Peer peer);
            if (resolved && _session.Direction == ConnectionDirection.Out)
            {
                peer!.IsAwaitingConnection = false;
            }

            if (_peerPool.ActivePeers.TryGetValue(_session.RemoteNodeId, out Peer activePeer))
            {
                // we want to always update reputation
                _stats.ReportDisconnect(_session.Node, _disconnectType, _reason);
                if (activePeer.InSession?.SessionId != _session.SessionId &&
                    activePeer.OutSession?.SessionId != _session.SessionId)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"Received a disconnect on a different session than the active peer runs. Ignoring. Id: {activePeer.Node.Id}");
                }

                DeactivatePeerIfDisconnected(activePeer, "session disconnected");
            }
        }

        public override string ToString()
        {
            return $"Session disconnected: {_session} {_disconnectType} {_reason}";
        }
    }
}
