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
            [Test]
            public void
                ResolveBlockRange_IfLastBlockIsPendingBlockAndPendingBlockExists_LastBlockNumberSetToPendingBlockNumber()
            {
                
            }
            
            [Test]
            public void
                ResolveBlockRange_IfLastBlockIsPendingBlockAndPendingBlockIsNull_LastBlockNumberSetToLatestBlockNumber()
            {
                long lastBlockNumber = PendingBlockNumber;
                long blockCount = 2;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns((Block) null);
                BlockRangeManager blockRangeManager = new(blockFinder);

                blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1);

                lastBlockNumber.Should().Be(LatestBlockNumber);
            }

            [Test]
            public void
                ResolveBlockRange_IfLastBlockIsPendingBlockAndPendingBlockIsNullAndBlockCountEquals1_ErrorReturned()
            {
                long lastBlockNumber = PendingBlockNumber;
                long blockCount = 1;
                IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
                blockFinder.FindPendingBlock().Returns((Block) null);
                BlockRangeManager blockRangeManager = new(blockFinder);

                ResultWrapper<ResolveBlockRangeInfo> resultWrapper = blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, 1);

                ResultWrapper<ResolveBlockRangeInfo> expected =
                    ResultWrapper<ResolveBlockRangeInfo>.Fail("Invalid pending block reduced blockCount to 0.");
                
                resultWrapper.Result.Error.Should().Be("Invalid pending block reduced blockCount to 0.");
                resultWrapper.Result.ResultType.Should().Be(ResultType.Failure);
            }
            [Test]
            public void ResolveBlockRange_IfLastBlockIsNotPendingBlockAndHeadBlockNumberIsNull_ErrorReturned()
            {
                
            }

            [Test]
            public void ResolveBlockRange_IfLastBlockIsEqualToLatestBlockNumber_SetLastBlockToHeadBlockNumber()
            {
                
            }

            [Test]
            public void
                ResolveBlockRange_IfPendingBlockDoesNotExistAndLastBlockNumberGreaterThanHeadNumber_ReturnsError()
            {
                
            }

            [Test]
            public void ResolveBlockRange_IfMaxHistoryIsNot0_TooOldCountCalculatedCorrectly()
            {
                
            }

            [Test]
            public void
                ResolveBlockRange_IfBlockCountMoreThanBlocksUptoLastBlockNumber_BlockCountSetToBlocksUptoLastBlockNumber()
            {
                
            }
        }
    }
}
