#nullable enable
using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.EthRpcModuleTests.GasPriceTest;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class GasPriceOracleTests
    {
        private class TestableGasPriceOracle : GasPriceOracle
        {
            public TestableGasPriceOracle(
                bool eip1559Enabled = false, 
                UInt256? ignoreUnder = null, 
                int? blockLimit = null, 
                UInt256? baseFee = null, 
                ITxInsertionManager? txInsertionManager = null,
                IHeadBlockChangeManager? headBlockChangeManager = null) : 
                base(
                    eip1559Enabled,
                    ignoreUnder,
                    blockLimit,
                    baseFee,
                    txInsertionManager,
                    headBlockChangeManager)
                {
                }
            
            public override UInt256? GetLastGasPrice()
            {
                return 10;
            }
        }
        
        [Test]
        public void GasPriceEstimate_ChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            IHeadBlockChangeManager headBlockChangeManager = Substitute.For<IHeadBlockChangeManager>();
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(headBlockChangeManager: headBlockChangeManager);
            Dictionary<long, Block> testDictionary = Substitute.For<Dictionary<long, Block>>();
            Block testBlock = Build.A.Block.WithNumber(1).TestObject;
            headBlockChangeManager.ShouldReturnSameGasPrice(
                    Arg.Any<Block?>(),
                    Arg.Any<Block?>(),
                    Arg.Any<UInt256?>()).Returns(true);
            
            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GasPriceEstimate(testBlock, testDictionary);
            
            resultWrapper.Data.Should().Be((UInt256?) 10);
        }

        [Test]
        public void GasPriceEstimate_IfPreviousGasPriceExists_DefaultGasPriceIsSetToPreviousGasPrice()
        {
            IHeadBlockChangeManager headBlockChangeManager = Substitute.For<IHeadBlockChangeManager>();
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(headBlockChangeManager: headBlockChangeManager);
            Dictionary<long, Block> testDictionary = Substitute.For<Dictionary<long, Block>>();
            Block testBlock = Build.A.Block.WithNumber(1).TestObject;
            headBlockChangeManager.ShouldReturnSameGasPrice(
                    Arg.Any<Block?>(),
                    Arg.Any<Block?>(),
                    Arg.Any<UInt256?>()).Returns(true);
            
            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GasPriceEstimate(testBlock, testDictionary);
            
            resultWrapper.Data.Should().Be((UInt256?) 10);
        }

        [Test]
        public void GasPriceEstimate_IfHeadBlockWasNotTheSame_AddValidTxAndReturnCountCalledNumberOfBlocksToGoBack()
        {
            
        }

        [Test]
        public void GasPriceEstimate_BlockcountEqualToBlocksToCheck_SixtiethPercentileOfMaxIndexReturned()
        {
            
        }

        [Test]
        public void GasPriceEstimate_IfCalculatedGasPriceGreaterThanMax_MaxGasPriceReturned()
        {
            
        }

        private TestableGasPriceOracle GetTestableGasPriceOracle(
            bool eip1559Enabled = false, 
            UInt256? ignoreUnder = null, 
            int? blockLimit = null, 
            UInt256? baseFee = null, 
            ITxInsertionManager txInsertionManager = null,
            IHeadBlockChangeManager headBlockChangeManager = null)
        {
            return new TestableGasPriceOracle(
                eip1559Enabled,
                ignoreUnder,
                blockLimit,
                baseFee,
                txInsertionManager ?? Substitute.For<ITxInsertionManager>(),
                headBlockChangeManager ?? Substitute.For<IHeadBlockChangeManager>()
                );
        }
    }
}
