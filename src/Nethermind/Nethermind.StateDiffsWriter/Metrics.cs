// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;

namespace Nethermind.StateDiffsWriter;

/// <summary>
/// Prometheus surface for the v19 sidecar feed plugin. Property names are
/// transformed by <c>MetricsController.BuildGaugeName</c> into snake_case under
/// the canonical <c>nethermind_</c> prefix, so e.g.
/// <see cref="StateDiffsWriterLastBlock"/> becomes
/// <c>nethermind_state_diffs_writer_last_block</c>.
/// </summary>
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

    /// <summary>
    /// Labeled counter for encode/write failures. The <c>reason</c> label replaces
    /// the previous silent log-and-continue path so operators can alert on the
    /// labeled breakdown rather than scraping logs. See
    /// <c>StateDiffsWriterEncodeErrorReasons</c> for the canonical label values.
    /// </summary>
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
}

/// <summary>
/// Canonical <c>reason</c> label values for
/// <see cref="Metrics.StateDiffsWriterEncodeErrorsTotal"/>. Centralising the
/// strings keeps the Prometheus label cardinality bounded and the dashboards
/// stable across plugin revisions.
/// </summary>
public static class StateDiffsWriterEncodeErrorReasons
{
    public const string Compute = "compute";
    public const string Write = "write";
    public const string ParentMissing = "parent_missing";
}
