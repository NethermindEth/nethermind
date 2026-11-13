// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.Evm;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
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
            ResultWrapper<FeeHistoryResults> expected = ResultWrapper<FeeHistoryResults>.Fail("blockCount: Value 0 is less than 1", ErrorCodes.InvalidParams);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(0, BlockParameter.Latest, []);
            Assert.That(resultWrapper, Is.EqualTo(expected).UsingPropertiesComparer());
        }

        [Test]
        public void GetFeeHistory_HashParameter_ReturnsFailingWrapper()
        {
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle();
            ResultWrapper<FeeHistoryResults> expected = ResultWrapper<FeeHistoryResults>.Fail("newestBlock: Is not correct block number", ErrorCodes.InvalidParams);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(0, new BlockParameter(TestItem.KeccakA), []);
            Assert.That(resultWrapper, Is.EqualTo(expected).UsingPropertiesComparer());
        }

        [Test]
        public void GetFeeHistory_NewestBlockIsNull_ReturnsFailingWrapper()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(Arg.Any<ulong>()).Returns((Block?)null);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);
            ResultWrapper<FeeHistoryResults> expected = ResultWrapper<FeeHistoryResults>.Fail("request beyond head block", ErrorCodes.ResourceNotFound);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(0UL), []);
            Assert.That(resultWrapper, Is.EqualTo(expected).UsingPropertiesComparer());
        }


        [TestCase(3UL, 5UL)]
        [TestCase(4UL, 10UL)]
        [TestCase(0UL, 1UL)]
        public void GetFeeHistory_IfPendingBlockDoesNotExistAndLastBlockNumberGreaterThanHeadNumber_ReturnsError(ulong pendingBlockNumber, ulong lastBlockNumber)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindPendingBlock().Returns(Build.A.Block.WithNumber(pendingBlockNumber).TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);
            ResultWrapper<FeeHistoryResults> expected = ResultWrapper<FeeHistoryResults>.Fail("request beyond head block", ErrorCodes.ResourceNotFound);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(lastBlockNumber), []);

            Assert.That(resultWrapper, Is.EqualTo(expected).UsingPropertiesComparer());
        }

        [Test]
        public void GetFeeHistory_IfBlockBodyPrunedButHeaderExists_ReturnsPrunedHistoryUnavailable()
        {
            // On snap-synced nodes, pre-pivot blocks have headers but no bodies.
            // FindBlock returns null; FindHeader returns the header.
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindHeader(Arg.Any<BlockParameter>()).Returns(Build.A.BlockHeader.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);
            ResultWrapper<FeeHistoryResults> expected = ResultWrapper<FeeHistoryResults>.Fail(ErrorMessages.PrunedHistoryUnavailable, ErrorCodes.PrunedHistoryUnavailable);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(0UL), []);
            Assert.That(resultWrapper, Is.EqualTo(expected).UsingPropertiesComparer());
        }

        [Test]
        public void GetFeeHistory_IfRewardPercentilesNotInAscendingOrder_ResultsInFailure()
        {
            ulong blockCount = 10;
            double[] rewardPercentiles = [0, 2, 3, 5, 1];
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, BlockParameter.Latest, rewardPercentiles);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultWrapper.Result.Error, Is.EqualTo("rewardPercentiles: Value at index 4: 1 is less than or equal to the value at previous index 3: 5."));
                Assert.That(resultWrapper.Result.ResultType, Is.EqualTo(ResultType.Failure));
            }
        }

        [Test]
        public void GetFeeHistory_IfTooManyRewardPercentiles_ResultsInFailure()
        {
            ulong blockCount = 10;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            double[] percentiles = Enumerable.Range(1, 999).Select(p => p / 10d).ToArray();
            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, BlockParameter.Latest, percentiles);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultWrapper.Result.Error, Is.EqualTo("rewardPercentiles: 999 is over the query limit 100."));
                Assert.That(resultWrapper.Result.ResultType, Is.EqualTo(ResultType.Failure));
            }
        }

        [TestCase(new double[] { -1, 1, 2 }, TestName = "NegativePercentile")]
        [TestCase(new[] { 1, 2.2, 101, 102 }, TestName = "PercentileOver100")]
        public void GetFeeHistory_IfRewardPercentilesContainInvalidNumber_ResultsInFailure(double[] rewardPercentiles)
        {
            ulong blockCount = 10;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, BlockParameter.Latest, rewardPercentiles);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultWrapper.Result.Error, Is.EqualTo("rewardPercentiles: Some values are below 0 or greater than 100."));
                Assert.That(resultWrapper.Result.ResultType, Is.EqualTo(ResultType.Failure));
            }
        }

        [TestCase(3UL, 3UL, 5, 6)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5)/1.5 * 5) / 8, 1) = 1 | Next Base Fee = 5 + 1 = 6
        [TestCase(3UL, 3UL, 11, 13)] //Target gas used: 3/2 = 1.5 | Actual Gas used = 3 | Base Fee Delta = Max((((3-1.5)/1.5) * 11) / 8, 1) = 2 | Next Base Fee = 11 + 2 = 13
        [TestCase(100UL, 95UL, 20, 22)] //Target gas used: 100/2 = 50 | Actual Gas used = 95 | Base Fee Delta = Max((((95-50)/50) * 20) / 8, 1) = 2 | Next Base Fee = 20 + 2 = 22
        [TestCase(100UL, 40UL, 20, 20)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 20) / 8 = 0 | Next Base Fee = 20 - 0 = 20
        [TestCase(100UL, 40UL, 50, 49)] //Target gas used: 100/2 = 50 | Actual Gas used = 40 | Base Fee Delta = (((50-40)/50) * 50) / 8 = 1 | Next Base Fee = 50 - 1 = 49
        public void GetFeeHistory_IfLondonEnabled_NextBaseFeePerGasCalculatedCorrectly(ulong gasLimit, ulong gasUsed, long baseFee, long expectedNextBaseFee)
        {
            ulong blockCount = 1;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithBaseFee((UInt256)baseFee).WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;
            BlockParameter newestBlock = new(0UL);
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            blockTree.FindBlock(newestBlock).Returns(headBlock);
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(true);

            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, specProvider: specProvider);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, newestBlock, []);

            UInt256 resultNextBaseFee = resultWrapper.Data.BaseFeePerGas[1];
            UInt256 resultBaseFee = resultWrapper.Data.BaseFeePerGas[0];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultNextBaseFee, Is.EqualTo((UInt256)expectedNextBaseFee));
                Assert.That(resultBaseFee, Is.EqualTo((UInt256)baseFee));
            }

        }

        [Test]
        public async Task GetFeeHistory_DoesNotLoadTheParentOfTheOldestBlock()
        {
            await using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddDecorator<IBlockTree, BodyReadHookBlockTree>()
                .Build();
            BodyReadHookBlockTree blockTree = (BodyReadHookBlockTree)container.Resolve<IBlockTree>();
            Block genesis = Build.A.Block.Genesis.TestObject;
            Block parent = Build.A.Block.WithParent(genesis).TestObject;
            Block oldest = Build.A.Block.WithParent(parent).WithGasLimit(100).WithGasUsed(50).TestObject;
            // A head above the requested block, so the requested block is not the one held in memory.
            Block head = Build.A.Block.WithParent(oldest).TestObject;
            foreach (Block block in (Block[])[genesis, parent, oldest, head])
            {
                AddToMainChain(blockTree, block);
            }

            IFeeHistoryOracle feeHistoryOracle = container.Resolve<IFeeHistoryOracle>();
            int oldestReadsBefore = blockTree.BodyReads(oldest.Number);
            int parentReadsBefore = blockTree.BodyReads(parent.Number);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(oldest.Number), []);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo(oldest.Number));
                Assert.That(blockTree.BodyReads(oldest.Number), Is.GreaterThan(oldestReadsBefore), "the requested block is loaded");
                Assert.That(blockTree.BodyReads(parent.Number), Is.EqualTo(parentReadsBefore), "the parent of the oldest block is not loaded");
            }
        }

        [TestCase(3UL, 3UL, 1)]
        [TestCase(100UL, 95UL, 0.95)]
        [TestCase(12UL, 3UL, 0.25)]
        [TestCase(100UL, 40UL, 0.4)]
        [TestCase(3UL, 1UL, 0.3333333333333333)]
        public void GetFeeHistory_GasUsedRatioCalculatedCorrectly(ulong gasLimit, ulong gasUsed, double expectedGasUsedRatio)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockHeader blockHeader = Build.A.BlockHeader.WithGasLimit(gasLimit).WithGasUsed(gasUsed).TestObject;

            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            BlockParameter newestBlock = new(0UL);
            blockTree.Head.Returns(headBlock);
            blockTree.FindBlock(newestBlock).Returns(headBlock);
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(true);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, specProvider: specProvider);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlock, []);

            Assert.That(resultWrapper.Data.GasUsedRatio![0], Is.EqualTo(expectedGasUsedRatio));
        }

        [TestCase(3, 3)]
        [TestCase(5, 5)]
        public void GetFeeHistory_IfLondonNotEnabled_NextBaseFeeIsParentBaseFee(long baseFee, long expectedNextBaseFee)
        {
            ISpecProvider specProvider = GetSpecProviderWithEip1559EnabledAs(false);
            BlockHeader blockHeader = Build.A.BlockHeader.WithBaseFee((UInt256)baseFee).TestObject;
            Block headBlock = Build.A.Block.Genesis.WithHeader(blockHeader).TestObject;
            BlockParameter newestBlock = new(0UL);
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(newestBlock).Returns(headBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, specProvider: specProvider);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlock, []);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultWrapper.Data.BaseFeePerGas![1], Is.EqualTo((UInt256)expectedNextBaseFee));
                Assert.That(resultWrapper.Data.BaseFeePerGas![0], Is.EqualTo((UInt256)baseFee));
            }
        }

        [Test]
        public void GetFeeHistory_IfRewardPercentilesIsEmpty_RewardsIsNull()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Latest).Returns(Build.A.Block.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, BlockParameter.Latest, []);

            Assert.That(resultWrapper.Data.Reward, Is.Null, "an empty rewardPercentiles array means no reward computation was requested");
        }

        [TestCase(5, new ulong[] { 0, 0, 0, 0, 0 })]
        [TestCase(7, new ulong[] { 0, 0, 0, 0, 0, 0, 0 })]
        public void GetFeeHistory_NoTxsInBlock_ReturnsArrayOfZerosAsBigAsRewardPercentiles(int sizeOfRewardPercentiles, ulong[] expectedRewards)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block noTxBlock = Build.A.Block.TestObject;
            BlockParameter newestBlock = new(0UL);
            blockTree.FindBlock(newestBlock).Returns(noTxBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);
            double[] rewardPercentiles = Enumerable.Range(1, sizeOfRewardPercentiles).Select(static x => (double)x).ToArray();

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlock, rewardPercentiles);

            UInt256[] expectedRewardsUInt256 = expectedRewards.Select(static x => (UInt256)x).ToArray();
            Assert.That(resultWrapper.Data.Reward![0], Is.EqualTo(expectedRewardsUInt256));
        }


        [TestCase(5UL, 10UL, 6UL)]
        [TestCase(5UL, 3UL, 0UL)]
        public void GetFeeHistory_GivenValidInputs_FirstBlockNumberCalculatedCorrectly(ulong blockCount, ulong newestBlockNumber, ulong expectedOldestBlockNumber)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            const BlockTreeLookupOptions options = BlockTreeLookupOptions.ExcludeTxHashes |
                                                   BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                   BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

            Block? parent = null;
            Block? latestBlock = null;
            ulong latestBlockNumber = 0;
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

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(blockCount, newestBlockParameter, []);

            Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo(expectedOldestBlockNumber));
        }

        [Test]
        public void GetFeeHistory_IfAncestorBodyUnavailable_TruncatesAtOldestAvailableBlock([Values] bool withRewards)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            const BlockTreeLookupOptions options = BlockTreeLookupOptions.ExcludeTxHashes |
                                                   BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                   BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

            // Block 2's body is unavailable (e.g. expired history), so only blocks 3..5 can be reported.
            Block block2 = Build.A.Block.WithNumber(2UL).WithParentHash(TestItem.KeccakH).WithGasLimit(100).WithGasUsed(2).TestObject;
            Block block3 = Build.A.Block.WithParent(block2).WithGasLimit(100).WithGasUsed(30).WithBaseFeePerGas(3).TestObject;
            Block block4 = Build.A.Block.WithParent(block3).WithGasLimit(100).WithGasUsed(40).WithBaseFeePerGas(4).TestObject;
            Block block5 = Build.A.Block.WithParent(block4).WithGasLimit(100).WithGasUsed(50).WithBaseFeePerGas(5).TestObject;
            foreach (Block block in (Block[])[block3, block4, block5])
            {
                blockTree.FindBlock(block.Hash!, options, block.Number).Returns(block);
            }
            blockTree.Head.Returns(block5);
            blockTree.FindBlock(new BlockParameter(5UL)).Returns(block5);

            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(10UL, new BlockParameter(5UL), withRewards ? [50] : []);

            FeeHistoryResults result = resultWrapper.Data;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.OldestBlock, Is.EqualTo(3UL));
                Assert.That(result.GasUsedRatio, Is.EqualTo((double[])[0.3, 0.4, 0.5]));
                Assert.That(result.BlobGasUsedRatio.Count, Is.EqualTo(3));
                Assert.That(result.BaseFeePerGas.Take(3), Is.EqualTo((UInt256[])[3, 4, 5]));
                Assert.That(result.BaseFeePerGas.Count, Is.EqualTo(4));
                Assert.That(result.BaseFeePerBlobGas.Count, Is.EqualTo(4));
                Assert.That(result.Reward?.Count, Is.EqualTo(withRewards ? 3 : null));
                if (result.Reward is not null) Assert.That(result.Reward, Has.None.Null);
            }
        }

        [Test]
        public void GetFeeHistory_IfCachedAncestorBodyGoesMissingBeforeRewards_TruncatesAtOldestAvailableBlock()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block block3 = Build.A.Block.WithNumber(3UL).WithParentHash(TestItem.KeccakH).WithBaseFeePerGas(3).WithTransactions(GetTestTransactions()).TestObject;
            Block block4 = Build.A.Block.WithParent(block3).WithBaseFeePerGas(4).WithTransactions(GetTestTransactions()).TestObject;
            Block block5 = Build.A.Block.WithParent(block4).WithBaseFeePerGas(5).WithTransactions(GetTestTransactions()).TestObject;
            foreach (Block block in (Block[])[block3, block4, block5])
            {
                blockTree.FindBlock(block.Hash!, Arg.Any<BlockTreeLookupOptions>(), block.Number).Returns(block);
            }
            blockTree.Head.Returns(block5);
            blockTree.FindBlock(new BlockParameter(5UL)).Returns(block5);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            feeHistoryOracle.GetFeeHistory(3UL, new BlockParameter(5UL), []).Dispose();
            blockTree.FindBlock(block3.Hash!, Arg.Any<BlockTreeLookupOptions>(), block3.Number).Returns((Block?)null);
            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(3UL, new BlockParameter(5UL), [50]);

            FeeHistoryResults result = resultWrapper.Data;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.OldestBlock, Is.EqualTo(4UL));
                Assert.That(result.GasUsedRatio.Count, Is.EqualTo(2));
                Assert.That(result.BaseFeePerGas.Take(2), Is.EqualTo((UInt256[])[4, 5]));
                Assert.That(result.Reward, Has.Count.EqualTo(2).And.None.Null);
            }
        }

        [Test]
        public async Task GetFeeHistory_IfNewestBlockIsCached_DoesNotLoadItsBody()
        {
            await using IContainer container = new ContainerBuilder()
                .AddModule(new TestNethermindModule())
                .AddDecorator<IBlockTree, BodyReadHookBlockTree>()
                .Build();
            BodyReadHookBlockTree blockTree = (BodyReadHookBlockTree)container.Resolve<IBlockTree>();
            Block genesis = Build.A.Block.Genesis.TestObject;
            Block block = Build.A.Block.WithParent(genesis).WithGasLimit(100).WithGasUsed(50).TestObject;
            // A head above the requested block, so the requested block is not the one held in memory.
            Block head = Build.A.Block.WithParent(block).TestObject;
            AddToMainChain(blockTree, genesis);
            AddToMainChain(blockTree, block);
            AddToMainChain(blockTree, head);
            IFeeHistoryOracle feeHistoryOracle = container.Resolve<IFeeHistoryOracle>();
            BlockParameter newestBlock = new(block.Number);
            int bodyLoadsInitially = blockTree.TotalBodyReads;

            feeHistoryOracle.GetFeeHistory(1, newestBlock, []).Dispose();
            int bodyLoadsBefore = blockTree.TotalBodyReads;
            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlock, []);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(bodyLoadsBefore, Is.GreaterThan(bodyLoadsInitially), "the first call loads the body");
                Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo(block.Number));
                Assert.That(resultWrapper.Data.GasUsedRatio, Is.EqualTo((double[])[0.5]));
                Assert.That(blockTree.TotalBodyReads, Is.EqualTo(bodyLoadsBefore), "a cached newest block is not loaded again");
            }
        }

        private static void AddToMainChain(IBlockTree blockTree, Block block)
        {
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block);
        }

        [TestCase(-1, 6)]
        [TestCase(0, 6)]
        [TestCase(2, 2)]
        [TestCase(1024, 6)]
        public async Task GetFeeHistory_BlockCountAboveConfiguredMax_IsLowered(int maxBlockCount, int expectedBlockCount)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxBlockCount = maxBlockCount });

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(10UL, new BlockParameter(5UL), []);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo((ulong)(6 - expectedBlockCount)));
                Assert.That(resultWrapper.Data.GasUsedRatio.Count, Is.EqualTo(expectedBlockCount));
            }
        }

        [TestCase(2UL, 5UL, TestName = "Newest block cached, its parent is not")]
        [TestCase(1UL, 2UL, TestName = "One uncached block")]
        [CancelAfter(30_000)]
        public async Task GetFeeHistory_UncachedRequestsAtLimit_RejectsMoreUncachedButServesCached(ulong blockCount, ulong newestBlock, CancellationToken cancellationToken)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            // Only the head is cached, so every other block is read from the database.
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxConcurrentRequests = 1 }, maxDistanceFromHead: 0);
            BlockParameter head = new(chain.BlockTree.Head!.Number);
            feeHistoryOracle.GetFeeHistory(1, head, []).Dispose();

            ulong oldestBlock = newestBlock - blockCount + 1;
            // Building the chain reads blocks too.
            int readsBefore = chain.LookupHooks.BodyReads(oldestBlock);
            await using PausedRequest uncached = chain.PauseUncachedRequest(feeHistoryOracle, blockCount, newestBlock, cancellationToken);

            Assert.That(() => feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(newestBlock), []), Throws.TypeOf<LimitExceededException>());
            Assert.That(chain.LookupHooks.BodyReads(oldestBlock), Is.EqualTo(readsBefore + 1), "the rejected request must not read the block");
            using (ResultWrapper<FeeHistoryResults> cached = feeHistoryOracle.GetFeeHistory(1, head, []))
            {
                Assert.That(cached.Data.OldestBlock, Is.EqualTo(head.BlockNumber));
            }

            using ResultWrapper<FeeHistoryResults> uncachedResult = await uncached.Release();
            using ResultWrapper<FeeHistoryResults> afterSlotReleased = feeHistoryOracle.GetFeeHistory(blockCount, new BlockParameter(newestBlock), []);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(uncachedResult.Data.OldestBlock, Is.EqualTo(oldestBlock));
                Assert.That(afterSlotReleased.Data.OldestBlock, Is.EqualTo(oldestBlock));
                Assert.That(chain.LookupHooks.BodyReads(oldestBlock), Is.EqualTo(readsBefore + 2), "the block is not cached, so the last request read it with the released slot");
            }
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task GetFeeHistory_UncachedRequestsAtLimit_ServesRewardsOfCachedBlocks(CancellationToken cancellationToken)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            // The head and its parent are cached, so the block before them is read from the database.
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxConcurrentRequests = 1 }, maxDistanceFromHead: 1);
            BlockParameter head = new(chain.BlockTree.Head!.Number);
            ulong uncachedBlock = head.BlockNumber!.Value - 2;
            // Caches the two newest blocks without their rewards, as processing a new head does.
            feeHistoryOracle.GetFeeHistory(2, head, []).Dispose();

            await using PausedRequest uncached = chain.PauseUncachedRequest(feeHistoryOracle, 1, uncachedBlock, cancellationToken);

            using (ResultWrapper<FeeHistoryResults> withRewards = feeHistoryOracle.GetFeeHistory(2, head, [50]))
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(withRewards.Data.OldestBlock, Is.EqualTo(uncachedBlock + 1));
                    Assert.That(withRewards.Data.Reward!.Count, Is.EqualTo(2));
                    Assert.That(withRewards.Data.Reward, Has.All.Count.EqualTo(1));
                }
            }

            using ResultWrapper<FeeHistoryResults> uncachedResult = await uncached.Release();
            Assert.That(uncachedResult.Data.OldestBlock, Is.EqualTo(uncachedBlock));
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task GetFeeHistory_ConcurrentRewardRequests_ReadCachedBlockOnce(CancellationToken cancellationToken)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig());
            ulong headNumber = chain.BlockTree.Head!.Number;
            BlockParameter head = new(headNumber);
            // Caches the head without its rewards, as processing a new head does.
            feeHistoryOracle.GetFeeHistory(1, head, []).Dispose();
            int readsBefore = chain.LookupHooks.BodyReads(headNumber);

            using BodyReadPause pause = chain.LookupHooks.PauseFirstBodyRead(headNumber, cancellationToken);
            Task<ResultWrapper<FeeHistoryResults>> first = Task.Run(() => feeHistoryOracle.GetFeeHistory(1, head, [50]), cancellationToken);
            pause.WaitUntilReading();

            ResultWrapper<FeeHistoryResults>? secondResult = null;
            Exception? secondError = null;
            Thread second = new(() =>
            {
                try
                {
                    secondResult = feeHistoryOracle.GetFeeHistory(1, head, [50]);
                }
                catch (Exception e)
                {
                    secondError = e;
                }
            });
            second.Start();
            // The second request either waits for the first one's read or reads the body itself.
            while (second.IsAlive
                   && (second.ThreadState & ThreadState.WaitSleepJoin) == 0
                   && chain.LookupHooks.BodyReads(headNumber) == readsBefore + 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Yield();
            }

            pause.Release();
            using ResultWrapper<FeeHistoryResults> firstResult = await first;
            second.Join();
            Assert.That(secondError, Is.Null, "the second request is served");
            using ResultWrapper<FeeHistoryResults> secondResultWrapper = secondResult!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(chain.LookupHooks.BodyReads(headNumber), Is.EqualTo(readsBefore + 1), "the rewards of a cached block are read once");
                Assert.That(secondResultWrapper.Data.Reward![0], Is.EqualTo(firstResult.Data.Reward![0]));
            }
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task GetFeeHistory_UncachedRequestsAtLimit_ReportsBlockBeyondHead(CancellationToken cancellationToken)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxConcurrentRequests = 1 }, maxDistanceFromHead: 0);
            await using PausedRequest uncached = chain.PauseUncachedRequest(feeHistoryOracle, 1, 2, cancellationToken);

            using ResultWrapper<FeeHistoryResults> beyondHead = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(10UL), []);
            Assert.That(beyondHead.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task GetFeeHistory_UncachedRequestsAtLimit_NewestHeaderNotFound_ReadsNoBody(CancellationToken cancellationToken)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxConcurrentRequests = 1 }, maxDistanceFromHead: 0);
            await using PausedRequest uncached = chain.PauseUncachedRequest(feeHistoryOracle, 1, 2, cancellationToken);
            int readsBefore = chain.LookupHooks.BodyReads(3);
            // The header lookup misses, as just before the block is added, while a body lookup would find the block.
            chain.LookupHooks.MissingHeaderOnce = 3;

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(3UL), [50]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resultWrapper.ErrorCode, Is.EqualTo(ErrorCodes.ResourceNotFound));
                Assert.That(chain.LookupHooks.BodyReads(3), Is.EqualTo(readsBefore), "a block whose header is not found is not read");
            }
        }

        [Test]
        [CancelAfter(30_000)]
        public async Task GetFeeHistory_UncachedRequestsAtLimit_ServesHeadNotCachedYet(CancellationToken cancellationToken)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            // Created after the chain is built, so the head is not cached, as right before its asynchronous fill.
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxConcurrentRequests = 1 }, maxDistanceFromHead: 0);
            await using PausedRequest uncached = chain.PauseUncachedRequest(feeHistoryOracle, 1, 2, cancellationToken);

            using ResultWrapper<FeeHistoryResults> latest = feeHistoryOracle.GetFeeHistory(1, BlockParameter.Latest, [50]);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(latest.Data.OldestBlock, Is.EqualTo(chain.BlockTree.Head!.Number));
                Assert.That(latest.Data.Reward![0], Has.Count.EqualTo(1));
            }
        }

        [TestCase(0)]
        [TestCase(-1)]
        [CancelAfter(30_000)]
        public async Task GetFeeHistory_ConcurrentRequestsLimitNotPositive_ServesConcurrentUncachedRequests(int limit, CancellationToken cancellationToken)
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxConcurrentRequests = limit }, maxDistanceFromHead: 0);
            await using PausedRequest first = chain.PauseUncachedRequest(feeHistoryOracle, 1, 2, cancellationToken);

            using (ResultWrapper<FeeHistoryResults> second = feeHistoryOracle.GetFeeHistory(2, new BlockParameter(4UL), []))
            {
                Assert.That(second.Data.OldestBlock, Is.EqualTo(3UL));
            }

            using ResultWrapper<FeeHistoryResults> firstResult = await first.Release();
            Assert.That(firstResult.Data.OldestBlock, Is.EqualTo(2UL));
        }

        [Test]
        public async Task GetFeeHistory_LookupFails_ReleasesUncachedRequestSlot()
        {
            using FeeHistoryTestBlockchain chain = await FeeHistoryTestBlockchain.Create(blockCount: 5);
            using FeeHistoryOracle feeHistoryOracle = chain.CreateOracle(new JsonRpcConfig { FeeHistoryMaxConcurrentRequests = 1 }, maxDistanceFromHead: 0);
            chain.LookupHooks.BeforeBodyRead = blockNumber =>
            {
                if (blockNumber == 3) throw new InvalidOperationException("Database read failed");
            };

            Assert.That(() => feeHistoryOracle.GetFeeHistory(1, new BlockParameter(3UL), []), Throws.InvalidOperationException);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, new BlockParameter(2UL), []);
            Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo(2UL));
        }

        [TestCase(2UL, 2UL)]
        [TestCase(7UL, 7UL)]
        [TestCase(32UL, 32UL)]
        public void GetFeeHistory_IfLastBlockIsPendingBlock_LastBlockNumberSetToPendingBlockNumber(ulong blockNumber, ulong lastBlockNumberExpected)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block pendingBlock = Build.A.Block.WithNumber(blockNumber).TestObject;
            blockTree.FindBlock(BlockParameter.Pending).Returns(pendingBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Pending, []);

            Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo(lastBlockNumberExpected));
        }

        [TestCase(2UL, 2UL)]
        [TestCase(7UL, 7UL)]
        [TestCase(32UL, 32UL)]
        public void GetFeeHistory_IfLastBlockIsLatestBlock_LastBlockNumberSetToHeadBlockNumber(ulong blockNumber, ulong lastBlockNumberExpected)
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block headBlock = Build.A.Block.WithNumber(blockNumber).TestObject;
            blockTree.FindBlock(BlockParameter.Latest).Returns(headBlock);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Latest, []);

            Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo(lastBlockNumberExpected));
        }

        [Test]
        public void GetFeeHistory_IfLastBlockIsEarliestBlock_LastBlockNumberSetToGenesisBlockNumber()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(BlockParameter.Earliest).Returns(Build.A.Block.Genesis.TestObject);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree);

            using ResultWrapper<FeeHistoryResults> resultWrapper =
                feeHistoryOracle.GetFeeHistory(1, BlockParameter.Earliest, []);

            Assert.That(resultWrapper.Data.OldestBlock, Is.EqualTo(0));
        }

        [TestCase(new double[] { 20, 40, 60, 80.5 }, new ulong[] { 4, 10, 10, 22 })]
        [TestCase(new double[] { 10, 20, 30, 40 }, new ulong[] { 4, 4, 10, 10 })]
        public void GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectly(double[] rewardPercentiles, ulong[] expected)
        {
            Transaction[] transactions = GetTestTransactions();
            Block headBlock = Build.A.Block.Genesis.WithBaseFeePerGas(3).WithGasUsed(100).WithTransactions(transactions).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockParameter newestBlockParameter = new(0UL);
            blockTree.FindBlock(newestBlockParameter).Returns(headBlock);
            IReceiptStorage? receiptStorage = GetTestReceiptStorageForBlockWithGasUsed(headBlock, [10, 20, 30, 40]);
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, rewardPercentiles);

            UInt256[] expectedUInt256 = expected.Select(static x => (UInt256)x).ToArray();
            Assert.That(resultWrapper.Data.Reward!.Count, Is.EqualTo(1));
            Assert.That(resultWrapper.Data.Reward![0], Is.EqualTo(expectedUInt256));
        }

        [Test]
        public void GetFeeHistory_WithoutRewardPercentiles_DoesNotReadReceipts()
        {
            Block headBlock = Build.A.Block.Genesis.WithBaseFeePerGas(3).WithGasUsed(100).WithTransactions(GetTestTransactions()).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockParameter newestBlockParameter = new(0UL);
            blockTree.FindBlock(newestBlockParameter).Returns(headBlock);
            blockTree.FindBlock(headBlock.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(headBlock);
            InMemoryReceiptStorage receiptStorage = new();
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage);

            feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, []).Dispose();
            // Rewards built by the call above would have fallen back to the gas limits: 4, 10, 13, 22.
            receiptStorage.Insert(headBlock, BuildReceiptsWithGasUsed([10, 20, 30, 40], eip8116Receipts: false), ensureCanonical: false);
            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, [20, 40, 60, 80.5]);

            Assert.That(resultWrapper.Data.Reward![0], Is.EqualTo((UInt256[])[4, 10, 10, 22]), "the cached block reads its receipts once percentiles are requested");
        }

        [Test]
        public async Task GetFeeHistory_RewardPercentilesWeighEachReceiptByItsOwnGas([Values] bool eip8116Enabled, [Values] ReceiptSource receiptSource)
        {
            using BasicTestBlockchain chain = await BasicTestBlockchain.Create();
            Block headBlock = Build.A.Block.WithParent(chain.BlockTree.Head!).WithBaseFeePerGas(3).WithGasUsed(100).WithTransactions(GetTestTransactions()).TestObject;
            // Skip processing: the chain's processor would reject this hand-built block and drop it from the tree.
            chain.BlockTree.SuggestBlock(headBlock, BlockTreeSuggestOptions.None);
            chain.BlockTree.TryUpdateMainChain(headBlock.Header, true, preloadedBlocks: headBlock);
            BlockParameter newestBlockParameter = new(headBlock.Number);
            IReceiptStorage receiptStorage = receiptSource switch
            {
                ReceiptSource.InMemory => new InMemoryReceiptStorage(),
                ReceiptSource.InMemoryWithoutIterator => new InMemoryReceiptStorage(allowReceiptIterator: false),
                _ => chain.ReceiptStorage,
            };
            receiptStorage.Insert(headBlock, BuildReceiptsWithGasUsed([10, 20, 30, 40], eip8116Enabled), ensureCanonical: false);
            // Read the receipts back from the database rather than the insert-populated cache.
            if (receiptSource == ReceiptSource.Database) ((PersistentReceiptStorage)receiptStorage).ClearCache();
            TestSpecProvider specProvider = new(new OverridableReleaseSpec(Bogota.Instance) { IsEip8116Enabled = eip8116Enabled });
            using FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: chain.BlockTree, receiptStorage: receiptStorage, specProvider: specProvider);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, [20, 40, 60, 80.5]);

            Assert.That(resultWrapper.Data.Reward![0], Is.EqualTo(new UInt256[] { 4, 10, 10, 22 }));
        }

        // Rewards are weighted by receipt gas, or by tx GasLimit when receipts are unavailable; the
        // percentile threshold must use that same total rather than the header GasUsed, which is
        // pre-refund under EIP-7778 and never matched the GasLimit weights.
        [TestCase(2_000_000ul, new ulong[] { 100_000, 100_000 }, 50.0, 1ul, TestName = "Post-refund receipt gas, not the pre-refund header total")]
        [TestCase(50_000ul, null, 60.0, 10ul, TestName = "Tx gas limits when receipts are pruned, not the header total")]
        public void GetFeeHistory_RewardPercentilesUseTheWeightTotal_NotHeaderGasUsed(
            ulong headerGasUsed, ulong[]? receiptGasUsed, double percentile, ulong expectedReward)
        {
            const ulong gasLimitPerTx = 100_000;
            Transaction[] transactions =
            [
                Build.A.Transaction.WithHash(TestItem.KeccakA).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(1)
                    .WithType(TxType.EIP1559).WithGasLimit(gasLimitPerTx).TestObject,
                Build.A.Transaction.WithHash(TestItem.KeccakB).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(10)
                    .WithType(TxType.EIP1559).WithGasLimit(gasLimitPerTx).TestObject,
            ];
            Block headBlock = Build.A.Block.Genesis.WithBaseFeePerGas(3)
                .WithGasUsed(headerGasUsed)
                .WithTransactions(transactions).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockParameter newestBlockParameter = new(0UL);
            blockTree.FindBlock(newestBlockParameter).Returns(headBlock);

            // A null case means no receipts, which is how the pruned-receipts fallback onto tx gas limits is reached.
            IReceiptStorage receiptStorage = GetTestReceiptStorageForBlockWithGasUsed(headBlock, receiptGasUsed ?? []);

            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage);

            using ResultWrapper<FeeHistoryResults> resultWrapper = feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, [percentile]);

            Assert.That(resultWrapper.Data.Reward!.Count, Is.EqualTo(1));
            Assert.That(resultWrapper.Data.Reward![0], Is.EqualTo(new UInt256[] { expectedReward }));
        }

        private static IEnumerable<TestCaseData> GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectlyOnMultipleCalls_TestCases()
        {
            yield return new TestCaseData(
                    new double[][] { [20, 40, 60, 80.5], [10, 20, 30, 40] },
                    new ulong[][] { [4, 10, 10, 22], [4, 4, 10, 10] },
                    3,
                    15)
                .SetName("High then low percentiles");
            yield return new TestCaseData(
                    new double[][] { [10, 20, 30, 40], [20, 40, 60, 80.5] },
                    new ulong[][] { [4, 4, 10, 10], [4, 10, 10, 22] },
                    3,
                    15)
                .SetName("Low then high percentiles");
        }

        [TestCaseSource(nameof(GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectlyOnMultipleCalls_TestCases))]
        public void GetFeeHistory_GivenValidInputs_CalculatesPercentilesCorrectlyOnMultipleCalls(double[][] rewardPercentilesArray, ulong[][] expectedArray, int cacheSize, int repetitions)
        {
            Transaction[] transactions = GetTestTransactions();
            Block headBlock = Build.A.Block.Genesis.WithBaseFeePerGas(3).WithGasUsed(100).WithTransactions(transactions).TestObject;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            BlockParameter newestBlockParameter = new(0UL);
            blockTree.FindBlock(newestBlockParameter).Returns(headBlock);
            IReceiptStorage? receiptStorage = GetTestReceiptStorageForBlockWithGasUsed(headBlock, new ulong[] { 10, 20, 30, 40 });
            FeeHistoryOracle feeHistoryOracle = GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage);
            while (repetitions-- > 0)
            {
                for (int i = 0; i < rewardPercentilesArray.Length; i++)
                {
                    using ResultWrapper<FeeHistoryResults> resultWrapper =
                        feeHistoryOracle.GetFeeHistory(1, newestBlockParameter, rewardPercentilesArray[i]);

                    UInt256[] expectedUInt256 = expectedArray[i].Select(static x => (UInt256)x).ToArray();
                    Assert.That(resultWrapper.Data.Reward!.Count, Is.EqualTo(1));
                    Assert.That(resultWrapper.Data.Reward![0], Is.EqualTo(expectedUInt256));
                }

            }
        }

        private static IReceiptStorage GetTestReceiptStorageForBlockWithGasUsed(Block block, ulong[] gasUsedArray)
        {
            InMemoryReceiptStorage receiptStorage = new();
            receiptStorage.Insert(block, BuildReceiptsWithGasUsed(gasUsedArray, eip8116Receipts: false), ensureCanonical: false);
            return receiptStorage;
        }

        public enum ReceiptSource
        {
            InMemory,
            InMemoryWithoutIterator,
            Database,
        }

        private static TxReceipt[] BuildReceiptsWithGasUsed(ulong[] gasUsedArray, bool eip8116Receipts)
        {
            TxReceipt[] txReceiptsArray = new TxReceipt[gasUsedArray.Length];
            ulong gasUsedTotal = 0;
            for (int i = 0; i < gasUsedArray.Length; i++)
            {
                gasUsedTotal += gasUsedArray[i];
                txReceiptsArray[i] = new TxReceipt() { GasUsedTotal = eip8116Receipts ? gasUsedArray[i] : gasUsedTotal, Logs = [Build.A.LogEntry.TestObject] };
            }
            return txReceiptsArray;
        }

        private static Transaction[] GetTestTransactions()
        {
            Transaction[] transactions = new Transaction[]
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
            BlockParameter newestBlockParameter = new(1UL);
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
            Assert.That(resultWrapper.Data, Is.EqualTo(expected).UsingPropertiesComparer());

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

                InMemoryReceiptStorage receiptStorage = new();
                receiptStorage.Insert(firstBlock, [new() { GasUsed = 3 }], ensureCanonical: false);
                receiptStorage.Insert(secondBlock, [new() { GasUsed = 2 }], ensureCanonical: false);
                FeeHistoryOracle feeHistoryOracle1 =
                    GetSubstitutedFeeHistoryOracle(blockTree: blockTree, receiptStorage: receiptStorage, spec: spec);
                return feeHistoryOracle1;
            }
        }

        [Test]
        public void GetFeeHistory_BaseFeePerBlobGasNewestPlusOne_UsesNextBlockEstimate()
        {
            // The last entry of baseFeePerBlobGas (index = blockCount) is the NEXT block's
            // estimated fee — derived from CalculateExcessBlobGas(newestBlock.Header), not the current fee.
            // Use large excess so the fake-exponential produces a fee well above MinBlobGasPrice,
            // ensuring current and next are numerically distinct after BlobGasUsed=0 reduces excess.
            const BlockTreeLookupOptions options = BlockTreeLookupOptions.ExcludeTxHashes |
                                                   BlockTreeLookupOptions.TotalDifficultyNotNeeded |
                                                   BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

            IReleaseSpec spec = Cancun.Instance;
            ulong excessBlobGas = spec.GasCosts.MaxBlobGasPerBlock * 16;
            Block block = Build.A.Block.Genesis
                .WithBaseFeePerGas(1)
                .WithGasUsed(1).WithGasLimit(10)
                .WithBlobGasUsed(0)
                .WithExcessBlobGas(excessBlobGas)
                .TestObject;

            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.FindBlock(Arg.Any<BlockParameter>()).Returns(block);
            blockTree.Head.Returns(block);
            blockTree.FindBlock(block.Hash!, options, block.Number).Returns(block);

            ISpecProvider specProvider = SpecProviderSubstitute.Create();
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).BaseFeeCalculator.Returns(new DefaultBaseFeeCalculator());

            FeeHistoryOracle oracle = new(blockTree, NullReceiptStorage.Instance, specProvider, new JsonRpcConfig());
            using ResultWrapper<FeeHistoryResults> result = oracle.GetFeeHistory(1, new BlockParameter(0UL), []);

            ArrayPoolList<UInt256> fees = result.Data.BaseFeePerBlobGas;
            Assert.That(fees.Count, Is.EqualTo(2), "blockCount + 1 entries");

            BlobGasCalculator.TryCalculateFeePerBlobGas(excessBlobGas, spec.BlobBaseFeeUpdateFraction, out UInt256 expectedCurrentFee);
            Assert.That(fees[0], Is.EqualTo(expectedCurrentFee), "index 0 is the current block's blob base fee");

            ulong nextExcess = BlobGasCalculator.CalculateExcessBlobGas(block.Header, spec) ?? 0;
            BlobGasCalculator.TryCalculateFeePerBlobGas(nextExcess, spec.BlobBaseFeeUpdateFraction, out UInt256 expectedNextFee);
            Assert.That(fees[1], Is.EqualTo(expectedNextFee), "index 1 is the next block's estimated blob base fee");
            Assert.That(fees[1], Is.LessThan(fees[0]), "next fee must be lower since BlobGasUsed=0 reduces excess blob gas");
        }

        private static FeeHistoryOracle GetSubstitutedFeeHistoryOracle(
            IBlockTree? blockTree = null,
            IReceiptStorage? receiptStorage = null,
            ISpecProvider? specProvider = null,
            ulong? maxDistFromHead = null,
            IReleaseSpec? spec = null)
        {
            ISpecProvider provider;
            if (specProvider is not null)
            {
                provider = specProvider;
            }
            else
            {
                provider = SpecProviderSubstitute.Create();
                provider.GetSpec(Arg.Any<ForkActivation>()).BaseFeeCalculator.Returns(new DefaultBaseFeeCalculator());
                if (spec is not null)
                {
                    provider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);
                }
            }

            return new(
                blockTree ?? Substitute.For<IBlockTree>(),
                receiptStorage ?? NullReceiptStorage.Instance,
                provider,
                new JsonRpcConfig(),
                maxDistFromHead);
        }

        private sealed class FeeHistoryTestBlockchain : BasicTestBlockchain
        {
            public BodyReadHookBlockTree LookupHooks => (BodyReadHookBlockTree)BlockTree;

            public static async Task<FeeHistoryTestBlockchain> Create(int blockCount)
            {
                FeeHistoryTestBlockchain chain = new();
                // EIP-1559 from genesis, so blocks have a base fee and accept typed transactions.
                await chain.Build(builder => builder
                    .AddSingleton<ISpecProvider>(new TestSpecProvider(Cancun.Instance))
                    .AddDecorator<IBlockTree>((_, blockTree) => new BodyReadHookBlockTree(blockTree)));
                await chain.BuildSomeBlocks(blockCount);
                return chain;
            }

            /// <remarks>Created after the chain is built, so no block is cached until a request reads it.</remarks>
            public FeeHistoryOracle CreateOracle(IJsonRpcConfig jsonRpcConfig, ulong? maxDistanceFromHead = null) =>
                new(BlockTree, ReceiptStorage, SpecProvider, jsonRpcConfig, maxDistanceFromHead);

            /// <summary>
            /// Runs a request without reward percentiles in the background and returns once it reads the body of its oldest
            /// block, which it then waits on, holding its slot, until released.
            /// </summary>
            public PausedRequest PauseUncachedRequest(FeeHistoryOracle oracle, ulong blockCount, ulong newestBlock, CancellationToken cancellationToken)
            {
                BodyReadPause pause = LookupHooks.PauseFirstBodyRead(newestBlock - blockCount + 1, cancellationToken);
                Task<ResultWrapper<FeeHistoryResults>> request = Task.Run(() => oracle.GetFeeHistory(blockCount, new BlockParameter(newestBlock), []), cancellationToken);
                pause.WaitUntilReading();
                return new PausedRequest(pause, request);
            }
        }

        /// <summary>Counts block body reads per block number and runs <see cref="BeforeBodyRead"/> before each one.</summary>
        private sealed class BodyReadHookBlockTree(IBlockTree inner) : BlockTreeTestDouble(inner)
        {
            private readonly ConcurrentDictionary<ulong, int> _bodyReads = new();
            private int _totalBodyReads;

            public Action<ulong>? BeforeBodyRead { get; set; }

            public int BodyReads(ulong blockNumber) => _bodyReads.GetValueOrDefault(blockNumber);

            public int TotalBodyReads => Volatile.Read(ref _totalBodyReads);

            /// <summary>The next header lookup of this block number finds nothing.</summary>
            public ulong? MissingHeaderOnce { get; set; }

            public override BlockHeader? FindHeader(ulong blockNumber, BlockTreeLookupOptions options)
            {
                if (MissingHeaderOnce == blockNumber)
                {
                    MissingHeaderOnce = null;
                    return null;
                }

                return base.FindHeader(blockNumber, options);
            }

            /// <summary>Holds the next read of <paramref name="blockNumber"/>'s body until the pause is released.</summary>
            public BodyReadPause PauseFirstBodyRead(ulong blockNumber, CancellationToken cancellationToken)
            {
                BodyReadPause pause = new(cancellationToken);
                BeforeBodyRead = number =>
                {
                    if (number == blockNumber) pause.HoldFirst();
                };
                return pause;
            }

            public override Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null)
            {
                if (blockNumber is { } number) OnBodyRead(number);
                else Interlocked.Increment(ref _totalBodyReads);
                return base.FindBlock(blockHash, options, blockNumber);
            }

            public override Block? FindBlock(ulong blockNumber, BlockTreeLookupOptions options)
            {
                OnBodyRead(blockNumber);
                return base.FindBlock(blockNumber, options);
            }

            private void OnBodyRead(ulong blockNumber)
            {
                Interlocked.Increment(ref _totalBodyReads);
                _bodyReads.AddOrUpdate(blockNumber, 1, static (_, reads) => reads + 1);
                BeforeBodyRead?.Invoke(blockNumber);
            }
        }

        /// <summary>A request held at a body read by <see cref="FeeHistoryTestBlockchain.PauseUncachedRequest"/>.</summary>
        private sealed class PausedRequest(BodyReadPause pause, Task<ResultWrapper<FeeHistoryResults>> request) : IAsyncDisposable
        {
            private bool _released;

            /// <summary>Lets the request finish and returns its result, which the caller disposes.</summary>
            public Task<ResultWrapper<FeeHistoryResults>> Release()
            {
                _released = true;
                pause.Release();
                return request;
            }

            /// <remarks>Finishes a request the test did not release, so a failed assertion does not leave it waiting.</remarks>
            public async ValueTask DisposeAsync()
            {
                if (_released) return;
                pause.Release();
                (await request).Dispose();
            }
        }

        /// <summary>Holds the first body read it is given until <see cref="Release"/>; later reads pass.</summary>
        private sealed class BodyReadPause(CancellationToken cancellationToken) : IDisposable
        {
            private readonly ManualResetEventSlim _reading = new();
            private readonly ManualResetEventSlim _release = new();
            private int _held;

            public void HoldFirst()
            {
                if (Interlocked.Exchange(ref _held, 1) == 1) return;
                _reading.Set();
                _release.Wait(cancellationToken);
            }

            public void WaitUntilReading() => _reading.Wait(cancellationToken);

            public void Release() => _release.Set();

            /// <remarks>Releases a held read, so a failed assertion does not leave the reading request waiting.</remarks>
            public void Dispose() => Release();
        }
    }
}
