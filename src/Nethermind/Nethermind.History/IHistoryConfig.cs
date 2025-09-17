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
    long RetentionEpochs { get; set; }

    // This member needs to be a method instead of a property
    // not to be picked up by the configuration handler
    public bool Enabled() => Pruning != PruningModes.Disabled;
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
