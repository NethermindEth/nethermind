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
using System.Linq;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class ParityNetPeers
    {
        public int Active { get; set; }
        public int Connected { get; set; }
        public int Max { get; set; }
        public PeerInfo[] Peers { get; set; }
        
        public ParityNetPeers(IReadOnlyCollection<Peer> activePeers, IReadOnlyCollection<Peer> connectedPeers, int maxActivePeers)
        {
            Active = activePeers.Count;
            Connected = connectedPeers.Count;
            Max = maxActivePeers;
            Peers = activePeers.Select(p => new PeerInfo(p)).ToArray();
        }
    }
}
