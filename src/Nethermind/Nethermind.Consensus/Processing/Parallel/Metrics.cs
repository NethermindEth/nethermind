// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus.Processing.Parallel;

public static class Metrics
{
    [CounterMetric]
    [Description("Total number of parallel state diff merge attempts.")]
    public static long ParallelStateDiffMergeAttempts;

    [CounterMetric]
    [Description("Total number of parallel state diff merge conflicts (fell back to re-execution).")]
    public static long ParallelStateDiffMergeConflicts;
}
