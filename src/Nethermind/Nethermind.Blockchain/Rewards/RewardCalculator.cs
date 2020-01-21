//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Rewards
{
    public class RewardCalculator : IRewardCalculator
    {
        private readonly ISpecProvider _specProvider;

        public RewardCalculator(ISpecProvider specProvider)
        {
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        }

        [Todo(Improve.MissingFunctionality, "Use ChainSpec for block rewards")]
        private UInt256 GetBlockReward(Block block)
        {
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            return spec.IsEip649Enabled ? spec.IsEip1234Enabled ? 2.Ether() : 3.Ether() : 5.Ether();
        }
        
        public BlockReward[] CalculateRewards(Block block)
        {
            UInt256 blockReward = GetBlockReward(block);
            BlockReward[] rewards = new BlockReward[1 + block.Ommers.Length];

            BlockHeader blockHeader = block.Header;
            BigInteger mainReward = blockReward + (uint) block.Ommers.Length * (blockReward >> 5);
            rewards[0] = new BlockReward(blockHeader.Beneficiary, mainReward);

            for (int i = 0; i < block.Ommers.Length; i++)
            {
                BigInteger ommerReward = GetOmmerReward(blockReward, blockHeader, block.Ommers[i]);
                rewards[i + 1] = new BlockReward(block.Ommers[i].Beneficiary, ommerReward, BlockRewardType.Uncle);
            }

            return rewards;
        }

        private UInt256 GetOmmerReward(UInt256 blockReward, BlockHeader blockHeader, BlockHeader ommer)
        {
            return blockReward - ((uint) (blockHeader.Number - ommer.Number) * blockReward >> 3);
        }
    }
}