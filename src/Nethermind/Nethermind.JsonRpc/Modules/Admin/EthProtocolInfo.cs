//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Admin
{
    public class EthProtocolInfo
    {
        [JsonProperty("difficulty", Order = 0)]
        public UInt256 Difficulty { get; set; }
        [JsonProperty("genesis", Order = 1)]
        public Keccak GenesisHash { get; set; }
        [JsonProperty("head", Order = 2)]
        public Keccak HeadHash { get; set; }
        [JsonProperty("network", Order = 3)]
        public int ChainId { get; set; }
    }
}