// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Consensus.Rewards;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public interface IRewardsStore
{
    void SaveEpochRewards(ulong epochBlockNumber, BlockReward[] rewards);
    bool HasEpochRewards(ulong epochBlockNumber);
    bool TryGetAccountReward(Address account, ulong epochBlockNumber, out UInt256 reward);
    bool TryGetRetainedRange(out ulong oldestEpochBlockNumber, out ulong newestEpochBlockNumber);
}
