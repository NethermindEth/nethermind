// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;

namespace Nethermind.Db.LogIndex;

[ConfigCategory(Description = "Configuration of the log index behaviour.")]
public class LogIndexConfig : ILogIndexConfig
{
    public bool Enabled { get; set; } = false;
    public bool Reset { get; set; } = false;

    public int MaxReorgDepth { get; set; } = 64;

    public int MaxBatchSize { get; set; } = 256;
    public int MaxAggregationQueueSize { get; set; } = 16;
    public int MaxSavingQueueSize { get; set; } = 16;

    public int MaxReceiptsParallelism { get; set; } = Math.Max(Environment.ProcessorCount / 2, 1);
    public int MaxAggregationParallelism { get; set; } = Math.Max(Environment.ProcessorCount / 2, 1);
    public int MaxCompressionParallelism { get; set; } = Math.Max(Environment.ProcessorCount / 2, 1);

    public int CompressionDistance { get; set; } = 128;
    public int CompactionDistance { get; set; } = 262_144;

    public string? CompressionAlgorithm { get; set; } = LogIndexStorage.CompressionAlgorithm.Best.Key;

    public bool DetailedLogs { get; set; } = false;
}
