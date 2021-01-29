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

using Nethermind.Network;
using Nethermind.Network.P2P;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class PeerNetworkInfo
    {
        public string LocalAddress { get; set; }
        public string RemoteAddress { get; set; }

        public PeerNetworkInfo(Peer peer)
        {
            LocalAddress = peer.Node.Host;
            RemoteAddress = peer.InSession != null ? GetRemoteAdress(peer.InSession)
                : peer.OutSession != null ? GetRemoteAdress(peer.OutSession)
                : null;
        }
        
        private string GetRemoteAdress(ISession session)
        {
            return session.State != SessionState.New ? session.RemoteHost : "Handshake";
        }
    }
}
