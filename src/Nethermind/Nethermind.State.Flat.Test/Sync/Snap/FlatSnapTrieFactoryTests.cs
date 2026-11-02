// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync.Snap;

[TestFixture]
public class FlatSnapTrieFactoryTests
{
    private static (FlatSnapTrieFactory factory, IPersistence persistence, IPersistence.IPersistenceReader reader) Build(bool doubleWriteCheck = false, IPersistenceManager? persistenceManager = null)
    {
        IPersistence persistence = Substitute.For<IPersistence>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        persistence.CreateReader(Arg.Any<ReaderFlags>()).Returns(reader);
        // NSubstitute proxies fail with InvalidProgramException on the ReadOnlySpan parameters,
        // so the write batch is a hand-rolled no-op fake.
        persistence.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<Nethermind.Core.WriteFlags>())
            .Returns(_ => new NoOpWriteBatch());

        ISyncConfig syncConfig = Substitute.For<ISyncConfig>();
        syncConfig.EnableSnapDoubleWriteCheck.Returns(doubleWriteCheck);

        FlatSnapTrieFactory factory = new(persistence, persistenceManager ?? Substitute.For<IPersistenceManager>(), new MemColumnsDb<FlatDbColumns>(), new MemDb(), syncConfig, LimboLogs.Instance);
        return (factory, persistence, reader);
    }

    private class NoOpWriteBatch : IPersistence.IWriteBatch
    {
        public void SelfDestruct(Address addr) { }
        public void SetAccount(Address addr, Account? account) { }
        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value) { }
        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) { }
        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue) { }
        public void SetAccountRaw(in ValueHash256 addrHash, Account account) { }
        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void SelfDestructRange(in ValueHash256 fromPath, in ValueHash256 toPath) { }
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) { }
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) { }
        public void Dispose() { }
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
            batch.SetAccountRaw(Keccak.Zero.ValueHash256, new Account(1));
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
            Assert.That(stateTree, Is.Not.Null);
            Assert.That(storageTree, Is.Not.Null);
            Assert.That(nonDefaultStorageTree, Is.Not.Null);
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

        Assert.That(stateTree, Is.Not.Null);
        Assert.That(storageTree, Is.Not.Null);
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

    private static readonly ValueHash256 FirstPartitionLimit = new("0x7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
    private static readonly byte[] BranchRlp = BuildBranchRlp();
    private static readonly byte[] SlotRlp = Rlp.Encode(new byte[] { 1 }).Bytes;
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
                batch.SetAccountRaw(account, new Account(1));
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
            storagesToRetrieve: [new PathWithAccount(storageQueued, new Account(1))],
            storageRanges: [new StorageRangeProgress([new PathWithAccount(storageQueuedFromSlot8, new Account(1))], Path("8"), null)],
            accountsToRefresh: [new AccountWithStorageStartingHash { PathAndAccount = new PathWithAccount(refreshedFromSlot8, new Account(1)), StorageStartingHash = Path("8"), StorageHashLimit = ValueKeccak.MaxValue }]));

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
                    Assert.That(reader.TryLoadStorageRlp(account.ToCommitment(), TreePath.FromHexString(node), ReadFlags.None) is not null,
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
                Assert.That(reader.TryLoadStateRlp(TreePath.FromHexString(node), ReadFlags.None) is not null, Is.EqualTo(kept), $"state node {node}");
            }
        }
    }

    public enum Unresumable { ResumeDisabled, PartitionCountChanged, Unreadable }

    [Test]
    public void EnsureInitialize_StartsOver_WhenTheCheckpointCannotBeResumed([Values] Unresumable reason)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.SetAccountRaw(LastAccount, new Account(1));
        }

        CreateResumingFactory(persistence, db).SaveCheckpoint(CreateCheckpoint());
        if (reason == Unresumable.Unreadable) BasePersistence.WriteSnapCheckpoint(db, [0xff]);

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
        FlatSnapTrieFactory factory = CreateResumingFactory(persistence, new MemColumnsDb<FlatDbColumns>());

        factory.SaveCheckpoint(CreateCheckpoint());
        persistence.Received(1).Flush();
        Assert.That(factory.LoadCheckpoint()?.AnchorHash, Is.EqualTo(TestItem.KeccakA));

        factory.SaveCheckpoint(null);
        Assert.That(factory.LoadCheckpoint(), Is.Null);
    }

    [Test]
    public void SaveCheckpoint_KeepsTheLastCheckpoint_WhenTheWritesItCoversFailToFlush()
    {
        MemColumnsDb<FlatDbColumns> columns = new();
        FlushFailingDb accounts = new();
        IColumnsDb<FlatDbColumns> db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(c => c.Arg<FlatDbColumns>() == FlatDbColumns.Account ? accounts : columns.GetColumnDb(c.Arg<FlatDbColumns>()));
        FlatSnapTrieFactory factory = CreateResumingFactory(Substitute.For<IPersistence>(), db);
        factory.SaveCheckpoint(CreateCheckpoint());

        accounts.FailFlush = true;

        Assert.Throws<InvalidOperationException>(() => factory.SaveCheckpoint(CreateCheckpoint(anchorHash: TestItem.KeccakB)));
        Assert.That(factory.LoadCheckpoint()?.AnchorHash, Is.EqualTo(TestItem.KeccakA));
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
                batch.SetAccountRaw(account, new Account(1));
                batch.SetStorageRawEncoded(account, Path("1"), SlotRlp);
                batch.SetStorageTrieNode(account.ToCommitment(), TreePath.FromHexString("1"), BranchRlp);
            }

            batch.SetStateTrieNode(TreePath.FromHexString("a"), BranchRlp);
        }

        CreateResumingFactory(persistence, db).SaveCheckpoint(CreateCheckpoint(storagesToRetrieve: [new PathWithAccount(storageQueued, new Account(1))]));
        CreateResumingFactory(persistence, db).EnsureInitialize();

        Assert.That(db.DeleteFlags, Is.Not.Empty);
        Assert.That(db.DeleteFlags, Has.All.Matches<WriteFlags>(static flags => flags.HasFlag(WriteFlags.DisableWAL)));
    }

    private static ValueHash256 Path(string hexPrefix) => new(hexPrefix.PadRight(64, '0'));

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
