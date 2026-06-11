// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public BlockReward[] CalculateRewards(Block block) =>
            [new BlockReward(block.Beneficiary ?? throw new InvalidOperationException("Block beneficiary is required to calculate rewards."), 0)];
    }
}
