// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Db;

public interface ILogIndexConfig : IConfig
{
    [ConfigItem(
        Description = "Whether log index should be enabled.",
        DefaultValue = "true"
    )]
    public bool Enabled { get; set; }

    [ConfigItem(
        Description = "Reset log index on startup if enabled.",
        DefaultValue = "false"
    )]
    public bool Reset { get; set; }

    [ConfigItem(
        Description = "Max allowed reorg depth for the index.",
        DefaultValue = "64",
        HiddenFromDocs = true
    )]
    public int MaxReorgDepth { get; set; } // TODO: take from chain config?

    [ConfigItem(
        Description = "Log index sync: maximum blocks batch size.",
        DefaultValue = "256",
        HiddenFromDocs = true
    )]
    public int SyncBatchSize { get; set; }

    [ConfigItem(
        Description = "Log index sync: maximum size of queue of blocks for aggregating.",
        DefaultValue = "16",
        HiddenFromDocs = true
    )]
    public int SyncAggregateBatchQueueSize { get; set; }

    [ConfigItem(
        Description = "Log index sync: maximum size of queue of batches for adding to storage.",
        DefaultValue = "16",
        HiddenFromDocs = true
    )]
    public int SyncSaveBatchQueueSize { get; set; }

    [ConfigItem(
        Description = "Log index sync: degree of parallelism for fetching receipts.",
        DefaultValue = "16",
        HiddenFromDocs = true
    )]
    public int SyncFetchBatchParallelism { get; set; }

    [ConfigItem(
        Description = "Log index sync: degree of parallelism for aggregating block batches.",
        DefaultValue = "Max(ProcessorCount / 2, 1)",
        HiddenFromDocs = true
    )]
    public int SyncAggregateParallelism { get; set; }

    [ConfigItem(
        Description = "Log index sync: degree of parallelism for compressing sequential block numbers.",
        DefaultValue = "Max(ProcessorCount / 2, 1)",
        HiddenFromDocs = true
    )]
    public int CompressionParallelism { get; set; }

    [ConfigItem(
        Description = "Log index sync: minimum number of blocks in a single value to compress.",
        DefaultValue = "128",
        HiddenFromDocs = true
    )]
    public int CompressionDistance { get; set; }

    [ConfigItem(
        Description = "Log index sync: number of newly added blocks after which to run compaction.",
        DefaultValue = "262,144",
        HiddenFromDocs = true
    )]
    public int CompactionDistance { get; set; }
}
