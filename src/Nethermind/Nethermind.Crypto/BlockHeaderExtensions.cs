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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public static class BlockHeaderExtensions
    {
        private static readonly HeaderDecoder _headerDecoder = new();

        public static Keccak CalculateHash(this BlockHeader header, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            KeccakRlpStream stream = new();
            _headerDecoder.Encode(stream, header, behaviors);

            return stream.GetHash();
        }

        public static Keccak CalculateHash(this Block block, RlpBehaviors behaviors = RlpBehaviors.None) => CalculateHash(block.Header, behaviors);

        public static Keccak GetOrCalculateHash(this BlockHeader header) => header.Hash ?? header.CalculateHash();

        public static Keccak GetOrCalculateHash(this Block block) => block.Hash ?? block.CalculateHash();

        public static bool IsNonZeroTotalDifficulty(this Block block) => block.TotalDifficulty is not null && block.TotalDifficulty != UInt256.Zero;
        public static bool IsNonZeroTotalDifficulty(this BlockHeader header) => header.TotalDifficulty is not null && header.TotalDifficulty != UInt256.Zero;
    }
}
