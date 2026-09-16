// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public interface IFlatDbManager : IFlatCommitTarget
{
    SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage);
    ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(in StateId stateId);

    /// <summary>Drops every snapshot not on the ancestry of <paramref name="head"/> and releases the
    /// bundles cached over them.</summary>
    void ResetHead(in StateId head);
}

// Used by overridable world state env that has its own snapshot repositories.
public interface IFlatCommitTarget
{
    void AddSnapshot(Snapshot snapshot, TransientResource transientResource);
}
