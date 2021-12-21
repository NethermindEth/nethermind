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

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Stats.Model;

namespace Nethermind.Network;

public partial class PeerManager
{
    private class SessionCreated : PeeringEvent
    {
        private readonly ISession _session;

        public SessionCreated(PeerManager peerManager, ISession session) : base(peerManager)
        {
            _session = session;
        }
        
        public override void Execute()
        {
            ToggleSessionEventListeners(_session, true);
            if (_session.Direction == ConnectionDirection.Out)
            {
                ProcessOutgoingConnection();
            }
        }

        private void ProcessOutgoingConnection()
        {
            if (AvailableActivePeersCount <= 0)
            {
                _session.InitiateDisconnect(DisconnectReason.TooManyPeers,
                    $"Outgoing cancelled, available: {AvailableActivePeersCount}");
            }
            else
            {
                PublicKey id = _session.RemoteNodeId;
                if (!_peerPool.ActivePeers.TryGetValue(id, out Peer peer))
                {
                    // this is an edge case when we initiate connection with a peer that subsequently is removed from
                    // the list of active peers
                    _session.MarkDisconnected(
                        DisconnectReason.DisconnectRequested,
                        DisconnectType.Local,
                        "peer removed");
                    return;
                }

                _stats.ReportEvent(peer.Node, NodeStatsEventType.ConnectionEstablished);
                AddSession(_session, peer);   
            }
        }

        public override string ToString()
        {
            return $"Session created: {_session}";
        }
    }
}
