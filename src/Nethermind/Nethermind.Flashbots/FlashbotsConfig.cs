// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Flashbots;

public class FlashbotsConfig : IFlashbotsConfig
{
    public bool Enabled { get; set; } = false;
    public bool UseBalanceDiffProfit { get; set; } = false;

    public bool ExcludeWithdrawals { get; set; } = false;

    public bool EnablePreWarmer { get; set; } = true;
    public bool EnableValidation { get; set; } = false;

    public int? FlashbotsModuleConcurrentInstances { get; set; } = null;
}
