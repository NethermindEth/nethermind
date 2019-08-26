/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.GenesisFileStyle.Json
{
    internal class GenesisFileJson
    {   
        public UInt256 Difficulty { get; set; }
        
        public Address Author { get; set; }
        
        public UInt256 Timestamp { get; set; }
        
        public Keccak ParentHash { get; set; }
        
        public byte[] ExtraData { get; set; }
        
        public UInt256 GasLimit { get; set; }
        
        public UInt256 Nonce { get; set; }
        
        public Keccak MixHash { get; set; }
        
        public UInt256 GasUsed { get; set; }
        
        public Address Coinbase { get; set; }
        
        public Dictionary<string, AllocationJson> Alloc { get; set; }

        public GenesisFileConfigJson Config { get; set; }
    }
}