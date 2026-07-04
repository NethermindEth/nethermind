// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Xdc;

internal sealed class ReadOnlyRewardsStore(RewardsStore inner) : IRewardsStore
{
    public void SaveEpochRewards(ulong epochBlockNumber, BlockReward[] rewards)
    {
    }

    public bool HasEpochRewards(ulong epochBlockNumber) => inner.HasEpochRewards(epochBlockNumber);

    public bool TryGetAccountReward(Address account, ulong epochBlockNumber, out UInt256 reward) =>
        inner.TryGetAccountReward(account, epochBlockNumber, out reward);

    public bool TryGetRetainedRange(out ulong oldestEpochBlockNumber, out ulong newestEpochBlockNumber) =>
        inner.TryGetRetainedRange(out oldestEpochBlockNumber, out newestEpochBlockNumber);
}
