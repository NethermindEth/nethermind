// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupPrefetchTests
{
    private static IEnumerable<TestCaseData> NodeGroupPrefetchCases()
    {
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 100))), new[] { TestItem.AddressA }, Array.Empty<Address>())
            .SetName("balance writes the account zone");
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(7, new StorageChange(1, 1u))), new[] { TestItem.AddressA }, Array.Empty<Address>())
            .SetName("header slot writes the account zone");
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u)).WithStorageChanges(2000, new StorageChange(1, 2u))), Array.Empty<Address>(), new[] { TestItem.AddressA })
            .SetName("storage-zone slots share one storage path");
        yield return new TestCaseData(Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(1000)), Array.Empty<Address>(), Array.Empty<Address>())
            .SetName("storage reads write nothing");
        yield return new TestCaseData(Bal(
                Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithNonceChanges(new NonceChange(1, 1)).WithStorageChanges(7, new StorageChange(1, 1u)).WithStorageChanges(1000, new StorageChange(1, 1u)),
                Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u))),
                new[] { TestItem.AddressA }, new[] { TestItem.AddressA, TestItem.AddressB })
            .SetName("both zones across accounts");

        static ReadOnlyBlockAccessList Bal(params AccountChangesBuilder[] accounts) =>
            Build.A.BlockAccessList.WithAccountChanges([.. accounts.Select(static account => account.TestObject)]).TestObject;
    }

    [TestCaseSource(nameof(NodeGroupPrefetchCases))]
    public void FirstGroupPaths_are_the_first_groups_below_the_top_groups(ReadOnlyBlockAccessList bal, Address[] accountZone, Address[] storageZone)
    {
        IEnumerable<PbtStorageNodePath> expected = accountZone.Select(static address => new PbtStorageNodePath(Bytes.FromHexString("00" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()[..6]), 32))
            .Concat(storageZone.Select(static address => new PbtStorageNodePath(Bytes.FromHexString("ff" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()), 264)));

        Assert.That(PbtNodeGroupPrefetch.FirstGroupPaths(bal), Is.EquivalentTo(expected));
    }

    public enum PrefetchSkip { EmptyBal, SnapshotBacked, ReadByFold }

    [Test]
    public void NodeGroupPrefetch_skips_empty_snapshot_backed_or_fold_read_lists([Values] PrefetchSkip skip)
    {
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 100))
                .WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);
        foreach (PbtStorageNodePath path in PbtNodeGroupPrefetch.FirstGroupPaths(bal))
        {
            if (skip == PrefetchSkip.SnapshotBacked) bundle.SetNodeGroup(path, default, null);
            if (skip == PrefetchSkip.ReadByFold) ((IDisposable?)bundle.GetNodeGroup(path, default))?.Dispose();
        }
        if (skip == PrefetchSkip.EmptyBal) bal = Build.A.BlockAccessList.TestObject;
        reader.ClearReceivedCalls();

        PbtNodeGroupPrefetch.Prefetch(bundle, bal, CancellationToken.None);

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
        if (readByFold)
            foreach (PbtStorageNodePath path in PbtNodeGroupPrefetch.FirstGroupPaths(bal)) ((IDisposable?)bundle.GetNodeGroup(path, default))?.Dispose();

        PbtNodeGroupPrefetch.Prefetch(bundle, bal, new CancellationToken(cancelled));

        HashSet<PbtStorageNodePath> expected = cancelled && !readByFold ? [] : PbtNodeGroupPrefetch.FirstGroupPaths(bal);
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
            foreach (PbtStorageNodePath path in PbtNodeGroupPrefetch.FirstGroupPaths(bal))
            {
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

            PbtNodeGroupPrefetch.Prefetch(bundle, bal, CancellationToken.None);

            reader.DidNotReceive().GetNodeGroup(Arg.Any<PbtStorageNodePath>());
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [Test]
    public void NodeGroupPrefetch_releases_first_level_results_when_a_parallel_single_read_fails()
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

        Exception? error = Assert.Catch(() => PbtNodeGroupPrefetch.Prefetch(bundle, bal, CancellationToken.None));
        bundle.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.ToString(), Does.Contain("single prefetch read failed"));
            Assert.That(memory.RentCount, Is.EqualTo(1));
            Assert.That(LifecycleSingleReads(reader).All(path => path.BitDepth == 264), Is.True);
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    [Test]
    public void NodeGroupPrefetch_releases_snapshot_lease_when_the_persistence_read_fails()
    {
        ReadOnlyBlockAccessList bal = LifecyclePrefetchBal();
        PbtStorageNodePath[] paths = [.. PbtNodeGroupPrefetch.FirstGroupPaths(bal)];
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(_ => throw new System.IO.IOException("prefetch read failed"));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        using (RefCountingMemory payload = LifecyclePrefetchPayload(memory, paths[0], 1025)) content.SetNodeGroup(paths[0], payload);
        using (PbtSnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(pool, content),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader, recordDetailedMetrics: false),
            pool, PbtResourcePool.Usage.MainBlockProcessing, NoopPbtTrieNodeCache.Instance))
        {
            Exception? error = Assert.Catch(() => PbtNodeGroupPrefetch.Prefetch(bundle, bal, CancellationToken.None));
            Assert.That(error!.ToString(), Does.Contain("prefetch read failed"));
            Assert.That(LifecycleSingleReads(reader), Is.EqualTo(new[] { paths[1] }));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero, "only the snapshot's reference may remain after the failed prefetch");
    }

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
