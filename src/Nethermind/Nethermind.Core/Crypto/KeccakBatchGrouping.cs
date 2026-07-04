// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Crypto;

/// <summary>
/// Groups a keccak batch by the number of sponge blocks each message absorbs, so vertical-SIMD and GPU backends can
/// form uniform-length lanes and minimize warp/lane divergence.
/// </summary>
/// <remarks>
/// Shared, stable API consumed by later batch backends (the vertical multi-buffer kernel and the GPU kernel). All
/// results are written into caller-provided spans, sized via <see cref="MaxGroups"/> and the batch message count; the
/// only internal working memory is small sort scratch, stack-allocated for small batches and heap-allocated above an
/// internal threshold. Offsets follow the <see cref="IKeccakBatchHasher"/> convention: <c>offsets[i]</c> is the
/// exclusive end of message <c>i</c>; message <c>i</c> starts at <c>offsets[i-1]</c>, and the first message starts at 0.
/// </remarks>
public static class KeccakBatchGrouping
{
    /// <summary>Keccak256 sponge rate in bytes; a message absorbs one block per <see cref="Rate"/> bytes plus one for padding.</summary>
    public const int Rate = 136;

    /// <summary>Returns the number of <see cref="Rate"/>-byte blocks a message of the given length absorbs after padding.</summary>
    /// <param name="messageLength">Unpadded message length in bytes; must be non-negative.</param>
    /// <returns><c>ceil((messageLength + 1) / 136)</c>, i.e. <c>messageLength / 136 + 1</c> - padding always adds a final block.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="messageLength"/> is negative.</exception>
    public static int BlockCount(int messageLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageLength);
        return messageLength / Rate + 1;
    }

    /// <summary>Computes each message's block count, in the batch's original order.</summary>
    /// <param name="offsets">Exclusive end offsets, one per message (see the type remarks for the convention).</param>
    /// <param name="blockCounts">Receives <see cref="BlockCount"/> of message <c>i</c> at index <c>i</c>; must be at least as long as <paramref name="offsets"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="blockCounts"/> is shorter than <paramref name="offsets"/>, or <paramref name="offsets"/> is non-monotonic.</exception>
    public static void ComputeBlockCounts(ReadOnlySpan<int> offsets, Span<int> blockCounts)
    {
        if (blockCounts.Length < offsets.Length) ThrowOutputTooShort();

        int start = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            int end = offsets[i];
            if (end < start) ThrowNonMonotonic();
            blockCounts[i] = BlockCount(end - start);
            start = end;
        }
    }

    /// <summary>Upper bound on the number of uniform-block-count groups a batch of <paramref name="messageCount"/> messages can produce.</summary>
    /// <remarks>Every message could have a distinct block count, so the safe caller-buffer size for group boundaries equals the message count.</remarks>
    public static int MaxGroups(int messageCount) => messageCount;

    /// <summary>
    /// Produces a stable permutation of message indices sorted ascending by block count, and the boundaries of the
    /// resulting uniform-block-count runs.
    /// </summary>
    /// <param name="offsets">Exclusive end offsets, one per message (see the type remarks for the convention).</param>
    /// <param name="permutation">
    /// Receives message indices in block-count order; equal-block-count messages keep their original relative order
    /// (stable). Must be at least as long as <paramref name="offsets"/>.
    /// </param>
    /// <param name="groupBoundaries">
    /// Receives the exclusive end index (in <paramref name="permutation"/> space) of each group; group <c>g</c> spans
    /// <c>[g == 0 ? 0 : groupBoundaries[g-1], groupBoundaries[g])</c> and its messages all share one block count. Must be
    /// at least <see cref="MaxGroups"/> long.
    /// </param>
    /// <returns>The number of groups written to <paramref name="groupBoundaries"/> (0 for an empty batch).</returns>
    /// <exception cref="ArgumentException">A caller buffer is too short, or <paramref name="offsets"/> is non-monotonic.</exception>
    public static int GroupByBlockCount(ReadOnlySpan<int> offsets, Span<int> permutation, Span<int> groupBoundaries)
    {
        int n = offsets.Length;
        if (permutation.Length < n) ThrowOutputTooShort();
        if (groupBoundaries.Length < MaxGroups(n)) ThrowOutputTooShort();
        if (n == 0) return 0;

        // Separate scratch (never aliasing permutation): the scatter below reads block counts while writing permutation.
        Span<int> blockCounts = n <= StackThreshold ? stackalloc int[n] : new int[n];
        ComputeBlockCounts(offsets, blockCounts);

        // Counting sort keeps equal keys stable and avoids a comparator allocation; block counts are small and dense.
        int maxBlockCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (blockCounts[i] > maxBlockCount) maxBlockCount = blockCounts[i];
        }

        // counts[b] tallies messages with block count b (b in 1..maxBlockCount); index 0 is unused (no message is 0 blocks).
        Span<int> counts = maxBlockCount + 1 <= StackThreshold
            ? stackalloc int[maxBlockCount + 1]
            : new int[maxBlockCount + 1];
        counts.Clear();
        for (int i = 0; i < n; i++)
        {
            counts[blockCounts[i]]++;
        }

        // Prefix-sum counts into group start positions, and emit one boundary per non-empty block count.
        Span<int> groupStart = maxBlockCount + 1 <= StackThreshold
            ? stackalloc int[maxBlockCount + 1]
            : new int[maxBlockCount + 1];
        int running = 0;
        int groupCount = 0;
        for (int b = 1; b <= maxBlockCount; b++)
        {
            groupStart[b] = running;
            running += counts[b];
            if (counts[b] > 0)
            {
                groupBoundaries[groupCount++] = running;
            }
        }

        // Scatter original indices into sorted positions; scanning ascending with a per-key cursor is stable within a group.
        Span<int> cursor = groupStart;
        for (int i = 0; i < n; i++)
        {
            permutation[cursor[blockCounts[i]]++] = i;
        }

        return groupCount;
    }

    // Block counts and their tally arrays are tiny for realistic batches; stackalloc below this width, heap above it.
    private const int StackThreshold = 256;

    private static void ThrowOutputTooShort() =>
        throw new ArgumentException("Output span is shorter than the batch requires.");

    private static void ThrowNonMonotonic() =>
        throw new ArgumentException("offsets must be non-decreasing.");
}
