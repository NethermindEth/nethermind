// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Rewards
{
    public interface IRewardCalculator
    {
        BlockReward[] CalculateRewards(Block block);
    }
}
