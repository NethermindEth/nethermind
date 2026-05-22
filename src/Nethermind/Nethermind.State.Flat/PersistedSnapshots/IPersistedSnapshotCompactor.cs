// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.PersistedSnapshots;

public interface IPersistedSnapshotCompactor
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
}
