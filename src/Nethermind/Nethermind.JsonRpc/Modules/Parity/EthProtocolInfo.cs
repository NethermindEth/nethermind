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

using Nethermind.Int256;
using Nethermind.Network;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Parity
{
    public class EthProtocolInfo
    {
        [JsonProperty("version", Order = 0)]
        public byte Version { get; set; }
        
        [JsonProperty("difficulty", Order = 1)]
        public UInt256 Difficulty { get; set; }
        
        [JsonProperty("head", Order = 2)]
        public string HeadHash { get; set; }

        public EthProtocolInfo(Peer peer)
        {
            Version = peer.InSession?.Node.EthProtocolVersion ?? peer.OutSession?.Node.EthProtocolVersion ?? 0;
            Difficulty = peer.InSession?.Node.Difficulty ?? peer.OutSession?.Node.Difficulty ?? 0;
            HeadHash = peer.InSession?.Node.HeadHash?.ToString() ?? peer.OutSession?.Node.HeadHash?.ToString();
        }
    }
}
