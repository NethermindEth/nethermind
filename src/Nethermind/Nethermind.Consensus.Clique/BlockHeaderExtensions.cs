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
            if (blockHeader.ExtraData == null)
            {
                throw new Exception("");
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
