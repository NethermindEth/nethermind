// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;
using Autofac;
using ConcurrentCollections;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatSnapTrieFactoryTests
{
    [Test]
    public void Storage_constructor_failure_does_not_acquire_transaction()
    {
        IPersistence persistence = Substitute.For<IPersistence>();
        NoOpWriteBatch batch = new();
        persistence.CreateAtomicWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<WriteFlags>()).Returns(batch);
        ILogManager logs = Substitute.For<ILogManager>();
        logs.GetClassLogger<PatriciaTree>().Returns(_ => throw new IOException("Tree logger acquisition failed."));
        FlatSnapTrieFactory factory = new(persistence, Substitute.For<IPersistenceManager>(), new MemColumnsDb<FlatDbColumns>(), new MemDb(), new SyncConfig(), logs);
        Assert.Throws<IOException>(() => factory.CreateStorageTree(ValueKeccak.Zero));
        Assert.That(batch.DisposeCount, Is.Zero);
        persistence.DidNotReceive().CreateAtomicWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<WriteFlags>());
        persistence.DidNotReceive().CreateReader(Arg.Any<ReaderFlags>());
    }

    [Test]
    public void Terminal_atomic_storage_batch_rejects_empty_snapshot_scans(
        [Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout, [Values(0, 1, 2)] int terminal)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        SnapshotFaults faults = new(SnapshotOwner.Transaction, failCommit: terminal == 1) { EnableCleanupFault = false };
        FaultColumnsDb faultedDb = new(db, faults);
        IPersistence persistence = layout == FlatLayout.Flat
            ? new RocksDbPersistence(faultedDb, LimboLogs.Instance, NullTrieNodeLog.Instance)
            : new FlatInTriePersistence(faultedDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
        using IPersistence.IAtomicPersistenceBatch batch = persistence.CreateAtomicWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        if (terminal == 0) batch.Commit();
        else if (terminal == 1)
        {
            Assert.That(Assert.Throws<IOException>(() => batch.Commit()), Is.SameAs(faults.CommitFailure));
        }
        else batch.Dispose();

        Assert.Throws<ObjectDisposedException>(() => batch.SelfDestruct(TestItem.AddressA));
        Assert.Throws<ObjectDisposedException>(() => batch.DeleteAccountRange(ValueKeccak.Zero, ValueKeccak.Zero));
        Assert.Throws<ObjectDisposedException>(() => batch.DeleteStorageRange(ValueKeccak.Zero, ValueKeccak.Zero, ValueKeccak.Zero));
        Assert.Throws<ObjectDisposedException>(() => batch.DeleteStateTrieNodeRange(ValueKeccak.Zero, ValueKeccak.Zero));
        Assert.Throws<ObjectDisposedException>(() => batch.DeleteStorageTrieNodeRange(ValueKeccak.Zero, ValueKeccak.Zero, ValueKeccak.Zero));
    }

    [Test]
    public void Throwing_storage_commit_leaves_no_native_slots_or_nodes_before_retry(
        [Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout, [Values] bool failDuringTrie)
    {
        string path = System.IO.Path.Combine("testdb", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(path);
        DbConfig dbConfig = new();
        using HyperClockCacheWrapper blockCache = new();
        RocksDbConfigFactory options = new(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false);
        RocksDbFactory dbFactory = new(options, dbConfig, blockCache, new RealFileSystem(), LimboLogs.Instance, path);
        using IColumnsDb<FlatDbColumns> database = dbFactory.CreateColumnsDb<FlatDbColumns>(new DbSettings(DbNames.Flat, path) { DeleteOnStart = true });
        IPersistence persistence = layout == FlatLayout.Flat
            ? new RocksDbPersistence(database, LimboLogs.Instance, NullTrieNodeLog.Instance)
            : new FlatInTriePersistence(database, LimboLogs.Instance, NullTrieNodeLog.Instance);
        Hash256 address = TestItem.KeccakA;
        PathWithStorageSlot[] entries = Enumerable.Range(0, 350)
            .Select(i => new PathWithStorageSlot(Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256, new byte[] { 1 }))
            .OrderBy(slot => slot.Path).ToArray();
        Hash256 expectedRoot;
        using (SnapshotableMemDb source = new())
        {
            StorageTree storage = new(new RawScopedTrieStore(source), LimboLogs.Instance);
            foreach (PathWithStorageSlot slot in entries) storage.Set(slot.Path, slot.SlotRlpValue, false);
            storage.UpdateRootHash();
            expectedRoot = storage.RootHash;
        }

        using (FlatSnapStorageTree tree = new(persistence.CreateReader(),
            new ThrowingStorageWriteBatch(persistence.CreateAtomicWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL), failDuringTrie),
            address, enableDoubleWriteCheck: true, LimboLogs.Instance))
        {
            tree.BulkSetAndUpdateRootHash(entries);
            Assert.Throws<IOException>(() => tree.Commit(ValueKeccak.MaxValue));
        }

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            using IPersistence.IFlatIterator slots = reader.CreateStorageIterator(address.ValueHash256);
            Assert.That(slots.MoveNext(), Is.False);
        }
        Assert.That(database.GetColumnDb(FlatDbColumns.StorageNodes).GetAllKeys(), Is.Empty);

        FlatSnapTrieFactory factory = new(persistence, Substitute.For<IPersistenceManager>(), database, new MemDb(), new SyncConfig { EnableSnapDoubleWriteCheck = true }, LimboLogs.Instance);
        using (ISnapTree<PathWithStorageSlot> retry = factory.CreateStorageTree(address.ValueHash256))
        {
            retry.BulkSetAndUpdateRootHash(entries);
            Assert.That(retry.RootHash, Is.EqualTo(expectedRoot));
            retry.Commit(ValueKeccak.MaxValue);
        }

        using IPersistence.IPersistenceReader after = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach (PathWithStorageSlot slot in entries)
            {
                UInt256 value = default;
                Assert.That(after.TryGetStorageRaw(address.ValueHash256, slot.Path, ref value), Is.True);
                Assert.That(value, Is.EqualTo(UInt256.One));
            }
            Assert.That(after.TryLoadStorageRlp(address, TreePath.Empty, ReadFlags.None,
                static (rlp, expected) => Assert.That(Keccak.Compute(rlp), Is.EqualTo(expected)), expectedRoot), Is.True);
        }
    }

    private sealed class ThrowingStorageWriteBatch(IPersistence.IAtomicPersistenceBatch inner, bool failDuringTrie, int failAfter = 300) : IPersistence.IAtomicPersistenceBatch
    {
        private int _slots;
        private int _nodes;
        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
            if (!failDuringTrie && ++_slots == failAfter) throw new IOException("Storage slot staging failed.");
        }
        public void SetStorageTrieNode(in ValueHash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp)
        {
            inner.SetStorageTrieNode(address, path, rlp);
            if (failDuringTrie && ++_nodes == 2) throw new IOException("Storage trie staging failed.");
        }
        public void Commit() => inner.Commit();
        public void Dispose() => inner.Dispose();
        public void SelfDestruct(Address addr) => inner.SelfDestruct(addr);
        public void SetAccount(Address addr, Account? account) => inner.SetAccount(addr, account);
        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value) => inner.SetStorage(addr, slot, value);
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStateTrieNode(path, rlp);
        public void SetAccountRaw(in ValueHash256 addrHash, in AccountStruct account) => inner.SetAccountRaw(addrHash, account);
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteAccountRange(fromPath, toPath);
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) => inner.DeleteStorageRange(addressHash, fromPath, toPath);
        public bool IsStorageRangeEmpty(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) => inner.IsStorageRangeEmpty(addressHash, from, to);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) => inner.DeleteStateTrieNodeRange(from, to);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to, Action? beforeDelete = null) => inner.DeleteStorageTrieNodeRange(addressHash, from, to, beforeDelete);
        public void SelfDestructRange(in ValueHash256 from, in ValueHash256 to, in ValueHash256? kept) => inner.SelfDestructRange(from, to, kept);
        public void SetMigrationCursor(in MigrationCursor? cursor) => inner.SetMigrationCursor(cursor);
    }

    [Test]
    public void Interrupted_storage_batch_retries_without_rewriting_committed_prefix(
        [Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout, [Values] bool doubleWriteCheck)
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        (FlatDbColumns Column, string Key, string Value)[] nativePrefix = [];
        SnapshotFaults faults = new(SnapshotOwner.Transaction, false)
        {
            EnableCleanupFault = false,
            FailBatchNumber = 2,
            FailStageAt = 300,
            BeforeFailedBatch = database => nativePrefix = ReadNativeRows(database)
        };
        using IContainer container = CreateStorageContainer(layout, doubleWriteCheck, faults, tempPath.Path, LimboLogs.Instance);
        ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();
        IPersistence stored = container.Resolve<IPersistence>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        ISnapProvider provider = container.Resolve<ISnapProvider>();
        using Reactive.AnonymousDisposable _ = new(provider.Dispose);
        PathWithStorageSlot[] entries = new[] { new PathWithStorageSlot(ValueKeccak.Zero, [1]) }
            .Concat(Enumerable.Range(1, 349).Select(i => new PathWithStorageSlot(Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256, [1])))
            .OrderBy(slot => slot.Path).ToArray();
        Hash256 root;
        using (ISnapTree<PathWithStorageSlot> source = factory.CreateStorageTree(ValueKeccak.Zero))
        {
            source.BulkSetAndUpdateRootHash(entries);
            root = source.RootHash;
        }
        Account account = new(0, 0, root, Keccak.OfAnEmptyString);
        PathWithAccount[] accounts =
        [
            new(Keccak.Compute("first").ValueHash256, account.ToStruct()),
            new(Keccak.Compute("second").ValueHash256, account.ToStruct()),
            new(Keccak.Compute("third").ValueHash256, account.ToStruct())
        ];
        tracker.IsFinished(out SnapSyncBatch? initial);
        using (initial)
        {
            tracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.MaxValue, false);
            provider.ReleaseRequest(initial!, true);
        }
        foreach (PathWithAccount entry in accounts) tracker.EnqueueAccountStorage(entry);
        tracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        using (SlotsAndProofs response = StorageResponse(entries, 3))
        {
            Assert.That(() => provider.AddStorageRange(batch!.StorageRangeRequest!, response), Throws.InstanceOf<IOException>());
            provider.ReleaseRequest(batch!, false);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nativePrefix, Is.Not.Empty);
            Assert.That(ReadNativeRows(container.Resolve<IColumnsDb<FlatDbColumns>>()), Is.EquivalentTo(nativePrefix));
        }
        using (IPersistence.IPersistenceReader beforeRetry = stored.CreateReader(ReaderFlags.Sync))
        {
            UInt256 value = default;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(faults.StagedWrites, Is.EqualTo(300));
                Assert.That(beforeRetry.TryGetStorageRaw(accounts[0].Path, ValueKeccak.Zero, ref value), Is.True);
                Assert.That(beforeRetry.TryGetStorageRaw(accounts[1].Path, ValueKeccak.Zero, ref value), Is.False);
                Assert.That(beforeRetry.TryLoadStorageRlp(accounts[1].Path.ToCommitment(), TreePath.Empty, ReadFlags.None, static (_, _) => { }, 0), Is.False);
            }
            using IPersistence.IFlatIterator partialSlots = beforeRetry.CreateStorageIterator(accounts[1].Path);
            Assert.That(partialSlots.MoveNext(), Is.False);
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        using (SlotsAndProofs response = StorageResponse(entries, 2))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
                Assert.That(retry.StorageRangeRequest.Accounts[0], Is.SameAs(accounts[1]));
                Assert.That(retry.StorageRangeRequest.Accounts[1], Is.SameAs(accounts[2]));
            }
            Assert.That(provider.AddStorageRange(retry!.StorageRangeRequest!, response), Is.EqualTo(AddRangeResult.OK));
            provider.ReleaseRequest(retry!, true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
        using IPersistence.IPersistenceReader reader = stored.CreateReader(ReaderFlags.Sync);
        foreach (PathWithAccount entry in accounts)
        {
            foreach (PathWithStorageSlot slot in entries)
            {
                UInt256 value = default;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(reader.TryGetStorageRaw(entry.Path, slot.Path, ref value), Is.True);
                    Assert.That(value, Is.EqualTo(UInt256.One));
                }
            }
        }
        Assert.That(faults.BatchCount, Is.EqualTo(4));
    }

    public enum SnapshotOwner { Transaction, TreeReader, CachedReader }

    [Test]
    public void Storage_publication_preserves_backend_errors_and_logs_owned_cleanup_failures(
        [Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout,
        [Values] SnapshotOwner owner,
        [Values] bool commitFailure)
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        SnapshotFaults faults = new(owner, commitFailure);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsError.Returns(true);
        ILogManager logs = new OneLoggerLogManager(new ILogger(logger));
        using IContainer container = CreateStorageContainer(layout, true, faults, tempPath.Path, logs);
        ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();
        Assert.That(faults.LiveReaderSnapshots, Is.EqualTo(1));
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        ISnapProvider provider = container.Resolve<ISnapProvider>();
        using Reactive.AnonymousDisposable _ = new(provider.Dispose);
        PathWithStorageSlot[] slots = Enumerable.Range(0, 350)
            .Select(i => new PathWithStorageSlot(Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256, [1]))
            .OrderBy(slot => slot.Path).ToArray();
        Hash256 root;
        using (ISnapTree<PathWithStorageSlot> source = factory.CreateStorageTree(TestItem.KeccakA.ValueHash256))
        {
            source.BulkSetAndUpdateRootHash(slots);
            root = source.RootHash;
        }
        PathWithAccount account = new(TestItem.KeccakA.ValueHash256, new Account(0, 0, root, Keccak.OfAnEmptyString).ToStruct());
        tracker.IsFinished(out SnapSyncBatch? initial);
        using (initial)
        {
            tracker.UpdateAccountRangePartitionProgress(ValueKeccak.MaxValue, ValueKeccak.MaxValue, false);
            provider.ReleaseRequest(initial!, true);
        }
        tracker.EnqueueAccountStorage(account);
        tracker.IsFinished(out SnapSyncBatch? request);
        faults.Armed = true;
        using (request)
        using (SlotsAndProofs response = StorageResponse(slots))
        {
            if (commitFailure)
            {
                IOException exception = Assert.Throws<IOException>(() => provider.AddStorageRange(request!.StorageRangeRequest!, response))!;
                Assert.That(exception, Is.SameAs(faults.CommitFailure));
                provider.ReleaseRequest(request!, false);
            }
            else
            {
                Assert.That(provider.AddStorageRange(request!.StorageRangeRequest!, response), Is.EqualTo(AddRangeResult.OK));
                provider.ReleaseRequest(request!, true);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(faults.DisposeCounts, Is.EqualTo(new[] { 1, 1, 1 }));
            Assert.That(faults.AtomicDisposals, Is.EqualTo(1));
            Assert.That(faults.CleanupFailures, Is.EqualTo(1));
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.EqualTo(!commitFailure));
        }
        logger.Received(1).Error(Arg.Any<string>(), Arg.Is<Exception>(exception => ReferenceEquals(exception, faults.CleanupFailure)));
        IColumnsDb<FlatDbColumns> database = container.Resolve<IColumnsDb<FlatDbColumns>>();
        if (commitFailure)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(database.GetColumnDb(FlatDbColumns.Storage).GetAllKeys(), Is.Empty);
                Assert.That(database.GetColumnDb(FlatDbColumns.StorageNodes).GetAllKeys(), Is.Empty);
            }
            faults.Armed = false;
            faults.FailCommit = false;
            tracker.IsFinished(out SnapSyncBatch? retry);
            using (retry)
            using (SlotsAndProofs response = StorageResponse(slots))
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(retry!.StorageRangeRequest!.Accounts.Single(), Is.SameAs(account));
                    Assert.That(provider.AddStorageRange(retry!.StorageRangeRequest!, response), Is.EqualTo(AddRangeResult.OK));
                }
                provider.ReleaseRequest(retry!, true);
            }
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
        }
        using IPersistence.IPersistenceReader reader = container.Resolve<IPersistence>().CreateReader(ReaderFlags.Sync);
        foreach (PathWithStorageSlot slot in slots)
        {
            UInt256 value = default;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.TryGetStorageRaw(account.Path, slot.Path, ref value), Is.True);
                Assert.That(value, Is.EqualTo(UInt256.One));
            }
        }
    }

    [Test]
    public void Stale_storage_tail_verification_does_not_acquire_a_native_transaction(
        [Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout, [Values] bool invalidRoot)
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        SnapshotFaults faults = new(SnapshotOwner.Transaction, false) { EnableCleanupFault = false };
        using IContainer container = CreateStorageContainer(layout, false, faults, tempPath.Path, LimboLogs.Instance);
        ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();
        SnapProvider provider = (SnapProvider)container.Resolve<ISnapProvider>();
        using Reactive.AnonymousDisposable _ = new(provider.Dispose);
        PathWithStorageSlot[] firstSlots = [new(ValueKeccak.Zero, [1])];
        PathWithStorageSlot[] tailSlots = [new(ValueKeccak.Zero, [2])];
        Hash256 firstRoot = StorageRoot(firstSlots);
        Hash256 tailRoot = StorageRoot(tailSlots);
        PathWithAccount stale = new(ValueKeccak.Zero, new Account(0, 1, firstRoot, Keccak.OfAnEmptyString).ToStruct());
        PathWithAccount current = new(ValueKeccak.Zero.IncrementPath(), stale.Account);
        PathWithAccount tail = new(ValueKeccak.MaxValue.DecrementPath(), new Account(0, 1,
            invalidRoot ? firstRoot : tailRoot, Keccak.OfAnEmptyString).ToStruct());
        PathWithAccount omitted = new(ValueKeccak.MaxValue, current.Account);
        PathWithAccount[] previousAccounts = [stale, current, tail, omitted];
        Hash256 previousRoot = StateRoot(previousAccounts);
        Assert.That(provider.AddAccountRange(1, previousRoot, ValueKeccak.Zero, previousAccounts), Is.EqualTo(AddRangeResult.OK));
        PathWithAccount[] currentAccounts =
        [
            new(stale.Path, new Account(0, 2).ToStruct()),
            new(current.Path, current.Account),
            new(tail.Path, tail.Account),
            new(omitted.Path, omitted.Account)
        ];
        Hash256 currentRoot = StateRoot(currentAccounts);
        Assert.That(provider.AddAccountRange(2, currentRoot, ValueKeccak.Zero, currentAccounts), Is.EqualTo(AddRangeResult.OK));
        using StorageRange request = new()
        {
            RootHash = currentRoot,
            Accounts = new ArrayPoolList<PathWithAccount>(new[] { stale, currentAccounts[1], currentAccounts[2], currentAccounts[3] })
        };
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(3)
            {
                new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [3]) },
                new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, [3]) },
                new ArrayPoolList<PathWithStorageSlot>(tailSlots)
            }
        };
        int batchesBeforeResponse = faults.BatchCount;
        AddRangeResult result = provider.AddStorageRange(request, response);
        IColumnsDb<FlatDbColumns> database = container.Resolve<IColumnsDb<FlatDbColumns>>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(faults.BatchCount, Is.EqualTo(batchesBeforeResponse));
            Assert.That(faults.BatchCount, Is.Zero);
            Assert.That(database.GetColumnDb(FlatDbColumns.Storage).GetAllKeys(), Is.Empty);
            Assert.That(database.GetColumnDb(FlatDbColumns.StorageNodes).GetAllKeys(), Is.Empty);
            Assert.That(container.Resolve<IStateSyncPivot>().UpdatedStorages, Is.EquivalentTo(new[] { stale.Path.ToCommitment() }));
        }

        Hash256 StorageRoot(PathWithStorageSlot[] slots)
        {
            using ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(ValueKeccak.Zero);
            tree.BulkSetAndUpdateRootHash(slots);
            return tree.RootHash;
        }

        Hash256 StateRoot(PathWithAccount[] accounts)
        {
            using ISnapTree<PathWithAccount> tree = factory.CreateStateTree();
            tree.BulkSetAndUpdateRootHash(accounts);
            return tree.RootHash;
        }
    }

    private static (FlatDbColumns Column, string Key, string Value)[] ReadNativeRows(IColumnsDb<FlatDbColumns> database) =>
        database.ColumnKeys.SelectMany(column => database.GetColumnDb(column).GetAll()
            .Select(entry => (Column: column, Key: Convert.ToHexString(entry.Key), Value: Convert.ToHexString(entry.Value))))
            .ToArray();

    private static IContainer CreateStorageContainer(FlatLayout layout, bool doubleWriteCheck,
        SnapshotFaults faults, string basePath, ILogManager logs)
    {
        IProcessExitSource exit = Substitute.For<IProcessExitSource>();
        exit.Token.Returns(new CancellationToken(canceled: true));
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        ConcurrentHashSet<Hash256> updatedStorages = [];
        pivot.UpdatedStorages.Returns(updatedStorages);
        pivot.GetPivotHeader().Returns(Nethermind.Core.Test.Builders.Build.A.BlockHeader.WithStateRoot(Keccak.EmptyTreeHash).TestObject);
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new InitConfig { BaseDbPath = basePath },
                new SyncConfig { SnapSync = true, SnapSyncAccountRangePartitionCount = 1, EnableSnapDoubleWriteCheck = doubleWriteCheck },
                new FlatDbConfig { Enabled = true, Layout = layout, EnableCarryForwardCache = true }))
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .AddColumnDatabase<FlatDbColumns>(DbNames.Flat, static settings => settings.PersistRepairMarkerUntilAcknowledged = true)
            .AddSingleton(faults)
            .AddSingleton(exit)
            .AddSingleton(pivot)
            .AddSingleton(logs)
            .AddDecorator<IColumnsDb<FlatDbColumns>, FaultColumnsDb>();
        IContainer container = builder.Build();
        try
        {
            using (Assert.EnterMultipleScope())
            {
                IColumnsDb<FlatDbColumns> database = container.Resolve<IColumnsDb<FlatDbColumns>>();
                foreach (FlatDbColumns column in database.ColumnKeys)
                    Assert.That(database.GetColumnDb(column), Is.TypeOf<ColumnDb>());
            }
            return container;
        }
        catch
        {
            container.Dispose();
            throw;
        }
    }

    private static SlotsAndProofs StorageResponse(PathWithStorageSlot[] entries, int count = 1)
    {
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> ranges = [with(count)];
        for (int i = 0; i < count; i++) ranges.Add(new ArrayPoolList<PathWithStorageSlot>(entries));
        return new SlotsAndProofs { PathsAndSlots = ranges };
    }

    private sealed class SnapshotFaults(SnapshotOwner owner, bool failCommit)
    {
        internal int ReaderSnapshotsCreated;
        internal int LiveReaderSnapshots;
        internal bool Armed;
        internal bool FailCommit = failCommit;
        internal bool EnableCleanupFault = true;
        internal int FailBatchNumber;
        internal int FailStageAt;
        internal int BatchCount;
        internal int StagedWrites;
        internal Action<IColumnsDb<FlatDbColumns>>? BeforeFailedBatch;
        internal IOException CommitFailure { get; } = new("Injected native commit failure.");
        internal IOException CleanupFailure { get; } = new("Injected snapshot cleanup failure.");
        internal int[] DisposeCounts { get; } = new int[3];
        internal int AtomicDisposals;
        internal int CleanupFailures;
        internal void Released(SnapshotOwner released)
        {
            if (!Armed) return;
            DisposeCounts[(int)released]++;
            if (EnableCleanupFault && released == owner && CleanupFailures == 0)
            {
                CleanupFailures++;
                throw CleanupFailure;
            }
        }
    }

    private sealed class FaultColumnsDb(IColumnsDb<FlatDbColumns> inner, SnapshotFaults faults) : IColumnsDb<FlatDbColumns>
    {
        public IDb GetColumnDb(FlatDbColumns key) => inner.GetColumnDb(key);
        public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => inner.StartWriteBatch();
        public IAtomicColumnsWriteBatch<FlatDbColumns> StartAtomicWriteBatch()
        {
            if (faults.BatchCount + 1 == faults.FailBatchNumber) faults.BeforeFailedBatch?.Invoke(inner);
            return new FaultAtomicBatch(inner.StartAtomicWriteBatch(), faults, ++faults.BatchCount);
        }
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => new FaultSnapshot(inner.CreateSnapshot(), faults, SnapshotOwner.Transaction);
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot(bool sequentialReadAhead)
        {
            IColumnDbSnapshot<FlatDbColumns> snapshot = inner.CreateSnapshot(sequentialReadAhead);
            SnapshotOwner owner = faults.ReaderSnapshotsCreated++ == 0 ? SnapshotOwner.CachedReader : SnapshotOwner.TreeReader;
            faults.LiveReaderSnapshots++;
            return new FaultSnapshot(snapshot, faults, owner);
        }
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() { }
    }

    private sealed class FaultSnapshot(IColumnDbSnapshot<FlatDbColumns> inner, SnapshotFaults faults, SnapshotOwner owner) : IColumnDbSnapshot<FlatDbColumns>
    {
        private bool _disposed;
        public IReadOnlyKeyValueStore GetColumn(FlatDbColumns key) => inner.GetColumn(key);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            inner.Dispose();
            if (owner != SnapshotOwner.Transaction) faults.LiveReaderSnapshots--;
            faults.Released(owner);
        }
    }

    private sealed class FaultAtomicBatch(IAtomicColumnsWriteBatch<FlatDbColumns> inner, SnapshotFaults faults, int batchNumber) : IAtomicColumnsWriteBatch<FlatDbColumns>
    {
        private bool _disposed;
        public IWriteBatch GetColumnBatch(FlatDbColumns key) => faults.FailBatchNumber == batchNumber
            ? new StageFaultWriteBatch(inner.GetColumnBatch(key), faults)
            : inner.GetColumnBatch(key);
        public void Clear() => inner.Clear();
        public void Commit()
        {
            if (faults.FailCommit) throw faults.CommitFailure;
            inner.Commit();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            inner.Dispose();
            if (faults.Armed) faults.AtomicDisposals++;
        }
    }

    private sealed class StageFaultWriteBatch(IWriteBatch inner, SnapshotFaults faults) : IWriteBatch
    {
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            inner.Set(key, value, flags);
            if (++faults.StagedWrites == faults.FailStageAt) throw new IOException("Native account staging failed.");
        }
        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.Merge(key, value, flags);
        public void Clear() => inner.Clear();
        public void Dispose() { }
    }

    [Test]
    public void Incomplete_range_proof_cannot_read_persisted_nodes([Values] bool doubleWriteCheck)
    {
        using MemDb db = new();
        StateTree source = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        PathWithAccount first = new(ValueKeccak.Zero, new Account(1, 100).ToStruct());
        source.Set(first.Path, first.Account.ToAccount());
        source.Set(first.Path.IncrementPath(), first.Account.ToAccount());
        source.Commit();
        AccountProofCollector collector = new(first.Path.Bytes);
        source.Accept(collector, source.RootHash);
        byte[][] proof = collector.BuildResult().Proof;
        (FlatSnapTrieFactory factory, _, IPersistence.IPersistenceReader reader) = Build(doubleWriteCheck);
        ((SubstitutablePersistenceReader)reader).TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>())
            .Returns(call => proof[call.Arg<TreePath>().Length == 63 ? 1 : ^1]);
        using IByteArrayList incomplete = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { proof[0] });

        Assert.That(SnapProviderHelper.VerifyAccountRange(factory, source.RootHash, ValueKeccak.Zero,
            ValueKeccak.MaxValue, [first], incomplete), Is.EqualTo(AddRangeResult.InvalidProof));
        ((SubstitutablePersistenceReader)reader).DidNotReceive().TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>());
    }

    private static (FlatSnapTrieFactory factory, IPersistence persistence, IPersistence.IPersistenceReader reader) Build(bool doubleWriteCheck = false, IPersistenceManager? persistenceManager = null)
    {
        IPersistence persistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader reader = Substitute.For<SubstitutablePersistenceReader>();
        persistence.CreateReader(Arg.Any<ReaderFlags>()).Returns(reader);
        // NSubstitute proxies fail with InvalidProgramException on the ReadOnlySpan parameters,
        // so the write batch is a hand-rolled no-op fake.
        persistence.CreateAtomicWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<Nethermind.Core.WriteFlags>())
            .Returns(_ => new NoOpWriteBatch());
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<Nethermind.Core.WriteFlags>())
            .Returns(_ => new NoOpWriteBatch());

        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.EnableSnapDoubleWriteCheck.Returns(doubleWriteCheck);

        FlatSnapTrieFactory factory = new(persistence, persistenceManager ?? Substitute.For<IPersistenceManager>(), new MemColumnsDb<FlatDbColumns>(), new MemDb(), syncConfig, LimboLogs.Instance);
        return (factory, persistence, reader);
    }

    private class NoOpWriteBatch : IPersistence.IAtomicPersistenceBatch
    {
        internal int DisposeCount;
        public void Commit() { }

        public void SelfDestruct(Address addr) { }
        public void SetAccount(Address addr, Account? account) { }
        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value) { }
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageTrieNode(in ValueHash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) { }
        public void SetAccountRaw(in ValueHash256 addrHash, in AccountStruct account) { }
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath, in ValueHash256? keptAccount) { }
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) { }
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to, Action? beforeDelete = null) { }
        public bool IsStorageRangeEmpty(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) => false;
        public void SetMigrationCursor(in MigrationCursor? cursor) { }
        public void Dispose() => DisposeCount++;
    }

    [Test]
    public void EnsureInitialize_ClearsDatabase()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

        factory.EnsureInitialize();

        persistence.Received(1).Clear();
    }

    // The wipe drops the persisted state pointer. A stale cached pointer (genesis, flushed at startup) kept
    // HasStateForBlock true for the whole sync, so every read of it retried for 5 s and failed with -32603.
    [Test]
    public void EnsureInitialize_ResetsPersistedStatePointerAfterClear()
    {
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build(persistenceManager: persistenceManager);

        factory.EnsureInitialize();

        Received.InOrder(() =>
        {
            persistence.Clear();
            persistenceManager.ResetPersistedStateId();
        });
    }

    [Test]
    public void EnsureInitialize_WarnsOnlyWhenDiscardingState_AndLogsWipeDuration([Values] bool hasState)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);
        if (hasState)
        {
            using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            batch.SetAccountRaw(Keccak.Zero.ValueHash256, new AccountStruct(1));
        }

        TestLogger logger = new();
        FlatSnapTrieFactory factory = new(persistence, Substitute.For<IPersistenceManager>(), db, new MemDb(), Substitute.For<ISyncConfig>(), new OneLoggerLogManager(new ILogger(logger)));

        factory.EnsureInitialize();

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using IPersistence.IFlatIterator iterator = reader.CreateAccountIterator(ValueKeccak.Zero, ValueKeccak.MaxValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(iterator.MoveNext(), Is.False);
            Assert.That(logger.LogList.Count(static l => l.Contains("cannot resume")), Is.EqualTo(hasState ? 1 : 0));
            Assert.That(logger.LogList.Count(static l => l.StartsWith("Cleared database in")), Is.EqualTo(1));
            Assert.That(logger.LogList.FindIndex(static l => l == "Clearing database"), Is.EqualTo(0), "the first line must come before the probe");
        }
    }

    [Test]
    public void FinalizeSync_FlushesPersistence()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

        factory.FinalizeSync();

        persistence.Received(1).Flush();
    }

    [Test]
    public void CreateTrees_DoNotClearDatabase()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

        using (ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree())
        using (ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default))
        using (ISnapTree<PathWithStorageSlot> nonDefaultStorageTree = factory.CreateStorageTree(new ValueHash256(Bytes.FromHexString("11" + new string('0', 62)))))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stateTree, Is.Not.Null);
                Assert.That(storageTree, Is.Not.Null);
                Assert.That(nonDefaultStorageTree, Is.Not.Null);
            }
        }

        // Clear is the runner's responsibility via EnsureInitialize; tree creation must not invoke it.
        persistence.DidNotReceive().Clear();
    }

    [Test]
    public void RangePhase_NeverCarriesIntoTheNextRun()
    {
        (FlatSnapTrieFactory flatFactory, _, _) = Build();
        ISnapTrieFactory factory = flatFactory;

        factory.MarkRangePhaseFinished();

        Assert.That(factory.IsRangePhaseFinished(), Is.False);
    }

    [Test]
    public void Factory_CreatesTreesWithoutThrowing_ForBothDoubleWriteFlagValues([Values] bool doubleWriteCheck)
    {
        (FlatSnapTrieFactory factory, _, _) = Build(doubleWriteCheck);

        using ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree();
        using ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateTree, Is.Not.Null);
            Assert.That(storageTree, Is.Not.Null);
        }
    }

    [Test]
    public void ProoflessRange_DoesNotCreateReader()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();

        using (ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default))
        {
            // Proofless flow: no boundary stitching, no IsPersisted, double-write check off.
            storageTree.BulkSetAndUpdateRootHash([new PathWithStorageSlot(new ValueHash256("0x1000000000000000000000000000000000000000000000000000000000000000"), [1])]);
            storageTree.Commit(Keccak.MaxValue);
        }

        using (ISnapTree<PathWithAccount> stateTree = factory.CreateStateTree())
        {
        }

        // Creating a reader takes a DB snapshot per account per storage response — it must stay lazy.
        persistence.DidNotReceive().CreateReader(Arg.Any<ReaderFlags>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Reader_IsCreatedOnlyOnUse_AndCannotBeUsedAfterDisposal(bool readBeforeDisposal)
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, IPersistence.IPersistenceReader reader) = Build();

        ISnapTree<PathWithStorageSlot> storageTree = factory.CreateStorageTree(default);
        TreePath path = TreePath.Empty;
        if (readBeforeDisposal)
        {
            storageTree.IsPersisted(path, Keccak.EmptyTreeHash.ValueHash256);
            storageTree.IsPersisted(path, Keccak.EmptyTreeHash.ValueHash256);
        }

        storageTree.Dispose();
        storageTree.Dispose();

        Assert.Throws<ObjectDisposedException>(() => storageTree.IsPersisted(path, Keccak.EmptyTreeHash.ValueHash256));
        persistence.Received(readBeforeDisposal ? 1 : 0).CreateReader(ReaderFlags.Sync);
        reader.Received(readBeforeDisposal ? 1 : 0).Dispose();
    }

    // Snap sync skips boundary cleanup only where the batch proves the range empty, so a storage tree's batch must pass
    // the probe to the atomic batch it defers; the default answer would run the cleanup on every commit.
    [Test]
    public void StorageTree_ProbesTheAtomicBatch_ThatItsCleanupAndCommitWriteTo()
    {
        (FlatSnapTrieFactory factory, IPersistence persistence, _) = Build();
        FakeWriteBatch batch = new() { StorageRangeEmpty = true };
        persistence.CreateAtomicWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<WriteFlags>()).Returns(batch);
        ValueHash256 account = Path("1");

        using (ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(account))
        {
            tree.BulkSetAndUpdateRootHash([new PathWithStorageSlot(Path("2"), SlotRlp)]);
            tree.DeleteOutsideCompleteTree(ValueKeccak.Zero, ValueKeccak.MaxValue);
            tree.Commit(ValueKeccak.MaxValue);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.IsStorageRangeEmptyCalls, Is.EqualTo(new[] { (account, ValueKeccak.Zero, ValueKeccak.MaxValue) }));
            Assert.That(batch.DeleteStorageRangeCalls, Is.Empty);
            Assert.That(batch.DeleteStorageTrieNodeRangeCalls, Is.Empty);
            Assert.That(batch.SetStorageRawEncodedCalls, Has.Count.EqualTo(1));
            persistence.Received(1).CreateAtomicWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<WriteFlags>());
        }
    }

    private static readonly ValueHash256 FirstPartitionLimit = new("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
    private static readonly byte[] BranchRlp = BuildBranchRlp();
    private static readonly byte[] SlotRlp = Rlp.Encode(new byte[] { 1 });
    private static readonly string[] StorageNodePaths = ["", "1", "8", "c", "1111111111111111", "cccccccccccccccc"];

    // The last account before the checkpoint, and the first path still to download, which shares its 20-byte key.
    private static readonly ValueHash256 LastAccount = Path("9");
    private static readonly ValueHash256 NextAccount = LastAccount.IncrementPath();

    [Test]
    public void EnsureInitialize_Resuming_DiscardsWhatTheResumedRunRequestsAgain([Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = CreatePersistence(db, layout);

        ValueHash256 writtenAfter = Path("a");
        ValueHash256 inFinishedPartition = Path("1");
        ValueHash256 storageQueued = Path("2");
        ValueHash256 storageQueuedFromSlot8 = Path("3");
        ValueHash256 refreshedFromSlot8 = Path("4");
        ValueHash256 slot1 = Path("1");
        ValueHash256 slotC = Path("c");

        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            foreach (ValueHash256 account in (ValueHash256[])[LastAccount, writtenAfter, inFinishedPartition, storageQueued, storageQueuedFromSlot8, refreshedFromSlot8])
            {
                batch.SetAccountRaw(account, new AccountStruct(1));
                batch.SetStorageRawEncoded(account, slot1, SlotRlp);
                batch.SetStorageRawEncoded(account, slotC, SlotRlp);
                foreach (string node in StorageNodePaths) batch.SetStorageTrieNode(account.ToCommitment(), TreePath.FromHexString(node), BranchRlp);
            }

            foreach (string node in (string[])["", "1", "8", "a", "80000000", "90000000", "a0000000", "8000000000000000000000", "9000000000000000000000"])
            {
                batch.SetStateTrieNode(TreePath.FromHexString(node), BranchRlp);
            }

            // Leaves on the path to the next account: one of the last account downloaded, one past it.
            batch.SetStateTrieNode(TreePath.FromHexString("9"), BuildLeafRlp(new string('0', 63)));
            batch.SetStateTrieNode(TreePath.FromHexString("900"), BuildLeafRlp("f" + new string('0', 60)));
        }

        CreateResumingFactory(persistence, db).SaveCheckpoint(CreateCheckpoint(
            storagesToRetrieve: [new PathWithAccount(storageQueued, new AccountStruct(1))],
            storageRanges: [new StorageRangeProgress([new PathWithAccount(storageQueuedFromSlot8, new AccountStruct(1))], Path("8"), null)],
            accountsToRefresh: [new AccountWithStorageStartingHash { PathAndAccount = new PathWithAccount(refreshedFromSlot8, new AccountStruct(1)), StorageStartingHash = Path("8"), StorageHashLimit = ValueKeccak.MaxValue }]));

        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, db);
        factory.EnsureInitialize();

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.LoadCheckpoint(), Is.Not.Null, "the checkpoint is what the resumed run starts from");

            foreach ((ValueHash256 account, bool kept) in (ValueTuple<ValueHash256, bool>[])
                     [(LastAccount, true), (writtenAfter, false), (inFinishedPartition, true), (storageQueued, true), (storageQueuedFromSlot8, true), (refreshedFromSlot8, true)])
            {
                Assert.That(reader.GetAccountRaw(account) is not null, Is.EqualTo(kept), $"account {account}");
            }

            foreach ((ValueHash256 account, ValueHash256 slot, bool kept) in (ValueTuple<ValueHash256, ValueHash256, bool>[])
                     [
                         (LastAccount, slot1, true), (LastAccount, slotC, true),
                         (writtenAfter, slot1, false), (writtenAfter, slotC, false),
                         (inFinishedPartition, slot1, true), (inFinishedPartition, slotC, true),
                         (storageQueued, slot1, false), (storageQueued, slotC, false),
                         (storageQueuedFromSlot8, slot1, true), (storageQueuedFromSlot8, slotC, false),
                         (refreshedFromSlot8, slot1, true), (refreshedFromSlot8, slotC, false),
                     ])
            {
                UInt256 value = default;
                Assert.That(reader.TryGetStorageRaw(account, slot, ref value), Is.EqualTo(kept), $"slot {slot} of {account}");
            }

            foreach ((ValueHash256 account, string[] keptNodes) in (ValueTuple<ValueHash256, string[]>[])
                     [
                         (LastAccount, StorageNodePaths),
                         (writtenAfter, []),
                         (inFinishedPartition, StorageNodePaths),
                         (storageQueued, []),
                         // The root and the node at 8 are on the path to slot 8, the ones at c are past it.
                         (storageQueuedFromSlot8, ["1", "1111111111111111"]),
                         (refreshedFromSlot8, ["1", "1111111111111111"]),
                     ])
            {
                foreach (string node in StorageNodePaths)
                {
                    Assert.That(reader.LoadStorageRlp(account.ToCommitment(), TreePath.FromHexString(node), ReadFlags.None) is not null,
                        Is.EqualTo(keptNodes.Contains(node)), $"storage node {node} of {account}");
                }
            }

            // The branches at 9 lie on the path to the next account and the nodes at a past it. The leaf at 9 is on that
            // path too, but it holds the last account downloaded before the checkpoint, so it stays.
            foreach ((string node, bool kept) in (ValueTuple<string, bool>[])
                     [
                         ("", false), ("1", true), ("8", true), ("9", true), ("900", false), ("a", false), ("80000000", true),
                         ("90000000", false), ("a0000000", false), ("8000000000000000000000", true), ("9000000000000000000000", false),
                     ])
            {
                Assert.That(reader.LoadStateRlp(TreePath.FromHexString(node), ReadFlags.None) is not null, Is.EqualTo(kept), $"state node {node}");
            }
        }
    }

    public enum Unresumable { ResumeDisabled, PartitionCountChanged, Unreadable, WipedByAnOlderVersion, WipeByAnOlderVersionInterrupted }

    [Test]
    public void EnsureInitialize_StartsOver_WhenTheCheckpointCannotBeResumed([Values] Unresumable reason)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);
        WriteLastAccount(persistence);

        CreateResumingFactory(persistence, db).SaveCheckpoint(CreateCheckpoint());
        switch (reason)
        {
            case Unresumable.Unreadable:
                BasePersistence.WriteSnapCheckpoint(db, [0xff]);
                break;
            case Unresumable.WipedByAnOlderVersion:
                // The older version leaves the checkpoint behind, then syncs data the checkpoint does not describe.
                WipeLikeAnOlderVersion(db, maxKeys: int.MaxValue);
                WriteLastAccount(persistence);
                break;
            case Unresumable.WipeByAnOlderVersionInterrupted:
                // The least such a wipe commits: the first key of its first batch.
                WipeLikeAnOlderVersion(db, maxKeys: 1);
                break;
        }

        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, db,
            partitionCount: reason == Unresumable.PartitionCountChanged ? 4 : 2,
            snapResume: reason != Unresumable.ResumeDisabled);
        // Callers ahead of EnsureInitialize, such as the BAL healing anchor, must not resume what it is going to discard.
        if (reason != Unresumable.ResumeDisabled) Assert.That(factory.LoadCheckpoint(), Is.Null);
        factory.EnsureInitialize();

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccountRaw(LastAccount), Is.Null);
            Assert.That(BasePersistence.ReadSnapCheckpoint(db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        }
    }

    [Test]
    public void SaveCheckpoint_FlushesTheWritesItCovers_AndDeletesItWhenNull()
    {
        IPersistence persistence = Substitute.For<IPersistence>();
        MemColumnsDb<FlatDbColumns> db = new();
        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, db);

        factory.SaveCheckpoint(CreateCheckpoint());
        persistence.Received(1).Flush();
        Assert.That(factory.LoadCheckpoint()?.AnchorHash, Is.EqualTo(TestItem.KeccakA));

        factory.SaveCheckpoint(null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.LoadCheckpoint(), Is.Null);
            Assert.That(BasePersistence.HasSnapCheckpointSentinel(db), Is.False);
        }
    }

    [Test]
    public void Verified_state_cleanup_invalidates_a_checkpoint_only_if_it_removes_covered_state([Values] bool overlaps, [Values] bool existing)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = CreatePersistence(db, FlatLayout.Flat);
        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, db);
        TreePath path = TreePath.FromHexString(overlaps ? "0" : "f");
        if (existing)
        {
            using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            batch.SetAccountRaw(path.ToLowerBoundPath(), new AccountStruct(1));
        }
        factory.SaveCheckpoint(CreateCheckpoint());

        using ISnapTree<PathWithAccount> tree = factory.CreateStateTree();
        TrieNode extension = TrieNodeFactory.CreateExtension([1], new TrieNode(NodeType.Unknown, TestItem.KeccakA));
        tree.DeleteOutsideChildren(path, extension, path.ToLowerBoundPath(), path.ToUpperBoundPath());

        Assert.That(factory.LoadCheckpoint(), overlaps && existing ? Is.Null : Is.Not.Null);
    }

    [Test]
    public void Verified_state_cleanup_invalidates_checkpoint_for_last_account_key_in_deleted_range()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = CreatePersistence(db, FlatLayout.Flat);
        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, db);
        TreePath path = TreePath.FromHexString("0");
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.SetAccountRaw(path.ToUpperBoundPath(), new AccountStruct(1));
        }
        factory.SaveCheckpoint(CreateCheckpoint());

        using ISnapTree<PathWithAccount> tree = factory.CreateStateTree();
        TrieNode extension = TrieNodeFactory.CreateExtension([1], new TrieNode(NodeType.Unknown, TestItem.KeccakA));
        tree.DeleteOutsideChildren(path, extension, path.ToLowerBoundPath(), path.ToUpperBoundPath());

        Assert.That(factory.LoadCheckpoint(), Is.Null);
    }

    [Test]
    public void Verified_storage_cleanup_invalidates_checkpoint_before_removing_saved_data([Values] StorageCleanupData data)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        IPersistence persistence = CreatePersistence(db, FlatLayout.Flat);
        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, db);
        ValueHash256 account = Path("0");
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            if (data == StorageCleanupData.Slot) batch.SetStorageRawEncoded(account, Path("2"), SlotRlp);
            if (data == StorageCleanupData.RangeNode) batch.SetStorageTrieNode(account.ToCommitment(), TreePath.FromHexString("2"), BranchRlp);
            if (data == StorageCleanupData.ExactNode) batch.SetStorageTrieNode(account.ToCommitment(), TreePath.FromHexString("1"), BranchRlp);
        }
        factory.SaveCheckpoint(CreateCheckpoint());

        using ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(account);
        TrieNode extension = TrieNodeFactory.CreateExtension([1, 2], new TrieNode(NodeType.Unknown, TestItem.KeccakA));
        tree.DeleteOutsideChildren(TreePath.Empty, extension, ValueKeccak.Zero, ValueKeccak.MaxValue);

        Assert.That(factory.LoadCheckpoint(), data == StorageCleanupData.None ? Is.Not.Null : Is.Null);
    }

    public enum StorageCleanupData { None, Slot, RangeNode, ExactNode }

    [Test]
    public void Failed_storage_commit_keeps_slots_and_trie_nodes_out_of_the_database([Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout, [Values] bool throughMigration)
    {
        using RejectingCommitDb db = new();
        IPersistence persistence = CreatePersistence(db, layout);
        if (throughMigration) persistence = new HalfPathFallbackPersistence(persistence, Substitute.For<INodeStorage>(), new HalfPathMigrationCursor(db));
        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, db);
        ValueHash256 account = Path("1");
        ValueHash256 slot = Path("2");
        using ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(account);
        tree.BulkSetAndUpdateRootHash([new PathWithStorageSlot(slot, SlotRlp)]);
        Assert.Throws<InvalidOperationException>(() => tree.Commit(ValueKeccak.MaxValue));

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        using ISnapTree<PathWithStorageSlot> reopenedTree = factory.CreateStorageTree(account);
        UInt256 value = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.CommitAttempted, Is.True);
            Assert.That(reader.TryGetStorageRaw(account, slot, ref value), Is.False);
            Assert.That(reopenedTree.IsPersisted(TreePath.Empty, tree.RootHash.ValueHash256), Is.False);
        }
    }

    private sealed class RejectingCommitDb : SnapshotableMemColumnsDb<FlatDbColumns>, IColumnsDb<FlatDbColumns>
    {
        internal bool CommitAttempted;

        IAtomicColumnsWriteBatch<FlatDbColumns> IColumnsDb<FlatDbColumns>.StartAtomicWriteBatch()
        {
            IAtomicColumnsWriteBatch<FlatDbColumns> staged = StartAtomicWriteBatch();
            IAtomicColumnsWriteBatch<FlatDbColumns> batch = Substitute.For<IAtomicColumnsWriteBatch<FlatDbColumns>>();
            batch.GetColumnBatch(Arg.Any<FlatDbColumns>()).Returns(call => staged.GetColumnBatch(call.Arg<FlatDbColumns>()));
            batch.When(b => b.Dispose()).Do(_ => staged.Dispose());
            batch.When(b => b.Commit()).Do(_ =>
            {
                CommitAttempted = true;
                throw new InvalidOperationException("Commit failed");
            });
            return batch;
        }
    }

    [Test]
    public void SaveCheckpoint_KeepsTheLastCheckpoint_WhenTheWritesItCoversFailToFlush()
    {
        FlushFailingDb accounts = new();
        IColumnsDb<FlatDbColumns> db = CreateColumnsDbWithAccounts(accounts);
        FlatSnapTrieFactory factory = CreateResumingFactory(Substitute.For<IPersistence>(), db);
        factory.SaveCheckpoint(CreateCheckpoint());

        accounts.FailFlush = true;

        Assert.Throws<InvalidOperationException>(() => factory.SaveCheckpoint(CreateCheckpoint(anchorHash: TestItem.KeccakB)));
        Assert.That(factory.LoadCheckpoint()?.AnchorHash, Is.EqualTo(TestItem.KeccakA));
    }

    [Test]
    public void WriteSnapCheckpoint_WritesNoCheckpoint_WhenItsSentinelFailsToFlush()
    {
        IColumnsDb<FlatDbColumns> db = CreateColumnsDbWithAccounts(new FlushFailingDb { FailFlush = true });

        Assert.Throws<InvalidOperationException>(() => BasePersistence.WriteSnapCheckpoint(db, [1, 2, 3]));
        Assert.That(BasePersistence.ReadSnapCheckpoint(db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
    }

    // RocksDB commits a batch without the WAL every few hundred writes, which bounds the memory a large discard takes.
    [Test]
    public void EnsureInitialize_Resuming_DeletesWithoutTheWal([Values(FlatLayout.Flat, FlatLayout.FlatInTrie)] FlatLayout layout)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> inner = new();
        DeleteFlagsSpyColumnsDb db = new(inner);
        IPersistence persistence = CreatePersistence(db, layout);
        ValueHash256 writtenAfter = Path("a");
        ValueHash256 storageQueued = Path("2");
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            foreach (ValueHash256 account in (ValueHash256[])[writtenAfter, storageQueued])
            {
                batch.SetAccountRaw(account, new AccountStruct(1));
                batch.SetStorageRawEncoded(account, Path("1"), SlotRlp);
                batch.SetStorageTrieNode(account.ToCommitment(), TreePath.FromHexString("1"), BranchRlp);
            }

            batch.SetStateTrieNode(TreePath.FromHexString("a"), BranchRlp);
        }

        CreateResumingFactory(persistence, db).SaveCheckpoint(CreateCheckpoint(storagesToRetrieve: [new PathWithAccount(storageQueued, new AccountStruct(1))]));
        CreateResumingFactory(persistence, db).EnsureInitialize();

        Assert.That(db.DeleteFlags, Is.Not.Empty);
        Assert.That(db.DeleteFlags, Has.All.Matches<WriteFlags>(static flags => flags.HasFlag(WriteFlags.DisableWAL)));
    }

    private static ValueHash256 Path(string hexPrefix) => new(hexPrefix.PadRight(64, '0'));

    private static void WriteLastAccount(IPersistence persistence)
    {
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        batch.SetAccountRaw(LastAccount, new AccountStruct(1));
    }

    /// <summary>Deletes the first <paramref name="maxKeys"/> keys in the order a wipe of a version that predates the snap checkpoint does.</summary>
    /// <remarks>Such a wipe goes through every column but the metadata, each in key order, as a RocksDB iterator yields them.</remarks>
    private static void WipeLikeAnOlderVersion(IColumnsDb<FlatDbColumns> db, int maxKeys)
    {
        foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
        {
            if (column == FlatDbColumns.Metadata) continue;

            IDb columnDb = db.GetColumnDb(column);
            foreach (byte[] key in columnDb.GetAllKeys().Order(Bytes.Comparer))
            {
                if (maxKeys-- == 0) return;
                columnDb.Remove(key);
            }
        }
    }

    private static byte[] BuildBranchRlp()
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch[0] = TrieNodeFactory.CreateLeaf([1], new byte[32]);
        branch[1] = TrieNodeFactory.CreateLeaf([2], new byte[32]);
        return Encode(branch);
    }

    private static byte[] BuildLeafRlp(string keyNibblesHex) =>
        Encode(TrieNodeFactory.CreateLeaf(keyNibblesHex.Select(static c => (byte)Convert.ToInt32(c.ToString(), 16)).ToArray(), new byte[32]));

    private static byte[] Encode(TrieNode node)
    {
        TreePath empty = TreePath.Empty;
        node.ResolveKey(NullTrieNodeResolver.Instance, ref empty);
        return node.FullRlp.ToArray()!;
    }

    private static IPersistence CreatePersistence(IColumnsDb<FlatDbColumns> db, FlatLayout layout) => layout switch
    {
        FlatLayout.Flat => new RocksDbPersistence(db, LimboLogs.Instance, NullTrieNodeLog.Instance),
        FlatLayout.FlatInTrie => new FlatInTriePersistence(db, LimboLogs.Instance, NullTrieNodeLog.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    private static FlatSnapTrieFactory CreateResumingFactory(IPersistence persistence, IColumnsDb<FlatDbColumns> db, int partitionCount = 2, bool snapResume = true) =>
        new(persistence, Substitute.For<IPersistenceManager>(), db, new MemDb(),
            new SyncConfig { SnapResume = snapResume, SnapSyncAccountRangePartitionCount = partitionCount }, LimboLogs.Instance);

    private static IColumnsDb<FlatDbColumns> CreateColumnsDbWithAccounts(IDb accounts)
    {
        MemColumnsDb<FlatDbColumns> columns = new();
        IColumnsDb<FlatDbColumns> db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(c => c.Arg<FlatDbColumns>() == FlatDbColumns.Account ? accounts : columns.GetColumnDb(c.Arg<FlatDbColumns>()));
        return db;
    }

    /// <summary>Two account partitions: the first finished, the second downloaded up to <see cref="LastAccount"/>.</summary>
    private static SnapCheckpoint CreateCheckpoint(
        PathWithAccount[]? storagesToRetrieve = null,
        StorageRangeProgress[]? storageRanges = null,
        AccountWithStorageStartingHash[]? accountsToRefresh = null,
        Hash256? anchorHash = null) => new()
        {
            AnchorNumber = 1,
            AnchorHash = anchorHash ?? TestItem.KeccakA,
            Partitions =
        [
            new AccountRangeProgress(FirstPartitionLimit, FirstPartitionLimit, MoreAccountsToRight: false),
            new AccountRangeProgress(NextAccount, ValueKeccak.MaxValue, MoreAccountsToRight: true),
        ],
            StoragesToRetrieve = storagesToRetrieve ?? [],
            StorageRanges = storageRanges ?? [],
            AccountsToRefresh = accountsToRefresh ?? [],
            CodesToRetrieve = [],
            UpdatedStorages = [],
        };

    private sealed class FlushFailingDb : MemDb, IDbMeta
    {
        public bool FailFlush { get; set; }

        void IDbMeta.FlushOrThrow()
        {
            if (FailFlush) throw new InvalidOperationException("Flush failed");
        }
    }

    /// <summary>Records the write flags of every delete staged through its batches.</summary>
    private sealed class DeleteFlagsSpyColumnsDb(IColumnsDb<FlatDbColumns> inner) : IColumnsDb<FlatDbColumns>
    {
        public List<WriteFlags> DeleteFlags { get; } = [];

        public IDb GetColumnDb(FlatDbColumns key) => inner.GetColumnDb(key);
        public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => new SpyColumnsWriteBatch(inner.StartWriteBatch(), DeleteFlags);
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() { }

        private sealed class SpyColumnsWriteBatch(IColumnsWriteBatch<FlatDbColumns> inner, List<WriteFlags> deleteFlags) : IColumnsWriteBatch<FlatDbColumns>
        {
            public IWriteBatch GetColumnBatch(FlatDbColumns key) => new SpyWriteBatch(inner.GetColumnBatch(key), deleteFlags);
            public void Clear() => inner.Clear();
            public void Dispose() => inner.Dispose();
        }

        private sealed class SpyWriteBatch(IWriteBatch inner, List<WriteFlags> deleteFlags) : IWriteBatch
        {
            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                if (value is null) deleteFlags.Add(flags);
                inner.Set(key, value, flags);
            }

            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => inner.Merge(key, value, flags);
            public void Clear() => inner.Clear();

            // The column batches are owned and committed by the columns batch this view was taken from.
            public void Dispose() { }
        }
    }
}
