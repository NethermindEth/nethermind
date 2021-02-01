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
using Nethermind.Network;
using Nethermind.Stats.Model;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class PeerInfo
    {
        [JsonProperty("id", Order = 0)]
        public string Id { get; set; }
        
        [JsonProperty("name", Order = 1)]
        public string Name { get; set; }
        
        [JsonProperty("caps", Order = 2)]
        public List<Capability> Caps { get; set; }
        
        [JsonProperty("network", Order = 3)]
        public PeerNetworkInfo Network { get; set; }
        
        [JsonProperty("protocols", Order = 4)]
        public Dictionary<string, EthProtocolInfo> Protocols { get; set; }
        
        public PeerInfo(Peer peer)
        {
            Id = peer.InSession?.RemoteNodeId.ToString() ?? peer.OutSession?.RemoteNodeId.ToString();
            Name = peer.Node.ClientId;
            Network = new PeerNetworkInfo(peer);

            Caps = peer.InSession?.Node.AgreedCapabilities ?? peer.OutSession?.Node.AgreedCapabilities;
          
            Protocols = new Dictionary<string, EthProtocolInfo>();
            Protocols.Add("eth", new EthProtocolInfo(peer));
        }
    }
}
