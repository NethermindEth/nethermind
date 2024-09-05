// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.BlockValidation;

public interface IBlockValidationConfig: IConfig
{
    [ConfigItem(Description = "If set to true, proposer payment is calculated as a balance difference of the fee recipient", DefaultValue = "false")]
    public bool UseBalanceDiffProfit { get; set; }

    [ConfigItem(Description = "If set to true, withdrawals to the fee recipient are excluded from the balance delta", DefaultValue = "false")]
    public bool ExcludeWithdrawals { get; set; }
}
