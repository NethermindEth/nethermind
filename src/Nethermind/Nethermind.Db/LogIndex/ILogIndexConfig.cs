// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.TurboPForBindings;

namespace Nethermind.Db.LogIndex;

public interface ILogIndexConfig : IConfig
{
    [ConfigItem(
        Description = "Whether log index should be enabled.",
        DefaultValue = "true"
    )]
    public bool Enabled { get; set; }

    [ConfigItem(
        Description = "Log index is reset on startup if enabled.",
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
        Description = "Maximum number of blocks with receipts to add to index per iteration.",
        DefaultValue = "256",
        HiddenFromDocs = true
    )]
    public int MaxBatchSize { get; set; }

    [ConfigItem(
        Description = "Maximum number of batches to queue for aggregation.",
        DefaultValue = "16",
        HiddenFromDocs = true
    )]
    public int MaxAggregationQueueSize { get; set; }

    [ConfigItem(
        Description = "Maximum number of aggregated batches to queue for inclusion to the index.",
        DefaultValue = "16",
        HiddenFromDocs = true
    )]
    public int MaxSavingQueueSize { get; set; }

    [ConfigItem(
        Description = "Maximum degree of parallelism for fetching receipts.",
        DefaultValue = "Max(ProcessorCount / 2, 1)",
        HiddenFromDocs = true
    )]
    public int MaxReceiptsParallelism { get; set; }

    [ConfigItem(
        Description = "Maximum degree of parallelism for aggregating batches.",
        DefaultValue = "Max(ProcessorCount / 2, 1)",
        HiddenFromDocs = true
    )]
    public int MaxAggregationParallelism { get; set; }

    [ConfigItem(
        Description = "Maximum degree of parallelism for compressing overgrown key values.",
        DefaultValue = "Max(ProcessorCount / 2, 1)",
        HiddenFromDocs = true
    )]
    public int MaxCompressionParallelism { get; set; }

    [ConfigItem(
        Description = "Minimum number of blocks under a single key to compress.",
        DefaultValue = "128",
        HiddenFromDocs = true
    )]
    public int CompressionDistance { get; set; }

    [ConfigItem(
        Description = "Number of newly added blocks after which to run DB compaction.",
        DefaultValue = "262,144",
        HiddenFromDocs = true
    )]
    public int CompactionDistance { get; set; }

    [ConfigItem(
        Description = "Compression algorithm to use for block numbers.",
        DefaultValue = nameof(TurboPFor.p4nd1enc256v32) + " if supported, otherwise " + nameof(TurboPFor.p4nd1enc128v32),
        HiddenFromDocs = true
    )]
    string? CompressionAlgorithm { get; set; }

    [ConfigItem(
        Description = "Whether to show detailed stats in progress logs.",
        DefaultValue = "false",
        HiddenFromDocs = true
    )]
    bool DetailedLogs { get; set; }

    [ConfigItem(
        Description = "Whether to verify that eth_getLogs response generated using index matches one generated without.",
        DefaultValue = "false",
        HiddenFromDocs = true
    )]
    bool VerifyRpcResponse { get; set; }
}
