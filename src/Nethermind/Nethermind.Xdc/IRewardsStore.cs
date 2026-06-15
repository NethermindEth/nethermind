// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public interface IRewardsStore
{
    void SaveEpochRewards(ulong epochBlockNumber, Dictionary<string, Dictionary<string, Dictionary<string, string>>> rewards);
    bool HasEpochRewards(ulong epochBlockNumber);
    bool TryGetAccountReward(Address account, ulong epochBlockNumber, out UInt256 reward);
    bool TryGetEpochRewards(ulong epochBlockNumber, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? rewards);
    bool TryGetRetainedRange(out ulong oldestEpochBlockNumber, out ulong newestEpochBlockNumber);
}
