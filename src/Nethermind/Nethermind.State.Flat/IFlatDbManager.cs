// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public interface IFlatDbManager
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    SnapshotBundle GatherReaderAtBaseBlock(StateId baseBlock, ResourcePool.Usage usage);
    ReadOnlySnapshotBundle GatherReadOnlyReaderAtBaseBlock(StateId baseBlock);
    void AddSnapshot(Snapshot snapshot, TransientResource transientResource);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(StateId stateId);
    IPersistence.IPersistenceReader CreateReader();
}
