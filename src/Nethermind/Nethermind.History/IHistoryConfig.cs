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

    [ConfigItem(
        Description = "The number of epochs to retain historical blocks and receipts when using 'Rolling' pruning mode. Must be at least the chain's minHistoryRetentionEpochs chainspec parameter, which is the default: 33024 on Mainnet, 14299 on Sepolia.",
        DefaultValue = "null")]
    uint? RetentionEpochs { get; set; }

    [ConfigItem(
        Description = "The number of epochs to retain block access lists (BALs). Must be at least the chain's minBalRetentionEpochs chainspec parameter. Defaults to that minimum, including on archive nodes. Set to 4294967295 to keep all BALs when history pruning is disabled.",
        DefaultValue = "null")]
    uint? BalRetentionEpochs { get; set; }

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
        Description = "Whether to run a one-time, store-wide compaction of the blocks database after pruning reclaims history, reclaiming stale RocksDB blob files that pruning alone leaves behind. Applies only to 'UseAncientBarriers' pruning and is ignored under 'Rolling', whose advancing cutoff re-pins fresh blob files. Expensive: heavy I/O that can take hours on a large database, so it runs only once. Most effective when history slices ('HistorySliceAddresses') retain scattered bodies across the pruned range, leaving partially-referenced blob files a plain compaction cannot reclaim; under a plain contiguous prune the reclamation is marginal.",
        DefaultValue = "false")]
    bool CompactBlocksOnPrune { get; set; }

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
