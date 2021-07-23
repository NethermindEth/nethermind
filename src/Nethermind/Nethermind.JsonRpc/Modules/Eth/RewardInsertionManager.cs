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

using System.Linq;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Int256;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule.FeeHistoryManager;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class RewardInsertionManager
    {
        private IBlockchainBridge _blockchainBridge;

        public RewardInsertionManager(IBlockchainBridge blockchainBridge)
        {
            _blockchainBridge = blockchainBridge;
        }

        public UInt256[]? CalculateAndInsertRewards(BlockFeeInfo blockFeeInfo, double[] rewardPercentiles)
        {
            GasUsedAndReward[] gasPriceAndRewardArray = GetEffectiveGasPriceAndRewards(blockFeeInfo);

            return GetRewardsAtPercentiles(blockFeeInfo, rewardPercentiles, gasPriceAndRewardArray);
        }

        protected internal virtual GasUsedAndReward[] GetEffectiveGasPriceAndRewards(BlockFeeInfo blockFeeInfo)
        {
            Transaction[] transactionsInBlock = blockFeeInfo.Block!.Transactions;
            GasUsedAndReward[] gasPriceAndRewardArray = GetGasUsedAndRewardArrayFrom(transactionsInBlock, blockFeeInfo);
            gasPriceAndRewardArray = gasPriceAndRewardArray.OrderBy(g => g.Reward).ToArray();
            return gasPriceAndRewardArray;
        }

        private GasUsedAndReward[] GetGasUsedAndRewardArrayFrom(Transaction[] transactions, BlockFeeInfo blockFeeInfo)
        {
            GasUsedAndReward[] gasUsedAndRewardArray = new GasUsedAndReward[transactions.Length];
            int index = 0;
            foreach (Transaction transaction in transactions)
            {
                if (transaction.Hash != null)
                {
                    (TxReceipt Receipt, UInt256? EffectiveGasPrice) txInfo = _blockchainBridge.GetReceiptAndEffectiveGasPrice(transaction.Hash);
                    gasUsedAndRewardArray[index++] = new GasUsedAndReward(txInfo.Receipt.GasUsed, transaction.CalculateEffectiveGasTip(blockFeeInfo.BaseFee));
                }
            }

            return gasUsedAndRewardArray;
        }

        protected internal UInt256[] GetRewardsAtPercentiles(BlockFeeInfo blockFeeInfo, double[] rewardPercentiles, GasUsedAndReward[] gasPriceAndRewardArray)
        {
            UInt256[] rewards = new UInt256[rewardPercentiles.Length];
            int txIndex;
            int gasPriceArrayLength = gasPriceAndRewardArray.Length;
            int rewardsIndex = 0;
            long totalGasUsed;
            long thresholdGasUsed;
            foreach (double percentile in rewardPercentiles)
            {
                totalGasUsed = 0;
                thresholdGasUsed = (long) ((percentile / 100) * (blockFeeInfo.Block!.GasUsed));
                for (txIndex = 0; totalGasUsed < thresholdGasUsed && txIndex < gasPriceArrayLength; txIndex++)
                {
                    totalGasUsed += gasPriceAndRewardArray[txIndex].GasUsed;
                }

                rewards[rewardsIndex++] = gasPriceAndRewardArray[txIndex].Reward;
            }

            return rewards;
        }
    }
}
