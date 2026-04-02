// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Xdc;

public class AccountRewardResponse
{
    public AccountEpochReward[]? EpochRewards { get; set; }
    public TotalRewards? Total { get; set; }
}
