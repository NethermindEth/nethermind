// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>The fewest operations a bucket worker folds, chosen by the stored size below the buckets it takes.</summary>
/// <remarks>
/// Descendants below <see cref="LargeSubtreeBytes"/> are one read and then CPU-bound, so only a wide run pays for
/// fanning out; above it every operation may cost a read, so a few operations already do. The size is each run's
/// own, so a run over buckets with little stored below them stays CPU-bound however large its siblings are.
/// </remarks>
internal readonly record struct FoldFanOut(int MinOperationsPerWorker, long LargeSubtreeBytes, int LargeSubtreeMinOperationsPerWorker)
{
    internal const int DefaultMinOperationsPerWorker = 128;
    internal const long DefaultLargeSubtreeBytes = 32 * 1024;
    internal const int DefaultLargeSubtreeMinOperationsPerWorker = 16;

    /// <param name="descendantBytes">The stored size below the buckets the worker would take.</param>
    internal int MinOperationsFor(long descendantBytes) => descendantBytes < LargeSubtreeBytes ? MinOperationsPerWorker : LargeSubtreeMinOperationsPerWorker;
}
