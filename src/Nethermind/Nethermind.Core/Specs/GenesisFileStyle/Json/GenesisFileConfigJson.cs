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

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.GenesisFileStyle.Json
{
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    internal class GenesisFileConfigJson
    {
        public UInt256 ChainId { get; set; }
        
        public long? HomesteadBlock { get; set; }
        
        public long? Eip150Block { get; set; }
        
        public Keccak Eip150Hash { get; set; }
        
        public long? Eip155Block { get; set; }
        
        public long? Eip158Block { get; set; }
        
        public long? ByzantiumBlock { get; set; }
        
        public long? ConstantinopleBlock { get; set; }
        
        public GenesisFileConfigCliqueJson Clique { get; set; }
    }
}