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

using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Distributions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Org.BouncyCastle.Crypto.Tls;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        public partial class FeeHistoryManager : IFeeHistoryManager
        {
            private IBlockFinder _blockFinder;
            public FeeHistoryManager(IBlockFinder blockFinder)
            {
                _blockFinder = blockFinder;
            }
            public ResultWrapper<FeeHistoryResult> GetFeeHistory(long blockCount, long lastBlockNumber,
                double[]? rewardPercentiles = null)
            {
                ResultWrapper<FeeHistoryResult> failingResultWrapper = null;
                if (InitialChecksFailed(blockCount, rewardPercentiles, ref failingResultWrapper))
                    return failingResultWrapper;
                int maxHistory = 1;
                ResultWrapper<ResolveBlockRangeInfo> pendingBlock = ResolveBlockRange(lastBlockNumber, blockCount, maxHistory);
                return FeeHistoryLookup(blockCount, lastBlockNumber, rewardPercentiles);
            }

            private ResultWrapper<ResolveBlockRangeInfo> ResolveBlockRange(long lastBlockNumber, long blockCount, int maxHistory)
            {
                Block? pendingBlock = null;
                long? headBlockNumber = null;
                Block? latestBlock = _blockFinder.FindLatestBlock();
                if (lastBlockNumber == LastBlockNumberConsts.PendingBlockNumber)
                {
                    pendingBlock = _blockFinder.FindPendingBlock();
                    if (pendingBlock != null)
                    {
                        lastBlockNumber = pendingBlock.Number;
                        headBlockNumber = pendingBlock.Number - 1;
                    }
                    else
                    {
                        lastBlockNumber = LastBlockNumberConsts.LatestBlockNumber;
                        blockCount--;
                        if (blockCount == 0)
                            return ResultWrapper<ResolveBlockRangeInfo>.Fail("Invalid pending block reduced blockCount to 0."); //return fail results
                    }
                }

                if (headBlockNumber == null)
                {
                    headBlockNumber = _blockFinder.FindHeadBlock()?.Number;
                    if (headBlockNumber == null)
                    {
                        return ResultWrapper<ResolveBlockRangeInfo>.Fail("Head block not found"); //return fail results
                    }
                }

                if (lastBlockNumber == LastBlockNumberConsts.LatestBlockNumber)
                {
                    lastBlockNumber = (long) headBlockNumber!;
                }
                else if (pendingBlock != null && lastBlockNumber > headBlockNumber)
                {
                    return ResultWrapper<ResolveBlockRangeInfo>.Fail("Pending block not present and last block number greater than head number.");
                }
                if (maxHistory != 0)
                {
                    long tooOldCount = (long) (headBlockNumber! - maxHistory - lastBlockNumber - blockCount)!;
                    if (blockCount > tooOldCount)
                        blockCount = tooOldCount;
                    else
                    {
                        return ResultWrapper<ResolveBlockRangeInfo>.Fail("Block count is less than old blocks to remove.");
                    }
                }
                if (blockCount > lastBlockNumber + 1)
                {
                    blockCount = lastBlockNumber + 1;
                }
                return ResultWrapper<ResolveBlockRangeInfo>.Success(new ResolveBlockRangeInfo(pendingBlock, lastBlockNumber, blockCount));
            }

            class ResolveBlockRangeInfo
            {
                public Block? pendingBlock;
                public long? LastBlockNumber;
                public long? BlockCount;

                public ResolveBlockRangeInfo(Block? block, long? lastBlockNumber, long? blockCount)
                {
                    pendingBlock = block;
                    LastBlockNumber = lastBlockNumber;
                    BlockCount = blockCount;
                }

                public ResolveBlockRangeInfo() : this(null, null, null)
                {
                    
                }
            }
            private bool InitialChecksFailed(long blockCount, double[] rewardPercentiles, ref ResultWrapper<FeeHistoryResult> fail)
            {
                if (blockCount < 1)
                {
                    {
                        fail = ResultWrapper<FeeHistoryResult>.Fail($"blockCount: Block count, {blockCount}, is less than 1.");
                        return true;
                    }
                }

                if (blockCount > 1024)
                {
                    blockCount = GetMaxBlockCount();
                }

                if (rewardPercentiles != null)
                {
                    int index = -1;
                    int count = rewardPercentiles.Length;
                    int[] incorrectlySortedIndexes = rewardPercentiles
                        .Select(val => ++index)
                        .Where(val => index > 0 
                                      && index < count 
                                      && rewardPercentiles[index] < rewardPercentiles[index - 1])
                        .ToArray();
                    if (incorrectlySortedIndexes.Any())
                    {
                        int firstIndex = incorrectlySortedIndexes.ElementAt(0);
                        {
                            fail = ResultWrapper<FeeHistoryResult>.Fail(
                                $"rewardPercentiles: Value at index {firstIndex}: {rewardPercentiles[firstIndex]} is less than " +
                                $"the value at previous index {firstIndex - 1}: {rewardPercentiles[firstIndex - 1]}.");
                            return true;
                        }
                    }

                    double[] invalidValues = rewardPercentiles.Select(val => val).Where(val => val < 0 || val > 100)
                        .ToArray();
                    
                    if (invalidValues.Any())
                    {
                        {
                            fail = ResultWrapper<FeeHistoryResult>.Fail(
                                $"rewardPercentiles: Values {String.Join(", ", invalidValues)} are below 0 or greater than 100."
                            );
                            return true;
                        }
                    }
                }

                return false;
            }

            protected virtual int GetMaxBlockCount()
            {
                return 1024;
            }

            private ResultWrapper<FeeHistoryResult> FeeHistoryLookup(long blockCount, long lastBlockNumber, double[]? rewardPercentiles = null)
            {
                Block pendingBlock = _blockFinder.FindPendingBlock();
                long pendingBlockNumber = pendingBlock.Number;
                long firstBlockNumber = lastBlockNumber + 1 - blockCount;
                List<BlockFeeInfo> blockFeeInfos = new();
                for (; firstBlockNumber < lastBlockNumber; firstBlockNumber++)
                {
                    BlockFeeInfo blockFeeInfo = new();
                    if (firstBlockNumber > pendingBlockNumber)
                    {
                        blockFeeInfo.Block = pendingBlock;
                        blockFeeInfo.BlockNumber = pendingBlockNumber;
                        blockFeeInfo.BlockHeader = pendingBlock.Header;
                    }
                    else
                    {
                        Block block = _blockFinder.FindBlock(firstBlockNumber);
                        blockFeeInfo.Block = block;
                        blockFeeInfo.BlockNumber = firstBlockNumber;
                        blockFeeInfo.BlockHeader = block.Header;
                    }

                    blockFeeInfos.Add(blockFeeInfo);
                }
                return ResultWrapper<FeeHistoryResult>.Fail("");
            }
        }
    }
}
