// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Decorates an <see cref="IFlatDbManager"/> to serve reads for blocks below the finalization barrier — whose
/// per-block tip snapshots have been pruned — from the finalized history index, via
/// <see cref="GatherReadOnlySnapshotBundle"/> / <see cref="GatherSnapshotBundle"/>.
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
    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage)
    {
        if (!IsBelowBarrier(baseBlock, out bool prunedBelowFloor))
        {
            ThrowIfPrunedBelowFloor(baseBlock, prunedBelowFloor);
            return inner.GatherSnapshotBundle(baseBlock, usage);
        }

        // A historical bundle reads values at baseBlock but exposes the current trie; executing main-chain
        // blocks on that mix produces a corrupt state root and cascades into invalid-block deletions.
        if (usage is ResourcePool.Usage.MainBlockProcessing or ResourcePool.Usage.PostMainBlockProcessing)
        {
            throw new InvalidOperationException(
                $"Main block processing requested a writable scope at historical state {baseBlock}; history serves read-only execution.");
        }

        return new SnapshotBundle(BuildHistoricalBundle(baseBlock), trieNodeCache, resourcePool, usage);
    }

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
    {
        if (IsBelowBarrier(baseBlock, out bool prunedBelowFloor)) return BuildHistoricalBundle(baseBlock);

        ThrowIfPrunedBelowFloor(baseBlock, prunedBelowFloor);
        return inner.GatherReadOnlySnapshotBundle(baseBlock);
    }

    public bool HasStateForBlock(in StateId stateId) =>
        IsBelowBarrier(stateId, out _) || inner.HasStateForBlock(stateId);

    public void FlushCache(CancellationToken cancellationToken) => inner.FlushCache(cancellationToken);

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource) =>
        inner.AddSnapshot(snapshot, transientResource);

    /// <summary>
    /// Distinguishes "servable from history" (true) from two different false outcomes: covered-but-pruned
    /// (<paramref name="prunedBelowFloor"/> true — must fail loudly, never fall through) versus genuinely not
    /// history's concern (root mismatch / history disabled / above the barrier — existing fallthrough to
    /// <paramref name="inner"/> is correct as before).
    /// </summary>
    private bool IsBelowBarrier(in StateId baseBlock, out bool prunedBelowFloor)
    {
        prunedBelowFloor = false;
        StateId persisted = persistenceManager.GetCurrentPersistedStateId();
        if (persisted == StateId.PreGenesis || baseBlock.BlockNumber >= persisted.BlockNumber) return false;

        if (historyReader.IsAvailable(baseBlock)) return true;

        prunedBelowFloor = historyReader.IsPrunedBelowFloor(baseBlock.BlockNumber);
        return false;
    }

    /// <summary>
    /// A block covered by the watermark but pruned below the retention floor must never fall through to
    /// <paramref name="inner"/> — inner's tiers only reach back to the finalization barrier, and answering "no
    /// state here" for a query this old risks reading as "account/slot does not exist" upstream (e.g.
    /// eth_getBalance returning a false zero) instead of the deliberate "unavailable" this represents.
    /// </summary>
    private static void ThrowIfPrunedBelowFloor(in StateId baseBlock, bool prunedBelowFloor)
    {
        if (prunedBelowFloor)
        {
            throw new StateUnavailableException(
                $"Historical state for block {baseBlock.BlockNumber} has been pruned below the flat history retention floor.");
        }
    }

    // Trie-less bundle: empty snapshot list over a history-backed reader. The reader serves account/storage values
    // only and throws on trie traversal / iteration, so post-block state-root recomputation must not walk it.
    private ReadOnlySnapshotBundle BuildHistoricalBundle(in StateId baseBlock) =>
        new(new SnapshotPooledList(0),
            new HistoryBackedPersistenceReader(historyReader, baseBlock, scopeGate),
            enableDetailedMetrics,
            PersistedSnapshotStack.Empty(enableDetailedMetrics),
            isHistorical: true);
}
