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
// 

using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule.LastBlockNumberConsts;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [TestFixture]
        public class BlockRangeManagerTests
        {
            [TestCase(2,2,1)]
            [TestCase(7,7,6)]
            [TestCase(32,32,31)]
            public void
                ResolveBlockRange_IfLastBlockIsPendingBlockAndPendingBlockExists_LastBlockNumberSetToPendingBlockNumber(long blockNumber, long lastBlockNumberExpected, long headBlockNumberExpected)
            {
                long lastBlockNumber = PendingBlockNumber;
                long blockCount = 1;
                long? headBlockNumber = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns((Block) Build.A.Block.WithNumber(blockNumber).TestObject);
                BlockRangeManager blockRangeManager = new(blockFinder);

                blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1, ref headBlockNumber);

                lastBlockNumber.Should().Be(lastBlockNumberExpected);
                headBlockNumber.Should().Be(headBlockNumberExpected);
            }
            
            [Test]
            public void
                ResolveBlockRange_IfLastBlockNumberIsPendingBlockNumberAndPendingBlockIsNull_LastBlockNumberSetToLatestBlockNumberMode()
            {
                long lastBlockNumber = PendingBlockNumber;
                long blockCount = 1;
                long? headBlockNumber = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns((Block) null);
                BlockRangeManager blockRangeManager = new(blockFinder);

                blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1, ref headBlockNumber);

                lastBlockNumber.Should().Be(LatestBlockNumber);
            }

            [Test]
            public void
                ResolveBlockRange_IfLastBlockIsPendingBlockAndPendingBlockIsNullAndBlockCountEquals1_ErrorReturned()
            {
                long lastBlockNumber = PendingBlockNumber;
                long blockCount = 1;
                long? headBlockNumber = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns((Block) null);
                BlockRangeManager blockRangeManager = new(blockFinder);

                ResultWrapper<BlockRangeInfo> resultWrapper = blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1, ref headBlockNumber);

                ResultWrapper<BlockRangeInfo> expected =
                    ResultWrapper<BlockRangeInfo>.Fail("Invalid pending block reduced blockCount to 0.");
                
                resultWrapper.Result.Error.Should().Be("Invalid pending block reduced blockCount to 0.");
                resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
            }
            
            [TestCase(2)]
            [TestCase(7)]
            [TestCase(32)]
            public void ResolveBlockRange_IfLastBlockIsNotPendingBlockAndHeadBlockNumberIsNull_ErrorReturned(long lastBlockNumber)
            {
                long blockCount = 1;
                long? headBlockNumber = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindHeadBlock().Returns((Block) null);
                BlockRangeManager blockRangeManager = new(blockFinder);

                ResultWrapper<BlockRangeInfo> resultWrapper = blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1, ref headBlockNumber);

                ResultWrapper<BlockRangeInfo> expected =
                    ResultWrapper<BlockRangeInfo>.Fail("Invalid pending block reduced blockCount to 0.");
                
                resultWrapper.Result.Error.Should().Be("Head block not found.");
                resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
            }

            [Test]
            public void ResolveBlockRange_IfLastBlockIsPendingBlockAndPendingBlockIsNull_ErrorReturned()
            {
                long lastBlockNumber = PendingBlockNumber;
                long blockCount = 1;
                long? headBlockNumber = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns((Block) null);
                BlockRangeManager blockRangeManager = new(blockFinder);

                ResultWrapper<BlockRangeInfo> resultWrapper = blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1, ref headBlockNumber);

                ResultWrapper<BlockRangeInfo> expected =
                    ResultWrapper<BlockRangeInfo>.Fail("Invalid pending block reduced blockCount to 0.");
                
                resultWrapper.Result.Error.Should().Be("Invalid pending block reduced blockCount to 0.");
                resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
            }
            
            [TestCase(2,2)]
            [TestCase(7,7)]
            [TestCase(32,32)]
            public void ResolveBlockRange_IfLastBlockIsSetToLatestBlockNumberMode_SetLastBlockToHeadBlockNumber(long headBlockNumber, long headBlockNumberExpected)
            {
                long lastBlockNumber = LatestBlockNumber;
                long blockCount = 1;
                long? headBlockNumberVar = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                BlockRangeInfo blockRangeInfo = new();
                blockFinder.FindHeadBlock().Returns(Build.A.Block.WithNumber(headBlockNumber).TestObject);
                BlockRangeManager blockRangeManager = new(blockFinder);

                blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1, ref headBlockNumberVar);

                lastBlockNumber.Should().Be(headBlockNumberExpected);
                headBlockNumberVar.Should().Be(headBlockNumberExpected);
            }

            [TestCase(3,5)]
            [TestCase(4,10)]
            [TestCase(0,1)]
            public void ResolveBlockRange_IfPendingBlockDoesNotExistAndLastBlockNumberGreaterThanHeadNumber_ReturnsError(long headBlockNumber, long lastBlockNumber)
            {
                long blockCount = 1;
                long? headBlockNumberVar = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns((Block) null);
                blockFinder.FindHeadBlock().Returns(Build.A.Block.WithNumber(headBlockNumber).TestObject);
                BlockRangeManager blockRangeManager = new(blockFinder);

                ResultWrapper<BlockRangeInfo> resultWrapper = blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1, ref headBlockNumberVar);

                resultWrapper.Result.Error.Should().Be("Pending block not present and last block number greater than head number.");
                resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
            }

            [Test]
            public void ResolveBlockRange_IfMaxHistoryIsNot0_CalculateTooOldCountCalled()
            {
                long lastBlockNumber = 0;
                long blockCount = 1;
                long? headBlockNumber = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindHeadBlock().Returns(Build.A.Block.Genesis.TestObject);
                TestableBlockRangeManager testableBlockRangeManager = new(blockFinder);

                testableBlockRangeManager.tooOldCountCalled.Should().BeFalse();
                testableBlockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1,
                    ref headBlockNumber);

                testableBlockRangeManager.tooOldCountCalled.Should().BeTrue();
            }

            [TestCase(3, 1,1,7, 2)]
            [TestCase(4, 4,2,15, 5)]
            public void CalculateTooOldCount_IfTooOldCountGreaterThanOrEqualToThanBlockCount_ReturnsError(long lastBlockNumber, long blockCount, int maxHistory, long? headBlockNumber, long tooOldCount)
            {
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                BlockRangeManager blockRangeManager = new BlockRangeManager(blockFinder);

                ResultWrapper<long> resultWrapper = blockRangeManager.CalculateTooOldCount(lastBlockNumber, blockCount, maxHistory, headBlockNumber);
                
                resultWrapper.Result.Error.Should().BeEquivalentTo($"Block count: {blockCount}, is less than old blocks to remove: {tooOldCount}.");
            }
            
            [TestCase(3, 3,1,7, 0)]
            [TestCase(4, 6,2,15, 3)]
            public void CalculateTooOldCount_IfTooOldCountLessThanBlockCount_CalculatesOutputCorrectly(long lastBlockNumber, long blockCount, int maxHistory, long? headBlockNumber, long tooOldCount)
            {
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                BlockRangeManager blockRangeManager = new BlockRangeManager(blockFinder);

                ResultWrapper<long> resultWrapper = blockRangeManager.CalculateTooOldCount(lastBlockNumber, blockCount, maxHistory, headBlockNumber);
                
                resultWrapper.Data.Should().Be(tooOldCount);
            }
            [Test]
            public void ResolveBlockRange_IfBlockCountMoreThanBlocksUptoLastBlockNumber_BlockCountSetToBlocksUptoLastBlockNumber()
            {
                long lastBlockNumber = 5;
                long blockCount = 10;
                long? headBlockNumber = null;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindHeadBlock().Returns(Build.A.Block.WithNumber(11).TestObject);
                TestableBlockRangeManager testableBlockRangeManager = new(blockFinder);

                ResultWrapper<BlockRangeInfo> resultWrapper = testableBlockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1,
                    ref headBlockNumber);

                resultWrapper.Data.BlockCount.Should().Be(6);
            }

            public class TestableBlockRangeManager : BlockRangeManager
            {
                public bool tooOldCountCalled;
                public TestableBlockRangeManager(IBlockFinder blockFinder) : base(blockFinder)
                {
                    tooOldCountCalled = false;
                }

                public override ResultWrapper<long> CalculateTooOldCount(long lastBlockNumber, long blockCount, int maxHistory, long? headBlockNumber)
                {
                    tooOldCountCalled = true;
                    return ResultWrapper<long>.Success(0);
                }
            }
        }
    }
}
