// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.Config;

public interface IHistoryConfig : IConfig
{
    bool Enabled { get; }

    [ConfigItem(
        Description = "Pruning mode.",
        DefaultValue = "UseAncientBarriers")]
    PruningModes Pruning { get; set; }

    // For EIP-4444 should be 82125
    [ConfigItem(
        Description = "The number of epochs to retain historical blocks and receipts when using 'Rolling' pruning mode. For mainnet this must be at least 82125.",
        DefaultValue = "82125")]
    long RetentionEpochs { get; set; }
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
