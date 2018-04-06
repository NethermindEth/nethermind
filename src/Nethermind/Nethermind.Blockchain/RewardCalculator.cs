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

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain
{
    public class RewardCalculator : IRewardCalculator
    {
        private readonly ISpecProvider _specProvider;

        public RewardCalculator(ISpecProvider specProvider)
        {
            _specProvider = specProvider;
        }

        public Dictionary<Address, BigInteger> CalculateRewards(Block block)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            
            BigInteger blockReward = 5.Ether();
            if (spec.IsEip649Enabled)
            {
                blockReward = 3.Ether();
            }

            BlockHeader blockHeader = block.Header;
            Dictionary<Address, BigInteger> rewards = new Dictionary<Address, BigInteger>();
            rewards[blockHeader.Beneficiary] = blockReward + block.Ommers.Length * blockReward / 32;
            foreach (BlockHeader ommerHeader in block.Ommers)
            {
                if (!rewards.ContainsKey(ommerHeader.Beneficiary))
                {
                    rewards[ommerHeader.Beneficiary] = 0;
                }

                rewards[ommerHeader.Beneficiary] += blockReward + (ommerHeader.Number - blockHeader.Number) * blockReward / 8;
            }

            return rewards;
        }
    }
}