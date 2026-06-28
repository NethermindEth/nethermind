// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Decorates an <see cref="IFlatDbManager"/> to serve reads for blocks below the finalization barrier — whose
/// per-block tip snapshots have been pruned — from the finalized history index. Every historical read path (the
/// state reader, the scope provider, and the override scope that layers its overrides on top of the bundle) funnels
/// through <see cref="GatherReadOnlySnapshotBundle"/> / <see cref="GatherSnapshotBundle"/>
/// </summary>
public sealed class HistoricalFlatDbManager(
    IFlatDbManager inner,
    IPersistenceManager persistenceManager,
    HistoryReader historyReader,
    ITrieNodeCache trieNodeCache,
    IResourcePool resourcePool,
    bool enableDetailedMetrics) : IFlatDbManager, IAsyncDisposable
{
    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => inner.ReorgBoundaryReached += value;
        remove => inner.ReorgBoundaryReached -= value;
    }

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) =>
        IsBelowBarrier(baseBlock)
            ? new SnapshotBundle(BuildHistoricalBundle(baseBlock), trieNodeCache, resourcePool, usage)
            : inner.GatherSnapshotBundle(baseBlock, usage);

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
        IsBelowBarrier(baseBlock)
            ? BuildHistoricalBundle(baseBlock)
            : inner.GatherReadOnlySnapshotBundle(baseBlock);

    public bool HasStateForBlock(in StateId stateId) =>
        IsBelowBarrier(stateId) || inner.HasStateForBlock(stateId);

    public void FlushCache(CancellationToken cancellationToken) => inner.FlushCache(cancellationToken);

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) =>
        inner.AddSnapshot(snapshot, transientResource);

    // The inner manager owns the background tasks; the container only tracks this decorator, so forward disposal.
    public async ValueTask DisposeAsync()
    {
        switch (inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    // Assumes history covers from genesis (the capture-from-genesis model); a partial-archive lower bound is a
    // follow-up.
    private bool IsBelowBarrier(in StateId baseBlock)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        return persisted != StateId.PreGenesis && baseBlock.BlockNumber < persisted.BlockNumber;
    }

    // Trie-less bundle: empty snapshot list over a history-backed reader. The reader serves account/storage values
    // only and throws on trie traversal / iteration, so post-block state-root recomputation must not walk it.
    private ReadOnlySnapshotBundle BuildHistoricalBundle(in StateId baseBlock) =>
        new(new SnapshotPooledList(0),
            new HistoryBackedPersistenceReader(historyReader, baseBlock),
            enableDetailedMetrics,
            isHistorical: true);
}
