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

using Nethermind.Core.Crypto;

namespace Nethermind.Core.Test.Builders
{
    public class BlockHeaderBuilder : BuilderBase<BlockHeader>
    {
        public BlockHeaderBuilder()
        {
            TestObject = new BlockHeader(
                                     Keccak.Compute("parent"),
                                     Keccak.OfAnEmptySequenceRlp,
                                     Address.Zero,
                                     1_000_000, 1,
                                     4_000_000,
                                     1_000_000,
                                     new byte[] {1, 2, 3});
            TestObject.Bloom = new Bloom();
            TestObject.MixHash = Keccak.Compute("mix_hash");
            TestObject.Nonce = 1000;
            TestObject.ReceiptsRoot = Keccak.EmptyTreeHash;
            TestObject.StateRoot = Keccak.EmptyTreeHash;
            TestObject.TransactionsRoot = Keccak.EmptyTreeHash;
            TestObject.RecomputeHash();
        }
        
        public BlockHeaderBuilder WithParent(BlockHeader parentHeader)
        {
            TestObject.Number = parentHeader.Number + 1;
            TestObject.GasLimit = parentHeader.GasLimit;
            return this;
        }
    }
}