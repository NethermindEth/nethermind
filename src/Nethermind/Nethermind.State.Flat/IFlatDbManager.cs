// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public interface IFlatDbManager : IFlatCommitTarget
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage);
    ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock);

    /// <summary>
    /// Like <see cref="GatherReadOnlySnapshotBundle"/> but returns false instead of throwing when the
    /// state was pruned or fell out of the historical serving window. Still throws on timeout.
    /// </summary>
    bool TryGatherReadOnlySnapshotBundle(in StateId baseBlock, [NotNullWhen(true)] out ReadOnlySnapshotBundle? bundle);

    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(in StateId stateId);
}

// Used by overridable world state env that has its own snapshot repositories.
public interface IFlatCommitTarget
{
    void AddSnapshot(Snapshot snapshot, TransientResource transientResource);
}
