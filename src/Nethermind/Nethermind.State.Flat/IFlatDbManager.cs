// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public interface IFlatDbManager : IFlatCommitTarget
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage);
    ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(in StateId stateId);
}

// Used by overridable world state env that has its own snapshot repositories.
public interface IFlatCommitTarget
{
    void AddSnapshot(Snapshot snapshot, TransientResource transientResource);
}
