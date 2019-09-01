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

using System.Linq;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Test.Builders
{
    public class BlockBuilder : BuilderBase<Block>
    {
        public BlockBuilder()
        {
            BlockHeader header = Build.A.BlockHeader.TestObject;
            TestObjectInternal = new Block(header);
        }

        public BlockBuilder WithHeader(BlockHeader header)
        {
            TestObjectInternal.Header = header;
            return this;
        }
        
        public BlockBuilder WithNumber(long number)
        {
            TestObjectInternal.Header.Number = number;
            return this;
        }
        
        public BlockBuilder WithExtraData(byte[] extraData)
        {
            TestObjectInternal.Header.ExtraData = extraData;
            return this;
        }
        
        public BlockBuilder WithGasLimit(long gasLimit)
        {
            TestObjectInternal.Header.GasLimit = gasLimit;
            return this;
        }
        
        public BlockBuilder WithTimestamp(UInt256 timestamp)
        {
            TestObjectInternal.Header.Timestamp = timestamp;
            return this;
        }
        
        public BlockBuilder WithTransactions(params Transaction[] transactions)
        {
            TestObjectInternal.Body.Transactions = transactions;
            return this;
        }
        
        public BlockBuilder WithBeneficiary(Address address)
        {
            TestObjectInternal.Header.Beneficiary = address;
            return this;
        }

        public BlockBuilder WithTotalDifficulty(UInt256 difficulty)
        {
            TestObjectInternal.Header.TotalDifficulty = difficulty;
            return this;
        }

        public BlockBuilder WithNonce(ulong nonce)
        {
            TestObjectInternal.Header.Nonce = nonce;
            return this;
        }
        
        public BlockBuilder WithMixHash(Keccak mixHash)
        {
            TestObjectInternal.Header.MixHash = mixHash;
            return this;
        }
        
        public BlockBuilder WithDifficulty(UInt256 difficulty)
        {
            TestObjectInternal.Header.Difficulty = difficulty;
            return this;
        }

        public BlockBuilder WithParent(BlockHeader blockHeader)
        {
            TestObjectInternal.Header.Number = blockHeader?.Number + 1 ?? 0;
            TestObjectInternal.Header.Timestamp = blockHeader?.Timestamp + 1 ?? 0;
            TestObjectInternal.Header.ParentHash = blockHeader == null ? Keccak.Zero : blockHeader.Hash;
            return this;
        }

        public BlockBuilder WithParent(Block block)
        {
            return WithParent(block.Header);
        }
        
        public BlockBuilder WithOmmers(params Block[] ommers)
        {
            TestObjectInternal.Body.Ommers = ommers.Select(o => o.Header).ToArray();
            return this;
        }
        
        public BlockBuilder WithOmmers(params BlockHeader[] ommers)
        {
            TestObjectInternal.Body.Ommers = ommers;
            return this;
        }

        public BlockBuilder WithParentHash(Keccak parent)
        {
            TestObjectInternal.Header.ParentHash = parent;
            return this;
        }
        
        public BlockBuilder WithStateRoot(Keccak stateRoot)
        {
            TestObjectInternal.Header.StateRoot = stateRoot;
            return this;
        }
        
        public BlockBuilder WithBloom(Bloom bloom)
        {
            TestObjectInternal.Bloom = bloom;
            return this;
        }

        public BlockBuilder Genesis => WithNumber(0).WithParentHash(Keccak.Zero).WithMixHash(Keccak.Zero);

        protected override void BeforeReturn()
        {
            base.BeforeReturn();
            TestObjectInternal.Header.Hash = BlockHeader.CalculateHash(TestObjectInternal.Header);
        }
    }
}