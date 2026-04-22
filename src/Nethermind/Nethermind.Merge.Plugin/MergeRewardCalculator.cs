// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin
{
    public class MergeRewardCalculator(IRewardCalculator? beforeTheMerge, IPoSSwitcher poSSwitcher) : IRewardCalculator
    {
        private readonly IRewardCalculator _beforeTheMerge = beforeTheMerge ?? throw new ArgumentNullException(nameof(beforeTheMerge));
        private readonly IPoSSwitcher _poSSwitcher = poSSwitcher ?? throw new ArgumentNullException(nameof(poSSwitcher));

        public BlockReward[] CalculateRewards(Block block)
        {
            if (_poSSwitcher.IsPostMerge(block.Header))
            {
                return NoBlockRewards.Instance.CalculateRewards(block);
            }

            return _beforeTheMerge.CalculateRewards(block);
        }
    }
}
