// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.StateDiffArchive;

/// <summary>Prometheus metrics for the state-diff archive plugin.</summary>
public static class Metrics
{
    [CounterMetric]
    [Description("Total blocks for which a state-diff record was written")]
    public static long BlocksRecorded { get; set; }

    [GaugeMetric]
    [Description("Highest block number recorded to the state-diff archive")]
    public static long LastRecordedBlock { get; set; }

    [CounterMetric]
    [Description("Total blocks replayed from the state-diff archive without the EVM")]
    public static long BlocksReplayed { get; set; }

    [GaugeMetric]
    [Description("Highest block number replayed from the state-diff archive")]
    public static long LastReplayedBlock { get; set; }
}
