// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;

namespace Nethermind.Xdc;

/// <summary>
/// Calculates epoch rewards on demand for a given epoch checkpoint block.
/// Unlike <see cref="IRewardCalculator"/>, this interface is safe to call outside
/// block-processing context and does not mutate state.
/// </summary>
public interface IXdcRewardCalculator
{
    BlockReward[] CalculateEpochRewards(XdcBlockHeader epochHeader);
}
