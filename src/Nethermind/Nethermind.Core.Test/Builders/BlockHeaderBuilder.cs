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
        private readonly BlockHeader _blockHeader;

        public BlockHeaderBuilder()
        {
            _blockHeader = new BlockHeader(
                                     Keccak.Compute("parent"),
                                     Keccak.OfAnEmptySequenceRlp,
                                     Address.Zero,
                                     1_000_000, 1,
                                     4_000_000,
                                     1_000_000,
                                     new byte[] {1, 2, 3});
            _blockHeader.Bloom = new Bloom();
            _blockHeader.MixHash = Keccak.Compute("mix_hash");
            _blockHeader.Nonce = 1000;
            _blockHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
            _blockHeader.StateRoot = Keccak.EmptyTreeHash;
            _blockHeader.TransactionsRoot = Keccak.EmptyTreeHash;
            _blockHeader.RecomputeHash();
        }
        
        public BlockHeaderBuilder WithParent(BlockHeader parentHeader)
        {
            _blockHeader.Number = parentHeader.Number + 1;
            _blockHeader.GasLimit = parentHeader.GasLimit;
            return this;
        }
        
        public override BlockHeader ToTest()
        {
            return _blockHeader;
        }
    }
}