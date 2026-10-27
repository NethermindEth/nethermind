// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.ScopeProvider;
using Nethermind.State.Pbt.Snapshot;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtNodeGroupPrefetchTests
{
    private const int SlotsPerAccount = 512;
    private const int FillerAccounts = 4096;
    private const int HeaderSlot = 7;

    public enum PrefetchCase { AllWrites, EmptyBal, SnapshotBacked, MissingAccount }

    private static readonly Lazy<(ValueHash256 Root, PbtPhysicalPayload[] Groups)> _tree = new(BuildTree);
    private static readonly Lazy<Dictionary<PbtStorageNodePath, byte[]>> _payloads = new(static () =>
        _tree.Value.Groups.ToDictionary(static group => group.Key, static group => group.Payload.ToArray()));

    [Test]
    public void NodeGroupPrefetch_walks_the_paths_of_the_written_keys([Values] PrefetchCase prefetch)
    {
        AccountChangesBuilder account = Build.An.AccountChanges.WithAddress(TestItem.AddressA)
            .WithStorageChanges(HeaderSlot, new StorageChange(1, 1u))
            .WithStorageChanges(1000, new StorageChange(1, 1u))
            .WithStorageChanges(2000, new StorageChange(1, 1u))
            .WithStorageReads(1001);
        ReadOnlyBlockAccessList bal = prefetch == PrefetchCase.EmptyBal ? Build.A.BlockAccessList.TestObject : Build.A.BlockAccessList.WithAccountChanges(
            (prefetch == PrefetchCase.MissingAccount ? account : account.WithBalanceChanges(new BalanceChange(1, 100))).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageReads(1000).TestObject,
            Build.An.AccountChanges.WithAddress(TestItem.AddressC).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;
        Address[] missing = prefetch == PrefetchCase.MissingAccount ? [TestItem.AddressA] : [];
        // A snapshot holds the tree but for the storage of C, so the walk of C still reads persistence.
        Func<PbtStorageNodePath, bool>? inMemory = prefetch == PrefetchCase.SnapshotBacked ? static path => !IsUnderStorageOf(path, TestItem.AddressC) : null;
        List<Action<PbtSnapshotBundle>> walks = prefetch == PrefetchCase.EmptyBal ? [] : [WalkStorage(TestItem.AddressC, 1000)];
        if (prefetch is PrefetchCase.AllWrites or PrefetchCase.SnapshotBacked)
        {
            ValueHash256 addressHash = AddressHash(TestItem.AddressA);
            walks.Add(bundle => bundle.WarmUp<PbtPath, PbtNodePath>(Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.BasicDataLeafKey)));
            walks.Add(bundle => bundle.WarmUp<PbtPath, PbtNodePath>(Eip8297KeyDerivation.HeaderStorageKey(addressHash, HeaderSlot)));
            walks.Add(WalkStorage(TestItem.AddressA, 1000));
            walks.Add(WalkStorage(TestItem.AddressA, 2000));
        }
        PbtStorageNodePath[] expected = Walked(inMemory, walks);

        (PbtSnapshotBundle bundle, PbtSnapshotTestExtensions.GroupLoadingReader reader) = Bundle(Persisted, inMemory, missing);
        using (bundle)
        {
            PbtWorldStateScope scope = HintBal(bundle, bal);
            Assert.That(() => reader.Reads.Distinct(), Is.EquivalentTo(expected).After(5000, 10));
            scope.UpdateRootHash();
            PbtStorageNodePath[] prefetched = [.. reader.Reads.Distinct()];
            reader.ClearReads();
            byte[]?[] kept = [.. expected.Select(bundle.GetNodeGroup)];
            using (Assert.EnterMultipleScope())
            {
                Assert.That(prefetched, Is.EquivalentTo(expected));
                Assert.That(expected, prefetch == PrefetchCase.EmptyBal ? Is.Empty : Is.Not.Empty);
                Assert.That(kept, Is.EqualTo(expected.Select(Persisted)));
                Assert.That(reader.Reads, Is.Empty, "the fold must read prefetched groups from the bundle");
            }
        }
    }

    [Test]
    public void NodeGroupPrefetch_logs_a_read_that_fails_after_earlier_reads()
    {
        int reads = 0;
        (PbtSnapshotBundle bundle, _) = Bundle(path =>
        {
            if (Interlocked.Increment(ref reads) == 2) throw new System.IO.IOException("single prefetch read failed");
            return Persisted(path);
        });
        using (bundle)
        {
            InterfaceLogger logger = ErrorLogger();
            PbtWorldStateScope scope = HintBal(bundle, LifecyclePrefetchBal(), logger);
            Assert.That(() => Volatile.Read(ref reads), Is.GreaterThanOrEqualTo(2).After(5000, 10));
            scope.UpdateRootHash();

            Assert.That(LoggedErrors(logger), Does.Contain("single prefetch read failed"));
        }
    }

    [Test]
    public void NodeGroupPrefetch_logs_a_failed_persistence_read_beside_a_snapshot_group()
    {
        // A snapshot holds the tree but for the storage of B, so only the walk of B reaches persistence.
        Func<PbtStorageNodePath, bool> inMemory = static path => !IsUnderStorageOf(path, TestItem.AddressB);
        PbtStorageNodePath firstRead = Walked(inMemory, [WalkStorage(TestItem.AddressB, 1000)])[0];
        (PbtSnapshotBundle bundle, PbtSnapshotTestExtensions.GroupLoadingReader reader) = Bundle(static _ => throw new System.IO.IOException("prefetch read failed"), inMemory);
        using (bundle)
        {
            InterfaceLogger logger = ErrorLogger();
            PbtWorldStateScope scope = HintBal(bundle, LifecyclePrefetchBal(), logger);
            Assert.That(() => reader.Reads, Is.EqualTo(new[] { firstRead }).After(5000, 10));
            scope.UpdateRootHash();
            Assert.That(LoggedErrors(logger), Does.Contain("prefetch read failed"));
        }
    }

    /// <summary>The groups <paramref name="walks"/> read from persistence, run on a bundle of their own.</summary>
    private static PbtStorageNodePath[] Walked(Func<PbtStorageNodePath, bool>? inMemory, IEnumerable<Action<PbtSnapshotBundle>> walks)
    {
        (PbtSnapshotBundle bundle, PbtSnapshotTestExtensions.GroupLoadingReader reader) = Bundle(Persisted, inMemory);
        using (bundle)
        {
            foreach (Action<PbtSnapshotBundle> walk in walks) walk(bundle);
            return [.. reader.Reads.Distinct()];
        }
    }

    private static Action<PbtSnapshotBundle> WalkStorage(Address address, UInt256 slot)
    {
        PbtStoragePath key = StorageKey(address, slot);
        return bundle => bundle.WarmUp<PbtStoragePath, PbtStorageNodePath>(key);
    }

    private static PbtStoragePath StorageKey(Address address, in UInt256 slot)
    {
        ValueHash256 address32 = address.ToHash();
        return Eip8297KeyDerivation.StorageKey(address32.Bytes, AddressHash(address), slot);
    }

    /// <summary>A bundle over the persisted tree whose groups <paramref name="loadGroup"/> serves, with the groups <paramref name="inMemory"/> selects published in memory.</summary>
    /// <remarks>The bundle has a group record buffer of its own, so no other test's roots resolve in it.</remarks>
    private static (PbtSnapshotBundle Bundle, PbtSnapshotTestExtensions.GroupLoadingReader Reader) Bundle(Func<PbtStorageNodePath, byte[]?> loadGroup,
        Func<PbtStorageNodePath, bool>? inMemory = null, params Address[] missing)
    {
        PbtSnapshotTestExtensions.GroupLoadingReader reader = Reader(loadGroup, missing);
        PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(new PbtResourcePool(new PbtConfig()), reader,
            new PbtGroupRecordBuffer(0, PbtGroupRecordBuffer.DefaultGenerationBytes, 1));
        if (inMemory is not null) bundle.SetPhysicalTree(_tree.Value.Groups.Where(group => inMemory(group.Key)));
        return (bundle, reader);
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

    /// <summary>A reader of the persisted tree holding every account but <paramref name="missing"/> and no slot, so the buffering finds the hinted accounts present, and the groups of <paramref name="loadGroup"/>.</summary>
    private static PbtSnapshotTestExtensions.GroupLoadingReader Reader(Func<PbtStorageNodePath, byte[]?> loadGroup, params Address[] missing)
    {
        IPbtPersistence.IReader reader = Substitute.For<IPbtPersistence.IReader>();
        reader.CurrentRoot.Returns(_tree.Value.Root);
        reader.GetAccount(Arg.Any<ValueHash256>()).Returns(new Account(1, 1).ToPbtAccount());
        foreach (Address address in missing) reader.GetAccount(AddressHash(address)).Returns((PbtAccount?)null);
        reader.GetSlotRun(Arg.Any<PbtPath>()).Returns(_ => SlotRun.Empty);
        reader.GetSlotRun(Arg.Any<PbtStoragePath>()).Returns(_ => SlotRun.Empty);
        return reader.LoadingGroupsFrom(loadGroup);
    }

    /// <summary>A tree with the basic data, a header slot and <see cref="SlotsPerAccount"/> slots of A, B and C beside filler accounts, so their paths cross several groups.</summary>
    private static (ValueHash256 Root, PbtPhysicalPayload[] Groups) BuildTree()
    {
        List<(byte[] Key, byte[]? Value)> leaves = [];
        foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC })
        {
            ValueHash256 addressHash = AddressHash(address);
            leaves.Add((Eip8297KeyDerivation.AccountKey(addressHash, PbtKeyDerivation.BasicDataLeafKey).Bytes.ToArray(), PbtStoreTestExtensions.Value(1)));
            leaves.Add((Eip8297KeyDerivation.HeaderStorageKey(addressHash, HeaderSlot).Bytes.ToArray(), PbtStoreTestExtensions.Value(1)));
            for (UInt256 slot = 1000; slot < 1000 + SlotsPerAccount; slot++) leaves.Add((StorageKey(address, slot).Bytes.ToArray(), PbtStoreTestExtensions.Value(1)));
        }
        for (int filler = 0; filler < FillerAccounts; filler++)
            leaves.Add((Eip8297KeyDerivation.AccountKey(ValueKeccak.Compute(BitConverter.GetBytes(filler)), PbtKeyDerivation.BasicDataLeafKey).Bytes.ToArray(), PbtStoreTestExtensions.Value(1)));

        using PbtNodeGroupStore store = new();
        ValueHash256 root = store.Fold(default, leaves);
        return (root, [.. store.ExportPhysicalPayloads()]);
    }

    private static byte[]? Persisted(PbtStorageNodePath path) => _payloads.Value.GetValueOrDefault(path);

    private static bool IsUnderStorageOf(PbtStorageNodePath path, Address address) =>
        path.BitDepth >= PbtNodeGroupLayout.StemTopDepth
        && path.ToPathArray().AsSpan(0, 33).SequenceEqual([Eip8297KeyDerivation.StorageZone, .. AddressHash(address).Bytes]);

    private static ValueHash256 AddressHash(Address address) => Eip8297KeyDerivation.AddressHash(address.ToHash().Bytes);

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

    private static ReadOnlyBlockAccessList LifecyclePrefetchBal() => Build.A.BlockAccessList.WithAccountChanges(
        Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject,
        Build.An.AccountChanges.WithAddress(TestItem.AddressB).WithStorageChanges(1000, new StorageChange(1, 1u)).TestObject).TestObject;
}
