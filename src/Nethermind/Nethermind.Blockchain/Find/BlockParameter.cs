// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Find
{
    public class BlockParameter : IEquatable<BlockParameter>
    {
        public static BlockParameter Earliest = new(BlockParameterType.Earliest);

        public static BlockParameter Pending = new(BlockParameterType.Pending);

        public static BlockParameter Latest = new(BlockParameterType.Latest);

        public static BlockParameter Finalized = new(BlockParameterType.Finalized);

        public static BlockParameter Safe = new(BlockParameterType.Safe);

        public BlockParameterType Type { get; }
        public long? BlockNumber { get; }

        public Keccak? BlockHash { get; }

        public bool RequireCanonical { get; }

        public BlockParameter(BlockParameterType type)
        {
            Type = type;
        }

        public BlockParameter(long number)
        {
            Type = BlockParameterType.BlockNumber;
            BlockNumber = number;
        }

        public BlockParameter(Keccak blockHash, bool requireCanonical = false)
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
            return Equals((BlockParameter)obj);
        }

        public override int GetHashCode() => HashCode.Combine(Type, BlockNumber, BlockHash, RequireCanonical);
    }
}
