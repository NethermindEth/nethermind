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
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        public class FeeHistoryGenerator : IFeeHistoryGenerator
        {
            private readonly IBlockFinder _blockFinder;
            private readonly IProcessBlockManager _processBlockManager;

            public FeeHistoryGenerator(IBlockFinder blockFinder, IProcessBlockManager processBlockManager)
            {
                _blockFinder = blockFinder;
                _processBlockManager = processBlockManager;
            }

            public ResultWrapper<FeeHistoryResult> FeeHistoryLookup(long blockCount, long lastBlockNumber, double[]? rewardPercentiles = null)
            {
                Block pendingBlock = _blockFinder.FindPendingBlock();
                long currentBlockNumber = Math.Max(lastBlockNumber + 1 - blockCount, 0);
                List<BlockFeeInfo> blockFeeInfos = new();
                for (; currentBlockNumber <= lastBlockNumber; currentBlockNumber++)
                {
                    BlockFeeInfo blockFeeInfo = GetBlockFeeInfo(currentBlockNumber, rewardPercentiles, pendingBlock); //rename firstBlockNumber

                    blockFeeInfos.Add(blockFeeInfo);
                }

                return SuccessfulResult(blockCount, blockFeeInfos);
            }
            public virtual BlockFeeInfo GetBlockFeeInfo(long blockNumber, double[]? rewardPercentiles, Block? pendingBlock)
            {
                if (blockNumber < 0)
                {
                    throw new ArgumentException($"Block number, {blockNumber}, is less than 0.");
                }

                BlockFeeInfo blockFeeInfo = new();
                if (pendingBlock != null && blockNumber > pendingBlock.Number)
                {
                    blockFeeInfo.Block = pendingBlock;
                    blockFeeInfo.BlockNumber = pendingBlock.Number;
                }
                else
                {
                    if (rewardPercentiles != null && rewardPercentiles.Length != 0)
                    {
                        blockFeeInfo.Block = _blockFinder.FindBlock(blockNumber);
                    }
                    else
                    {
                        blockFeeInfo.BlockHeader = _blockFinder.FindHeader(blockNumber);
                    }

                    blockFeeInfo.BlockNumber = blockNumber;
                }

                if (blockFeeInfo.Block != null)
                {
                    blockFeeInfo.BlockHeader = blockFeeInfo.Block.Header;
                }

                if (blockFeeInfo.BlockHeader != null)
                {
                    blockFeeInfo.Reward = _processBlockManager.ProcessBlock(ref blockFeeInfo, rewardPercentiles);
                }

                return blockFeeInfo;
            }

            protected virtual ResultWrapper<FeeHistoryResult> SuccessfulResult(long blockCount, List<BlockFeeInfo> blockFeeInfos)
            {
                return ResultWrapper<FeeHistoryResult>.Success(CreateFeeHistoryResult(blockFeeInfos, blockCount));
            }


            protected internal FeeHistoryResult CreateFeeHistoryResult(List<BlockFeeInfo> blockFeeInfos, long blockCount)
            {
                CreateFeeHistoryArgumentChecks(blockFeeInfos, blockCount);

                FeeHistoryResult feeHistoryResult = new(
                    blockFeeInfos[0].BlockNumber,
                    new UInt256[blockCount][],
                    new UInt256[blockCount + 1],
                    new float[blockCount] 
                );
                int index = 0;
                foreach (BlockFeeInfo blockFeeInfo in blockFeeInfos)
                {
                    feeHistoryResult._reward![index] = blockFeeInfo.Reward;
                    feeHistoryResult._baseFee![index] = blockFeeInfo.BaseFee;
                    feeHistoryResult._baseFee[index + 1] = blockFeeInfo.NextBaseFee;
                    feeHistoryResult._gasUsedRatio![index] = blockFeeInfo.GasUsedRatio;
                    index++;
                }

                return feeHistoryResult;
            }

            private static void CreateFeeHistoryArgumentChecks(List<BlockFeeInfo> blockFeeInfos, long blockCount)
            {
                if (blockFeeInfos.Count == 0)
                {
                    throw new ArgumentException("`blockFeeInfos` has 0 elements.");
                }

                if (blockCount == 0)
                {
                    throw new ArgumentException("`blockCount` is equal to 0.");
                }

                if (blockFeeInfos.Count != blockCount)
                {
                    throw new ArgumentException(
                        $"`blockCount`: {blockCount} was not equal to number of blocks' information in `blockFeeInfos`: {blockFeeInfos.Count}.");
                }
            }
        }
    }
}
