// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain
{
    public static class UnclesHash
    {
        public static Hash256 Calculate(Block block) => Calculate(block.Uncles);

        public static Hash256 Calculate(BlockHeader[] uncles)
        {
            if (uncles.Length == 0)
            {
                return Keccak.OfAnEmptySequenceRlp;
            }

            using ArrayPoolSpan<byte> rlp = Rlp.GetDecoderOrThrow<BlockHeader>().EncodeToArrayPoolSpan<BlockHeader>(uncles);
            return Keccak.Compute(rlp);
        }
    }
}
