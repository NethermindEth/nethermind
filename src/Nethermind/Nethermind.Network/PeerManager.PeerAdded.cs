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

using Nethermind.Stats.Model;

namespace Nethermind.Network;

public partial class PeerManager
{
    private class PeerAdded : PeeringEvent
    {
        private readonly Peer _peer;

        public override void Execute()
        {
            _stats.ReportEvent(_peer.Node, NodeStatsEventType.NodeDiscovered);
            if (AvailableActivePeersCount > 0)
            {
#pragma warning disable 4014
                // fire and forget - all the surrounding logic will be executed
                // exceptions can be lost here without issues
                // this for rapid connections to newly discovered peers without having to go through the UpdatePeerLoop
                _peerManager.SetupPeerConnection(_peer);
#pragma warning restore 4014
            }
        }

        public PeerAdded(PeerManager peerManager, Peer peer) : base(peerManager)
        {
            _peer = peer;
        }

        public override string ToString()
        {
            return $"Peer added: {_peer}";
        }
    }
}
