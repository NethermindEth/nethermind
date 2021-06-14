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

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders
{
    public class BlockHeaderBuilder : BuilderBase<BlockHeader>
    {
        public static UInt256 DefaultDifficulty = 1_000_000;

        protected override void BeforeReturn()
        {
            if (!_doNotCalculateHash)
            {
                TestObjectInternal.Hash = TestObjectInternal.CalculateHash();
            }

            base.BeforeReturn();
        }

        public BlockHeaderBuilder()
        {
            TestObjectInternal = new BlockHeader(
                Keccak.Compute("parent"),
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                DefaultDifficulty, 0,
                4_000_000,
                1_000_000,
                new byte[] {1, 2, 3});
            TestObjectInternal.Bloom = Bloom.Empty;
            TestObjectInternal.MixHash = Keccak.Compute("mix_hash");
            TestObjectInternal.Nonce = 1000;            
            TestObjectInternal.ReceiptsRoot = Keccak.EmptyTreeHash;
            TestObjectInternal.StateRoot = Keccak.EmptyTreeHash;
            TestObjectInternal.TxRoot = Keccak.EmptyTreeHash;
        }

        public BlockHeaderBuilder WithParent(BlockHeader parentHeader)
        {
            TestObjectInternal.ParentHash = parentHeader.Hash;
            TestObjectInternal.Number = parentHeader.Number + 1;
            TestObjectInternal.GasLimit = parentHeader.GasLimit;
            return this;
        }

        public BlockHeaderBuilder WithParentHash(Keccak parentHash)
        {
            TestObjectInternal.ParentHash = parentHash;
            return this;
        }

        public BlockHeaderBuilder WithHash(Keccak hash)
        {
            TestObjectInternal.Hash = hash;
            _doNotCalculateHash = true;
            return this;
        }

        private bool _doNotCalculateHash;

        public BlockHeaderBuilder WithOmmersHash(Keccak ommersHash)
        {
            TestObjectInternal.OmmersHash = ommersHash;
            return this;
        }

        public BlockHeaderBuilder WithBeneficiary(Address beneficiary)
        {
            TestObjectInternal.Beneficiary = beneficiary;
            return this;
        }

        public BlockHeaderBuilder WithAuthor(Address address)
        {
            TestObjectInternal.Author = address;
            return this;
        }

        public BlockHeaderBuilder WithBloom(Bloom bloom)
        {
            TestObjectInternal.Bloom = bloom;
            return this;
        }
        
        public BlockHeaderBuilder WithBaseFee(UInt256 baseFee)
        {
            TestObjectInternal.BaseFeePerGas = baseFee;
            return this;
        }

        public BlockHeaderBuilder WithStateRoot(Keccak stateRoot)
        {
            TestObjectInternal.StateRoot = stateRoot;
            return this;
        }

        public BlockHeaderBuilder WithTransactionsRoot(Keccak transactionsRoot)
        {
            TestObjectInternal.TxRoot = transactionsRoot;
            return this;
        }

        public BlockHeaderBuilder WithReceiptsRoot(Keccak receiptsRoot)
        {
            TestObjectInternal.ReceiptsRoot = receiptsRoot;
            return this;
        }

        public BlockHeaderBuilder WithDifficulty(UInt256 difficulty)
        {
            TestObjectInternal.Difficulty = difficulty;
            return this;
        }

        public BlockHeaderBuilder WithNumber(long blockNumber)
        {
            TestObjectInternal.Number = blockNumber;
            return this;
        }
        
        public BlockHeaderBuilder WithTotalDifficulty(long totalDifficulty)
        {
            TestObjectInternal.TotalDifficulty = (ulong)totalDifficulty;
            return this;
        }

        public BlockHeaderBuilder WithGasLimit(long gasLimit)
        {
            TestObjectInternal.GasLimit = gasLimit;
            return this;
        }

        public BlockHeaderBuilder WithGasUsed(long gasUsed)
        {
            TestObjectInternal.GasUsed = gasUsed;
            return this;
        }

        public BlockHeaderBuilder WithTimestamp(UInt256 timestamp)
        {
            TestObjectInternal.Timestamp = timestamp;
            return this;
        }

        public BlockHeaderBuilder WithExtraData(byte[] extraData)
        {
            TestObjectInternal.ExtraData = extraData;
            return this;
        }

        public BlockHeaderBuilder WithMixHash(Keccak mixHash)
        {
            TestObjectInternal.MixHash = mixHash;
            return this;
        }

        public BlockHeaderBuilder WithNonce(ulong nonce)
        {
            TestObjectInternal.Nonce = nonce;
            return this;
        }
        
        public BlockHeaderBuilder WithAura(long step, byte[] signature = null)
        {
            TestObjectInternal.AuRaStep = step;
            TestObjectInternal.AuRaSignature = signature;
            return this;
        }
    }
}
