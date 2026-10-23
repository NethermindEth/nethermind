// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Memory;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.Test;

/// <summary>Repository and bundle queries the tests need, composed from their public API.</summary>
internal static class PbtSnapshotTestExtensions
{
    private static readonly SnapshotTier[] MemoryTiers = [SnapshotTier.InMemoryBase, SnapshotTier.InMemoryCompacted];

    /// <summary>Leases the next memory snapshot extending <paramref name="persistedState"/> towards <paramref name="seed"/>.</summary>
    internal static PbtSnapshot? FindSnapshotToPersist(this PbtSnapshotRepository repository, in StateId seed, in StateId persistedState, ulong compactSize)
    {
        (PbtPersistedSnapshot? persisted, PbtSnapshot? snapshot) = repository.FindCandidateToPersist(seed, persistedState, compactSize, static _ => true);
        persisted?.Dispose();
        return snapshot;
    }

    /// <summary>Whether the repository indexes this exact persisted instance.</summary>
    internal static bool ContainsPersistedSource(this PbtSnapshotRepository repository, PbtPersistedSnapshot snapshot)
    {
        if (!repository.TryLeasePersistedCatalogKey(snapshot.To, snapshot.Depth, out PbtPersistedSnapshot? current)) return false;
        using (current) return ReferenceEquals(current, snapshot);
    }

    internal static bool TryLeasePersisted(this PbtSnapshotRepository repository, in StateId to, long depth, SnapshotTier tier, out PbtPersistedSnapshot? snapshot)
    {
        if (repository.TryLeasePersistedCatalogKey(to, depth, out snapshot) && snapshot!.Tier == tier) return true;
        snapshot?.Dispose();
        snapshot = null;
        return false;
    }

    /// <summary>Whether a persisted snapshot of <paramref name="tier"/> spans <paramref name="depth"/> blocks up to <paramref name="to"/>.</summary>
    internal static bool HasPersisted(this PbtSnapshotRepository repository, in StateId to, long depth, SnapshotTier tier)
    {
        if (!repository.TryLeasePersisted(to, depth, tier, out PbtPersistedSnapshot? snapshot)) return false;
        snapshot!.Dispose();
        return true;
    }

    /// <summary>Removes and releases both memory layers at <paramref name="state"/>.</summary>
    internal static void RemoveMemoryState(this PbtSnapshotRepository repository, in StateId state)
    {
        foreach (SnapshotTier tier in MemoryTiers)
        {
            if (!repository.TryLeaseMemoryState(state, tier, out PbtSnapshot? snapshot)) continue;
            using (snapshot) repository.RemoveMemorySource(snapshot!);
        }
    }

    /// <summary>Returns a caller-owned group lease, or null when no layer has an entry.</summary>
    internal static RefCountingMemory? GetNodeGroup(this PbtReadOnlySnapshotBundle bundle, PbtStorageNodePath groupKey) =>
        bundle.TryGetSnapshotNodeGroup(groupKey, out RefCountingMemory? payload) ? payload : bundle.GetPersistedNodeGroup(groupKey);
}
