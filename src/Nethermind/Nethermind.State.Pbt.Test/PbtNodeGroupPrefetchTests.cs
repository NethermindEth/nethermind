// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Snapshot;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupPrefetchTests
{
    [Test]
    public void BalKeys_derive_each_key_once_and_sort_every_list([Values] bool withReads)
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 100))
                .WithStorageChanges(7, new StorageChange(1, 1u))
                .WithStorageChanges(1000, new StorageChange(1, 1u))
                .WithStorageChanges(1001, new StorageChange(1, 1u))
                .WithStorageChanges(2000, new StorageChange(1, 1u))
                .WithStorageReads(3, 3000).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressC).WithStorageReads(5).TestObject).TestObject;

        using PbtWorldStateScope.BalKeys keys = PbtWorldStateScope.BalKeys.Create(bal, withReads);

        Address[] accounts = withReads ? [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC] : [TestItem.AddressA, TestItem.AddressB];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Entries(keys.Accounts), Is.EqualTo(Sorted(accounts.Select(address =>
                (Eip8297KeyDerivation.AccountKey(PbtStateKey.AddressKeyHash(address), PbtKeyDerivation.BasicDataLeafKey).Bytes.ToHexString(), (UInt256)IndexOf(address))))));
            Assert.That(Entries(keys.HeaderWrites), Is.EqualTo(SlotKeys((TestItem.AddressA, 7))));
            Assert.That(Entries(keys.StorageWrites), Is.EqualTo(SlotKeys((TestItem.AddressA, 1000), (TestItem.AddressA, 1001), (TestItem.AddressA, 2000), (TestItem.AddressB, 1000))));
            Assert.That(Entries(keys.HeaderReads), Is.EqualTo(withReads ? SlotKeys((TestItem.AddressA, 3), (TestItem.AddressC, 5)) : []));
            Assert.That(Entries(keys.StorageReads), Is.EqualTo(withReads ? SlotKeys((TestItem.AddressA, 3000)) : []));
        }

        int IndexOf(Address address)
        {
            ReadOnlySpan<ReadOnlyAccountChanges> accountChanges = bal.AccountChanges.AsSpan();
            for (int index = 0; ; index++)
                if (accountChanges[index].Address == address) return index;
        }

        static (string Key, UInt256 Value)[] Entries<TKey>(ArrayPoolList<PbtWriteOperation<TKey>> operations) where TKey : struct, IPbtKey<TKey> =>
            [.. operations.Select(static operation =>
            {
                TKey key = operation.Key;
                return (key.Bytes.ToHexString(), operation.Value.ToUInt256());
            })];

        static (string Key, UInt256 Value)[] SlotKeys(params (Address Address, int Slot)[] slots) => Sorted(slots.Select(static slot =>
            (PbtStateKey.Slot(slot.Address, PbtStateKey.AddressKeyHash(slot.Address), (UInt256)slot.Slot).Bytes.ToHexString(), (UInt256)slot.Slot)));

        static (string Key, UInt256 Value)[] Sorted(IEnumerable<(string Key, UInt256 Value)> entries) => [.. entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal)];
    }

    public enum PrefetchSkip { EmptyBal, SnapshotBacked, ReadByFold, MissingAccount }

    [Test]
    public void NodeGroupPrefetch_skips_empty_snapshot_backed_or_fold_read_lists([Values] PrefetchSkip skip)
    {
        AccountChangesBuilder account = Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u));
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            (skip == PrefetchSkip.MissingAccount ? account : account.WithBalanceChanges(new BalanceChange(1, 100))).TestObject).TestObject;
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);
        foreach (PbtStorageNodePath path in new[] { FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.AccountZone), FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.StorageZone) })
        {
            if (skip == PrefetchSkip.SnapshotBacked) bundle.SetNodeGroup(path, default, null);
            if (skip == PrefetchSkip.ReadByFold) ((IDisposable?)bundle.GetNodeGroup(path, default))?.Dispose();
        }
        if (skip == PrefetchSkip.EmptyBal) bal = Build.A.BlockAccessList.TestObject;
        reader.ClearReceivedCalls();

        // A missing account is only found missing when the buffering reads it.
        Prefetch(bundle, bal, new CancellationToken(skip != PrefetchSkip.MissingAccount), CancellationToken.None);

        reader.DidNotReceive().GetNodeGroup(Arg.Any<PbtStorageNodePath>());
    }

    [Test]
    public void NodeGroupPrefetch_reads_storage_groups_as_deep_as_the_slot_subtree_size_suggests(
        [Values(-1L, 0L, 1024L, 1025L, 16 * 1024L, 16 * 1024 + 1L, 1024 * 1024L)] long descendantBytes,
        [Values] bool cancelled, [Values] bool snapshotBacked, [Values] bool readByFold)
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 100))
                .WithStorageChanges(7, new StorageChange(1, 1u))
                .WithStorageChanges(1000, new StorageChange(1, 1u))
                .WithStorageChanges(1001, new StorageChange(1, 1u))
                .WithStorageChanges(2000, new StorageChange(1, 1u))
                .WithStorageReads(3000).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;
        ValueHash256 addressHash = PbtStateKey.AddressKeyHash(TestItem.AddressA);
        PbtStoragePath storagePath = PbtStateKey.Storage(TestItem.AddressA, addressHash, 1000);
        PbtStorageNodePath storageGroup = new(storagePath.Bytes[..33], 264);
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(call => ReadPayload(call.Arg<PbtStorageNodePath>()));

        RefCountingMemory? ReadPayload(PbtStorageNodePath path)
        {
            if (descendantBytes < 0 || path.BitDepth == 264 && !path.Equals(storageGroup)) return null;
            long[] sizes = new long[PbtFourLevelGroupGeometry.BoundarySlots];
            Array.Fill(sizes, descendantBytes);
            byte[] branch = PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
            byte[] encoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(PbtTestPaths.PathOf(path, 0), branch)], sizes);
            RefCountingMemory payload = memory.Rent(encoding.Length);
            encoding.CopyTo(payload.GetSpan());
            return payload;
        }
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);
        PbtStorageNodePath deeperSnapshot = new(storagePath.Bytes[..34], 272);
        if (snapshotBacked)
        {
            using RefCountingMemory? payload = ReadPayload(storageGroup);
            bundle.SetNodeGroup(storageGroup, default, payload);
            using RefCountingMemory? deeperPayload = ReadPayload(deeperSnapshot);
            bundle.SetNodeGroup(deeperSnapshot, default, deeperPayload);
        }
        HashSet<PbtStorageNodePath> firstGroups = [FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.AccountZone), FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.StorageZone), FirstGroup(TestItem.AddressB, Eip8297KeyDerivation.StorageZone)];
        if (readByFold)
            foreach (PbtStorageNodePath path in firstGroups) ((IDisposable?)bundle.GetNodeGroup(path, default))?.Dispose();

        Prefetch(bundle, bal, new CancellationToken(true), new CancellationToken(cancelled));

        HashSet<PbtStorageNodePath> expected = cancelled && !readByFold ? [] : firstGroups;
        int levels = cancelled ? 0 : descendantBytes switch { <= 1024 => 0, <= 16 * 1024 => 1, <= 256 * 1024 => 2, _ => 3 };
        foreach (UInt256 slot in new UInt256[] { 1000, 1001, 2000 })
        {
            PbtStoragePath key = PbtStateKey.Storage(TestItem.AddressA, addressHash, slot);
            for (int depth = 268; depth <= 264 + 4 * levels; depth += 4)
            {
                byte[] path = key.Bytes[..((depth + 7) / 8)].ToArray();
                if (depth % 8 != 0) path[^1] &= 0xF0;
                expected.Add(new PbtStorageNodePath(path, depth));
            }
        }
        if (snapshotBacked)
        {
            expected.Remove(storageGroup);
            expected.Remove(deeperSnapshot);
        }
        PbtStorageNodePath[] singles = LifecycleSingleReads(reader);
        reader.ClearReceivedCalls();
        foreach (PbtStorageNodePath path in expected)
        {
            using RefCountingMemory? kept = bundle.GetNodeGroup(path, default);
            using RefCountingMemory? persisted = ReadPayload(path);
            Assert.That(kept?.GetSpan().ToArray(), Is.EqualTo(persisted?.GetSpan().ToArray()));
        }
        bool reread = reader.ReceivedCalls().Any();
        bundle.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(singles, Is.EquivalentTo(expected));
            Assert.That(reread, Is.False, "the fold must read prefetched groups from the bundle");
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    [Test]
    public void NodeGroupPrefetch_skips_deeper_snapshot_tombstones()
    {
        ReadOnlyBlockAccessList bal = LifecyclePrefetchBal();
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        using (PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader))
        {
            foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB })
            {
                PbtStorageNodePath path = FirstGroup(address, Eip8297KeyDerivation.StorageZone);
                using RefCountingMemory payload = LifecyclePrefetchPayload(memory, path, 1025);
                bundle.SetNodeGroup(path, default, payload);
            }
            foreach (ReadOnlyAccountChanges changes in bal.AccountChanges)
            {
                PbtStoragePath key = PbtStateKey.Storage(changes.Address, PbtStateKey.AddressKeyHash(changes.Address), 1000);
                byte[] path = key.Bytes[..34].ToArray();
                path[^1] &= 0xF0;
                bundle.SetNodeGroup(new PbtStorageNodePath(path, 268), default, null);
            }

            Prefetch(bundle, bal, new CancellationToken(true), CancellationToken.None);

            reader.DidNotReceive().GetNodeGroup(Arg.Any<PbtStorageNodePath>());
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [Test]
    public void NodeGroupPrefetch_releases_read_groups_when_a_later_read_fails()
    {
        ReadOnlyBlockAccessList bal = LifecyclePrefetchBal();
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        int reads = 0;
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(call =>
        {
            if (Interlocked.Increment(ref reads) == 2) throw new System.IO.IOException("single prefetch read failed");
            return LifecyclePrefetchPayload(memory, call.Arg<PbtStorageNodePath>(), 1025);
        });
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);
        using Nethermind.Core.Threading.ParallelUnbalancedWork.WorkerScope workers = Nethermind.Core.Threading.ParallelUnbalancedWork.BeginWorkerScope(1);

        Exception? error = Assert.Catch(() => Prefetch(bundle, bal, new CancellationToken(true), CancellationToken.None));
        bundle.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.ToString(), Does.Contain("single prefetch read failed"));
            Assert.That(memory.RentCount, Is.EqualTo(1));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    [Test]
    public void NodeGroupPrefetch_releases_snapshot_lease_when_the_persistence_read_fails()
    {
        ReadOnlyBlockAccessList bal = LifecyclePrefetchBal();
        PbtStorageNodePath[] paths = [FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.StorageZone), FirstGroup(TestItem.AddressB, Eip8297KeyDerivation.StorageZone)];
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(_ => throw new System.IO.IOException("prefetch read failed"));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        using (RefCountingMemory payload = LifecyclePrefetchPayload(memory, paths[0], 0)) content.SetNodeGroup(paths[0], payload);
        using (PbtSnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(pool, content),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader, recordDetailedMetrics: false),
            pool, PbtResourcePool.Usage.MainBlockProcessing, NoopPbtTrieNodeCache.Instance))
        {
            Exception? error = Assert.Catch(() => Prefetch(bundle, bal, new CancellationToken(true), CancellationToken.None));
            Assert.That(error!.ToString(), Does.Contain("prefetch read failed"));
            Assert.That(LifecycleSingleReads(reader), Is.EqualTo(new[] { paths[1] }));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero, "only the snapshot's reference may remain after the failed prefetch");
    }

    private static void Prefetch(PbtSnapshotBundle bundle, ReadOnlyBlockAccessList bal, CancellationToken reads, CancellationToken cancellation)
    {
        using PbtWorldStateScope.BalKeys keys = PbtWorldStateScope.BalKeys.Create(bal, false);
        PbtWorldStateScope.PrefetchBal(bundle, keys, null, reads);
        PbtWorldStateScope.PrefetchNodeGroups(bundle, keys, cancellation);
    }

    private static PbtStorageNodePath FirstGroup(Address address, byte zone) => zone == Eip8297KeyDerivation.AccountZone
        ? new(Bytes.FromHexString("00" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()[..6]), 32)
        : new(Bytes.FromHexString("ff" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()), 264);

    private static ReadOnlyBlockAccessList LifecyclePrefetchBal() => Build.A.BlockAccessList.WithAccountChanges(
        Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject,
        Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;

    private static RefCountingMemory LifecyclePrefetchPayload(TrackingMemoryProvider memory, PbtStorageNodePath path, long descendantBytes)
    {
        long[] sizes = new long[PbtFourLevelGroupGeometry.BoundarySlots];
        Array.Fill(sizes, descendantBytes);
        byte[] branch = PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
        byte[] encoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(PbtTestPaths.PathOf(path, 0), branch)], sizes);
        RefCountingMemory payload = memory.Rent(encoding.Length);
        encoding.CopyTo(payload.GetSpan());
        return payload;
    }

    private static PbtStorageNodePath[] LifecycleSingleReads(IPbtPersistence.IReader reader) => reader.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IPbtPersistence.IReader.GetNodeGroup))
        .Select(call => (PbtStorageNodePath)call.GetArguments()[0]!).ToArray();
}
