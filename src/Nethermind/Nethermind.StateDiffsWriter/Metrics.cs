// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;

namespace Nethermind.StateDiffsWriter;

/// <summary>Prometheus metrics for the state-diff writer plugin.</summary>
public static class Metrics
{
    [GaugeMetric]
    [Description("Highest block number written to the BlockDiffs CF")]
    public static long StateDiffsWriterLastBlock { get; set; }

    [GaugeMetric]
    [Description("Difference between chain head and last block written by the diffs writer")]
    public static long StateDiffsWriterHeadLagBlocks { get; set; }

    [ExponentialPowerHistogramMetric(Start = 0.0001, Factor = 2, Count = 16)]
    [Description("Per-block encode + RLP serialisation latency in seconds")]
    public static IMetricObserver StateDiffsWriterEncodeSeconds = NoopMetricObserver.Instance;

    [CounterMetric]
    [Description("Cumulative bytes of BlockDiffRecord payloads written to RocksDB")]
    public static long StateDiffsWriterPayloadBytesTotal { get; set; }

    /// <summary>Encode/write failures labeled by <c>reason</c> (see <see cref="StateDiffsWriterEncodeErrorReasons"/>).</summary>
    [CounterMetric]
    [KeyIsLabel("reason")]
    [Description("BlockDiffRecord encode/write failures, broken down by reason")]
    public static ConcurrentDictionary<string, long> StateDiffsWriterEncodeErrorsTotal { get; } = new();

    [CounterMetric]
    [Description("Total BlockDiffs CF rows removed by the background pruner")]
    public static long StateDiffsWriterPrunerRowsRemovedTotal { get; set; }

    [CounterMetric]
    [Description("Total blocks for which a BlockDiffRecord was successfully written")]
    public static long StateDiffsWriterBlocksWrittenTotal { get; set; }

    [CounterMetric]
    [Description("New-head events that did not build on the last-written block (reorg or non-contiguous jump)")]
    public static long StateDiffsWriterReorgsTotal { get; set; }
}

/// <summary>Canonical <c>reason</c> label values for <see cref="Metrics.StateDiffsWriterEncodeErrorsTotal"/>.</summary>
public static class StateDiffsWriterEncodeErrorReasons
{
    public const string Compute = "compute";
    public const string Write = "write";
    public const string ParentMissing = "parent_missing";
    public const string CodeMissing = "code_missing";
}
