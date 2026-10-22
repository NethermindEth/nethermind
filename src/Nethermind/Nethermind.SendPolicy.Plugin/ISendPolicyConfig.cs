// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.SendPolicy.Plugin;

public interface ISendPolicyConfig : IConfig
{
    [ConfigItem(DefaultValue = "false", Description = "Whether to check transactions submitted through this node's JSON-RPC against the rule file.")]
    bool Enabled { get; set; }

    [ConfigItem(DefaultValue = "null", Description = "The path to the rule file. The file is re-read when it changes. Update it by atomically replacing it with a fully written and closed temporary file in the same directory; in-place writes can expose incomplete rules.")]
    string? RulesPath { get; set; }

    [ConfigItem(DefaultValue = "null", Description = "The path to the journal of transactions submitted by guarded senders. Defaults to the rule file path with `.journal` appended.")]
    string? JournalPath { get; set; }

    [ConfigItem(DefaultValue = "false", Description = "Whether to log a transaction that breaks a rule and submit it anyway.")]
    bool WarnOnly { get; set; }
}
