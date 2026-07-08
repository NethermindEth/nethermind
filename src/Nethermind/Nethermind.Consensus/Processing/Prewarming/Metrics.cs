// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus.Processing.Prewarming;

public static class Metrics
{
    [CounterMetric]
    [Description("Number of blocks whose processing reused caches warmed speculatively from the mempool (handoff).")]
    public static long MempoolPrewarmHandoffs;

    [CounterMetric]
    [Description("Sender groups the reactive prewarmer skipped on a handoff block because they were already warmed speculatively.")]
    public static long MempoolPrewarmSendersSkipped;

    [CounterMetric]
    [Description("Sender groups the reactive prewarmer still warmed on a handoff block (not covered by speculative warming, e.g. builder transactions).")]
    public static long MempoolPrewarmSendersWarmed;

    [CounterMetric]
    [Description("Speculative delta passes executed while warming from the mempool.")]
    public static long MempoolPrewarmDeltaPasses;

    [CounterMetric]
    [Description("Transactions warmed speculatively from the mempool.")]
    public static long MempoolPrewarmTxsWarmed;
}
