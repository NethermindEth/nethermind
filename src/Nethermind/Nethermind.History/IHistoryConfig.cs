// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Config;

namespace Nethermind.History;

public interface IHistoryConfig : IConfig
{
    [ConfigItem(
        Description = "Pruning mode.",
        DefaultValue = "Disabled")]
    PruningModes Pruning { get; set; }

    // For EIP-4444 should be 82125
    [ConfigItem(
        Description = "The number of epochs to retain historical blocks and receipts when using 'Rolling' pruning mode. For mainnet this must be at least 82125.",
        DefaultValue = "82125")]
    uint RetentionEpochs { get; set; }

    // Default matches the mainnet weak-subjectivity period — BALs older than that are not useful for state reconstruction during weak-subjectivity sync
    [ConfigItem(
        Description = "The number of epochs to retain block access lists (BALs).",
        DefaultValue = "3533",
        HiddenFromDocs = true)]
    uint BalRetentionEpochs { get; set; }

    // Set to 0 to prune every slot
    [ConfigItem(
        Description = "Number of epochs to wait between each history pruning.",
        DefaultValue = "8")]
    ulong PruningInterval { get; set; }

    [ConfigItem(
        Description = "Maximum time in seconds allowed for a single history pruning pass. Set to 0 to disable the timeout.",
        DefaultValue = "2")]
    uint PruningTimeoutSeconds { get; set; }

    [ConfigItem(
        Description = "Whether to allow retention windows below the chain's required minimum (EIP-4444). WARNING: nodes retaining less history than the network minimum may fail to serve peers and violate protocol expectations; intended for dedicated RPC nodes such as rolling partial archives.",
        DefaultValue = "false")]
    bool AllowBelowMinRetention { get; set; }

    // This member needs to be a method instead of a property
    // not to be picked up by the configuration handler
    bool Enabled() => Pruning != PruningModes.Disabled;
}

public enum PruningModes
{
    [Description("No history pruning.")]
    Disabled,

    [Description("Prune outside of rolling window.")]
    Rolling,

    [Description("Prune up to ancient barriers.")]
    UseAncientBarriers,
}
