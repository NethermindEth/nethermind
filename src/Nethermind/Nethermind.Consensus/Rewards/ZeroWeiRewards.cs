// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Rewards
{
    /// <summary>
    /// This class may be used in Hive tests where 0 wei accounts are created for block authors.
    /// </summary>
    public class ZeroWeiRewards : IRewardCalculator
    {
        private ZeroWeiRewards() { }

        public static ZeroWeiRewards Instance { get; } = new();

        public BlockReward[] CalculateRewards(Block block)
        {
            return new[] { new BlockReward(block.Beneficiary, 0) };
        }
    }
}
