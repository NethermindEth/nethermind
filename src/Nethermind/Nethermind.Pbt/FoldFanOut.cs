// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Pbt;

/// <summary>The fewest operations a bucket worker folds, chosen by the stored size below the buckets it takes.</summary>
/// <remarks>
/// Descendants below <see cref="LargeSubtreeBytes"/> are one read and then CPU-bound, so only a wide run pays for
/// fanning out; above it every operation may cost a read, so a few operations already do. The size is each run's
/// own, so a run over buckets with little stored below them stays CPU-bound however large its siblings are.
/// </remarks>
/// <param name="MinOperationsPerWorker">The fewest operations per worker for buckets with less than <paramref name="LargeSubtreeBytes"/> stored below them.</param>
/// <param name="LargeSubtreeBytes">The stored size below a worker's buckets from which its share counts as read-bound.</param>
/// <param name="LargeSubtreeMinOperationsPerWorker">The fewest operations per worker for buckets with at least <paramref name="LargeSubtreeBytes"/> stored below them.</param>
public readonly record struct FoldFanOut(int MinOperationsPerWorker, long LargeSubtreeBytes, int LargeSubtreeMinOperationsPerWorker)
{
    internal const int DefaultMinOperationsPerWorker = 128;
    internal const long DefaultLargeSubtreeBytes = 32 * 1024;
    internal const int DefaultLargeSubtreeMinOperationsPerWorker = 16;

    /// <param name="descendantBytes">The stored size below the buckets the worker would take.</param>
    internal int MinOperationsFor(long descendantBytes) => descendantBytes < LargeSubtreeBytes ? MinOperationsPerWorker : LargeSubtreeMinOperationsPerWorker;

    /// <summary>Groups consecutive buckets into runs, each holding the operations this fan-out asks of the descendants it absorbs.</summary>
    /// <remarks>
    /// A run's minimum follows its own stored descendants rather than the frame's, so a run over buckets with
    /// nothing stored below them stays CPU-bound however large its siblings are. A trailing shortfall joins the
    /// preceding run, so a frame that cannot fill two runs yields one.
    /// </remarks>
    /// <param name="counts">Operation counts per touched bucket, in ascending slot order.</param>
    /// <param name="descendantBytes">The stored size below each of those buckets, in the same order.</param>
    /// <returns>The number of runs; <paramref name="runEnds"/> holds each run's exclusive end bucket index.</returns>
    internal int PlanBucketRuns(ReadOnlySpan<int> counts, ReadOnlySpan<long> descendantBytes, Span<int> runEnds)
    {
        Debug.Assert(counts.Length == descendantBytes.Length, "Every touched bucket carries its stored size.");
        int runCount = 0;
        int sum = 0;
        long bytes = 0;
        for (int bucket = 0; bucket < counts.Length; bucket++)
        {
            sum += counts[bucket];
            bytes += descendantBytes[bucket];
            if (sum < MinOperationsFor(bytes)) continue;
            runEnds[runCount++] = bucket + 1;
            sum = 0;
            bytes = 0;
        }
        if (runCount == 0) runCount = 1;
        runEnds[runCount - 1] = counts.Length;
        return runCount;
    }
}
