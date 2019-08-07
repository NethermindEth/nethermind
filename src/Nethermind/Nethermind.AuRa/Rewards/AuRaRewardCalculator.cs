/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Blockchain;
using Nethermind.Blockchain.Rewards;
using Nethermind.Core;
using Nethermind.Core.Specs.ChainSpecStyle;

namespace Nethermind.AuRa.Rewards
{
    public class AuRaRewardCalculator : IRewardCalculator
    {
        private readonly long _blockRewardContractTransition;
        private Address _blockRewardContractAddress;
        private readonly StaticRewardCalculator _blockRewardCalculator;

        public AuRaRewardCalculator(AuRaParameters auRaParameters)
        {
            _blockRewardCalculator = new StaticRewardCalculator(auRaParameters.BlockReward);
            _blockRewardContractTransition = auRaParameters.BlockRewardContractTransition;
            _blockRewardContractAddress = auRaParameters.BlockRewardContractAddress;
        }
        
        public BlockReward[] CalculateRewards(Block block)
        {
            if (block.Number < _blockRewardContractTransition)
            {
                return _blockRewardCalculator.CalculateRewards(block);
            }
            else
            {
                // TODO: Use RewardContract
                return null;
            }
        }
    }
}