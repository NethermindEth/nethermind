/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nevermind.Core.Crypto;

namespace Nevermind.Core.Test
{
    public class BlockHeaderBuilder : TestObjectBuilder<BlockHeader>
    {
        public override BlockHeader ForTest()
        {
            BlockHeader blockHeader = new BlockHeader(Keccak.Compute("parent"), Keccak.OfAnEmptySequenceRlp, Address.Zero, 1_000_000, 1, 4_000_000, 1_000_000, new byte[] {1, 2, 3});
            blockHeader.Bloom = new Bloom();
            blockHeader.MixHash = Keccak.Compute("mix_hash");
            blockHeader.Nonce = 1000;
            blockHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            blockHeader.StateRoot = Keccak.EmptyTreeHash;
            blockHeader.TransactionsRoot = Keccak.EmptyTreeHash;
            blockHeader.RecomputeHash();
            return blockHeader;
        }
    }
}