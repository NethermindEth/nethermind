// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public interface IRewardsStore
{
    void SaveEpochRewards(Hash256 epochBlockHash, Dictionary<string, Dictionary<string, Dictionary<string, string>>> rewards);
    bool HasEpochRewards(Hash256 epochBlockHash);
    bool TryGetAccountReward(Address account, Hash256 epochBlockHash, out UInt256 reward);
    bool TryGetEpochRewards(Hash256 epochBlockHash, out Dictionary<string, Dictionary<string, Dictionary<string, string>>>? rewards);
}
