// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.PersistedSnapshots;

public interface IPersistedSnapshotCompactor : IAsyncDisposable
{
    /// <summary>
    /// Compact the persisted snapshots ending at <paramref name="state"/> over the block's
    /// natural power-of-2 window. Produces sub-<c>CompactSize</c> intermediates and the
    /// <c>&gt;CompactSize</c> hierarchical merges; the <c>CompactSize</c>-wide window is
    /// reserved for <see cref="DoCompactPersistable"/>.
    /// </summary>
    void DoCompactSnapshot(StateId state);

    /// <summary>
    /// Produce the <c>CompactSize</c>-wide persistable snapshot ending at the boundary
    /// block <paramref name="state"/> — the snapshot <c>PersistenceManager</c> writes to RocksDB.
    /// </summary>
    void DoCompactPersistable(StateId state);

    /// <summary>
    /// Enqueue a batch of newly-converted persisted-snapshot <see cref="StateId"/>s for
    /// background compaction.
    /// </summary>
    /// <remarks>
    /// Takes ownership of <paramref name="batch"/> and disposes it once the batch has been
    /// processed (or drained on cancellation). Blocks the caller when the internal queue is
    /// full — the same backpressure that throttles the block-processing thread today.
    /// </remarks>
    /// <param name="batch">The converted states to compact; ownership transfers to the compactor.</param>
    void Enqueue(ArrayPoolList<StateId> batch);
}
