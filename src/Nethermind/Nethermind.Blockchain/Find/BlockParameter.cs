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

using System;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Blockchain.Find
{
    [Todo(Improve.Refactor, "Can make it struct?")]
    public class BlockParameter : IEquatable<BlockParameter>
    {
        public static BlockParameter Earliest = new BlockParameter(BlockParameterType.Earliest);

        public static BlockParameter Pending = new BlockParameter(BlockParameterType.Pending);

        public static BlockParameter Latest = new BlockParameter(BlockParameterType.Latest);

        public BlockParameterType Type { get; set; }
        public long? BlockNumber { get; }
        
        public Keccak BlockHash { get; }

        public bool RequireCanonical { get; }

        public BlockParameter()
        {
        }

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

        [Todo(Improve.Refactor,"Move it to converters")]
        public static BlockParameter FromJson(string jsonValue)
        {
            switch (jsonValue)
            {
                case { } earliest when string.Equals(earliest, "earliest", StringComparison.InvariantCultureIgnoreCase):
                    return Earliest;
                case { } pending when string.Equals(pending, "pending", StringComparison.InvariantCultureIgnoreCase):
                    return Pending;
                case { } latest when string.Equals(latest, "latest", StringComparison.InvariantCultureIgnoreCase):
                    return Latest;
                case { } empty when string.IsNullOrWhiteSpace(empty):
                    return Latest;
                case null:
                    return Latest;
                case { } hash when hash.Length == 66 && hash.StartsWith("0x"):
                    return Latest;
                default:
                    return new BlockParameter(LongConverter.FromString(jsonValue.Trim('"')));
            }
        }

        public override string ToString()
        {
            return $"{Type}, {BlockNumber?.ToString() ?? BlockHash?.ToString()}";
        }
        public bool Equals(BlockParameter other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Type == other.Type && BlockNumber == other.BlockNumber && BlockHash == other.BlockHash && other.RequireCanonical == RequireCanonical;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((BlockParameter) obj);
        }

        public override int GetHashCode()
        {
            throw new NotSupportedException();
        }
    }
}