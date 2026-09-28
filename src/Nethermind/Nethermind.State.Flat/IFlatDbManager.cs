// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat;

public interface IFlatDbManager : IFlatCommitTarget
{
    SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage);
    ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock);

    /// <inheritdoc cref="GatherReadOnlySnapshotBundle(in StateId)"/>
    /// <param name="readerFlags">Forwarded to the persistence reader backing the bundle; implementations may ignore it.</param>
    ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock, ReaderFlags readerFlags) => GatherReadOnlySnapshotBundle(baseBlock);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(in StateId stateId);

    /// <summary>Checks whether state is available for the requested processing usage.</summary>
    /// <remarks>Read-only historical state cannot serve main block processing.</remarks>
    bool HasStateForBlock(in StateId stateId, ResourcePool.Usage usage) => HasStateForBlock(stateId);

    /// <summary>Drops every snapshot not on the ancestry of <paramref name="head"/> and releases the
    /// bundles cached over them.</summary>
    void DropStateNotReachableFrom(in StateId head);
}

// Used by overridable world state env that has its own snapshot repositories.
public interface IFlatCommitTarget
{
    void AddSnapshot(Snapshot snapshot, TransientResource transientResource);
}
