// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Rewards
{
    public interface IRewardCalculator
    {
        BlockReward[] CalculateRewards(Block block);

        /// <summary>False when no block with this header is ever rewarded, so work that exists only to apply or trace
        /// rewards can be skipped without calculating them.</summary>
        /// <remarks>Must depend only on the header and configuration, not on state: callers may ask it outside block
        /// processing, from any thread, on an instance whose processing scope has been returned.</remarks>
        bool MayReward(BlockHeader header) => true;
    }
}
