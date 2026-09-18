// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>The fewest operations a bucket worker folds, chosen by the stored size of the frame's subtree.</summary>
/// <remarks>
/// A subtree below <see cref="LargeSubtreeBytes"/> is one read and then CPU-bound, so only a wide frame pays for
/// fanning out; above it every operation may cost a read, so a few operations already do.
/// </remarks>
internal readonly record struct FoldFanOut(int MinOperationsPerWorker, long LargeSubtreeBytes, int LargeSubtreeMinOperationsPerWorker)
{
    internal const int DefaultMinOperationsPerWorker = 128;
    internal const long DefaultLargeSubtreeBytes = 32 * 1024;
    internal const int DefaultLargeSubtreeMinOperationsPerWorker = 16;
    internal static readonly FoldFanOut Default = new(DefaultMinOperationsPerWorker, DefaultLargeSubtreeBytes, DefaultLargeSubtreeMinOperationsPerWorker);

    internal int MinOperationsFor(long subtreeBytes) => subtreeBytes < LargeSubtreeBytes ? MinOperationsPerWorker : LargeSubtreeMinOperationsPerWorker;
}
