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
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.HashLib;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Find
{
    public class BlockParameter : IEquatable<BlockParameter>
    {
        public static BlockParameter Earliest = new(BlockParameterType.Earliest);

        public static BlockParameter Pending = new(BlockParameterType.Pending);

        public static BlockParameter Latest = new(BlockParameterType.Latest);

        public BlockParameterType Type { get; }
        public long? BlockNumber { get; }
        
        public Keccak? BlockHash { get; }

        public bool RequireCanonical { get; set; }

        public BlockParameter(BlockParameterType type)
        {
            Type = type;
        }

        public BlockParameter(long number)
        {
            Type = BlockParameterType.BlockNumber;
            BlockNumber = number;
        }
        
        public BlockParameter(Keccak blockHash)
        {
            Type = BlockParameterType.BlockHash;
            BlockHash = blockHash;
        }
        
        public BlockParameter(Keccak blockHash, bool requireCanonical)
        {
            Type = BlockParameterType.BlockHash;
            BlockHash = blockHash;
            RequireCanonical = requireCanonical;
        }

        public override string ToString() => $"{Type}, {BlockNumber?.ToString() ?? BlockHash?.ToString()}";

        public bool Equals(BlockParameter? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Type == other.Type && BlockNumber == other.BlockNumber && BlockHash == other.BlockHash && other.RequireCanonical == RequireCanonical;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((BlockParameter) obj);
        }

        public override int GetHashCode() => HashCode.Combine(Type, BlockNumber, BlockHash, RequireCanonical);
    }
}
