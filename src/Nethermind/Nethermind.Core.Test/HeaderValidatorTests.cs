﻿/*
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
using Nethermind.Blockchain.TxPools;
using Nethermind.Blockchain.Validators;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Mining.Difficulty;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class HeaderValidatorTests
    {
        private IHeaderValidator _validator;
        private ISealValidator _ethash;
        private TestLogger _testLogger;
        private Block _parentBlock;
        private Block _block;

        [SetUp]
        public void Setup()
        {
            DifficultyCalculator calculator = new DifficultyCalculator(new SingleReleaseSpecProvider(Frontier.Instance, ChainId.MainNet));
            _ethash = new EthashSealValidator(NullLogManager.Instance, calculator, new Ethash(NullLogManager.Instance));
            _testLogger = new TestLogger();
            var blockInfoDb = new MemDb();
            BlockTree blockStore = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), FrontierSpecProvider.Instance, Substitute.For<ITxPool>(), NullLogManager.Instance);
            
            _validator = new HeaderValidator(blockStore, _ethash, new SingleReleaseSpecProvider(Byzantium.Instance, 3), new OneLoggerLogManager(_testLogger));
            _parentBlock = Build.A.Block.WithDifficulty(1).TestObject;
            _block = Build.A.Block.WithParent(_parentBlock)
                .WithDifficulty(131072)
                .WithMixHash(new Keccak("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
                .WithNonce(0).TestObject;

            blockStore.SuggestBlock(_parentBlock);
            blockStore.SuggestBlock(_block);
        }
        
        [Test]
        public void Valid_when_valid()
        {
            _block.Header.SealEngineType = SealEngineType.None;
            bool result = _validator.Validate(_block.Header);
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
            _block.Header.GasLimit = _parentBlock.Header.GasLimit + (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024) + 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        
        [Test]
        public void When_gas_limit_just_correct_high()
        {
            _block.Header.GasLimit = _parentBlock.Header.GasLimit + (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024);
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.True(result);
        }
        
        [Test]
        public void When_gas_limit_too_low()
        {
            _block.Header.GasLimit = _parentBlock.Header.GasLimit - (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024) - 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        
        [Test]
        public void When_gas_limit_just_correct_low()
        {
            _block.Header.GasLimit = _parentBlock.Header.GasLimit - (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024);
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.True(result);
        }
        
        [Test]
        public void When_gas_used_above_gas_limit()
        {
            _block.Header.GasUsed = _parentBlock.Header.GasLimit + 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        
        [Test]
        public void When_no_parent_invalid()
        {
            _block.Header.ParentHash = Keccak.Zero;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        
        [Test]
        public void When_timestamp_same_as_parent()
        {
            _block.Header.Timestamp = _parentBlock.Header.Timestamp;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        
        [Test]
        public void When_extra_data_too_long()
        {
            _block.Header.ExtraData = new byte[33];
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        
        [Test]
        public void When_incorrect_difficulty_then_invalid()
        {
            _block.Header.Difficulty = 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        
        [Test]
        public void When_incorrect_number_then_invalid()
        {
            _block.Header.Number += 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Hash = BlockHeader.CalculateHash(_block);
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
    }
}