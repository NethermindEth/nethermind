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
            private readonly UInt256? _lastGasPrice;
            private readonly List<UInt256>? _sortedTxList;
            public TestableGasPriceOracle(
                bool eip1559Enabled = false, 
                UInt256? ignoreUnder = null, 
                int? blockLimit = null, 
                UInt256? baseFee = null, 
                ITxInsertionManager? txInsertionManager = null,
                IHeadBlockChangeManager? headBlockChangeManager = null,
                UInt256? lastGasPrice = null,
                List<UInt256>? sortedTxList = null) : 
                base(
                    eip1559Enabled,
                    ignoreUnder,
                    blockLimit,
                    baseFee,
                    txInsertionManager,
                    headBlockChangeManager)
            {
                _lastGasPrice = lastGasPrice;
                _sortedTxList = sortedTxList;
            }


            protected override UInt256? GetLastGasPrice()
            {
                return _lastGasPrice;
            }

            protected override List<UInt256> GetSortedTxGasPriceList(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
            {
                return _sortedTxList ?? base.GetSortedTxGasPriceList(headBlock, blockNumToBlockMap);
            }
        }
        
        [Test]
        public void GasPriceEstimate_NoChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            IHeadBlockChangeManager headBlockChangeManager = Substitute.For<IHeadBlockChangeManager>();
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(headBlockChangeManager: headBlockChangeManager, lastGasPrice: 7);
            Dictionary<long, Block> testDictionary = Substitute.For<Dictionary<long, Block>>();
            Block testBlock = EthRpcModuleTests.GetNoTxTestBlock();
            headBlockChangeManager.ShouldReturnSameGasPrice(
                    Arg.Any<Block?>(),
                    Arg.Any<Block?>(),
                    Arg.Any<UInt256?>()).Returns(true);
            
            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GasPriceEstimate(testBlock, testDictionary);
            
            resultWrapper.Data.Should().Be((UInt256?) 7);
        }

        [Test]
        public void GasPriceEstimate_IfPreviousGasPriceDoesNotExist_FallbackGasPriceSetToDefaultGasPrice()
        {
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle();
            IDictionary<long, Block> testDictionary = Substitute.For<IDictionary<long, Block>>();
            Block testBlock = Build.A.Block.Genesis.TestObject;
            
            testableGasPriceOracle.GasPriceEstimate(testBlock, testDictionary);
            
            testableGasPriceOracle.FallbackGasPrice.Should().BeEquivalentTo((UInt256?) GasPriceConfig.DefaultGasPrice);
        }

        [TestCase(3)]
        [TestCase(10)]
        public void GasPriceEstimate_IfPreviousGasPriceExists_FallbackGasPriceIsSetToPreviousGasPrice(int lastGasPrice)
        {
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(lastGasPrice: (UInt256) lastGasPrice);
            IDictionary<long, Block> testDictionary = Substitute.For<IDictionary<long, Block>>();
            Block testBlock = Build.A.Block.Genesis.TestObject;
            
            testableGasPriceOracle.GasPriceEstimate(testBlock, testDictionary);
            
            testableGasPriceOracle.FallbackGasPrice.Should().BeEquivalentTo((UInt256?) lastGasPrice);
        }

        [Test]
        public void GasPriceEstimate_BlockcountEqualToBlocksToCheck_SixtiethPercentileOfMaxIndexReturned()
        {
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle();
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
            ITxInsertionManager? txInsertionManager = null,
            IHeadBlockChangeManager? headBlockChangeManager = null,
            UInt256? lastGasPrice = null)
        {
            return new TestableGasPriceOracle(
                eip1559Enabled,
                ignoreUnder,
                blockLimit,
                baseFee,
                txInsertionManager ?? Substitute.For<ITxInsertionManager>(),
                headBlockChangeManager ?? Substitute.For<IHeadBlockChangeManager>(),
                lastGasPrice);
        }
        
    }
}
