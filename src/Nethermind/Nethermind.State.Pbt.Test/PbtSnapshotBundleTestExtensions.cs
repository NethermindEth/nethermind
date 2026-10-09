// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.Test;

internal static class PbtSnapshotBundleTestExtensions
{
    public static void SetSlot(this PbtSnapshotBundle bundle, Address address, in UInt256 slot, in EvmWord value) =>
        bundle.SetSlots(address, PbtStateKey.AddressKeyHash(address), [new SlotWrite(slot, value)]);

    public static PbtSnapshot CollectSnapshot(this PbtSnapshotBundle bundle, in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        PbtSnapshot snapshot = bundle.CollectSnapshot(from, to, treeRoot, out PbtTransientResource retired);
        retired.ReleaseLease();
        return snapshot;
    }

    /// <summary>Retains one group in <paramref name="cache"/> by staging it in a throwaway transient resource and folding that in.</summary>
    public static void Add<TPath>(this PbtTrieNodeCache cache, in ValueHash256 groupHash, TPath path, RefCountingMemory payload) where TPath : struct, IPbtNodePath<TPath>
    {
        using PbtTransientResource staged = new(nodeGroupCapacity: 1);
        staged.NodeGroups.Set(groupHash, path, payload);
        cache.Add(staged);
    }

    public static PbtSnapshotBundle CreateBundle(IPbtResourcePool pool, IPbtPersistence.IReader reader) =>
        CreateBundle(pool, reader, NoopPbtTrieNodeCache.Instance);

    public static PbtSnapshotBundle CreateBundle(IPbtResourcePool pool, IPbtPersistence.IReader reader, IPbtTrieNodeCache cache) => new(
        new PbtSnapshotPooledList(0),
        new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader, recordDetailedMetrics: false),
        pool, PbtResourcePool.Usage.MainBlockProcessing, cache);

    /// <summary>Folds the bundle's pending leaf changes into the tree at <paramref name="root"/> and returns the new root.</summary>
    public static ValueHash256 Fold(this PbtSnapshotBundle bundle, ValueHash256 root)
    {
        PbtPartitionBatches changes = bundle.PrepareLeafChanges();
        try
        {
            ValueHash256 updated;
            using (PbtSnapshotStore store = new(bundle))
                updated = TrieUpdater.UpdateRoot(store, root, changes, PbtTreeHarness.FoldQuota(), PbtTreeHarness.DefaultFanOut, null);
            bundle.CompleteLeafChanges();
            return updated;
        }
        finally
        {
            changes.Dispose();
        }
    }

    public static PbtSnapshotPooledList Chain(IPbtResourcePool pool, params PbtSnapshotContent[] layersOldestFirst)
    {
        PbtSnapshotPooledList chain = new(layersOldestFirst.Length);
        for (int i = 0; i < layersOldestFirst.Length; i++)
            chain.Add(new PbtSnapshot(i == 0 ? StateId.PreGenesis : new StateId((ulong)i, default), new StateId((ulong)i + 1, default), default, layersOldestFirst[i], pool, PbtResourcePool.Usage.MainBlockProcessing));
        return chain;
    }
}
