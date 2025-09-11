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
        public static Hash256 CalculateHash(this BlockHeader header, RlpBehaviors behaviors = RlpBehaviors.None)
            => new Hash256(CalculateValueHash(header, behaviors));

        public static ValueHash256 CalculateValueHash(this BlockHeader header, RlpBehaviors behaviors = RlpBehaviors.None)
        {
            return Rlp.EncodeForHash(header, behaviors);
        }

        public static Hash256 CalculateHash(this Block block, RlpBehaviors behaviors = RlpBehaviors.None) => CalculateHash(block.Header, behaviors);

        public static Hash256 GetOrCalculateHash(this BlockHeader header) => header.Hash ?? header.CalculateHash();

        public static Hash256 GetOrCalculateHash(this Block block) => block.Hash ?? block.CalculateHash();

        public static bool IsNonZeroTotalDifficulty(this Block block) => block.Header.IsNonZeroTotalDifficulty();
        public static bool IsNonZeroTotalDifficulty(this BlockHeader header) => header.TotalDifficulty is not null && header.TotalDifficulty != UInt256.Zero;
    }
}
