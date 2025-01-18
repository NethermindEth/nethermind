// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Flashbots;

public interface IFlashbotsConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the Flashbots endpoints.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "If set to true, proposer payment is calculated as a balance difference of the fee recipient", DefaultValue = "false")]
    public bool UseBalanceDiffProfit { get; set; }

    [ConfigItem(Description = "If set to true, withdrawals to the fee recipient are excluded from the balance delta", DefaultValue = "false")]
    public bool ExcludeWithdrawals { get; set; }

    [ConfigItem(Description = "If set to true, the pre-warmer will be enabled", DefaultValue = "true")]
    public bool EnablePreWarmer { get; set; }

    [ConfigItem(Description = "If set to true, the validation will be enabled", DefaultValue = "false")]
    public bool EnableValidation { get; set; }

    [ConfigItem(
    Description = """
        The number of concurrent instances for non-sharable calls:
        - `flashbots_validateBuilderSubmissionV3`
        This limits the load on the CPU and I/O to reasonable levels. If the limit is exceeded, HTTP 503 is returned along with the JSON-RPC error. Defaults to the number of logical processors.
        """)]
    int? FlashbotsModuleConcurrentInstances { get; set; }
}
