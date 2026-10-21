// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt.Test;

internal static class PbtSnapshotBundleTestExtensions
{
    /// <summary>A read-only bundle pin for tests whose buffer records outlive the bundle.</summary>
    public static IDisposable NoNodeGroupPin => new Reactive.AnonymousDisposable(static () => { });

    public static void SetSlot(this PbtSnapshotBundle bundle, Address address, in UInt256 slot, in UInt256 value) =>
        bundle.SetSlots(address, Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes), [(slot, value)]);

    public static PbtSnapshotBundle CreateBundle(IPbtResourcePool pool, IPbtPersistence.IReader reader, IPbtGroupRecordBuffer? groupRecordBuffer = null) => new(
        new PbtSnapshotPooledList(0),
        new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader, recordDetailedMetrics: false, new PbtPersistedSnapshotList(0), NoNodeGroupPin, slotFilterBitsPerKey: 0),
        groupRecordBuffer ?? PbtSnapshotTestExtensions.DefaultGroupRecordBuffer, pool, PbtResourcePool.Usage.MainBlockProcessing, filterInMemorySlotReads: false);

    /// <summary>Folds the bundle's pending leaf changes into the tree at <paramref name="root"/> and returns the new root.</summary>
    public static ValueHash256 Fold(this PbtSnapshotBundle bundle, ValueHash256 root)
    {
        PbtPartitionBatches changes = bundle.PrepareLeafChanges();
        try
        {
            ValueHash256 updated;
            using (PbtSnapshotStore store = new(bundle))
                updated = TrieUpdater.UpdateRoot(store, root, changes, PbtTreeHarness.FoldQuota(), PbtTreeHarness.DefaultFanOut, null, PooledRefCountingMemoryProvider.Instance);
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
            chain.Add(Layer(pool, layersOldestFirst[i], i));
        return chain;
    }

    public static PbtReadOnlySnapshotBundle ReadOnlyBundle(PbtAssembledSnapshotResult assembled, IPbtPersistence.IReader reader, double slotFilterBitsPerKey) =>
        new(assembled.InMemory, reader, recordDetailedMetrics: false, assembled.Persisted, NoNodeGroupPin, slotFilterBitsPerKey);

    /// <remarks>A layer publishing a root group gets a tree root unique to that group, indexed in its buffer as a collected snapshot's is.</remarks>
    private static PbtSnapshot Layer(IPbtResourcePool pool, PbtSnapshotContent content, int index)
    {
        ValueHash256 treeRoot = default;
        if (content.RootGroupPointer != 0)
        {
            treeRoot = ValueKeccak.Compute(BitConverter.GetBytes(content.RootGroupPointer));
            content.GroupRecordBuffer!.SetRoot(treeRoot, content.RootGroupPointer);
        }
        return new(index == 0 ? StateId.PreGenesis : new StateId((ulong)index, default), new StateId((ulong)index + 1, default), treeRoot, content, pool, PbtResourcePool.Usage.MainBlockProcessing);
    }
}
