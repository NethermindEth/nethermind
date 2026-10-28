// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.Pbt.NodeBuffer;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Snapshot;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test.NodeBuffer;

public class PbtBufferWarmUpTests
{
    public enum Change { Update, Insert, Delete }

    [Test]
    public void Warm_up_links_the_groups_a_fold_of_the_key_opens([Values] bool storage, [Values] Change change, [Values(16, 2048)] int leafCount, [Values] bool stale)
    {
        using PbtNodeGroupStore tree = new();
        (byte[] Key, byte[]? Value)[] leaves = [.. Enumerable.Range(0, leafCount).Select(i => (Key(storage, i), (byte[]?)PbtStoreTestExtensions.Value(1)))];
        ValueHash256 root = tree.Fold(default, leaves);
        Dictionary<PbtStorageNodePath, byte[]> groups = [];
        if (stale)
        {
            // Groups of a deeper earlier tree stay in persistence where the current tree no longer reaches.
            foreach (PbtPhysicalPayload payload in tree.ExportPhysicalPayloads()) groups[payload.Key] = payload.Payload.ToArray();
            root = tree.Fold(root, leaves.Where((_, i) => i % 2 == 1).Select(leaf => (leaf.Key, (byte[]?)null)));
        }
        foreach (PbtPhysicalPayload payload in tree.ExportPhysicalPayloads()) groups[payload.Key] = payload.Payload.ToArray();

        byte[] key = change == Change.Insert ? Key(storage, leafCount) : leaves[0].Key;
        (byte[] Key, byte[]? Value)[] writes = [(key, change == Change.Delete ? null : PbtStoreTestExtensions.Value(2))];

        (ValueHash256 coldRoot, int pathGroups, _, _) = Fold(root, groups, storage, key, writes, warm: false);
        (ValueHash256 warmRoot, int loads, int warmedGroups, int foldReads) = Fold(root, groups, storage, key, writes, warm: true);
        ValueHash256 expected = tree.Fold(root, writes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(coldRoot, Is.EqualTo(expected), "cold fold");
            Assert.That(warmRoot, Is.EqualTo(expected), "warm fold");
            Assert.That(warmedGroups, Is.EqualTo(pathGroups), "the walk reads the groups a cold fold loads");
            Assert.That(loads, Is.EqualTo(1), "a warm fold loads only the root group, then follows transient links");
            Assert.That(foldReads, Is.Zero, "a warm fold reads nothing from persistence");
        }
    }

    /// <summary>Folds <paramref name="writes"/> into a bundle that reads <paramref name="groups"/> from persistence only.</summary>
    /// <returns>The new root, the groups the fold loaded by path, the groups the warm-up read, and the groups the fold read from persistence.</returns>
    private static (ValueHash256 Root, int Loads, int WarmedGroups, int FoldReads) Fold(ValueHash256 root, Dictionary<PbtStorageNodePath, byte[]> groups,
        bool storage, byte[] key, (byte[] Key, byte[]? Value)[] writes, bool warm)
    {
        IPbtPersistence.IReader inner = Substitute.For<IPbtPersistence.IReader>();
        inner.CurrentRoot.Returns(root);
        PbtSnapshotTestExtensions.GroupLoadingReader reader = inner.LoadingGroupsFrom(path => groups.GetValueOrDefault(path));
        using PbtGroupRecordBuffer buffer = new(0, PbtGroupRecordBuffer.DefaultGenerationBytes);
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader, buffer);
        if (warm) WarmUp(bundle, storage, key);
        int warmedGroups = reader.Reads.Length;
        if (warm) WarmUp(bundle, storage, key);
        Assert.That(reader.Reads, Has.Length.EqualTo(warmedGroups), "a second walk finds every group warmed");
        reader.ClearReads();

        LoadCountingStore store = new(new PbtSnapshotStore(bundle, (bundle.TreeRoot, bundle.RootGroupPointer)));
        using PbtPartitionBatches changes = PbtStoreTestExtensions.PreparePartitions(writes);
        (ValueHash256 Hash, ulong Pointer) updated = TrieUpdater.UpdateRoot(store, (root, bundle.RootGroupPointer), changes, PbtTreeHarness.FoldQuota(), PbtTreeHarness.DefaultFanOut, null, null);
        AssertPersistentLinks(buffer, updated.Pointer);
        return (updated.Hash, store.Loads, warmedGroups, reader.Reads.Length);
    }

    private static void WarmUp(PbtSnapshotBundle bundle, bool storage, byte[] key)
    {
        if (storage) bundle.WarmUp<PbtStoragePath, PbtStorageNodePath>(new PbtStoragePath(key));
        else bundle.WarmUp<PbtPath, PbtNodePath>(new PbtPath(key));
    }

    /// <summary>Asserts no committed record reachable from <paramref name="rootPointer"/> links a transient pointer.</summary>
    private static void AssertPersistentLinks(PbtGroupRecordBuffer buffer, ulong rootPointer)
    {
        Stack<ulong> pending = new([rootPointer]);
        while (pending.TryPop(out ulong pointer))
        {
            Assert.That(PbtGroupRecordCodec.IsTransient(pointer), Is.False, "committed records link no warmed group");
            if (!buffer.TryLocate(pointer, out PbtGroupRecord record)) continue;
            for (int slot = 0; slot < PbtTwoLevelGroupGeometry.BoundarySlots; slot++)
                if (record.Links[slot] != 0) pending.Push(record.Links[slot]);
        }
    }

    private static byte[] Key(bool storage, int seed)
    {
        byte[] key = new byte[storage ? PbtStoragePath.KeyLength : PbtPath.KeyLength];
        ValueKeccak.Compute(BitConverter.GetBytes(seed)).Bytes.CopyTo(key.AsSpan(1));
        key[0] = storage ? Eip8297KeyDerivation.StorageZone : Eip8297KeyDerivation.AccountZone;
        return key;
    }

    private sealed class LoadCountingStore(IPbtStore store) : IPbtStore
    {
        private int _loads;

        public int Loads => _loads;

        public IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength) => store.StartWriteBatch(alignedTotalLength);

        public bool TryLocate(ulong pointer, out PbtGroupRecord record) => store.TryLocate(pointer, out record);

        public bool TryLoad<TArg>(scoped in PbtTraversalPath path, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            Interlocked.Increment(ref _loads);
            return store.TryLoad(path, action, arg);
        }
    }
}
