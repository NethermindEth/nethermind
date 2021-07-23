#nullable enable
using System;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    partial class EthRpcModuleTests
    {
        [TestCase(5, 3,3, 6, 1)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5) * 5)/1 / 8, 1) = 1 | Next Base Fee = 5 + 1 = 6 
        [TestCase(11, 3,3, 12, 1)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5)/1) * 11) / 8, 1) = 2 | Next Base Fee = 11 + 1 = 12 
        [TestCase(20, 100, 95, 22, 0.95)] //Target gas used: 100/2 = 50 | Actual Gas used = 95 | Base Fee Delta = Max((((95-50)/50) * 20) / 8, 1) = 2 | Next Base Fee = 20 + 1 = 22
        [TestCase(20, 100, 40, 20, 0.4)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 20) / 8 = 0 | Next Base Fee = 20 - 0 = 20 
        [TestCase(50, 100, 40, 49, 0.4)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 50) / 8 = 1 | Next Base Fee = 50 - 1 = 49
        public void ProcessBlock_IfLondonEnabled_NextBaseFeeAndBlockFeeInfoCalculatedCorrectly(long baseFee, long gasLimit, long gasUsed, long expectedNextBaseFee, double expectedGasUsedRatio)
        {
            ILogger logger = Substitute.For<ILogger>();
            TestableProcessBlockManager testableProcessBlockManager = new(logger);
            BlockHeader testBlockHeader = Build.A.BlockHeader.WithBaseFee((UInt256) baseFee).WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;
            BlockFeeInfo blockFeeInfo = new() {BlockHeader = testBlockHeader};
            BlockFeeInfo expectedBlockFeeInfo = new()
            {
                BlockHeader = testBlockHeader, BaseFee = (UInt256) baseFee, NextBaseFee = (UInt256) expectedNextBaseFee, GasUsedRatio = (float) expectedGasUsedRatio
            };
            
            testableProcessBlockManager.ProcessBlock(ref blockFeeInfo, null);

            blockFeeInfo.Should().BeEquivalentTo(expectedBlockFeeInfo);
        }

        [TestCase(3,10,5, 0, 0.5)]
        [TestCase(5, 24, 6, 0, 0.25)]
        public void ProcessBlock_IfLondonNotEnabled_NextBaseFeeIs0AndBlockFeeInfoCalculatedCorrectly(long baseFee, long gasLimit, long gasUsed, long expectedNextBaseFee, double expectedGasUsedRatio)
        {
            ILogger logger = Substitute.For<ILogger>();
            TestableProcessBlockManager testableProcessBlockManager = new(logger, londonEnabled: false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithBaseFee((UInt256) baseFee).WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;
            BlockFeeInfo blockFeeInfo = new() {BlockHeader = blockHeader};
            BlockFeeInfo expectedBlockFeeInfo = new()
            {
                BlockHeader = blockHeader, BaseFee = (UInt256) baseFee, NextBaseFee = (UInt256) expectedNextBaseFee, GasUsedRatio = (float) expectedGasUsedRatio
            };
            
            testableProcessBlockManager.ProcessBlock(ref blockFeeInfo, null);
            
            blockFeeInfo.Should().BeEquivalentTo(expectedBlockFeeInfo);
        }
        
        [TestCase(null, false)]
        [TestCase(new double[]{}, false)]

        public void ProcessBlock_IfRewardPercentilesIsNullOrEmpty_EarlyReturn(double[]? rewardPercentiles, bool expected)
        {
            ILogger logger = Substitute.For<ILogger>();
            TestableProcessBlockManager testableProcessBlockManager = new(logger, overrideInitializeBlockFeeInfo: true, 
                overrideGetArrayOfRewards: true);
            BlockFeeInfo blockFeeInfo = new() {Block = Build.A.Block.TestObject};

            testableProcessBlockManager.ArgumentErrorsExistResult.Should().BeNull();
            testableProcessBlockManager.ProcessBlock(ref blockFeeInfo, rewardPercentiles);

            testableProcessBlockManager.ArgumentErrorsExistResult.Should().BeTrue();
        }

        class TestableProcessBlockManager : ProcessBlockManager
        {
            private readonly bool _overrideInitializeBlockFeeInfo;
            private readonly bool _overrideGetArrayOfRewards;
            private readonly bool _londonEnabled;
            internal bool ArrayOfRewardsCalled { get; set; }
            public bool? ArgumentErrorsExistResult { get; set; }
            
            public TestableProcessBlockManager(
                ILogger logger,
                bool overrideInitializeBlockFeeInfo = false,
                bool overrideGetArrayOfRewards = false,
                bool londonEnabled = true) : base(logger)
            {
                _overrideInitializeBlockFeeInfo = overrideInitializeBlockFeeInfo;
                _overrideGetArrayOfRewards = overrideGetArrayOfRewards;
                _londonEnabled = londonEnabled;
                ArrayOfRewardsCalled = false;
                ArgumentErrorsExistResult = null;
            }

            protected override bool IsLondonEnabled(BlockFeeInfo blockFeeInfo)
            {
                return _londonEnabled;
            }

            protected override void InitializeBlockFeeInfo(ref BlockFeeInfo blockFeeInfo, bool isLondonEnabled)
            {
                if (_overrideInitializeBlockFeeInfo == false)
                {
                    base.InitializeBlockFeeInfo(ref blockFeeInfo, isLondonEnabled);
                }
            }

            protected override bool ArgumentErrorsExist(BlockFeeInfo blockFeeInfo, double[]? rewardPercentiles)
            {
                ArgumentErrorsExistResult = base.ArgumentErrorsExist(blockFeeInfo, rewardPercentiles);
                return (bool) ArgumentErrorsExistResult!;
            }

            protected override UInt256[]? ArrayOfRewards(BlockFeeInfo blockFeeInfo, double[] rewardPercentiles)
            {
                if (_overrideGetArrayOfRewards == false)
                {
                    return base.ArrayOfRewards(blockFeeInfo, rewardPercentiles);
                }
                else
                {
                    ArrayOfRewardsCalled = true;
                    return Array.Empty<UInt256>();
                }
            }
        }
    }
}
