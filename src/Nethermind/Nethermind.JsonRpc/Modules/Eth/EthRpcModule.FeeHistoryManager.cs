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
using Microsoft.VisualBasic;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using static Nethermind.Core.BlockHeader;

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
            
            public ResultWrapper<FeeHistoryResult> GetFeeHistory(ref long blockCount, long lastBlockNumber,
                float[]? rewardPercentiles = null)
            {
                long? headBlockNumber = null;
                ResultWrapper<FeeHistoryResult> initialCheckResult = InitialChecksPassed(ref blockCount, rewardPercentiles);
                if (initialCheckResult.Result.ResultType == ResultType.Failure)
                {
                    return initialCheckResult;
                }
                
                int maxHistory = 1;
                ResultWrapper<BlockRangeInfo> blockRangeResult = _blockRangeManager.ResolveBlockRange(ref lastBlockNumber, ref blockCount, maxHistory, ref headBlockNumber);
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
                string output = Strings.Join(nullStrings.ToArray(), ", ");
                return output;
            }

            private ResultWrapper<FeeHistoryResult> InitialChecksPassed(ref long blockCount, float[] rewardPercentiles)
            {
                if (blockCount < 1)
                {
                    return ResultWrapper<FeeHistoryResult>.Fail($"blockCount: Block count, {blockCount}, is less than 1.");
                }

                if (blockCount > 1024)
                {
                    blockCount = 1024;
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
                        return ResultWrapper<FeeHistoryResult>.Fail(
                           $"rewardPercentiles: Value at index {firstIndex}: {rewardPercentiles[firstIndex]} is less than " +
                           $"the value at previous index {firstIndex - 1}: {rewardPercentiles[firstIndex - 1]}.");
                    }
                    
                    float[] invalidValues = rewardPercentiles.Select(val => val).Where(val => val < 0 || val > 100)
                        .ToArray();
                    
                    if (invalidValues.Any())
                    {
                        return ResultWrapper<FeeHistoryResult>.Fail(
                            $"rewardPercentiles: Values {String.Join(", ", invalidValues)} are below 0 or greater than 100.");
                    }
                }
                return ResultWrapper<FeeHistoryResult>.Success(new FeeHistoryResult(0,Array.Empty<UInt256[]>(),Array.Empty<UInt256>(),Array.Empty<float>()));
            }

            protected internal ResultWrapper<FeeHistoryResult> FeeHistoryLookup(long blockCount, long lastBlockNumber, float[]? rewardPercentiles = null)
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

            protected internal virtual BlockFeeInfo GetBlockFeeInfo(long blockNumber, float[]? rewardPercentiles, Block? pendingBlock)
            {
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
                    ProcessBlock(ref blockFeeInfo, rewardPercentiles);
                }

                return blockFeeInfo;
            }

            private FeeHistoryResult CreateFeeHistoryResult(List<BlockFeeInfo> blockFeeInfos, long blockCount)
            {
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

            private void ProcessBlock(ref BlockFeeInfo blockFeeInfo, float[]? rewardPercentiles)
            {
                IReleaseSpec london = London.Instance;
                bool isLondonEnabled = blockFeeInfo.BlockNumber >= london.Eip1559TransitionBlock;
                InitializeBlockFeeInfo(blockFeeInfo, isLondonEnabled);
                if (rewardPercentiles == null || rewardPercentiles.Length == 0)
                {
                    return;
                }

                if (blockFeeInfo.Block == null)
                    //put error in log
                {
                    return;
                }

                UInt256[] rewards = new UInt256[rewardPercentiles.Length];
                if (blockFeeInfo.Block.Transactions.Length == 0)
                {
                    for (int i = 0; i < rewardPercentiles.Length; i++)
                    {
                        rewards[i] = 0;
                    }

                    blockFeeInfo.Reward = rewards;
                    return;
                }

                CalculateAndInsertRewards(blockFeeInfo, rewardPercentiles, rewards);
            }

            private void InitializeBlockFeeInfo(BlockFeeInfo blockFeeInfo, bool isLondonEnabled)
            {
                blockFeeInfo.BaseFee = blockFeeInfo.BlockHeader?.BaseFeePerGas ?? 0;
                blockFeeInfo.NextBaseFee = isLondonEnabled
                    ? CalculateNextBaseFee(blockFeeInfo)
                    : 0;
                blockFeeInfo.GasUsedRatio = (float) blockFeeInfo.BlockHeader!.GasUsed / blockFeeInfo.BlockHeader!.GasLimit;
            }

            private static void CalculateAndInsertRewards(BlockFeeInfo blockFeeInfo, float[] rewardPercentiles, UInt256[] rewards)
            {
                GasPriceAndReward[] gasPriceAndRewardArray = CalculateRewards(blockFeeInfo);

                InsertRewardsIntoBlockFeeInfo(blockFeeInfo, rewardPercentiles, rewards, gasPriceAndRewardArray);

                blockFeeInfo.Reward = rewards;
            }

            private static GasPriceAndReward[] CalculateRewards(BlockFeeInfo blockFeeInfo)
            {
                Transaction[] transactionsInBlock = blockFeeInfo.Block.Transactions;
                GasPriceAndReward[] gasPriceAndRewardArray =
                    transactionsInBlock.Select(ConvertTxToGasPriceAndReward(blockFeeInfo)).ToArray();
                gasPriceAndRewardArray.OrderBy(g => g.Reward).ToArray();
                return gasPriceAndRewardArray;
            }

            private static void InsertRewardsIntoBlockFeeInfo(BlockFeeInfo blockFeeInfo, float[] rewardPercentiles, UInt256[] rewards,
                GasPriceAndReward[] gasPriceAndRewardArray)
            {
                int txIndex;
                int gasPriceArrayLength = gasPriceAndRewardArray.Length;
                int rewardsIndex = 0;
                UInt256 totalGasUsed;
                UInt256 thresholdGasUsed;
                foreach (double percentile in rewardPercentiles)
                {
                    totalGasUsed = 0;
                    thresholdGasUsed = (UInt256) ((percentile / 100) * (blockFeeInfo.Block.GasUsed));
                    for (txIndex = 0; totalGasUsed < thresholdGasUsed && txIndex < gasPriceArrayLength; txIndex++)
                    {
                        totalGasUsed += gasPriceAndRewardArray[txIndex].Reward;
                    }

                    rewards[rewardsIndex++] = gasPriceAndRewardArray[txIndex].Reward;
                }
            }

            private static Func<Transaction, GasPriceAndReward> ConvertTxToGasPriceAndReward(BlockFeeInfo blockFeeInfoCopy)
            {
                return tx =>
                {
                    UInt256 gasPrice = tx.GasPrice;
                    UInt256 effectiveGasTip = tx.CalculateEffectiveGasTip((UInt256)blockFeeInfoCopy.BaseFee!);
                    return new GasPriceAndReward(gasPrice, effectiveGasTip);
                };
            }
            private UInt256 CalculateNextBaseFee(BlockFeeInfo blockFeeInfo)
            {
                ulong gasLimit = (ulong) blockFeeInfo.BlockHeader!.GasLimit;
                ulong gasTarget = gasLimit / GasTargetToLimitMultiplier;
                ulong gasUsed = (ulong) blockFeeInfo.BlockHeader!.GasUsed;
                UInt256 currentBaseFee = blockFeeInfo.BlockHeader!.BaseFeePerGas;
                
                if (gasTarget < gasUsed)
                {
                    currentBaseFee *= ((gasUsed - gasTarget) / gasTarget);
                }
                else if (gasTarget > gasUsed)
                {
                    currentBaseFee *= ((gasTarget - gasUsed) / gasTarget);
                }
                return currentBaseFee;
            }

            class GasPriceAndReward
            {
                public UInt256 GasPrice { get; private set; }
                public UInt256 Reward { get; private set; }

                public GasPriceAndReward (UInt256 gasPrice, UInt256 reward)
                {
                    GasPrice = gasPrice;
                    Reward = reward;
                }
            }

            public UInt256 EffectiveGasTip(UInt256 baseFee, Transaction transaction)
            {
                if (baseFee < transaction.MaxFeePerGas)
                    throw new Exception("Base Fee is less than MaxFeePerGas.");
                return UInt256.Min(transaction.MaxFeePerGas - baseFee, transaction.MaxPriorityFeePerGas);
            }
        }
    }
}
