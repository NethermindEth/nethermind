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
    [Description("Sender groups on a handoff block that needed no reactive warming because speculative warming already covered every transaction. Counted when warm jobs are formed.")]
    public static long MempoolPrewarmSendersSkipped;

    [CounterMetric]
    [Description("Sender groups on a handoff block not fully covered by speculative warming (e.g. builder transactions), so reactive warm jobs were formed for them. Counted at job formation; a job can still be overtaken or cancelled before it warms.")]
    public static long MempoolPrewarmSendersWarmed;
}
