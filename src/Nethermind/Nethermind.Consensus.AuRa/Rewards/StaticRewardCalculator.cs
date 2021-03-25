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

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Rewards;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Rewards
{
    public class StaticRewardCalculator : IRewardCalculator
    {
        private readonly IList<BlockRewardInfo> _blockRewards;

        public StaticRewardCalculator(IDictionary<long, UInt256>? blockRewards)
        {
            _blockRewards = CreateBlockRewards(blockRewards);
        }

        public BlockReward[] CalculateRewards(Block block)
        {
            _blockRewards.TryGetForActivation(block.Number, out var blockReward);
            return new[] { new BlockReward(block.Beneficiary, blockReward.Reward) };
        }

        private IList<BlockRewardInfo> CreateBlockRewards(IDictionary<long, UInt256>? blockRewards)
        {
            List<BlockRewardInfo> blockRewardInfos = new();
            if (blockRewards?.Count > 0)
            {
                if (blockRewards.First().Key > 0)
                {
                    blockRewardInfos.Add(new BlockRewardInfo(0, 0));
                }
                foreach (var threshold in blockRewards)
                {
                    blockRewardInfos.Add(new BlockRewardInfo(threshold.Key, threshold.Value));
                }
            }
            else
            {
                blockRewardInfos.Add(new BlockRewardInfo(0, 0));
            }
            return blockRewardInfos;
        }
        
        private class BlockRewardInfo : IActivatedAt
        {
            public long BlockNumber { get; }
            public UInt256 Reward { get; }

            public BlockRewardInfo(long blockNumber, UInt256 reward)
            {
                BlockNumber = blockNumber;
                Reward = reward;
            }
            long IActivatedAt<long>.Activation => BlockNumber;
        }
    }
}
