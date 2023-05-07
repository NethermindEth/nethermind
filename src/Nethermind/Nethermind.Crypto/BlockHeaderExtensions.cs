// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            // Uncompressed version for hash calculation
            behaviors &= ~RlpBehaviors.Storage;
            _headerDecoder.Encode(stream, header, behaviors);

            return stream.GetHash();
        }

        public static Keccak CalculateHash(this Block block, RlpBehaviors behaviors = RlpBehaviors.None) => CalculateHash(block.Header, behaviors);

        public static Keccak GetOrCalculateHash(this BlockHeader header) => header.Hash ?? header.CalculateHash();

        public static Keccak GetOrCalculateHash(this Block block) => block.Hash ?? block.CalculateHash();

        public static bool IsNonZeroTotalDifficulty(this Block block) => block.Header.IsNonZeroTotalDifficulty();
        public static bool IsNonZeroTotalDifficulty(this BlockHeader header) => header.TotalDifficulty is not null && header.TotalDifficulty != UInt256.Zero;
    }
}
