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

using System;
using System.Globalization;
using System.Net;
using Nethermind.Network;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class PeerInfo
    {
        public string ClientId { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Address { get; set; }
        public bool IsBootnode { get; set; }
        public bool IsTrusted { get; set; }
        public bool IsStatic { get; set; }
        public string Enode { get; set; }

        public string ClientType { get; set; }
        public string EthDetails { get; set; }
        public string LastSignal { get; set; }

        public PeerInfo()
        {
        }

        public PeerInfo(Peer peer, bool includeDetails)
        {
            if (peer.Node == null)
            {
                throw new ArgumentException(
                    $"{nameof(PeerInfo)} cannot be created for a {nameof(Peer)} with an unknown {peer.Node}");
            }
            
            ClientId = peer.Node.ClientId;
            Host = peer.Node.Host == null ? null : IPAddress.Parse(peer.Node.Host).MapToIPv4().ToString();
            Port = peer.Node.Port;
            Address = peer.Node.Address.ToString();
            IsBootnode = peer.Node.IsBootnode;
            IsStatic = peer.Node.IsStatic;
            Enode = peer.Node.ToString("e");
            
            if (includeDetails)
            {
                ClientType = peer.Node.ClientType.ToString();
                EthDetails = peer.Node.EthDetails;
                LastSignal = (peer.InSession ?? peer.OutSession)?.LastPingUtc.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
