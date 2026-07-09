// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.State.Flat;

public interface ISnapshotCompactor
{
    bool DoCompactSnapshot(in StateId stateId);
    SnapshotPooledList GetSnapshotsToCompact(Snapshot snapshot);
    Snapshot CompactSnapshotBundle(SnapshotPooledList snapshots);

    /// <summary>
    /// Builds a new snapshot with the same state ids whose content is the sorted <see cref="MergedSnapshotContent"/>
    /// form of <paramref name="source"/>'s mutable content, so the mutable content can be returned to its pool.
    /// </summary>
    Snapshot ConvertToSorted(Snapshot source);
}
