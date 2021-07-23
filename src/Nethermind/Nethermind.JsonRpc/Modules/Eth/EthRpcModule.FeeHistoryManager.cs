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
using Nethermind.Logging;
using Microsoft.VisualBasic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Int256;
using static Nethermind.JsonRpc.Modules.Eth.FeeHistoryResult;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule
    {
        public class FeeHistoryManager : IFeeHistoryManager
        {
            private readonly IBlockFinder _blockFinder;
            private readonly IBlockRangeManager _blockRangeManager;
            private readonly ILogger _logger;
            private IProcessBlockManager ProcessBlockManager { get; }
            private IInitialCheckManager InitialCheckManager { get; }

            public FeeHistoryManager(IBlockFinder blockFinder, ILogger logger, IBlockRangeManager? blockRangeManager = null, IProcessBlockManager? processBlockManager = null,
                IInitialCheckManager? initialCheckManager = null)
            {
                _blockFinder = blockFinder;
                _logger = logger;
                _blockRangeManager = blockRangeManager ?? GetBlockRangeManager(_blockFinder);
                ProcessBlockManager = processBlockManager ?? new ProcessBlockManager(_logger);
                InitialCheckManager = initialCheckManager ?? new InitialCheckManager();
            }


            protected IBlockRangeManager GetBlockRangeManager(IBlockFinder blockFinder)
            {
                return new BlockRangeManager(blockFinder);
            }
            
            public ResultWrapper<FeeHistoryResult> GetFeeHistory(ref long blockCount, long lastBlockNumber,
                double[]? rewardPercentiles = null)
            {
                ResultWrapper<FeeHistoryResult> initialCheckResult = InitialCheckManager.InitialChecksPassed(ref blockCount, rewardPercentiles);
                if (initialCheckResult.Result.ResultType == ResultType.Failure)
                {
                    return initialCheckResult;
                }
                
                long? headBlockNumber = null;
                ResultWrapper<BlockRangeInfo> blockRangeResult = _blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, MaxHistory, ref headBlockNumber);
                if (blockRangeResult.Result.ResultType == ResultType.Failure)
                {
                    return ResultWrapper<FeeHistoryResult>.Fail(blockRangeResult.Result.Error ?? "Error message in ResolveBlockRange not set correctly.");
                }

                BlockRangeInfo blockRangeInfo = blockRangeResult.Data;
                long? oldestBlockNumber = blockRangeInfo.LastBlockNumber + 1 - blockRangeInfo.BlockCount;
                if (oldestBlockNumber == null)
                {
                    string output = StringOfNullElements(blockRangeInfo);
                    return ResultWrapper<FeeHistoryResult>.Fail($"{output} is null");
                }
                
                
                return FeeHistoryLookup(blockCount, lastBlockNumber, rewardPercentiles);
            }

            private static string StringOfNullElements(BlockRangeInfo blockRangeInfo)
            {
                List<string> nullStrings = new();
                if (blockRangeInfo.LastBlockNumber == null)
                    nullStrings.Add("blockRangeInfo.LastBlockNumber");
                if (blockRangeInfo.BlockCount == null)
                    nullStrings.Add("blockRangeInfo.BlockCount");
                string output = Strings.Join(nullStrings.ToArray(), ", ") ?? "";
                return output;
            }

            protected internal ResultWrapper<FeeHistoryResult> FeeHistoryLookup(long blockCount, long lastBlockNumber, double[]? rewardPercentiles = null)
            {
                Block pendingBlock = _blockFinder.FindPendingBlock();
                long firstBlockNumber = Math.Max(lastBlockNumber + 1 - blockCount, 0);
                List<BlockFeeInfo> blockFeeInfos = new();
                for (; firstBlockNumber <= lastBlockNumber; firstBlockNumber++)
                {
                    BlockFeeInfo blockFeeInfo = GetBlockFeeInfo(firstBlockNumber, rewardPercentiles, pendingBlock); //rename firstBlockNumber

                    blockFeeInfos.Add(blockFeeInfo);
                }

                return SuccessfulResult(blockCount, blockFeeInfos);
            }

            protected virtual ResultWrapper<FeeHistoryResult> SuccessfulResult(long blockCount, List<BlockFeeInfo> blockFeeInfos)
            {
                return ResultWrapper<FeeHistoryResult>.Success(CreateFeeHistoryResult(blockFeeInfos, blockCount));
            }

            protected internal virtual BlockFeeInfo GetBlockFeeInfo(long blockNumber, double[]? rewardPercentiles, Block? pendingBlock)
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
                    blockFeeInfo.Reward = ProcessBlockManager.ProcessBlock(ref blockFeeInfo, rewardPercentiles);
                }

                return blockFeeInfo;
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

            public class GasPriceAndReward
            {
                public UInt256 GasPrice { get; }
                public UInt256 Reward { get; }

                public GasPriceAndReward (UInt256 gasPrice, UInt256 reward)
                {
                    GasPrice = gasPrice;
                    Reward = reward;
                }
            }
        }
    }
}
