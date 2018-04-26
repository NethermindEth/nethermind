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

using System;
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Mining;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockHeaderValidatorTests
    {
        private IHeaderValidator _validator;
        private ISealEngine _ethash;
        private TestLogger _testLogger;
        private Block _parentBlock;
        private Block _block;
        private BlockHeader _parentHeader;
        private BlockHeader _blockHeader;

        [SetUp]
        public void Setup()
        {
            _ethash = new EthashSealEngine(new Ethash());
            _testLogger = new TestLogger();
            BlockTree blockStore = new BlockTree(FrontierSpecProvider.Instance, NullLogger.Instance);
            DifficultyCalculator calculator = new DifficultyCalculator(new SingleReleaseSpecProvider(Frontier.Instance, ChainId.MainNet));   
            
            _validator = new HeaderValidator(calculator, blockStore, _ethash, new SingleReleaseSpecProvider(Byzantium.Instance, 3), _testLogger);
            _parentHeader = new BlockHeader(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131072, 0, 21000, 0, new byte[]{});
            _parentHeader.Hash = BlockHeader.CalculateHash(_parentHeader);
            _parentBlock = new Block(_parentHeader);
            
            _blockHeader = new BlockHeader(_parentHeader.Hash, Keccak.OfAnEmptySequenceRlp, Address.Zero, 131136, 1, 21000, 1, new byte[]{});
            _blockHeader.Nonce = 7217048144105167954;
            _blockHeader.MixHash = new Keccak("0x37d9fb46a55e9dbbffc428f3a1be6f191b3f8eaf52f2b6f53c4b9bae62937105");
            _blockHeader.Hash = BlockHeader.CalculateHash(_blockHeader);
            _block = new Block(_blockHeader);
            
            blockStore.SuggestBlock(_parentBlock);
            blockStore.SuggestBlock(_block);
        }
        
        [Test]
        public void Valid_when_valid()
        {
            bool result = _validator.Validate(_blockHeader);
            if (!result)
            {
                foreach (string error in _testLogger.LogList)
                {
                    Console.WriteLine(error);
                }
            }
            
            Assert.True(result);
        }

        [Test]
        public void When_gas_limit_too_high()
        {
            _blockHeader.GasLimit = _parentHeader.GasLimit + (long)BigInteger.Divide(_parentHeader.GasLimit, 1024);
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_gas_limit_too_low()
        {
            _blockHeader.GasLimit = _parentHeader.GasLimit - (long)BigInteger.Divide(_parentHeader.GasLimit, 1024);
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_gas_used_above_gas_limit()
        {
            _blockHeader.GasUsed = _blockHeader.GasLimit + 1;
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_no_parent_invalid()
        {
            _blockHeader.ParentHash = Keccak.Zero;
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_timestamp_same_as_parent()
        {
            _blockHeader.Timestamp = _parentHeader.Timestamp;
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_extra_data_too_long()
        {
            _blockHeader.ExtraData = new byte[33];
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_incorrect_difficulty_then_invalid()
        {
            _parentHeader.Difficulty = _parentHeader.Difficulty - 1;
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_incorrect_number_then_invalid()
        {
            _parentHeader.Difficulty = 2;
            bool result = _validator.Validate(_blockHeader, false, true);
            Assert.False(result);
        }
        
        [Test]
        public void When_incorrect_nonce_then_invalid()
        {
            _blockHeader.Nonce = 1UL;
            _blockHeader.MixHash = null;
            bool result = _validator.Validate(_blockHeader);
            Assert.False(result);
        }
    }
}