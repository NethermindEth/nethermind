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
    /// processed (or drained on cancellation). Asynchronously awaits a free slot when the internal
    /// queue is full, providing backpressure to the block-processing pipeline without blocking a
    /// thread.
    /// </remarks>
    /// <param name="batch">The converted states to compact; ownership transfers to the compactor.</param>
    /// <param name="persistedBlockNumber">The current persistence point (RocksDB persisted state block).
    /// Compaction windows are clamped to not reach below it — snapshots below are already in RocksDB,
    /// so merging them would be wasted work.</param>
    /// <param name="cancellationToken">Releases the backpressure wait when the producer is shutting down.</param>
    ValueTask EnqueueAsync(ArrayPoolList<StateId> batch, long persistedBlockNumber, CancellationToken cancellationToken);
}
