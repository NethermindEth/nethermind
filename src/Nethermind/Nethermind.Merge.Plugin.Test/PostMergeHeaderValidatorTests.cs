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
using System.Numerics;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
//I think I need this nextLine because I though I would need to use the RLP encoding, now I am not as sure
//using Nethermind.Serialization.Rlp;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
//I think I need this?
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    [TestFixture]
    public class PostMergeHeaderValidatorTests
    {
        private IPoSSwitcher _poSSwitcher;
        private IHeaderValidator _validator;
        private ISealValidator _ethash;
        private ISealEngine _mergeSealEngine;
        private TestLogger _testLogger;
        private Block _parentBlock;
        private Block _block;
        private IBlockTree _blockTree;
        private ISpecProvider _specProvider;
        private IMergeConfig _mergeConfig;

        [SetUp]
        public void Setup()
        {
            _testLogger = new TestLogger();
            MemDb blockInfoDb = new();
            //I am not sure, but I think I need to change the specProvider instances in the next two lines
            _blockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), FrontierSpecProvider.Instance, Substitute.For<IBloomStorage>(), LimboLogs.Instance);
            _specProvider = new SingleReleaseSpecProvider(Byzantium.Instance, 3);
            //Do I need to set up the whole MergeConfig? I assume so.
            _mergeConfig = new MergeConfig();
            //not sure if I can do this in the line above
            _mergeConfig.Enabled = true;
            //TODO: MetaDataDB, I also need an ILogManager
            _poSSwitcher = new PoSSwitcher(_mergeConfig, _plcHdr, _blockTree, _specProvider, _testLogger);
            
            //I think I need to create an AssemblyInfo.cs to the Nethermind
            EthashDifficultyCalculator calculator = new(new SingleReleaseSpecProvider(Frontier.Instance, ChainId.Mainnet));
            _ethash = new EthashSealValidator(LimboLogs.Instance, calculator, new CryptoRandom(), new Ethash(LimboLogs.Instance));
            
           //Not sure if I should be using Address.SysyemUser
           //Also expects _ethash to be a "_preMergeSealValidator" that is type ISealEngine,
           //but EthashSealValidator inherits from IsealValidator which ISealEngine Inherits from
            _mergeSealEngine = new MergeSealEngine(_ethash, _poSSwitcher, Address.SystemUser, _testLogger);
            _validator = new PostMergeHeaderValidator(_poSSwitcher, _blockTree, _mergeSealEngine, _specProvider, new OneLoggerLogManager(_testLogger));
            //Not sure if it needs WithPostMergeHeader tag?
            _parentBlock = Build.A.Block.WithDifficulty(0).TestObject;
            //but Do I need to get a real random number for the MixHash in this case? does it also need WithPostMergeHeader?
            _block = Build.A.Block.WithParent(_parentBlock)
                .WithDifficulty(0)
                .WithMixHash(new Keccak("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
                .WithNonce(0).TestObject;

            _blockTree.SuggestBlock(_parentBlock);
            _blockTree.SuggestBlock(_block);
        }
        //still good, unless I need to change the SealEngineType
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
        //still good, unless I need to change the SealEngineType 
        [Test]
        public void When_gas_limit_too_high()
        {
            _block.Header.GasLimit = _parentBlock.Header.GasLimit + (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024);
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //still good, unless I need to change the SealEngineType 
        [Test]
        public void When_gas_limit_just_correct_high()
        {
            _block.Header.GasLimit = _parentBlock.Header.GasLimit + (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024) - 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.True(result);
        }
        //still good, unless I need to change the SealEngineType 
        [Test]
        public void When_gas_limit_just_correct_low()
        {
            _block.Header.GasLimit = _parentBlock.Header.GasLimit - (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024) + 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.True(result);
        }
        //still good, unless I need to change the SealEngineType 
        [Test]
        public void When_gas_limit_is_just_too_low()
        {
            _block.Header.GasLimit = _parentBlock.Header.GasLimit - (long)BigInteger.Divide(_parentBlock.Header.GasLimit, 1024);
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //still good, unless I need to change the SealEngineType 
        [Test]
        public void When_gas_used_above_gas_limit()
        {
            _block.Header.GasUsed = _parentBlock.Header.GasLimit + 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //still good, unless I need to change the SealEngineType, I am not as confident about this one.
        [Test]
        public void When_no_parent_invalid()
        {
            _block.Header.ParentHash = Keccak.Zero;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            _block.Header.MaybeParent = null;
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //still good, unless I need to change the SealEngineType
        [Test]
        public void When_timestamp_same_as_parent()
        {
            // this test is failing during the Merge interop workshop but should be fine outside of it
            
            _block.Header.Timestamp = _parentBlock.Header.Timestamp;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //still good, unless I need to change the SealEngineType
        [Test]
        public void When_extra_data_too_long()
        {
            _block.Header.ExtraData = new byte[33];
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //still good, unless I need to change the SealEngineType
        [Test]
        public void When_incorrect_difficulty_then_invalid()
        {
            _block.Header.Difficulty = 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //still good, unless I need to change the SealEngineType
        [Test]
        public void When_incorrect_number_then_invalid()
        {
            _block.Header.Number += 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //Not good, I need to change the _validator to PostMergeHeaderValidator and that is going to need some extra vars
        //Already changed difficulty to 0, I think the mixHash is fine because that could be valid
        [TestCase(10000000, 4, 20000000, true)]
        [TestCase(10000000, 4, 20019530, true)]
        [TestCase(10000000, 4, 20019531, false)]
        [TestCase(10000000, 4, 19980470, true)]
        [TestCase(10000000, 4, 19980469, false)]
        [TestCase(20000000, 5, 20000000, true)]
        [TestCase(20000000, 5, 20019530, true)]
        [TestCase(20000000, 5, 20019531, false)]
        [TestCase(20000000, 5, 19980470, true)]
        [TestCase(20000000, 5, 19980469, false)]
        [TestCase(40000000, 5, 40039061, true)]
        [TestCase(40000000, 5, 40039062, false)]
        [TestCase(40000000, 5, 39960939, true)]
        [TestCase(40000000, 5, 39960938, false)]
        public void When_gaslimit_is_on_london_fork(long parentGasLimit, long blockNumber, long gasLimit, bool expectedResult)
        {
            OverridableReleaseSpec spec = new(London.Instance)
            {
                Eip1559TransitionBlock = 5
            };
            TestSpecProvider specProvider = new(spec);
            _validator = new HeaderValidator(_blockTree, _ethash, specProvider, new OneLoggerLogManager(_testLogger));
            _parentBlock = Build.A.Block.WithDifficulty(0)
                            .WithGasLimit(parentGasLimit)
                            .WithNumber(blockNumber)
                            .TestObject;
            _block = Build.A.Block.WithParent(_parentBlock)
                .WithDifficulty(0)
                .WithMixHash(new Keccak("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
                .WithGasLimit(gasLimit)
                .WithNumber(_parentBlock.Number + 1)
                .WithBaseFeePerGas(BaseFeeCalculator.Calculate(_parentBlock.Header, specProvider.GetSpec(_parentBlock.Number + 1)))
                .WithNonce(0).TestObject;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header, _parentBlock.Header);
            Assert.AreEqual(expectedResult, result);
        }
        //Not good, I need to change the _validator to PostMergeHeaderValidator
        //already fixed difficulty
        [Test]
        public void When_gas_limit_is_long_max_value()
        {
            _validator = new HeaderValidator(_blockTree, _ethash, _specProvider, new OneLoggerLogManager(_testLogger));
            _parentBlock = Build.A.Block.WithDifficulty(0)
                .WithGasLimit(long.MaxValue)
                .WithNumber(5)
                .TestObject;
            _block = Build.A.Block.WithParent(_parentBlock)
                .WithDifficulty(0)
                .WithMixHash(new Keccak("0xd7db5fdd332d3a65d6ac9c4c530929369905734d3ef7a91e373e81d0f010b8e8"))
                .WithGasLimit(long.MaxValue)
                .WithNumber(_parentBlock.Number + 1)
                .WithNonce(0).TestObject;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header, _parentBlock.Header);
            
            Assert.True(result);
        }
        //I think this is good
        [Test]
        public void When_incorrect_nonce_then_invalid()
        {
            _block.Header.Nonce = 1;
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result); 
        }
        //I think it is done
        [Test]
        public void When_incorrect_uncles_hash_then_invalid()
        {
            _block.Header.UnclesHash = new Keccak("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d4ffff");
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.False(result);
        }
        //I think it is done, but I am not sure if it is necessary
        [Test]
        public void When_correct_uncles_hash()
        {
            _block.Header.UnclesHash = new Keccak("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347");
            _block.Header.SealEngineType = SealEngineType.None;
            _block.Header.Hash = _block.CalculateHash();
            
            bool result = _validator.Validate(_block.Header);
            Assert.True(result);
        }
    }
}
