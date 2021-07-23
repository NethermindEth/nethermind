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

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule.FeeHistoryManager;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class ProcessBlockManager : IProcessBlockManager
    {
        private readonly ILogger _logger;
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly RewardInsertionManager _rewardInsertionManager;

        public ProcessBlockManager(ILogger logger, IBlockchainBridge blockchainBridge)
        {
            _logger = logger;
            _blockchainBridge = blockchainBridge;
            _rewardInsertionManager = new RewardInsertionManager(_blockchainBridge);
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
                return _rewardInsertionManager.CalculateAndInsertRewards(blockFeeInfo, rewardPercentiles);
            }
        }



        private UInt256[] GetZerosArrayAsLongAsRewardPercentiles(double[] rewardPercentiles)
        {
            UInt256[] rewards = new UInt256[rewardPercentiles.Length];
            for (int i = 0; i < rewardPercentiles.Length; i++)
            {
                rewards[i] = 0;
            }

            return rewards;
        }

        protected virtual GasUsedAndReward[] GetEffectiveGasPriceAndRewards(BlockFeeInfo blockFeeInfo)
        {
            return _rewardInsertionManager.GetEffectiveGasPriceAndRewards(blockFeeInfo);
        }
    }
}
