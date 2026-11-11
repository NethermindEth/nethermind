// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.PersistedSnapshots;

public interface IPersistedSnapshotCompactor : IAsyncDisposable
{
    /// <summary>
    /// Enqueue a batch of newly-converted persisted-snapshot <see cref="StateId"/>s for
    /// background compaction.
    /// </summary>
    /// <remarks>
    /// Takes ownership of <paramref name="batch"/> and disposes it once the batch has been
    /// processed, rejected during enqueue, or drained on cancellation. Asynchronously awaits a free
    /// slot when the internal queue is full, providing backpressure to the block-processing pipeline
    /// without blocking a thread.
    /// </remarks>
    /// <param name="batch">The converted states to compact; ownership transfers to the compactor.</param>
    /// <param name="persistedBlockNumber">The current persistence point (RocksDB persisted state block).
    /// Compaction windows are clamped to not reach below it — snapshots below are already in RocksDB,
    /// so merging them would be wasted work.</param>
    /// <param name="cancellationToken">Releases the backpressure wait when the producer is shutting down.</param>
    /// <exception cref="ObjectDisposedException">The compactor has been disposed. This check precedes cancellation;
    /// <paramref name="batch"/> is returned to its pool before the exception is propagated.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before or while
    /// waiting to enqueue; <paramref name="batch"/> is returned to its pool before the exception is propagated.</exception>
    ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, ulong persistedBlockNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Compacts, on the calling thread, the persisted base snapshots a start reloaded without their compacted layers:
    /// the tail a clean shutdown converted, and what an earlier run converted but never compacted.
    /// </summary>
    /// <remarks>
    /// For the start only, before any worker or reader uses the persisted tier. Writes the same sub-<c>CompactSize</c>
    /// and <c>CompactSize</c>-wide snapshots as <see cref="EnqueueAsync"/>, and no wider merge. The sub-<c>CompactSize</c>
    /// ones the head reads through get a full-size filter. A window that already has its snapshot is left alone.
    /// </remarks>
    /// <param name="persistedBlockNumber">The RocksDB persisted state block; windows are clamped to it as in
    /// <see cref="EnqueueAsync"/>.</param>
    /// <param name="cancellationToken">Process exit; the snapshots written so far stay.</param>
    /// <returns>The number of compacted snapshots written.</returns>
    int CompactReloaded(ulong persistedBlockNumber, CancellationToken cancellationToken);
}
