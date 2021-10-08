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
using Nethermind.Consensus.Rewards;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin
{
    public class MergeRewardCalculator : IRewardCalculator
    {
        private readonly IRewardCalculator _beforeTheMerge;
        private readonly IMergeConfig _mergeConfig;

        public MergeRewardCalculator(IRewardCalculator? beforeTheMerge, IMergeConfig? mergeConfig)
        {
            _beforeTheMerge = beforeTheMerge ?? throw new ArgumentNullException(nameof(beforeTheMerge));
            _mergeConfig = mergeConfig ?? throw new ArgumentNullException(nameof(mergeConfig));
        }

        public BlockReward[] CalculateRewards(Block block)
        {
            if (block.TotalDifficulty - block.Difficulty <= _mergeConfig.TerminalTotalDifficulty)
            {
                if (block.TotalDifficulty > _mergeConfig.TerminalTotalDifficulty)
                {
                    return _beforeTheMerge.CalculateRewards(block);
                }
            }

            block.Header.IsPostMerge = true;
            return NoBlockRewards.Instance.CalculateRewards(block);
        }
    }
}
