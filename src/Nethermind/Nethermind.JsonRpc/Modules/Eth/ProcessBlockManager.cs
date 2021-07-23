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
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule.FeeHistoryManager;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class ProcessBlockManager : IProcessBlockManager
    {
        private readonly ILogger _logger;

        public ProcessBlockManager(ILogger logger)
        {
            _logger = logger;
        }

        public UInt256[]? ProcessBlock(ref BlockFeeInfo blockFeeInfo, double[]? rewardPercentiles)
        {
            bool isLondonEnabled = IsLondonEnabled(blockFeeInfo);
            InitializeBlockFeeInfo(ref blockFeeInfo, isLondonEnabled);
            return ArgumentErrorsExist(blockFeeInfo, rewardPercentiles) ? 
                null :
                ArrayOfRewards(blockFeeInfo, rewardPercentiles!);
        }

        protected virtual bool IsLondonEnabled(BlockFeeInfo blockFeeInfo)
        {
            IReleaseSpec london = London.Instance;
            bool isLondonEnabled = blockFeeInfo.BlockNumber >= london.Eip1559TransitionBlock;
            return isLondonEnabled;
        }

        protected virtual void InitializeBlockFeeInfo(ref BlockFeeInfo blockFeeInfo, bool isLondonEnabled)
        {
            blockFeeInfo.BaseFee = blockFeeInfo.BlockHeader?.BaseFeePerGas ?? 0;
            blockFeeInfo.NextBaseFee = isLondonEnabled
                ? CalculateNextBaseFee(blockFeeInfo)
                : 0;
            blockFeeInfo.GasUsedRatio = (float) blockFeeInfo.BlockHeader!.GasUsed / blockFeeInfo.BlockHeader!.GasLimit;
        }

        protected virtual bool ArgumentErrorsExist(BlockFeeInfo blockFeeInfo, double[]? rewardPercentiles)
        {
            if (rewardPercentiles == null || rewardPercentiles.Length == 0)
            {
                return true;
            }

            if (blockFeeInfo.Block == null)
            {
                if (_logger.IsError)
                {
                    _logger.Error("Block missing when reward percentiles were requested.");
                }

                return true;
            }

            return false;
        }

        protected virtual UInt256[]? ArrayOfRewards(BlockFeeInfo blockFeeInfo, double[] rewardPercentiles)
        {
            if (blockFeeInfo.Block!.Transactions.Length == 0)
            {
                return GetZerosArrayAsLongAsRewardPercentiles(rewardPercentiles);
            }
            else
            {
                return CalculateAndInsertRewards(blockFeeInfo, rewardPercentiles);
            }
        }



        private static UInt256[] GetZerosArrayAsLongAsRewardPercentiles(double[] rewardPercentiles)
        {
            UInt256[] rewards = new UInt256[rewardPercentiles.Length];
            for (int i = 0; i < rewardPercentiles.Length; i++)
            {
                rewards[i] = 0;
            }

            return rewards;
        }

        private static UInt256[]? CalculateAndInsertRewards(BlockFeeInfo blockFeeInfo, double[] rewardPercentiles)
        {
            GasPriceAndReward[] gasPriceAndRewardArray = GetEffectiveGasPriceAndRewards(blockFeeInfo);

            return GetRewardsAtPercentiles(blockFeeInfo, rewardPercentiles, gasPriceAndRewardArray);
        }

        private static GasPriceAndReward[] GetEffectiveGasPriceAndRewards(BlockFeeInfo blockFeeInfo)
        {
            Transaction[] transactionsInBlock = blockFeeInfo.Block!.Transactions;
            GasPriceAndReward[] gasPriceAndRewardArray =
                transactionsInBlock.Select(ConvertTxToGasPriceAndReward(blockFeeInfo)).ToArray<GasPriceAndReward>();
            gasPriceAndRewardArray = gasPriceAndRewardArray.OrderBy(g => g.Reward).ToArray();
            return gasPriceAndRewardArray;
        }

        private static UInt256[] GetRewardsAtPercentiles(BlockFeeInfo blockFeeInfo, double[] rewardPercentiles, GasPriceAndReward[] gasPriceAndRewardArray)
        {
            UInt256[] rewards = new UInt256[rewardPercentiles.Length];
            int txIndex;
            int gasPriceArrayLength = gasPriceAndRewardArray.Length;
            int rewardsIndex = 0;
            UInt256 totalGasUsed;
            UInt256 thresholdGasUsed;
            foreach (double percentile in rewardPercentiles)
            {
                totalGasUsed = 0;
                thresholdGasUsed = (UInt256) ((percentile / 100) * (blockFeeInfo.Block!.GasUsed));
                for (txIndex = 0; totalGasUsed < thresholdGasUsed && txIndex < gasPriceArrayLength; txIndex++)
                {
                    totalGasUsed += gasPriceAndRewardArray[txIndex].Reward;
                }

                rewards[rewardsIndex++] = gasPriceAndRewardArray[txIndex].Reward;
            }

            return rewards;
        }

        private static Func<Transaction, GasPriceAndReward> ConvertTxToGasPriceAndReward(BlockFeeInfo blockFeeInfoCopy)
        {
            return tx =>
            {
                UInt256 gasPrice = tx.GasPrice;
                UInt256 effectiveGasTip = tx.CalculateEffectiveGasTip(blockFeeInfoCopy.BaseFee!);
                return new GasPriceAndReward(gasPrice, effectiveGasTip);
            };
        }

        private UInt256 CalculateNextBaseFee(BlockFeeInfo blockFeeInfo)
        {
            UInt256 gasLimit = (UInt256) blockFeeInfo.BlockHeader!.GasLimit;
            double gasTarget = (double) gasLimit / BlockHeader.GasTargetToLimitMultiplier;
            UInt256 gasTargetLong = (UInt256) gasTarget;
            long gasUsed = blockFeeInfo.BlockHeader!.GasUsed;
            UInt256 currentBaseFee = blockFeeInfo.BlockHeader!.BaseFeePerGas;
                
            if (gasTarget < gasUsed)
            {
                UInt256 baseFeeDelta = (UInt256) (gasUsed - gasTarget);
                baseFeeDelta *= currentBaseFee;
                baseFeeDelta /= gasTargetLong;
                baseFeeDelta = UInt256.Max(baseFeeDelta / BlockFeeInfo.ElasticityMultiplier, UInt256.One);
                currentBaseFee += baseFeeDelta;
            }
            else if (gasTarget > gasUsed)
            {
                UInt256 baseFeeDelta = (UInt256) (gasTarget - gasUsed);
                baseFeeDelta *= currentBaseFee;
                baseFeeDelta /= gasTargetLong;
                baseFeeDelta /= BlockFeeInfo.ElasticityMultiplier;
                currentBaseFee -= baseFeeDelta;
            }
            return currentBaseFee;
        }
    }
}
