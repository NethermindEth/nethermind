// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.StateDiffsWriter;

[ConfigCategory(Description = "State diffs writer plugin")]
public interface IStateDiffsWriterConfig : IConfig
{
    [ConfigItem(Description = "Enable the per-block state-diff writer.",
        DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Number of most-recent blocks to retain in the BlockDiffs column family. " +
                              "Older entries are pruned by the background pruner. A consumer's catch-up " +
                              "should never need a window larger than this; lower for tighter disk " +
                              "budgets, raise if catch-up windows can exceed the default. Must be >= 0 " +
                              "(0 keeps nothing); a negative value disables pruning entirely.",
        DefaultValue = "1000000")]
    long KeepLastNBlocks { get; set; }

    [ConfigItem(Description = "Interval in seconds between background pruner sweeps. The pruner " +
                              "removes BlockDiffs rows whose block number is older than " +
                              "(currentHead - KeepLastNBlocks). 0 or negative disables pruning.",
        DefaultValue = "600")]
    int PruneIntervalSeconds { get; set; }
}
