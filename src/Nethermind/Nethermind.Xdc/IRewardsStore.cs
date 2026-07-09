// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Consensus.Rewards;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public interface IRewardsStore
{
    void SaveEpochRewards(Hash256 epochBlockHash, BlockReward[] rewards);
    bool HasEpochRewards(Hash256 epochBlockHash);
    bool TryGetAccountReward(Address account, Hash256 epochBlockHash, out UInt256 reward);
}
