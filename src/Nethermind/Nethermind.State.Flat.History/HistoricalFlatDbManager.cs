// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Decorates an <see cref="IFlatDbManager"/> to serve reads for blocks below the finalization barrier — whose
/// per-block tip snapshots have been pruned — from the finalized history index. A block at or above the general
/// retention floor gets an unrestricted reader; below the floor, only addresses with a configured slice are
/// served, and with no slices configured the read is refused.
/// </summary>
public sealed class HistoricalFlatDbManager(
    IFlatDbManager inner,
    IPersistenceManager persistenceManager,
    HistoryReader historyReader,
    ITrieNodeCache trieNodeCache,
    IResourcePool resourcePool,
    bool enableDetailedMetrics,
    HistoryScopeGate scopeGate) : IFlatDbManager
{
    private enum HistoricalReadMode
    {
        NotHistorical,
        Normal,
        Restricted,
        Unavailable
    }

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage)
    {
        HistoricalReadMode mode = Classify(baseBlock);
        if (mode == HistoricalReadMode.NotHistorical) return inner.GatherSnapshotBundle(baseBlock, usage);
        if (mode == HistoricalReadMode.Unavailable) ThrowUnavailable(baseBlock);

        // A historical bundle reads values at baseBlock but exposes the current trie; executing main-chain
        // blocks on that mix produces a corrupt state root and cascades into invalid-block deletions.
        if (usage is ResourcePool.Usage.MainBlockProcessing or ResourcePool.Usage.PostMainBlockProcessing)
        {
            throw new InvalidOperationException(
                $"Main block processing requested a writable scope at historical state {baseBlock}; history serves read-only execution.");
        }

        ReadOnlySnapshotBundle bundle = BuildHistoricalBundle(baseBlock, mode);
        try
        {
            return new SnapshotBundle(bundle, trieNodeCache, resourcePool, usage);
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
    {
        HistoricalReadMode mode = Classify(baseBlock);
        if (mode == HistoricalReadMode.NotHistorical) return inner.GatherReadOnlySnapshotBundle(baseBlock);
        if (mode == HistoricalReadMode.Unavailable) ThrowUnavailable(baseBlock);
        return BuildHistoricalBundle(baseBlock, mode);
    }

    public bool HasStateForBlock(in StateId stateId) =>
        Classify(stateId) is HistoricalReadMode.Normal or HistoricalReadMode.Restricted || inner.HasStateForBlock(stateId);

    public void FlushCache(CancellationToken cancellationToken) => inner.FlushCache(cancellationToken);

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) =>
        inner.AddSnapshot(snapshot, transientResource);

    /// <summary>Once-per-call routing: <see cref="HistoricalReadMode.NotHistorical"/> falls through to the
    /// wrapped manager (above the persisted boundary, or covered-but-mismatched — decisions unrelated to the
    /// floor); <see cref="HistoricalReadMode.Unavailable"/> is a below-floor block no slice can serve.</summary>
    private HistoricalReadMode Classify(in StateId baseBlock)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted == StateId.PreGenesis || baseBlock.BlockNumber >= persisted.BlockNumber) return HistoricalReadMode.NotHistorical;

        if (!historyReader.IsCoveredAndRootMatches(baseBlock))
        {
            return historyReader.IsPrunedBelowFloor(baseBlock.BlockNumber)
                ? HistoricalReadMode.Unavailable
                : HistoricalReadMode.NotHistorical;
        }

        if (!historyReader.IsBelowGlobalFloor(baseBlock.BlockNumber)) return HistoricalReadMode.Normal;

        return historyReader.GetSliceScopes().Count > 0 ? HistoricalReadMode.Restricted : HistoricalReadMode.Unavailable;
    }

    private static void ThrowUnavailable(in StateId baseBlock) =>
        throw new StateUnavailableException(
            $"Historical state for block {baseBlock.BlockNumber} has been pruned below the flat history retention floor.");

    // Trie-less bundle: empty snapshot list over a history-backed reader. The reader serves account/storage values
    // only and throws on trie traversal / iteration, so post-block state-root recomputation must not walk it.
    private ReadOnlySnapshotBundle BuildHistoricalBundle(in StateId baseBlock, HistoricalReadMode mode)
    {
        HistoryBackedPersistenceReader reader = new(historyReader, baseBlock, scopeGate, restrictToSlices: mode == HistoricalReadMode.Restricted);
        try
        {
            return new(new SnapshotPooledList(0),
                reader,
                enableDetailedMetrics,
                PersistedSnapshotStack.Empty(enableDetailedMetrics),
                isHistorical: true);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }
}
