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
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Snapshot;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupPrefetchTests
{
    public enum PrefetchSkip { EmptyBal, SnapshotBacked, ReadByFold, MissingAccount }

    [Test]
    public void NodeGroupPrefetch_skips_empty_snapshot_backed_or_fold_read_lists([Values] PrefetchSkip skip)
    {
        AccountChangesBuilder account = Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u));
        // The storage group of C is always prefetched, so its read shows the prefetch has run.
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            (skip == PrefetchSkip.MissingAccount ? account : account.WithBalanceChanges(new BalanceChange(1, 100))).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressC).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;
        IPbtPersistence.IReader reader = Reader(skip == PrefetchSkip.MissingAccount ? [TestItem.AddressA] : []);
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);
        foreach (PbtStorageNodePath path in new[] { FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.AccountZone), FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.StorageZone) })
        {
            if (skip == PrefetchSkip.SnapshotBacked) bundle.SetNodeGroup(path, default, null);
            if (skip == PrefetchSkip.ReadByFold) ((IDisposable?)bundle.GetNodeGroup(path, default))?.Dispose();
        }
        if (skip == PrefetchSkip.EmptyBal) bal = Build.A.BlockAccessList.TestObject;
        reader.ClearReceivedCalls();

        PbtWorldStateScope scope = HintBal(bundle, bal);
        PbtStorageNodePath[] expected = skip == PrefetchSkip.EmptyBal ? [] : [FirstGroup(TestItem.AddressC, Eip8297KeyDerivation.StorageZone)];
        Assert.That(() => LifecycleSingleReads(reader), Is.EquivalentTo(expected).After(5000, 10));
        scope.UpdateRootHash();

        Assert.That(LifecycleSingleReads(reader), Is.EquivalentTo(expected));
    }

    [Test]
    public void NodeGroupPrefetch_reads_storage_groups_as_deep_as_the_slot_subtree_size_suggests(
        [Values(-1L, 0L, 1024L, 1025L, 8 * 1024L, 8 * 1024 + 1L, 1024 * 1024L)] long descendantBytes,
        [Values] bool snapshotBacked, [Values] bool readByFold)
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
        IPbtPersistence.IReader reader = Reader();
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(call => ReadPayload(call.Arg<PbtStorageNodePath>()));

        RefCountingMemory? ReadPayload(PbtStorageNodePath path)
        {
            if (descendantBytes < 0 || path.BitDepth == 264 && !path.Equals(storageGroup)) return null;
            long[] sizes = new long[PbtThreeLevelGroupGeometry.BoundarySlots];
            Array.Fill(sizes, descendantBytes);
            byte[] branch = PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256);
            byte[] encoding = PbtNodeGroupEncoder.Encode(path, [new PbtNodeRecord(PbtTestPaths.PathOf(path, 0), branch)], sizes);
            RefCountingMemory payload = memory.Rent(encoding.Length);
            encoding.CopyTo(payload.GetSpan());
            return payload;
        }
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);
        PbtStorageNodePath deeperSnapshot = PbtTestPaths.Prefix<PbtStorageNodePath>(storagePath.Bytes, 270);
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

        PbtWorldStateScope scope = HintBal(bundle, bal);

        HashSet<PbtStorageNodePath> expected = firstGroups;
        int levels = descendantBytes switch { <= 1024 => 0, <= 8 * 1024 => 1, <= 64 * 1024 => 2, <= 512 * 1024 => 3, _ => 4 };
        foreach (UInt256 slot in new UInt256[] { 1000, 1001, 2000 })
        {
            PbtStoragePath key = PbtStateKey.Storage(TestItem.AddressA, addressHash, slot);
            for (int depth = 267; depth <= 264 + 3 * levels; depth += 3)
                expected.Add(PbtTestPaths.Prefix<PbtStorageNodePath>(key.Bytes, depth));
        }
        if (snapshotBacked)
        {
            expected.Remove(storageGroup);
            expected.Remove(deeperSnapshot);
        }
        Assert.That(() => LifecycleSingleReads(reader), Is.EquivalentTo(expected).After(5000, 10));
        scope.UpdateRootHash();
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
        Address[] tombstoned = [TestItem.AddressA, TestItem.AddressB];
        // The storage group of C is always prefetched, so its read shows the prefetch has run.
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges([.. tombstoned.Append(TestItem.AddressC).Select(static address =>
            Build.An.AccountChanges.WithAddress(address).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject)]).TestObject;
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Reader();
        using (PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader))
        {
            foreach (Address address in tombstoned)
            {
                PbtStorageNodePath path = FirstGroup(address, Eip8297KeyDerivation.StorageZone);
                using RefCountingMemory payload = LifecyclePrefetchPayload(memory, path, 1025);
                bundle.SetNodeGroup(path, default, payload);
            }
            foreach (Address address in tombstoned)
            {
                PbtStoragePath key = PbtStateKey.Storage(address, PbtStateKey.AddressKeyHash(address), 1000);
                bundle.SetNodeGroup(PbtTestPaths.Prefix<PbtStorageNodePath>(key.Bytes, 267), default, null);
            }

            PbtWorldStateScope scope = HintBal(bundle, bal);
            PbtStorageNodePath[] expected = [FirstGroup(TestItem.AddressC, Eip8297KeyDerivation.StorageZone)];
            Assert.That(() => LifecycleSingleReads(reader), Is.EquivalentTo(expected).After(5000, 10));
            scope.UpdateRootHash();

            Assert.That(LifecycleSingleReads(reader), Is.EquivalentTo(expected));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    [Test]
    public void NodeGroupPrefetch_releases_read_groups_when_a_later_read_fails()
    {
        ReadOnlyBlockAccessList bal = LifecyclePrefetchBal();
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Reader();
        int reads = 0;
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(call =>
        {
            if (Interlocked.Increment(ref reads) == 2) throw new System.IO.IOException("single prefetch read failed");
            return LifecyclePrefetchPayload(memory, call.Arg<PbtStorageNodePath>(), 1025);
        });
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader);
        InterfaceLogger logger = ErrorLogger();

        PbtWorldStateScope scope = HintBal(bundle, bal, logger);
        Assert.That(() => Volatile.Read(ref reads), Is.GreaterThanOrEqualTo(2).After(5000, 10));
        scope.UpdateRootHash();
        bundle.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LoggedErrors(logger), Does.Contain("single prefetch read failed"));
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
    }

    [Test]
    public void NodeGroupPrefetch_releases_snapshot_lease_when_the_persistence_read_fails()
    {
        ReadOnlyBlockAccessList bal = LifecyclePrefetchBal();
        PbtStorageNodePath[] paths = [FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.StorageZone), FirstGroup(TestItem.AddressB, Eip8297KeyDerivation.StorageZone)];
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Reader();
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(_ => throw new System.IO.IOException("prefetch read failed"));
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotContent content = pool.GetSnapshotContent(PbtResourcePool.Usage.MainBlockProcessing);
        using (RefCountingMemory payload = LifecyclePrefetchPayload(memory, paths[0], 0)) content.SetNodeGroup(paths[0], payload);
        using (PbtSnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(pool, content),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader, recordDetailedMetrics: false, slotFilterBitsPerKey: 0),
            pool, PbtResourcePool.Usage.MainBlockProcessing, NoopPbtTrieNodeCache.Instance, filterInMemorySlotReads: false))
        {
            InterfaceLogger logger = ErrorLogger();
            PbtWorldStateScope scope = HintBal(bundle, bal, logger);
            Assert.That(() => LifecycleSingleReads(reader), Is.EqualTo(new[] { paths[1] }).After(5000, 10));
            scope.UpdateRootHash();
            Assert.That(LoggedErrors(logger), Does.Contain("prefetch read failed"));
        }
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero, "only the snapshot's reference may remain after the failed prefetch");
    }

    /// <summary>Hints <paramref name="bal"/> to a scope over <paramref name="bundle"/> as block processing does, returning once its accounts and slots are buffered.</summary>
    /// <remarks>
    /// The node group prefetch keeps running in the background until <see cref="PbtWorldStateScope.UpdateRootHash"/> stops it.
    /// The scope is left undisposed, as disposing it would dispose the caller's bundle.
    /// </remarks>
    private static PbtWorldStateScope HintBal(PbtSnapshotBundle bundle, ReadOnlyBlockAccessList bal, InterfaceLogger? logger = null)
    {
        PbtWorldStateScope scope = new(StateId.PreGenesis, null, bundle, Substitute.For<IWorldStateScopeProvider.ICodeDb>(), Substitute.For<IPbtCommitTarget>(),
            NullPbtChildHeaderSource.Instance, PooledRefCountingMemoryProvider.Instance, new PbtConfig(), logger is null ? LimboLogs.Instance : new OneLoggerLogManager(new ILogger(logger)));
        scope.HintBal(bal).GetAwaiter().GetResult();
        return scope;
    }

    /// <summary>A reader holding every account but <paramref name="missing"/> and no slot, so the buffering finds the hinted accounts present.</summary>
    private static IPbtPersistence.IReader Reader(params Address[] missing)
    {
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.GetAccount(Arg.Any<ValueHash256>()).Returns(new Account(1, 1).ToPbtAccount());
        foreach (Address address in missing) reader.GetAccount(PbtStateKey.AddressKeyHash(address)).Returns((PbtAccount?)null);
        reader.GetSlotRun(Arg.Any<PbtPath>()).Returns(_ => SlotRun.Empty);
        reader.GetSlotRun(Arg.Any<PbtStoragePath>()).Returns(_ => SlotRun.Empty);
        return reader;
    }

    private static InterfaceLogger ErrorLogger()
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        return logger;
    }

    /// <summary>Every error the scope logged, with its exception, as one text.</summary>
    private static string LoggedErrors(InterfaceLogger logger) => string.Join(Environment.NewLine, logger.ReceivedCalls()
        .Where(static call => call.GetMethodInfo().Name == nameof(InterfaceLogger.Error))
        .SelectMany(static call => call.GetArguments().Select(static argument => argument?.ToString())));

    private static PbtStorageNodePath FirstGroup(Address address, byte zone) => zone == Eip8297KeyDerivation.AccountZone
        ? PbtTestPaths.Prefix<PbtStorageNodePath>(Bytes.FromHexString("00" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()[..6]), 30)
        : new(Bytes.FromHexString("ff" + PbtStateKey.AddressKeyHash(address).Bytes.ToHexString()), 264);

    private static ReadOnlyBlockAccessList LifecyclePrefetchBal() => Build.A.BlockAccessList.WithAccountChanges(
        Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject,
        Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;

    private static RefCountingMemory LifecyclePrefetchPayload(TrackingMemoryProvider memory, PbtStorageNodePath path, long descendantBytes)
    {
        long[] sizes = new long[PbtThreeLevelGroupGeometry.BoundarySlots];
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
