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
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Snapshot;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupPrefetchTests
{
    public enum PrefetchSkip { EmptyBal, SnapshotBacked, MissingAccount }

    [Test]
    public void NodeGroupPrefetch_skips_empty_or_snapshot_backed_lists([Values] PrefetchSkip skip)
    {
        AccountChangesBuilder account = Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u));
        // The storage group of C is always prefetched, so its read shows the prefetch has run.
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            (skip == PrefetchSkip.MissingAccount ? account : account.WithBalanceChanges(new BalanceChange(1, 100))).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressC).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;
        IPbtPersistence.IReader reader = Reader(skip == PrefetchSkip.MissingAccount ? [TestItem.AddressA] : []);
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader.LoadingGroupsByLease());
        PbtStorageNodePath[] firstGroups = [FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.AccountZone), FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.StorageZone)];
        if (skip == PrefetchSkip.SnapshotBacked)
        {
            TrackingMemoryProvider memory = new();
            bundle.SetTree([.. firstGroups.Select(path =>
            {
                using RefCountingMemory payload = LifecyclePrefetchPayload(memory, path, 0);
                return (path, (byte[]?)payload.GetSpan().ToArray());
            })]);
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
        [Values(-1L, 0L, 512L, 513L, 4 * 512L, 4 * 512 + 1L, 1024 * 1024L)] long descendantBytes,
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
        ValueHash256 addressHash = Eip8297KeyDerivation.AddressHash(TestItem.AddressA.ToHash().Bytes);
        PbtStoragePath storagePath = Eip8297KeyDerivation.StorageKey(TestItem.AddressA.ToHash().Bytes, addressHash, 1000);
        PbtStorageNodePath storageGroup = new(storagePath.Bytes[..33], 264);
        TrackingMemoryProvider memory = new();
        IPbtPersistence.IReader reader = Reader();
        reader.GetNodeGroup(Arg.Any<PbtStorageNodePath>()).Returns(call => ReadPayload(call.Arg<PbtStorageNodePath>()));

        bool Absent(PbtStorageNodePath path) => descendantBytes < 0 || path.BitDepth == 264 && !path.Equals(storageGroup);
        RefCountingMemory? ReadPayload(PbtStorageNodePath path) => Absent(path) ? null : LifecyclePrefetchPayload(memory, path, descendantBytes);
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader.LoadingGroupsByLease());
        PbtStorageNodePath deeperSnapshot = PbtTestPaths.Prefix<PbtStorageNodePath>(storagePath.Bytes, 266);
        if (snapshotBacked)
        {
            using RefCountingMemory? payload = ReadPayload(storageGroup);
            using RefCountingMemory? deeperPayload = ReadPayload(deeperSnapshot);
            bundle.SetTree((storageGroup, payload?.GetSpan().ToArray()), (deeperSnapshot, deeperPayload?.GetSpan().ToArray()));
        }
        HashSet<PbtStorageNodePath> firstGroups = [FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.AccountZone), FirstGroup(TestItem.AddressA, Eip8297KeyDerivation.StorageZone), FirstGroup(TestItem.AddressB, Eip8297KeyDerivation.StorageZone)];
        // A fold's read warms nothing, so the prefetch reads again every group the fold read from persistence.
        List<PbtStorageNodePath> reread = readByFold ? [.. firstGroups] : [];
        if (readByFold)
            foreach (PbtStorageNodePath path in firstGroups) ((IDisposable?)bundle.GetNodeGroup(path))?.Dispose();

        PbtWorldStateScope scope = HintBal(bundle, bal);

        HashSet<PbtStorageNodePath> expected = firstGroups;
        int levels = descendantBytes switch { <= 512 => 0, <= 4 * 512 => 1, <= 16 * 512 => 2, <= 64 * 512 => 3, <= 256 * 512 => 4, <= 1024 * 512 => 5, _ => 6 };
        foreach (UInt256 slot in new UInt256[] { 1000, 1001, 2000 })
        {
            PbtStoragePath key = Eip8297KeyDerivation.StorageKey(TestItem.AddressA.ToHash().Bytes, addressHash, slot);
            for (int depth = 266; depth <= 264 + 2 * levels; depth += 2)
                expected.Add(PbtTestPaths.Prefix<PbtStorageNodePath>(key.Bytes, depth));
        }
        if (snapshotBacked && descendantBytes >= 0)
        {
            expected.Remove(storageGroup);
            expected.Remove(deeperSnapshot);
            reread.Remove(storageGroup);
        }
        PbtStorageNodePath[] expectedReads = [.. expected, .. reread];
        Assert.That(() => LifecycleSingleReads(reader), Is.EquivalentTo(expectedReads).After(5000, 10));
        scope.UpdateRootHash();
        PbtStorageNodePath[] singles = LifecycleSingleReads(reader);
        reader.ClearReceivedCalls();
        foreach (PbtStorageNodePath path in expected.Where(path => !Absent(path)))
        {
            using RefCountingMemory? kept = bundle.GetNodeGroup(path);
            using RefCountingMemory? persisted = ReadPayload(path);
            Assert.That(kept?.GetSpan().ToArray(), Is.EqualTo(persisted?.GetSpan().ToArray()));
        }
        bool keptGroupsReread = LifecycleSingleReads(reader).Length != 0;
        bundle.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(singles, Is.EquivalentTo(expectedReads));
            Assert.That(keptGroupsReread, Is.False, "the fold must read prefetched groups from the bundle");
            Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
        }
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
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader.LoadingGroupsByLease());
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
        using (RefCountingMemory payload = LifecyclePrefetchPayload(memory, paths[0], 0)) content.SetTree((paths[0], payload.GetSpan().ToArray()));
        using (PbtSnapshotBundle bundle = new(PbtSnapshotBundleTestExtensions.Chain(pool, content),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reader.LoadingGroupsByLease(), recordDetailedMetrics: false, new PbtPersistedSnapshotList(0), PbtSnapshotBundleTestExtensions.NoNodeGroupPin, slotFilterBitsPerKey: 0),
            PbtSnapshotTestExtensions.DefaultGroupRecordBuffer, pool, PbtResourcePool.Usage.MainBlockProcessing, filterInMemorySlotReads: false))
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
            NullPbtChildHeaderSource.Instance, new PbtConfig(), logger is null ? LimboLogs.Instance : new OneLoggerLogManager(new ILogger(logger)));
        scope.HintBal(bal).GetAwaiter().GetResult();
        return scope;
    }

    /// <summary>A reader holding every account but <paramref name="missing"/> and no slot, so the buffering finds the hinted accounts present.</summary>
    private static IPbtPersistence.IReader Reader(params Address[] missing)
    {
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        // A non-empty persisted tree, so the prefetch has groups to read.
        reader.CurrentRoot.Returns(TestItem.KeccakA.ValueHash256);
        reader.GetAccount(Arg.Any<ValueHash256>()).Returns(new Account(1, 1).ToPbtAccount());
        foreach (Address address in missing) reader.GetAccount(Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes)).Returns((PbtAccount?)null);
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
        ? PbtTestPaths.Prefix<PbtStorageNodePath>(Bytes.FromHexString("00" + Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes).Bytes.ToHexString()[..6]), 32)
        : new(Bytes.FromHexString("ff" + Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes).Bytes.ToHexString()), 264);

    private static ReadOnlyBlockAccessList LifecyclePrefetchBal() => Build.A.BlockAccessList.WithAccountChanges(
        Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject,
        Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;

    private static RefCountingMemory LifecyclePrefetchPayload(TrackingMemoryProvider memory, PbtStorageNodePath path, long descendantBytes)
    {
        long[] sizes = new long[PbtTwoLevelGroupGeometry.BoundarySlots];
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
