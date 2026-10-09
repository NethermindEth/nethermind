// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Threading;

namespace Nethermind.Pbt;

/// <summary>Builds a tree from empty out of a strictly ascending leaf stream, in bounded windows.</summary>
internal static class PbtSortedLeafFold
{
    internal const int DefaultWindowSize = 2_000_000;
    internal const int FoldChunkSize = 2048;

    /// <summary>Folds leaf chunks into a tree from empty, window by window, writing every group through to <paramref name="writeThrough"/>.</summary>
    /// <remarks>Reads are served from the tree's right edge in memory (see <see cref="PbtRightmostGroupStore"/>), never from where the groups were written.</remarks>
    /// <param name="source">Owned leaf chunks, strictly ascending across the whole stream; an out-of-order leaf is not detected and yields a wrong root.</param>
    /// <param name="writeThrough">Receives every group the fold writes, one call at a time.</param>
    /// <param name="windowSize">Maximum leaves per tree update.</param>
    /// <param name="onWindowFolded">Called with the window's leaf count once its fold ends.</param>
    /// <returns>The tree root.</returns>
    internal static async Task<ValueHash256> FoldWindows(ChannelReader<ArrayPoolList<RebuildEntry>> source, IPbtNodeGroupSink writeThrough, int windowSize,
        ConcurrencyController foldQuota, FoldFanOut foldFanOut, Action<int> onWindowFolded, CancellationToken cancellationToken)
    {
        using PbtRightmostGroupStore store = new(writeThrough);
        using PbtPartitionBatchesBuilder changes = new(new(), new(), new());
        ValueHash256 root = default;
        int windowCount = 0;

        await foreach (ArrayPoolList<RebuildEntry> chunk in source.ReadAllAsync(cancellationToken))
        {
            using (chunk)
            {
                foreach (RebuildEntry entry in chunk.AsSpan())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    changes.Set(entry.Key, entry.Leaf);
                    if (++windowCount == windowSize) FoldWindow();
                }
            }
        }

        if (windowCount != 0) FoldWindow();
        return root;

        void FoldWindow()
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (PbtPartitionBatches prepared = changes.Build())
                root = TrieUpdater.UpdateRoot(store, root, prepared, foldQuota, foldFanOut, null);
            store.ReleaseSuperseded();
            changes.Reset();
            onWindowFolded(windowCount);
            windowCount = 0;
        }
    }

    /// <summary>The root of the tree over <paramref name="leaves"/>, folded window by window without storing the tree.</summary>
    /// <remarks>The next window is read and chunked on another thread while the current one folds.</remarks>
    /// <param name="leaves">Strictly ascending account, code and storage leaves; an out-of-order leaf is not detected and yields a wrong root.</param>
    /// <param name="windowSize">Maximum leaves folded per tree update.</param>
    /// <param name="foldConcurrency">Maximum threads, including the calling one, folding a window.</param>
    public static ValueHash256 CalculateRoot(IEnumerable<RebuildEntry> leaves, int windowSize, int foldConcurrency, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<ArrayPoolList<RebuildEntry>> channel = Channel.CreateBounded<ArrayPoolList<RebuildEntry>>(windowSize / FoldChunkSize + 1);
        Task reading = Task.Run(() =>
        {
            try { foreach (RebuildEntry _ in Teed(leaves, channel.Writer, linked.Token)) { } }
            catch (Exception exception) { channel.Writer.TryComplete(exception); throw; }
        }, CancellationToken.None);
        try
        {
            return FoldWindows(channel.Reader, NullNodeGroupSink.Instance, windowSize, new ConcurrencyController(foldConcurrency),
                FoldFanOut.Default, static _ => { }, linked.Token).GetAwaiter().GetResult();
        }
        finally
        {
            linked.Cancel();
            try { reading.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { while (channel.Reader.TryRead(out ArrayPoolList<RebuildEntry>? chunk)) chunk.Dispose(); }
        }
    }

    /// <summary>Passes the leaves through while handing each to the fold, completing the fold's input once they run out.</summary>
    internal static IEnumerable<RebuildEntry> Teed(IEnumerable<RebuildEntry> leaves, ChannelWriter<ArrayPoolList<RebuildEntry>> fold,
        CancellationToken cancellationToken)
    {
        using (EntrySink sink = new(fold, FoldChunkSize, cancellationToken))
        {
            foreach (RebuildEntry entry in leaves)
            {
                sink.Add(entry).AsTask().GetAwaiter().GetResult();
                yield return entry;
            }
            sink.Complete().AsTask().GetAwaiter().GetResult();
        }
        fold.TryComplete();
    }

    /// <summary>Buffers leaves into pooled chunks and hands each full chunk to the fold.</summary>
    internal sealed class EntrySink(ChannelWriter<ArrayPoolList<RebuildEntry>> entries, int chunkSize, CancellationToken cancellationToken) : IDisposable
    {
        private ArrayPoolList<RebuildEntry> _chunk = new(chunkSize);
        private bool _owned = true;

        public ValueTask Add(in RebuildEntry entry)
        {
            _chunk.Add(entry);
            return _chunk.Count >= chunkSize ? Flush() : default;
        }

        public ValueTask Complete() => _chunk.Count > 0 ? Flush() : default;

        // A failed channel write leaves ownership with this sink.
        private async ValueTask Flush()
        {
            await entries.WriteAsync(_chunk, cancellationToken);
            _owned = false;
            _chunk = new ArrayPoolList<RebuildEntry>(chunkSize);
            _owned = true;
        }

        public void Dispose()
        {
            if (_owned) _chunk.Dispose();
        }
    }

    private sealed class NullNodeGroupSink : IPbtNodeGroupSink
    {
        public static readonly NullNodeGroupSink Instance = new();

        public void SetNodeGroup(scoped in PbtTraversalPath groupKey, in ValueHash256 groupHash, RefCountingMemory? payload) { }
    }
}
