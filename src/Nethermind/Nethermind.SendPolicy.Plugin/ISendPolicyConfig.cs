// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.SendPolicy.Plugin;

public interface ISendPolicyConfig : IConfig
{
    [ConfigItem(DefaultValue = "false", Description = "Whether to check transactions submitted through this node's JSON-RPC against the rule file.")]
    bool Enabled { get; set; }

    [ConfigItem(DefaultValue = "null", Description = "The path to the rule file. The file is re-read when it changes.")]
    string? RulesPath { get; set; }

    [ConfigItem(DefaultValue = "false", Description = "Whether to log a transaction that breaks a rule and submit it anyway.")]
    bool WarnOnly { get; set; }
}
