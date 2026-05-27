// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public interface IFlatDbManager : IFlatCommitTarget
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    /// <summary>
    /// Build a write-capable snapshot bundle anchored at <paramref name="baseBlock"/>, or return <c>null</c>
    /// when the state for that block is no longer available (e.g. pruned concurrently).
    /// </summary>
    SnapshotBundle? GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage);

    /// <summary>
    /// Build a read-only snapshot bundle anchored at <paramref name="baseBlock"/>, or return <c>null</c>
    /// when the state for that block is no longer available (e.g. pruned concurrently).
    /// </summary>
    ReadOnlySnapshotBundle? GatherReadOnlySnapshotBundle(in StateId baseBlock);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(in StateId stateId);
}

// Used by overridable world state env that has its own snapshot repositories.
public interface IFlatCommitTarget
{
    void AddSnapshot(Snapshot snapshot, TransientResource transientResource);
}
