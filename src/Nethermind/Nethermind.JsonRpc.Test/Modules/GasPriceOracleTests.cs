#nullable enable
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    class GasPriceOracleTests
    {
        [Test]
        public void GasPriceEstimate_NoChangeInHeadBlock_ReturnsPreviousGasPrice()
        {
            IHeadBlockChangeManager headBlockChangeManager = Substitute.For<IHeadBlockChangeManager>();
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(headBlockChangeManager: headBlockChangeManager, lastGasPrice: 7);
            Dictionary<long, Block> testDictionary = Substitute.For<Dictionary<long, Block>>();
            Block testBlock = Build.A.Block.Genesis.TestObject;
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
            
            testableGasPriceOracle.FallbackGasPrice.Should().BeEquivalentTo((UInt256?) EthGasPriceConstants.DefaultGasPrice);
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

        [TestCase(new[]{1,3,5,7,8,9}, 7)] //Last index: 6 - 1 = 5, 60th percentile: 5 * 3/5 = 3, Value: 7
        [TestCase(new[]{0,0,7,9,10,27,83,101}, 10)] //Last index: 8 - 1 = 7, 60th percentile: 7 * 3/5 rounds to 4, Value: 10
        public void GasPriceEstimate_BlockcountEqualToBlocksToCheck_SixtiethPercentileOfMaxIndexReturned(int[] gasPrice, int expected)
        {
            List<UInt256> listOfGasPrices = gasPrice.Select(n => (UInt256) n).ToList();
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(sortedTxList: listOfGasPrices);
            IDictionary<long, Block> testDictionary = Substitute.For<IDictionary<long, Block>>();
            Block testBlock = Build.A.Block.Genesis.TestObject;

            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GasPriceEstimate(testBlock, testDictionary);
            
            resultWrapper.Data.Should().BeEquivalentTo((UInt256?) expected);
        }

        [Test]
        public void GasPriceEstimate_IfCalculatedGasPriceGreaterThanMax_MaxGasPriceReturned()
        {
            
            List<UInt256> listOfGasPrices = new List<UInt256>
            {
                501
            }; 
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(sortedTxList: listOfGasPrices);
            IDictionary<long, Block> testDictionary = Substitute.For<IDictionary<long, Block>>();
            Block testBlock = Build.A.Block.Genesis.TestObject;

            ResultWrapper<UInt256?> resultWrapper = testableGasPriceOracle.GasPriceEstimate(testBlock, testDictionary);
            
            resultWrapper.Result.Should().Be(Result.Success);
            resultWrapper.Data.Should().BeEquivalentTo((UInt256?) EthGasPriceConstants._maxGasPrice);
        }
        
        [Test]
        public void GasPriceEstimate_IfEightBlocksWithTwoTransactions_CheckEightBlocks()
        {
            ITxInsertionManager txInsertionManager = Substitute.For<ITxInsertionManager>();
            txInsertionManager.AddValidTxFromBlockAndReturnCount(Arg.Any<Block>()).Returns(2);
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(txInsertionManager: txInsertionManager, blockLimit: 8);
            Block headBlock = Build.A.Block.WithNumber(8).TestObject;
            IDictionary<long, Block> testDictionary = Substitute.For<IDictionary<long, Block>>();
            
            testableGasPriceOracle.GasPriceEstimate(headBlock, testDictionary);
            
            txInsertionManager.Received(8).AddValidTxFromBlockAndReturnCount(Arg.Any<Block>());
        }

        [Test]
        public void GasPriceEstimate_IfLastFiveBlocksWithThreeTxAndFirstFourWithOne_CheckSixBlocks()
        {
            //Any blocks with zero/one tx doesn't count towards the blockLimit unless the number of 
            ITxInsertionManager txInsertionManager = Substitute.For<ITxInsertionManager>();
            TestableGasPriceOracle testableGasPriceOracle = GetTestableGasPriceOracle(txInsertionManager: txInsertionManager, blockLimit: 8);
            SetUpTxInsertionManagerForSpecificReturns(txInsertionManager, testableGasPriceOracle);
            Block headBlock = Build.A.Block.WithNumber(8).TestObject;
            IDictionary<long, Block> testNineEmptyBlocksDictionary = GetNumberToBlockMapForNineEmptyBlocks();
            
            testableGasPriceOracle.GasPriceEstimate(headBlock, testNineEmptyBlocksDictionary);
            
            txInsertionManager.Received(8).AddValidTxFromBlockAndReturnCount(Arg.Any<Block>());
        }

        private static void SetUpTxInsertionManagerForSpecificReturns(ITxInsertionManager txInsertionManager,
            TestableGasPriceOracle testableGasPriceOracle)
        {
            txInsertionManager.AddValidTxFromBlockAndReturnCount(Arg.Is<Block>(b => b.Number >= 4)).Returns(3);
            txInsertionManager
                .When(t => t.AddValidTxFromBlockAndReturnCount(Arg.Is<Block>(b => b.Number >= 4)))
                .Do(t => testableGasPriceOracle.AddToSortedTxList(1,2,3));
            
            txInsertionManager.AddValidTxFromBlockAndReturnCount(Arg.Is<Block>(b => b.Number < 4)).Returns(1);
            txInsertionManager
                .When(t => t.AddValidTxFromBlockAndReturnCount(Arg.Is<Block>(b => b.Number < 4)))
                .Do(t => testableGasPriceOracle.AddToSortedTxList(4));
        }

        private static Dictionary<long, Block> GetNumberToBlockMapForNineEmptyBlocks()
        {
            Block[] blocks = {
                Build.A.Block.Genesis.TestObject,
                Build.A.Block.WithNumber(1).TestObject,
                Build.A.Block.WithNumber(2).TestObject,
                Build.A.Block.WithNumber(3).TestObject,
                Build.A.Block.WithNumber(4).TestObject,
                Build.A.Block.WithNumber(5).TestObject,
                Build.A.Block.WithNumber(6).TestObject,
                Build.A.Block.WithNumber(7).TestObject,
                Build.A.Block.WithNumber(8).TestObject,
            };
            return new Dictionary<long, Block>()
            {
                {0, blocks[0]},
                {1, blocks[1]},
                {2, blocks[2]},
                {3, blocks[3]},
                {4, blocks[4]},
                {5, blocks[5]},
                {6, blocks[6]},
                {7, blocks[7]},
                {8, blocks[8]}
            };
        }

        private class TestableGasPriceOracle : GasPriceOracle
        {
            private readonly UInt256? _lastGasPrice;
            private readonly List<UInt256>? _sortedTxList;
            public TestableGasPriceOracle(
                ISpecProvider? specProvider = null,
                UInt256? ignoreUnder = null, 
                int? blockLimit = null, 
                UInt256? baseFee = null, 
                ITxInsertionManager? txInsertionManager = null,
                IHeadBlockChangeManager? headBlockChangeManager = null,
                UInt256? lastGasPrice = null,
                List<UInt256>? sortedTxList = null) : 
                base(
                    specProvider ?? Substitute.For<ISpecProvider>(),
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
                return _lastGasPrice ?? base.LastGasPrice;
            }

            protected override List<UInt256> GetSortedTxGasPriceList(Block? headBlock, IDictionary<long, Block> blockNumToBlockMap)
            {
                return _sortedTxList ?? base.GetSortedTxGasPriceList(headBlock, blockNumToBlockMap);
            }
            
            public void AddToSortedTxList(params UInt256[] numbers)
            {
                TxGasPriceList.AddRange(numbers.ToList());
            }
        }
        private TestableGasPriceOracle GetTestableGasPriceOracle(
            ISpecProvider? specProvider = null, 
            UInt256? ignoreUnder = null, 
            int? blockLimit = null, 
            UInt256? baseFee = null, 
            ITxInsertionManager? txInsertionManager = null,
            IHeadBlockChangeManager? headBlockChangeManager = null,
            UInt256? lastGasPrice = null,
            List<UInt256>? sortedTxList = null)
        {
            return new TestableGasPriceOracle(
                specProvider ?? Substitute.For<ISpecProvider>(),
                ignoreUnder,
                blockLimit,
                baseFee,
                txInsertionManager ?? Substitute.For<ITxInsertionManager>(),
                headBlockChangeManager ?? Substitute.For<IHeadBlockChangeManager>(),
                lastGasPrice,
                sortedTxList);
        }
        
    }
}
