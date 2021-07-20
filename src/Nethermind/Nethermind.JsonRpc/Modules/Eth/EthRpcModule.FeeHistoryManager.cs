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
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        public class FeeHistoryManager : IFeeHistoryManager
        {
            private readonly IBlockFinder _blockFinder;
            private readonly IBlockRangeManager _blockRangeManager;

            public FeeHistoryManager(IBlockFinder blockFinder, IBlockRangeManager? blockRangeManager = null)
            {
                _blockFinder = blockFinder;
                _blockRangeManager = blockRangeManager ?? GetBlockRangeManager(_blockFinder);
            }

            protected virtual IBlockRangeManager GetBlockRangeManager(IBlockFinder blockFinder)
            {
                return new BlockRangeManager(blockFinder);
            }
            
            public ResultWrapper<FeeHistoryResult> GetFeeHistory(long blockCount, long lastBlockNumber,
                double[]? rewardPercentiles = null)
            {
                long? headBlockNumber = null;
                if (InitialChecksFailed(blockCount, rewardPercentiles, out ResultWrapper<FeeHistoryResult> failingResultWrapper))
                {
                    return failingResultWrapper;
                }
                int maxHistory = 1;
                ResultWrapper<BlockRangeInfo> pendingBlock = _blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, maxHistory, ref headBlockNumber);
                return FeeHistoryLookup(blockCount, lastBlockNumber, rewardPercentiles);
            }

            private bool InitialChecksFailed(long blockCount, double[] rewardPercentiles, out ResultWrapper<FeeHistoryResult> fail)
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
                fail = ResultWrapper<FeeHistoryResult>.Success(new FeeHistoryResult(0,Array.Empty<UInt256[]>(),Array.Empty<UInt256>(),Array.Empty<UInt256>()));
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
                long firstBlockNumber = Math.Max(lastBlockNumber + 1 - blockCount, 0);
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
