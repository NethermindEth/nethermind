// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Threading;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.Pbt;

/// <summary>Builds a tree from empty out of a strictly ascending leaf stream, in bounded windows.</summary>
public static class PbtSortedLeafFold
{
    public const int DefaultWindowSize = 2_000_000;

    /// <summary>Folds leaves into a tree from empty, window by window, handing every group record to <paramref name="writeThrough"/>.</summary>
    /// <remarks>Reads are served from the tree's right edge in memory (see <see cref="PbtRightmostGroupStore"/>), never from where the groups were written.</remarks>
    /// <param name="source">Strictly ascending leaves; an out-of-order leaf is not detected and yields a wrong root.</param>
    /// <param name="writeThrough">Receives every group record the fold writes, borrowed for the call, one call at a time.</param>
    /// <param name="windowSize">Maximum leaves per tree update.</param>
    /// <param name="onWindowFolded">Called with the window's leaf count once its fold ends.</param>
    /// <returns>The tree root.</returns>
    public static ValueHash256 FoldWindows(IEnumerator<RebuildEntry> source, Action<PbtGroupRecord> writeThrough, int windowSize,
        ConcurrencyController foldQuota, FoldFanOut foldFanOut, Action<int> onWindowFolded, CancellationToken cancellationToken)
    {
        using PbtRightmostGroupStore store = new(writeThrough);
        using PbtPartitionBatchesBuilder changes = new(new(), new(), new());
        ValueHash256 root = default;
        int windowCount = 0;

        while (source.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RebuildEntry entry = source.Current;
            changes.Set(entry.Key, entry.Leaf);
            if (++windowCount == windowSize) FoldWindow();
        }

        if (windowCount != 0) FoldWindow();
        return root;

        void FoldWindow()
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (PbtPartitionBatches prepared = changes.Build())
                root = TrieUpdater.UpdateRoot(store, root, prepared, foldQuota, foldFanOut, null, PooledRefCountingMemoryProvider.Instance);
            store.ReleaseSuperseded();
            changes.Reset();
            onWindowFolded(windowCount);
            windowCount = 0;
        }
    }

    /// <summary>The root of the tree over <paramref name="leaves"/>, folded window by window without storing the tree.</summary>
    /// <param name="leaves">Strictly ascending account, code and storage leaves; an out-of-order leaf is not detected and yields a wrong root.</param>
    /// <param name="windowSize">Maximum leaves folded per tree update.</param>
    /// <param name="foldConcurrency">Maximum threads, including the calling one, folding a window.</param>
    public static ValueHash256 CalculateRoot(IEnumerable<RebuildEntry> leaves, int windowSize, int foldConcurrency, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        using IEnumerator<RebuildEntry> source = leaves.GetEnumerator();
        return FoldWindows(source, static _ => { }, windowSize, new ConcurrencyController(foldConcurrency),
            FoldFanOut.Default, static _ => { }, cancellationToken);
    }
}
