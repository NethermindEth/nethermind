// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Flashbots;

public class FlashbotsConfig : IFlashbotsConfig
{
    public bool Enabled { get; set; }
    public bool UseBalanceDiffProfit { get; set; } = false;

    public bool ExcludeWithdrawals { get; set; } = false;
}
