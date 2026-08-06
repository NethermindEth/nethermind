// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Decorates an <see cref="IFlatDbManager"/> to serve reads for blocks below the finalization barrier — whose
/// per-block tip snapshots have been pruned — from the finalized history index, via
/// <see cref="GatherReadOnlySnapshotBundle"/> / <see cref="GatherSnapshotBundle"/>.
/// </summary>
/// <remarks>
/// Two-tier bundle mode, decided once per call (never per read): a block at or above the general retention floor
/// gets a <see cref="HistoryBackedPersistenceReader"/> (Normal) with zero scope-awareness overhead, unchanged from
/// before per-contract slices existed. A block below the general floor with no slices configured is refused
/// exactly as before. A block below the general floor with slices configured gets a
/// <see cref="RestrictedHistoryBackedPersistenceReader"/> instead, carrying the in-memory slice scope set resolved
/// once here — every subsequent per-address read checks that set in memory, never the DB again.
/// </remarks>
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
        Normal,
        Restricted
    }

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage)
    {
        HistoricalReadMode? mode = ResolveMode(baseBlock);
        if (mode is null) return inner.GatherSnapshotBundle(baseBlock, usage);

        // A historical bundle reads values at baseBlock but exposes the current trie; executing main-chain
        // blocks on that mix produces a corrupt state root and cascades into invalid-block deletions.
        if (usage is ResourcePool.Usage.MainBlockProcessing or ResourcePool.Usage.PostMainBlockProcessing)
        {
            throw new InvalidOperationException(
                $"Main block processing requested a writable scope at historical state {baseBlock}; history serves read-only execution.");
        }

        return new SnapshotBundle(BuildHistoricalBundle(baseBlock, mode.Value), trieNodeCache, resourcePool, usage);
    }

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
    {
        HistoricalReadMode? mode = ResolveMode(baseBlock);
        return mode is null ? inner.GatherReadOnlySnapshotBundle(baseBlock) : BuildHistoricalBundle(baseBlock, mode.Value);
    }

    public bool HasStateForBlock(in StateId stateId) => IsHistoricallyServable(stateId) || inner.HasStateForBlock(stateId);

    public void FlushCache(CancellationToken cancellationToken) => inner.FlushCache(cancellationToken);

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) =>
        inner.AddSnapshot(snapshot, transientResource);

    /// <summary>
    /// Resolves the once-per-call bundle mode: <c>null</c> means "not history's concern here" (above the
    /// persisted boundary, or covered but root-mismatched/never-captured — routing decisions unrelated to the
    /// floor, unchanged from before per-contract slices existed), and the caller falls through to the wrapped
    /// manager. Throws directly for the below-floor-with-no-slices case, exactly the refusal that existed before
    /// this type had a third mode.
    /// </summary>
    private HistoricalReadMode? ResolveMode(in StateId baseBlock)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted == StateId.PreGenesis || baseBlock.BlockNumber >= persisted.BlockNumber) return null;

        if (!historyReader.IsCoveredAndRootMatches(baseBlock))
        {
            if (historyReader.IsPrunedBelowFloor(baseBlock.BlockNumber)) ThrowUnavailable(baseBlock);
            return null;
        }

        if (!historyReader.IsBelowGlobalFloor(baseBlock.BlockNumber)) return HistoricalReadMode.Normal;

        if (historyReader.GetSliceScopes().Count == 0) ThrowUnavailable(baseBlock);

        return HistoricalReadMode.Restricted;
    }

    /// <summary>Non-throwing counterpart to <see cref="ResolveMode"/> for the plain existence query - a
    /// below-floor block with no covering slice reports "no state here" rather than throwing.</summary>
    private bool IsHistoricallyServable(in StateId baseBlock)
    {
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted == StateId.PreGenesis || baseBlock.BlockNumber >= persisted.BlockNumber) return false;
        if (!historyReader.IsCoveredAndRootMatches(baseBlock)) return false;
        if (!historyReader.IsBelowGlobalFloor(baseBlock.BlockNumber)) return true;
        return historyReader.GetSliceScopes().Count > 0;
    }

    private static void ThrowUnavailable(in StateId baseBlock) =>
        throw new StateUnavailableException(
            $"Historical state for block {baseBlock.BlockNumber} has been pruned below the flat history retention floor.");

    // Trie-less bundle: empty snapshot list over a history-backed reader. The reader serves account/storage values
    // only and throws on trie traversal / iteration, so post-block state-root recomputation must not walk it.
    private ReadOnlySnapshotBundle BuildHistoricalBundle(in StateId baseBlock, HistoricalReadMode mode)
    {
        IPersistence.IPersistenceReader reader = mode == HistoricalReadMode.Restricted
            ? new RestrictedHistoryBackedPersistenceReader(historyReader, baseBlock, scopeGate, historyReader.GetSliceScopes())
            : new HistoryBackedPersistenceReader(historyReader, baseBlock, scopeGate);

        return new(new SnapshotPooledList(0),
            reader,
            enableDetailedMetrics,
            PersistedSnapshotStack.Empty(enableDetailedMetrics),
            isHistorical: true);
    }
}
