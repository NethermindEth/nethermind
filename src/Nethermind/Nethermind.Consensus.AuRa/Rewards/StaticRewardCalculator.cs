// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
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

        public BlockReward[] CalculateRewards(Block block, IBlockTracer tracer) => CalculateRewards(block);

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

            public BlockRewardInfo(long blockNumber, in UInt256 reward)
            {
                BlockNumber = blockNumber;
                Reward = reward;
            }
            long IActivatedAt<long>.Activation => BlockNumber;
        }
    }
}
