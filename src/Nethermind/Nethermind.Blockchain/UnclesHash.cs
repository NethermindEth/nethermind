// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain
{
    public static class UnclesHash
    {
        public static Keccak Calculate(Block block)
        {
            return block.Uncles.Length == 0
                ? Keccak.OfAnEmptySequenceRlp
                : Keccak.Compute(Rlp.Encode(block.Uncles).Bytes);
        }

        public static Keccak Calculate(BlockHeader[] uncles)
        {
            return uncles.Length == 0
                ? Keccak.OfAnEmptySequenceRlp
                : Keccak.Compute(Rlp.Encode(uncles).Bytes);
        }
    }
}
