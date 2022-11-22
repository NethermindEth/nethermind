// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Clique
{
    internal static class BlockHeaderExtensions
    {
        public static bool IsInTurn(this BlockHeader header)
        {
            return header.Difficulty == Clique.DifficultyInTurn;
        }

        internal static Address[] ExtractSigners(BlockHeader blockHeader)
        {
            if (blockHeader.ExtraData is null)
            {
                throw new Exception(string.Empty);
            }

            Span<byte> signersData = blockHeader.ExtraData.AsSpan()
                .Slice(Clique.ExtraVanityLength, blockHeader.ExtraData.Length - Clique.ExtraSealLength - Clique.ExtraVanityLength);
            Address[] signers = new Address[signersData.Length / Address.ByteLength];
            for (int i = 0; i < signers.Length; i++)
            {
                signers[i] = new Address(signersData.Slice(i * 20, 20).ToArray());
            }

            return signers;
        }
    }

    internal static class BlockExtensions
    {
        public static bool IsInTurn(this Block block)
        {
            return block.Difficulty == Clique.DifficultyInTurn;
        }
    }
}
