// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public interface IFlatDbManager : IFlatCommitTarget
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage);
    ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(in StateId stateId);

    /// <summary>
    /// Deferred state-root materialization window size (<c>FlatDb.CommitBatchSize</c>). 1 (default) means
    /// the trie is materialized and its root verified every block — the current behavior.
    /// </summary>
    ulong CommitBatchSize => 1;

    /// <summary>
    /// True when <paramref name="blockNumber"/> is a materialization boundary for the configured
    /// <see cref="CommitBatchSize"/> (always true when it is 1). At a boundary the window's dirty union is
    /// materialized into the trie and the recomputed root is verified against the block header.
    /// </summary>
    bool IsMaterializationBoundary(ulong blockNumber) => true;

    /// <summary>Last state whose full trie was materialized (block + real root), or <c>null</c> if none yet.</summary>
    StateId? GetLastMaterializedStateId() => null;

    /// <summary>Records the last materialized state; called by the scope at a boundary commit.</summary>
    void SetLastMaterializedStateId(in StateId stateId) { }
}

// Used by overridable world state env that has its own snapshot repositories.
public interface IFlatCommitTarget
{
    void AddSnapshot(Snapshot snapshot, TransientResource transientResource);
}
