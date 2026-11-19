// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Synchronization.FastSync;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class PatriciaSnapTrieFactoryTests
{
    // Pinned: databases written by earlier versions are read back with this key and value.
    private static readonly byte[] AccountProgressKey = "AccountProgressKey"u8.ToArray();

    [Test]
    public void Storage_constructor_failure_does_not_acquire_transaction()
    {
        using FullPruningDb db = new(new DbSettings(DbNames.State, "constructor-fault"), new MemDbFactory());
        ThrowingNodeStorage nodes = new(new NodeStorage(db, INodeStorage.KeyScheme.Hash));
        ILogManager logs = Substitute.For<ILogManager>();
        logs.GetClassLogger<PatriciaTree>().Returns(_ => throw new IOException("Tree logger acquisition failed."));
        PatriciaSnapTrieFactory factory = new(nodes, db, logs);
        Assert.Throws<IOException>(() => factory.CreateStorageTree(ValueKeccak.Zero));
        IPruningContext? context;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.AcquiredBatches, Is.Zero);
            Assert.That(db.TryStartPruning(out context), Is.True);
        }
        context!.Dispose();
    }

    [Test]
    public void Verify_only_storage_range_does_not_invalidate_active_rocks_pruning([Values] bool invalidRoot)
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new InitConfig { BaseDbPath = tempPath.Path },
                new PruningConfig { Mode = PruningMode.Full },
                new FlatDbConfig { Enabled = false }))
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .Build();
        ISnapTrieFactory factory = container.Resolve<PatriciaSnapTrieFactory>();
        FullPruningDb db = (FullPruningDb)container.ResolveKeyed<IDb>(DbNames.State);
        PathWithStorageSlot[] slots = [new(ValueKeccak.Zero, [1])];
        Hash256 expectedRoot;
        using (ISnapTree<PathWithStorageSlot> source = factory.CreateStorageTree(ValueKeccak.Zero))
        {
            source.BulkSetAndUpdateRootHash(slots);
            expectedRoot = source.RootHash;
        }
        PathWithAccount account = new(ValueKeccak.Zero, new Account(0, 0,
            invalidRoot ? Keccak.OfAnEmptyString : expectedRoot, Keccak.OfAnEmptyString).ToStruct());
        Assert.That(db.TryStartPruning(out IPruningContext? acquired), Is.True);
        IPruningContext pruning = acquired!;
        using (pruning)
        {
            pruning.Set([255], [42]);
            Assert.That(SnapProviderHelper.VerifyStorageRange(factory, account, slots,
                ValueKeccak.Zero, ValueKeccak.MaxValue, null),
                Is.EqualTo(invalidRoot ? AddRangeResult.DifferentRootHash : AddRangeResult.OK));
            Assert.That(pruning.Commit, Throws.Nothing);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Get([255]), Is.EqualTo(new byte[] { 42 }));
            Assert.That(db.CanStartPruning, Is.True);
        }
    }

    [Test]
    public void Throwing_native_storage_commit_aborts_nodes_before_retry(
        [Values(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)] INodeStorage.KeyScheme scheme)
    {
        string path = Path.Combine("testdb", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(path);
        DbConfig config = new();
        using HyperClockCacheWrapper cache = new();
        RocksDbFactory rocks = new(new RocksDbConfigFactory(config, new PruningConfig(), new TestHardwareInfo(),
            LimboLogs.Instance, validateConfig: false), config, cache, new Testably.Abstractions.RealFileSystem(), LimboLogs.Instance, path);
        using IDb native = rocks.CreateDb(new DbSettings(DbNames.State, "state") { DeleteOnStart = true });
        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(native);
        using FullPruningDb db = new(new DbSettings(DbNames.State, path), dbFactory);
        db.Set([255], [42]);
        NodeStorage nodes = new(db, scheme);
        Hash256 address = Keccak.Compute("atomic-account");
        PathWithStorageSlot[] slots = Enumerable.Range(0, 600)
            .Select(i => new PathWithStorageSlot(Keccak.Compute(BitConverter.GetBytes(i)).ValueHash256, new byte[] { 1 }))
            .OrderBy(slot => slot.Path).ToArray();
        ThrowingNodeStorage faultyNodes = new(nodes);
        PatriciaSnapTrieFactory faulty = new(faultyNodes, db, LimboLogs.Instance);
        Hash256 expectedRoot;
        using (ISnapTree<PathWithStorageSlot> tree = faulty.CreateStorageTree(address.ValueHash256))
        {
            tree.BulkSetAndUpdateRootHash(slots);
            expectedRoot = tree.RootHash;
            Assert.Throws<IOException>(() => tree.Commit(ValueKeccak.Zero, ValueKeccak.MaxValue));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(faultyNodes.StagedNodes, Is.EqualTo(300));
            Assert.That(native.GetAllKeys().Count(), Is.EqualTo(1), "No staged trie nodes may survive before retry.");
            Assert.That(db.Get([255]), Is.EqualTo(new byte[] { 42 }));
        }

        PatriciaSnapTrieFactory factory = new(nodes, db, LimboLogs.Instance);
        using (ISnapTree<PathWithStorageSlot> retry = factory.CreateStorageTree(address.ValueHash256))
        {
            retry.BulkSetAndUpdateRootHash(slots);
            Assert.That(retry.RootHash, Is.EqualTo(expectedRoot));
            retry.Commit(ValueKeccak.Zero, ValueKeccak.MaxValue);
        }
        StorageTree stored = new(new RawScopedTrieStore(nodes, address), expectedRoot, LimboLogs.Instance);
        using (Assert.EnterMultipleScope())
        {
            foreach (PathWithStorageSlot slot in slots) Assert.That(stored.Get(slot.Path.Bytes).ToArray(), Is.EqualTo(slot.SlotRlpValue));
            Assert.That(nodes.KeyExists(address.ValueHash256, TreePath.Empty, expectedRoot), Is.True);
            Assert.That(db.Get([255]), Is.EqualTo(new byte[] { 42 }));
        }
    }

    [Test]
    public void Storage_provider_fault_aborts_native_nodes_and_invalidates_active_pruning_before_retry()
    {
        using TempPath tempPath = TempPath.GetTempDirectory();
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        pivot.GetPivotHeader().Returns(Build.A.BlockHeader.WithStateRoot(Keccak.EmptyTreeHash).TestObject);
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new InitConfig { BaseDbPath = tempPath.Path },
                new SyncConfig { SnapSync = true, SnapSyncAccountRangePartitionCount = 1 },
                new PruningConfig { Mode = PruningMode.Full },
                new FlatDbConfig { Enabled = false }))
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .AddSingleton(pivot)
            .AddDecorator<INodeStorage, ThrowingNodeStorage>()
            .Build();
        ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();
        ISnapProvider provider = container.Resolve<ISnapProvider>();
        using Reactive.AnonymousDisposable _ = new(provider.Dispose);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        ThrowingNodeStorage nodes = (ThrowingNodeStorage)container.Resolve<INodeStorage>();
        FullPruningDb db = (FullPruningDb)container.ResolveKeyed<IDb>(DbNames.State);
        PathWithStorageSlot[] slots = Enumerable.Range(0, 600)
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
        string[] initialKeys = db.GetAllKeys().Select(Convert.ToHexString).Order().ToArray();
        tracker.EnqueueAccountStorage(account);
        Assert.That(db.TryStartPruning(out IPruningContext? acquired), Is.True);
        IPruningContext pruning = acquired!;
        using (pruning)
        {
            tracker.IsFinished(out SnapSyncBatch? request);
            using (request)
            using (SlotsAndProofs response = PatriciaStorageResponse(slots))
            {
                Assert.Throws<IOException>(() => provider.AddStorageRange(request!.StorageRangeRequest!, response));
                provider.ReleaseRequest(request!, false);
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nodes.StagedNodes, Is.EqualTo(300));
                Assert.That(db.GetAllKeys().Select(Convert.ToHexString).Order(), Is.EqualTo(initialKeys));
                Assert.That(nodes.KeyExists(account.Path, TreePath.Empty, root.ValueHash256), Is.False);
            }
            Assert.That(() => pruning.Commit(), Throws.TypeOf<InvalidOperationException>());
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        using (SlotsAndProofs response = PatriciaStorageResponse(slots))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.StorageRangeRequest!.Accounts.Single(), Is.SameAs(account));
                Assert.That(provider.AddStorageRange(retry.StorageRangeRequest, response), Is.EqualTo(AddRangeResult.OK));
            }
            provider.ReleaseRequest(retry, true);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
            Assert.That(nodes.KeyExists(account.Path, TreePath.Empty, root.ValueHash256), Is.True);
            Assert.That(db.CanStartPruning, Is.True);
        }
    }

    private static SlotsAndProofs PatriciaStorageResponse(PathWithStorageSlot[] slots) => new()
    {
        PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
        {
            new ArrayPoolList<PathWithStorageSlot>(slots)
        }
    };

    private sealed class ThrowingNodeStorage(INodeStorage inner) : INodeStorage
    {
        internal int StagedNodes { get; private set; }
        internal int AcquiredBatches { get; private set; }
        public INodeStorage.KeyScheme Scheme { get => inner.Scheme; set => inner.Scheme = value; }
        public bool RequirePath => inner.RequirePath;
        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) => inner.Get(address, path, hash, flags);
        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags flags = WriteFlags.None) => inner.Set(address, path, hash, data, flags);
        public INodeStorage.IWriteBatch StartWriteBatch() => new ThrowingNodeBatch(inner.StartWriteBatch(), this);
        public INodeStorage.IAtomicNodeStorageBatch StartAtomicWriteBatch()
        {
            AcquiredBatches++;
            return new ThrowingNodeBatch(inner.StartAtomicWriteBatch(), this);
        }
        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) => inner.KeyExists(address, path, hash);
        public void Flush(bool onlyWal) => inner.Flush(onlyWal);
        public void Compact() => inner.Compact();

        private sealed class ThrowingNodeBatch(INodeStorage.IWriteBatch batch, ThrowingNodeStorage owner) : INodeStorage.IAtomicNodeStorageBatch
        {
            public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags flags)
            {
                batch.Set(address, path, hash, data, flags);
                if (++owner.StagedNodes == 300) throw new IOException("Trie node staging failed.");
            }
            public void Commit() => ((INodeStorage.IAtomicNodeStorageBatch)batch).Commit();
            public void Dispose() => batch.Dispose();
        }
    }

    [Test]
    public void Records_finished_range_phase_in_the_state_db()
    {
        TestMemDb stateDb = new();
        PatriciaSnapTrieFactory factory = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);
        Assert.That(factory.IsRangePhaseFinished(), Is.False);

        factory.MarkRangePhaseFinished();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateDb[AccountProgressKey], Is.EqualTo(Keccak.MaxValue.BytesToArray()));
            Assert.That(stateDb.WasFlushed, Is.True);
        }
    }

    // The flag shares the state DB with the trie nodes it describes, so neither outlives the other.
    [Test]
    public void Reads_back_a_range_phase_finished_by_an_earlier_run()
    {
        TestMemDb stateDb = new();
        PatriciaSnapTrieFactory before = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);
        before.MarkRangePhaseFinished();

        PatriciaSnapTrieFactory afterRestart = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);

        Assert.That(afterRestart.IsRangePhaseFinished(), Is.True);
    }
}
