// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.GasPriceOracleTests;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class FeeHistoryOracleTests
    {
        [Test]
        public void GetFeeHistory_BlocksToCheckLess1_ReturnsFailingWrapper()
        {
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
            var expected = ResultWrapper<FeeHistoryResults>.Fail("blockCount: Value 0 is less than 1", ErrorCodes.InvalidParams);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(0, BlockParameter.Latest);
            resultWrapper.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void GetFeeHistory_HashParameter_ReturnsFailingWrapper()
        {
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
            var expected = ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Is not correct block number", ErrorCodes.InvalidParams);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(0, new BlockParameter(TestItem.KeccakA));
            resultWrapper.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void GetFeeHistory_NewestBlockIsNull_ReturnsFailingWrapper()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(Arg.Any<long>()).Returns((Block?)null);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);
            var expected = ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available", ErrorCodes.ResourceUnavailable);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter((long)0));
            resultWrapper.Should().BeEquivalentTo(expected);
        }


        [TestCase(3, 5)]
        [TestCase(4, 10)]
        [TestCase(0, 1)]
        public void GetFeeHistory_IfPendingBlockDoesNotExistAndLastBlockNumberGreaterThanHeadNumber_ReturnsError(long pendingBlockNumber, long lastBlockNumber)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindPendingBlock().Returns(Build.A.Block.WithNumber(pendingBlockNumber).TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);
            var expected = ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Block is not available", ErrorCodes.ResourceUnavailable);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(lastBlockNumber));

            resultWrapper.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void GetFeeHistory_IfRewardPercentilesNotInAscendingOrder_ResultsInFailure()
        {
            int blockCount = 10;
            double[] rewardPercentiles = [0, 2, 3, 5, 1];
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, BlockParameter.Latest, rewardPercentiles);

            resultWrapper.Result.Error.Should().Be("rewardPercentiles: Value at index 4: 1 is less than or equal to the value at previous index 3: 5.");
            resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [Test]
        public void GetFeeHistory_IfTooManyRewardPercentiles_ResultsInFailure()
        {
            int blockCount = 10;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            double[] percentiles = Enumerable.Range(1, 999).Select(p => p / 10d).ToArray();
            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, BlockParameter.Latest, percentiles);

            resultWrapper.Result.Error.Should().Be("rewardPercentiles: 999 is over the query limit 100.");
            resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [TestCase(new double[] { -1, 1, 2 })]
        [TestCase(new[] { 1, 2.2, 101, 102 })]
        public void GetFeeHistory_IfRewardPercentilesContainInvalidNumber_ResultsInFailure(double[] rewardPercentiles)
        {
            int blockCount = 10;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, BlockParameter.Latest, rewardPercentiles);

            resultWrapper.Result.Error.Should().Be("rewardPercentiles: Some values are below 0 or greater than 100.");
            resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
        }

        [TestCase(3, 3, 5, 6)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5)/1.5 * 5) / 8, 1) = 1 | Next Base Fee = 5 + 1 = 6
        [TestCase(3, 3, 11, 13)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5)/1.5) * 11) / 8, 1) = 2 | Next Base Fee = 11 + 2 = 13
        [TestCase(100, 95, 20, 22)] //Target gas used: 100/2 = 50 | Actual Gas used = 95 | Base Fee Delta = Max((((95-50)/50) * 20) / 8, 1) = 2 | Next Base Fee = 20 + 2 = 22
        [TestCase(100, 40, 20, 20)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 20) / 8 = 0 | Next Base Fee = 20 - 0 = 20
        [TestCase(100, 40, 50, 49)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 50) / 8 = 1 | Next Base Fee = 50 - 1 = 49
        public void GetFeeHistory_IfLondonEnabled_NextBaseFeePerGasCalculatedCorrectly(long gasLimit, long gasUsed, long baseFee, long expectedNextBaseFee)
        {
            int blockCount = 1;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithBaseFee((UInt256)baseFee).WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;
            BlockParameter newestBlock = new((long)0);
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            blockTree.FindBlock(newestBlock).Returns(headBlock);
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(true);

            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, specProvider: specProvider);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, newestBlock);

            var resultNextBaseFee = resultWrapper.Data.BaseFeePerGas[1];
            var resultBaseFee = resultWrapper.Data.BaseFeePerGas[0];

            resultNextBaseFee.Should().Be((UInt256)expectedNextBaseFee);
            resultBaseFee.Should().Be((UInt256)baseFee);

        }

        [TestCase(3, 3, 1)]
        [TestCase(100, 95, 0.95)]
        [TestCase(12, 3, 0.25)]
        [TestCase(100, 40, 0.4)]
        [TestCase(3, 1, 0.3333333333333333)]
        public void GetFeeHistory_GasUsedRatioCalculatedCorrectly(long gasLimit, long gasUsed, double expectedGasUsedRatio)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            BlockParameter newestBlock = new((long)0);
            blockTree.Head.Returns(headBlock);
            blockTree.FindBlock(newestBlock).Returns(headBlock);
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(true);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, specProvider: specProvider);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlock);

            resultWrapper.Data.GasUsedRatio![0].Should().Be(expectedGasUsedRatio);
        }

        [TestCase(3, 3)]
        [TestCase(5, 5)]
        public void GetFeeHistory_IfLondonNotEnabled_NextBaseFeeIsParentBaseFee(long baseFee, long expectedNextBaseFee)
        {
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithBaseFee((UInt256)baseFee).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            BlockParameter newestBlock = new((long)0);
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(newestBlock).Returns(headBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, specProvider: specProvider);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlock);

            resultWrapper.Data.BaseFeePerGas![1].Should().Be((UInt256)expectedNextBaseFee);
            resultWrapper.Data.BaseFeePerGas![0].Should().Be((UInt256)baseFee);
        }

        [TestCase(null)]
        [TestCase(new double[] { })]
        public void GetFeeHistory_IfRewardPercentilesIsNullOrEmpty_RewardsIsNull(double[]? rewardPercentiles)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, BlockParameter.Latest, rewardPercentiles);

            resultWrapper.Data.Reward.Should().BeNull();
        }

        [TestCase(5, new ulong[] { 0, 0, 0, 0, 0 })]
        [TestCase(7, new ulong[] { 0, 0, 0, 0, 0, 0, 0 })]
        public void GetFeeHistory_NoTxsInBlock_ReturnsArrayOfZerosAsBigAsRewardPercentiles(int sizeOfRewardPercentiles, ulong[] expectedRewards)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block noTxBlock = Build.A.Block.TestObject;
            BlockParameter newestBlock = new((long)0);
            blockTree.FindBlock(newestBlock).Returns(noTxBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);
            double[] rewardPercentiles = Enumerable.Range(1, sizeOfRewardPercentiles).Select(static x => (double)x).ToArray();

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlock, rewardPercentiles);

            UInt256[] expectedRewardsUInt256 = expectedRewards.Select(static x => (UInt256)x).ToArray();
            resultWrapper.Data.Reward![0].Should().BeEquivalentTo(expectedRewardsUInt256);
        }


        [TestCase(5, 10, 6)]
        [TestCase(5, 3, 0)]
        public void GetFeeHistory_GivenValidInputs_FirstBlockNumberCalculatedCorrectly(int blockCount, long newestBlockNumber, long expectedOldestBlockNumber)
        {
            // BlockParameter lastBlockNumber = new(newestBlockNumber);
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            const BlockTreeLookupOptions options = BlockTreeLookupOptions.ExcludeTxHashes |
                                                   BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                   BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

            Block? parent = null;
            Block? latestBlock = null;
            long latestBlockNumber = 0;
            // build a full chain
            while (latestBlockNumber <= newestBlockNumber)
            {
                latestBlock = parent is not null ? Build.A.Block.WithNumber(latestBlockNumber).WithParent(parent).TestObject : Build.A.Block.Genesis.TestObject;
                blockTree.FindBlock(latestBlock.Hash!, options, latestBlock.Number).Returns(latestBlock);
                blockTree.FindParent(latestBlock, BlockTreeLookupOptions.RequireCanonical).Returns(parent);
                parent = latestBlock;
                latestBlockNumber++;
            }

            latestBlockNumber--;

            BlockParameter newestBlockParameter = new(latestBlockNumber);
            blockTree.Head.Returns(latestBlock!);
            blockTree.FindBlock(new BlockParameter(latestBlockNumber)).Returns(latestBlock);

            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, newestBlockParameter);

            resultWrapper.Data.OldestBlock.Should().Be(expectedOldestBlockNumber);
        }

        [TestCase(2, 2)]
        [TestCase(7, 7)]
        [TestCase(32, 32)]
        public void GetFeeHistory_IfLastBlockIsPendingBlock_LastBlockNumberSetToPendingBlockNumber(long blockNumber, long lastBlockNumberExpected)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block pendingBlock = Build.A.Block.WithNumber(blockNumber).TestObject;
            blockTree.FindBlock(BlockParameter.Pending).Returns(pendingBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Pending);

            resultWrapper.Data.OldestBlock.Should().Be(lastBlockNumberExpected);
        }

        [TestCase(2, 2)]
        [TestCase(7, 7)]
        [TestCase(32, 32)]
        public void GetFeeHistory_IfLastBlockIsLatestBlock_LastBlockNumberSetToHeadBlockNumber(long blockNumber, long lastBlockNumberExpected)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block headBlock = Build.A.Block.WithNumber(blockNumber).TestObject;
            blockTree.FindBlock(BlockParameter.Latest).Returns(headBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Latest);

            resultWrapper.Data.OldestBlock.Should().Be(lastBlockNumberExpected);
        }

        [Test]
        public void GetFeeHistory_IfLastBlockIsEarliestBlock_LastBlockNumberSetToGenesisBlockNumber()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Earliest).Returns(Build.A.Block.Genesis.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Earliest);

            resultWrapper.Data.OldestBlock.Should().Be(0);
        }

        [TestCase(new double[] { 20, 40, 60, 80.5 }, new ulong[] { 4, 10, 10, 22 })]
        [TestCase(new double[] { 10, 20, 30, 40 }, new ulong[] { 4, 4, 10, 10 })]
        public void GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectly(double[] rewardPercentiles, ulong[] expected)
        {
            Transaction[] transactions = GetTestTransactions();
            Block headBlock = Build.A.Block.Genesis.WithBaseFeePerGas(3).WithGasUsed(100).WithTransactions(transactions).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockParameter newestBlockParameter = new((long)0);
            blockTree.FindBlock(newestBlockParameter).Returns(headBlock);
            IReceiptStorage? receiptStorage = GetTestReceiptStorageForBlockWithGasUsed(headBlock, [10, 20, 30, 40]);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, rewardPercentiles);

            UInt256[] expectedUInt256 = expected.Select(static x => (UInt256)x).ToArray();
            resultWrapper.Data.Reward!.Count.Should().Be(1);
            resultWrapper.Data.Reward![0].Should().BeEquivalentTo(expectedUInt256);
        }


        private static object[] GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectlyOnMultipleCalls_TestCases()
        {
            return
            [
                new object[]
                {
                    new double[][] {[ 20, 40, 60, 80.5 ], [10, 20, 30, 40 ]},
                    new ulong[][] { [4, 10, 10, 22], [4, 4, 10, 10] },
                    3,
                    15
                },
                new object[]
                {
                    new double[][] {[ 10, 20, 30, 40 ], [ 20, 40, 60, 80.5 ]},
                    new ulong[][] {[ 4, 4, 10, 10 ], [ 4, 10, 10, 22 ]},
                    3,
                    15
                }
            ];
        }

        [TestCaseSource(nameof(GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectlyOnMultipleCalls_TestCases))]
        public void GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectlyOnMultipleCalls(double[][] rewardPercentilesArray, ulong[][] expectedArray, int cacheSize, int repetitions)
        {
            Transaction[] transactions = GetTestTransactions();
            Block headBlock = Build.A.Block.Genesis.WithBaseFeePerGas(3).WithGasUsed(100).WithTransactions(transactions).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockParameter newestBlockParameter = new((long)0);
            blockTree.FindBlock(newestBlockParameter).Returns(headBlock);
            IReceiptStorage? receiptStorage = GetTestReceiptStorageForBlockWithGasUsed(headBlock, new long[] { 10, 20, 30, 40 });
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage, cacheSize: cacheSize);
            while (repetitions-- > 0)
            {
                for (var i = 0; i < rewardPercentilesArray.Length; i++)
                {
                    using ResultWrapper<FeeHistoryResults> resultWrapper =
                        feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, rewardPercentilesArray[i]);

                    UInt256[] expectedUInt256 = expectedArray[i].Select(static x => (UInt256)x).ToArray();
                    resultWrapper.Data.Reward!.Count.Should().Be(1);
                    resultWrapper.Data.Reward![0].Should().BeEquivalentTo(expectedUInt256);
                }

            }
        }

        private static IReceiptStorage GetTestReceiptStorageForBlockWithGasUsed(Block block, long[] gasUsedArray)
        {
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();

            var txReceiptsArray = new TxReceipt[gasUsedArray.Length];
            txReceiptsArray[0] = new TxReceipt() { GasUsedTotal = gasUsedArray[0] };
            for (var i = 1; i < gasUsedArray.Length; i++)
            {
                txReceiptsArray[i] = new TxReceipt()
                {
                    GasUsedTotal = txReceiptsArray[i - 1].GasUsedTotal + gasUsedArray[i]
                };
            }
            receiptStorage.Get(block).Returns(txReceiptsArray);
            receiptStorage.Get(block, false).Returns(txReceiptsArray);
            return receiptStorage;
        }

        private static Transaction[] GetTestTransactions()
        {
            var transactions = new Transaction[]
            {
                //Rewards:
                Build.A.Transaction.WithHash(TestItem.KeccakA).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(13)
                    .WithType(TxType.EIP1559).TestObject, //13
                Build.A.Transaction.WithHash(TestItem.KeccakB).WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(7).TestObject, //4
                Build.A.Transaction.WithHash(TestItem.KeccakC).WithMaxFeePerGas(25).WithMaxPriorityFeePerGas(24)
                    .WithType(TxType.EIP1559).TestObject, //22
                Build.A.Transaction.WithHash(TestItem.KeccakD).WithMaxFeePerGas(15).WithMaxPriorityFeePerGas(10)
                    .WithType(TxType.EIP1559).TestObject //10
            };
            return transactions;
        }

        public static IEnumerable<TestCaseData> AscendingBlockNumberTestCases()
        {
            yield return new TestCaseData(
                Cancun.Instance,
                new[] { 0.5, 0.3333333333333333 }
            );
            yield return new TestCaseData(
                Prague.Instance,
                new[] { 0.3333333333333333, 0.2222222222222222 }
            );
        }

        [Test]
        [TestCaseSource(nameof(AscendingBlockNumberTestCases))]
        public void GetFeeHistory_ResultsSortedInOrderOfAscendingBlockNumber(IReleaseSpec spec, IEnumerable<double> blobGasUsedRatio)
        {
            BlockParameter newestBlockParameter = new(1);
            FeeHistoryOracle feeHistoryOracle = SetUpFeeHistoryManager(newestBlockParameter, spec);
            double[] rewardPercentiles = { 0 };
            using FeeHistoryResults expected = new(0,
                new ArrayPoolList<UInt256>([2, 3, 3]),
                new ArrayPoolList<double>([0.6, 0.25]),
                new ArrayPoolList<UInt256>([1, 1, 1]),
                new ArrayPoolList<double>(2, blobGasUsedRatio),
                new ArrayPoolList<ArrayPoolList<UInt256>>(
                [
                    new ArrayPoolList<UInt256>([1]),
                    new ArrayPoolList<UInt256>([0])
                ])
            );

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(2, newestBlockParameter, rewardPercentiles);
            resultWrapper.Data.Should().BeEquivalentTo(expected);

            static FeeHistoryOracle SetUpFeeHistoryManager(BlockParameter blockParameter, IReleaseSpec spec)
            {
                Transaction txFirstBlock = Build.A.Transaction.WithGasPrice(3).TestObject; //Reward: Min (3, 3-2) => 1
                Transaction txSecondBlock = Build.A.Transaction.WithGasPrice(2).TestObject; //Reward: BaseFee > FeeCap => 0

                const BlockTreeLookupOptions options = BlockTreeLookupOptions.ExcludeTxHashes |
                                                       BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                       BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

                Block firstBlock = Build.A.Block.Genesis.WithBaseFeePerGas(2).WithGasUsed(3).WithBlobGasUsed(3 * Eip4844Constants.GasPerBlob).WithExcessBlobGas(0).WithGasLimit(5)
                    .WithTransactions(txFirstBlock).TestObject;
                Block secondBlock = Build.A.Block.WithNumber(1).WithBaseFeePerGas(3).WithGasUsed(2).WithBlobGasUsed(2 * Eip4844Constants.GasPerBlob).WithExcessBlobGas(0).WithGasLimit(8)
                    .WithTransactions(txSecondBlock).WithParent(firstBlock).TestObject;

                IBlockTree blockTree = Substitute.For<IBlockTree>();
                blockTree.FindBlock(blockParameter).Returns(secondBlock);
                blockTree.Head.Returns(secondBlock);
                blockTree.FindParent(secondBlock, BlockTreeLookupOptions.RequireCanonical).Returns(firstBlock);
                blockTree.FindBlock(firstBlock.Hash!, options, firstBlock.Number).Returns(firstBlock);

                IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
                receiptStorage.Get(firstBlock).Returns(new TxReceipt[] { new() { GasUsed = 3 } });
                receiptStorage.Get(firstBlock, false).Returns(new TxReceipt[] { new() { GasUsed = 3 } });
                receiptStorage.Get(secondBlock).Returns(new TxReceipt[] { new() { GasUsed = 2 } });
                receiptStorage.Get(secondBlock, false).Returns(new TxReceipt[] { new() { GasUsed = 2 } });
                FeeHistoryOracle feeHistoryOracle1 =
                    GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage, spec: spec);
                return feeHistoryOracle1;
            }
        }

        private static FeeHistoryOracle GetSubstitutedFeeHistoryOracle(
            IBlockTree? blockTree = null,
            IReceiptStorage? receiptStorage = null,
            ISpecProvider? specProvider = null,
            int? cacheSize = null,
            int? maxDistFromHead = null,
            IReleaseSpec? spec = null)
        {
            ISpecProvider provider;
            if (specProvider is not null)
            {
                provider = specProvider;
            }
            else
            {
                provider = Substitute.For<ISpecProvider>();
                provider.GetSpec(Arg.Any<ForkActivation>()).BaseFeeCalculator.Returns(new DefaultBaseFeeCalculator());
                if (spec is not null)
                {
                    provider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
                }
            }

            return new(
                blockTree ?? Substitute.For<IBlockTree>(),
                receiptStorage ?? Substitute.For<IReceiptStorage>(),
                provider,
                maxDistFromHead);
        }
    }
}
