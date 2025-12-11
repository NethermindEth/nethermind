// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public interface IFlatDiffRepository
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
    SnapshotBundle? GatherReaderAtBaseBlock(StateId baseBlock, SnapshotBundleUsage usage);
    void AddSnapshot(Snapshot snapshot, CachedResource cachedResource);
    void FlushCache(CancellationToken cancellationToken);
    bool HasStateForBlock(StateId stateId);
    StateId? FindStateIdForStateRoot(Hash256 stateRoot); // Ugh...
    StateId? FindLatestAvailableState();
    IPersistence.IPersistenceReader LeaseReader();

    enum SnapshotBundleUsage
    {
        MainBlockProcessing,
        StateReader,
        ReadOnlyProcessingEnv,
        Compactor,
    }
}
