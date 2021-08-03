#nullable enable
using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    partial class EthRpcModuleTests
    {
        [TestCase(null, false)]
        [TestCase(new double[]{}, false)]

        public void ProcessBlock_IfRewardPercentilesIsNullOrEmpty_EarlyReturn(double[]? rewardPercentiles, bool expected)
        {
            ILogger logger = Substitute.For<ILogger>();
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            TestableProcessBlockManager testableProcessBlockManager = new(logger, blockchainBridge, overrideInitializeBlockFeeInfo: true, 
                overrideGetArrayOfRewards: true);
            BlockFeeInfo blockFeeInfo = new() {Block = Build.A.Block.TestObject};

            testableProcessBlockManager.ArgumentErrorsExistResult.Should().BeNull();
            testableProcessBlockManager.ProcessBlock(ref blockFeeInfo, rewardPercentiles);

            testableProcessBlockManager.ArgumentErrorsExistResult.Should().BeTrue();
        }

        [Test]
        public void ProcessBlock_NoTxsInBlock_ReturnsArrayOfZerosAsBigAsRewardPercentiles()
        {
            ILogger logger = Substitute.For<ILogger>();
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            TestableProcessBlockManager testableProcessBlockManager = new(logger, blockchainBridge);
            BlockFeeInfo blockFeeInfo = new()
            {
                Block = Build.A.Block.WithTransactions(Array.Empty<Transaction>()).TestObject,
                BlockHeader = Build.A.BlockHeader.TestObject
            };

            UInt256[]? result = testableProcessBlockManager.ProcessBlock(ref blockFeeInfo, new double[] {1, 2, 3});

            result.Should().BeEquivalentTo(new UInt256[] {0, 0, 0});
        }
        
        [Test]
        public void ProcessBlock_BlockFeeInfoBlockParameterEmptyAfterInitialization_ReturnsNullAndLogsError()
        {
            ILogger logger = Substitute.For<ILogger>();
            logger.IsError.Returns(true);
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            TestableProcessBlockManager testableProcessBlockManager = new(logger, blockchainBridge, overrideInitializeBlockFeeInfo: true);
            BlockFeeInfo blockFeeInfo = new()
            {
                BlockHeader = Build.A.BlockHeader.TestObject
            };

            UInt256[]? result = testableProcessBlockManager.ProcessBlock(ref blockFeeInfo, new double[]{1,2,3});

            result.Should().BeNull();
            logger.Received().Error(Arg.Is("Block missing when reward percentiles were requested."));
        }
        
        class TestableProcessBlockManager : ProcessBlockManager
        {
            private readonly bool _overrideInitializeBlockFeeInfo;
            private readonly bool _overrideGetArrayOfRewards;
            private readonly bool _londonEnabled;
            private bool ArrayOfRewardsCalled { get; set; }
            public bool? ArgumentErrorsExistResult { get; private set; }
            
            public TestableProcessBlockManager(
                ILogger logger,
                IBlockchainBridge blockchainBridge,
                bool overrideInitializeBlockFeeInfo = false,
                bool overrideGetArrayOfRewards = false,
                bool londonEnabled = true) : 
                base(logger,
                    blockchainBridge)
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
                else
                {
                    blockFeeInfo.Block = null;
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
