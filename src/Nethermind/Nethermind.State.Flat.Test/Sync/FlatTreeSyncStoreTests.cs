// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

/// <summary>
/// The store over each flat layout, through the persistence <see cref="FlatWorldStateModule"/> builds over it: the reader
/// cache, and the carry-forward cache when enabled.
/// </summary>
[TestFixtureSource(nameof(Configurations))]
public class FlatTreeSyncStoreTests(FlatLayout layout, bool carryForwardCache)
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private FlatTestContainer _persistenceContainer = null!;
    private IPersistence _persistence = null!;

    public static IEnumerable<TestFixtureData> Configurations()
    {
        foreach (FlatLayout layout in (FlatLayout[])[FlatLayout.Flat, FlatLayout.FlatInTrie])
        {
            foreach (bool carryForwardCache in (bool[])[false, true])
            {
                yield return new TestFixtureData(layout, carryForwardCache).SetArgDisplayNames(layout.ToString(), carryForwardCache ? "carry-forward cache" : "no carry-forward cache");
            }
        }
    }

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistenceContainer = new FlatTestContainer(new FlatDbConfig { Layout = layout, EnableCarryForwardCache = carryForwardCache }, loadPersistedTier: false, configure: builder => builder
            .AddSingleton<IColumnsDb<FlatDbColumns>>(_columnsDb)
            .AddSingleton<ITrieNodeLog>(NullTrieNodeLog.Instance)
            .AddSingleton<IPersistenceManager>(Substitute.For<IPersistenceManager>()));
        _persistence = _persistenceContainer.Resolve<IPersistence>();
    }

    [TearDown]
    public void TearDown()
    {
        _persistenceContainer.Dispose();
        _columnsDb.Dispose();
    }

    private static FlatTestContainer CreateContainer(IPersistence persistence, IHistoryPivotSeeder? seeder = null) =>
        new(loadPersistedTier: false, configure: builder =>
        {
            builder
                .AddSingleton<IPersistence>(persistence)
                .AddSingleton<IPersistenceManager>(Substitute.For<IPersistenceManager>());
            if (seeder is not null) builder.AddSingleton<IHistoryPivotSeeder>(seeder);
        });

    /// <summary>Writes the slots as healing does, through a sync batch of the persistence under test.</summary>
    private void WriteSlots(params (Address Address, UInt256 Slot, byte[] Value)[] slots)
    {
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        foreach ((Address address, UInt256 slot, byte[] value) in slots) batch.SetStorage(address, slot, new UInt256(value, isBigEndian: true));
    }

    /// <summary>Writes the slots of hashed owners and slot paths as healing does, as the RLP of the value without leading zeros.</summary>
    private void WriteHashedSlots(params (ValueHash256 Owner, ValueHash256 Slot, byte[] Value)[] slots)
    {
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        foreach ((ValueHash256 owner, ValueHash256 slot, byte[] value) in slots) batch.SetStorageRawEncoded(owner, slot, Rlp.Encode(value));
    }

    private static ValueHash256 SlotPath(in UInt256 slot)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        return ValueKeccak.Compute(slotBytes);
    }

    /// <summary>Whether a new reader of the persistence under test finds the slot.</summary>
    private bool HasSlot(Address address, in UInt256 slot) => HasSlot(ValueKeccak.Compute(address.Bytes), SlotPath(slot));

    /// <inheritdoc cref="HasSlot(Address, in UInt256)"/>
    private bool HasSlot(in ValueHash256 owner, in ValueHash256 slot)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        UInt256 value = default;
        return reader.TryGetStorageRaw(owner, slot, ref value);
    }

    [Test]
    public void EnsureStorageEmpty_deletes_all_storage_entries()
    {
        Address address = TestItem.AddressA;
        UInt256[] slots = [0, 1, 42];
        WriteSlots((address, slots[0], [0x01, 0x15, 0xe8]), (address, slots[1], [0xab, 0xcd]), (address, slots[2], [0xff]), (TestItem.AddressB, slots[0], [0x01]));

        Assert.That(slots.Select(slot => HasSlot(address, slot)), Is.All.True, "Storage entries should exist before cleanup");

        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        store.EnsureStorageEmpty(Keccak.Compute(address.Bytes));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slots.Select(slot => HasSlot(address, slot)), Is.All.False, "Storage entries should be deleted after EnsureStorageEmpty");
            Assert.That(HasSlot(TestItem.AddressB, slots[0]), Is.True, "Storage of another account");
        }
    }

    /// <summary>
    /// A reader that read a slot before, through the caches of the persistence, does not keep the cleared slot for the
    /// readers after it: a new reader finds neither the slot nor the node, and a sync reader, which bypasses the caches,
    /// shows that the store itself no longer has them.
    /// </summary>
    /// <remarks>
    /// The carry-forward cache serves readers at the state it was last committed to, and a sync batch commits it to
    /// <see cref="StateId.Sync"/> while the persisted state stays where it was, so an empty batch at the persisted state
    /// brings ordinary readers back onto the cache before each read.
    /// </remarks>
    [Test]
    public void EnsureStorageEmpty_leaves_no_slot_for_a_reader_after_one_that_read_it()
    {
        Address address = TestItem.AddressA;
        Hash256 owner = Keccak.Compute(address.Bytes);
        UInt256 slot = 7;
        TreePath nodePath = TreePath.FromHexString("1");
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.SetAccount(address, new Account(1, 1));
            batch.SetStorage(address, slot, 42);
            batch.SetStorageTrieNode(owner, nodePath, StorageNodeRlp);
        }

        CommitCacheAtPersistedState();
        using (IPersistence.IPersistenceReader primed = _persistence.CreateReader())
        {
            UInt256 value = default;
            Assert.That(primed.TryGetSlot(address, slot, ref value) ? value : (UInt256?)null, Is.EqualTo((UInt256)42), "the slot before cleanup");
        }

        using FlatTestContainer container = CreateContainer(_persistence);
        container.Resolve<FlatTreeSyncStore>().EnsureStorageEmpty(owner);

        CommitCacheAtPersistedState();
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using IPersistence.IPersistenceReader syncReader = _persistence.CreateReader(ReaderFlags.Sync);
        using (Assert.EnterMultipleScope())
        {
            UInt256 value = default;
            Assert.That(reader.TryGetSlot(address, slot, ref value), Is.False, "slot through a new reader");
            Assert.That(syncReader.TryGetStorageRaw(owner, SlotPath(slot), ref value), Is.False, "slot through a sync reader");
            Assert.That(reader.LoadStorageRlp(owner, nodePath, ReadFlags.None), Is.Null, "storage node through a new reader");
            Assert.That(syncReader.LoadStorageRlp(owner, nodePath, ReadFlags.None), Is.Null, "storage node through a sync reader");
            Assert.That(reader.GetAccount(address), Is.Not.Null, "the account");
        }

        void CommitCacheAtPersistedState()
        {
            using (_persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
            {
            }
        }
    }

    /// <summary>
    /// Storage is keyed by the first 20 bytes of the account path, split into a four-byte prefix and 16 bytes after the
    /// slot or node path, so the bounds take in every account whose first 20 bytes are within them, whatever its last 12.
    /// Six accounts share 19 bytes and differ in the 20th; the range covers the middle four, with bounds whose last bytes
    /// are past those of the account at the bound, and keeps one of them when <paramref name="keepInterior"/>.
    /// </summary>
    [Test]
    public void SelfDestructRange_deletes_the_storage_of_accounts_within_its_inclusive_20_byte_bounds([Values] bool keepInterior)
    {
        ValueHash256[] owners =
        [
            Owner(0x50, 0xaa), Owner(0x51, 0x00), Owner(0x52, 0x33), Owner(0x53, 0x11), Owner(0x54, 0xff), Owner(0x55, 0xbb),
            // Another four-byte prefix, past the one of the range.
            Owner(0x52, 0x33, fourthByte: 0x79),
        ];
        ValueHash256 from = Owner(0x51, 0xff);
        ValueHash256 to = Owner(0x54, 0x00);
        ValueHash256? kept = keepInterior ? Owner(0x53, 0x22) : null;
        bool[] deleted = [false, true, true, !keepInterior, true, false, false];
        ValueHash256[] slots = [ValueKeccak.Zero, ValueKeccak.MaxValue];
        // The second is past the paths the storage node column holds.
        TreePath[] nodePaths = [TreePath.FromHexString("1"), TreePath.FromHexString("1111111111111111")];
        TreePath stateNodePath = TreePath.FromHexString("12");
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            for (int i = 0; i < owners.Length; i++)
            {
                for (int j = 0; j < slots.Length; j++) batch.SetStorageRawEncoded(owners[i], slots[j], Rlp.Encode(SlotValue(i, j)));
                foreach (TreePath nodePath in nodePaths) batch.SetStorageTrieNode(owners[i], nodePath, NodeRlp(i));
            }

            batch.SetAccountRaw(owners[2], new AccountStruct(1, 1));
            batch.SetStateTrieNode(stateNodePath, StorageNodeRlp);
        }

        AssertStorage(static _ => true, "before the range is deleted");

        // The range is deleted over what its batch sees when it opens, so the rows are written by an earlier one.
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.SelfDestructRange(from, to, kept);
        }

        AssertStorage(i => !deleted[i], "after the range is deleted");
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.GetAccountRaw(owners[2]), Is.Not.Null, "account in the range");
            Assert.That(reader.LoadStateRlp(stateNodePath, ReadFlags.None), Is.EqualTo(StorageNodeRlp), "state node");
        }

        void AssertStorage(Func<int, bool> present, string when)
        {
            using IPersistence.IPersistenceReader storageReader = _persistence.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                for (int i = 0; i < owners.Length; i++)
                {
                    for (int j = 0; j < slots.Length; j++)
                    {
                        UInt256 value = default;
                        Assert.That(storageReader.TryGetStorageRaw(owners[i], slots[j], ref value) ? value : (UInt256?)null,
                            present(i) ? Is.EqualTo(new UInt256(SlotValue(i, j), isBigEndian: true)) : Is.Null, $"{when}: slot {j} of account {i}");
                    }

                    foreach (TreePath nodePath in nodePaths)
                    {
                        Assert.That(storageReader.LoadStorageRlp(owners[i].ToCommitment(), nodePath, ReadFlags.None), present(i) ? Is.EqualTo(NodeRlp(i)) : Is.Null,
                            $"{when}: storage node {nodePath} of account {i}");
                    }
                }
            }
        }

        static ValueHash256 Owner(byte twentiethByte, byte lastBytes, byte fourthByte = 0x78)
        {
            byte[] path = new byte[32];
            Bytes.FromHexString("0x123456789a9a9a9a9a9a9a9a9a9a9a9a9a9a9a").CopyTo(path, 0);
            path[3] = fourthByte;
            path[19] = twentiethByte;
            path.AsSpan(20).Fill(lastBytes);
            return new ValueHash256(path);
        }

        static byte[] SlotValue(int owner, int slot) => [(byte)(0x50 + owner), (byte)(slot + 1)];

        static byte[] NodeRlp(int owner) => [0xc2, 0x80, (byte)(0x10 + owner)];
    }

    /// <summary>
    /// Healing finds a storage trie node stored at a path with the hash it expects saved with its subtree, so the nodes
    /// have to go with the slots, whether or not the root is among them, in both columns, and for that account only.
    /// </summary>
    [Test]
    public void EnsureStorageEmpty_deletes_the_storage_trie_nodes_of_that_account_only([Values] bool withRootNode)
    {
        // The second account shares the first four bytes of the cleared one, which storage keys start with.
        Hash256 cleared = new("0x1234567811111111111111111111111111111111111111111111111111111111");
        Hash256 samePrefix = new("0x1234567822222222222222222222222222222222222222222222222222222222");
        Hash256 other = TestItem.KeccakB;
        Hash256[] accounts = [cleared, samePrefix, other];
        ValueHash256 slot = TestItem.KeccakC;
        // The deepest of these is past the paths the storage node column holds.
        TreePath[] nodePaths = [TreePath.Empty, TreePath.FromHexString("1"), TreePath.FromHexString("f"), TreePath.FromHexString("fffffffffffffff"), TreePath.FromHexString("1111111111111111")];
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            foreach (Hash256 account in accounts)
            {
                foreach (TreePath nodePath in nodePaths)
                {
                    if (withRootNode || account != cleared || nodePath.Length > 0) batch.SetStorageTrieNode(account, nodePath, StorageNodeRlp);
                }
            }
        }

        WriteHashedSlots([.. accounts.Select(account => ((ValueHash256)account, slot, (byte[])[0x01, 0x02]))]);

        using FlatTestContainer container = CreateContainer(_persistence);
        container.Resolve<FlatTreeSyncStore>().EnsureStorageEmpty(cleared);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach (Hash256 account in accounts)
            {
                bool kept = account != cleared;
                UInt256 value = default;
                Assert.That(reader.TryGetStorageRaw(account, slot, ref value), Is.EqualTo(kept), $"slot of {account}");
                foreach (TreePath nodePath in nodePaths)
                {
                    Assert.That(reader.LoadStorageRlp(account, nodePath, ReadFlags.None), kept ? Is.EqualTo(StorageNodeRlp) : Is.Null, $"storage node {nodePath} of {account}");
                }
            }
        }
    }

    [Test]
    public void SaveNode_deletes_the_storage_of_accounts_the_new_state_node_no_longer_covers()
    {
        Address removed = TestItem.AddressA;
        Address kept = TestItem.AddressB;
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        SaveRootAccountLeaf(store, removed);
        WriteSlots((removed, 1, [0x01]), (kept, 1, [0x02]));

        SaveRootAccountLeaf(store, kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasSlot(removed, 1), Is.False);
            Assert.That(HasSlot(kept, 1), Is.True);
        }
    }

    [Test]
    public void SaveNode_keeps_the_storage_of_the_account_the_new_state_leaf_holds()
    {
        Address held = TestItem.AddressA;
        Address removed = TestItem.AddressB;
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        SaveRootAccountLeaf(store, removed);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            batch.SetAccountRaw(Keccak.Compute(held.Bytes), new AccountStruct(1, 1));
            foreach (Address address in (Address[])[held, removed])
            {
                foreach (TreePath nodePath in StorageNodePaths) batch.SetStorageTrieNode(Keccak.Compute(address.Bytes), nodePath, StorageNodeRlp);
            }
        }
        WriteSlots((held, 1, [0x01]), (removed, 1, [0x02]));

        SaveRootAccountLeaf(store, held);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasSlot(held, 1), Is.True);
            Assert.That(HasSlot(removed, 1), Is.False);
            foreach (TreePath nodePath in StorageNodePaths)
            {
                Assert.That(reader.LoadStorageRlp(Keccak.Compute(held.Bytes), nodePath, ReadFlags.None), Is.EqualTo(StorageNodeRlp), $"storage node {nodePath} of the held account");
                Assert.That(reader.LoadStorageRlp(Keccak.Compute(removed.Bytes), nodePath, ReadFlags.None), Is.Null, $"storage node {nodePath} of the removed account");
            }
        }
    }

    [Test]
    public void SaveNode_deletes_storage_that_has_no_flat_account_entry()
    {
        Address orphaned = TestItem.AddressC;
        Address kept = TestItem.AddressB;
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        SaveRootAccountLeaf(store, TestItem.AddressA);
        WriteSlots((orphaned, 1, [0x01]), (kept, 1, [0x02]));

        SaveRootAccountLeaf(store, kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasSlot(orphaned, 1), Is.False);
            Assert.That(HasSlot(kept, 1), Is.True);
        }
    }

    [Test]
    public void SaveNode_self_destructs_each_replaced_range_once_keeping_the_leaf_account()
    {
        Address kept = TestItem.AddressB;
        using (FlatTestContainer container = CreateContainer(_persistence))
        {
            SaveRootAccountLeaf(container.Resolve<FlatTreeSyncStore>(), TestItem.AddressA);
        }

        FakeWriteBatch batch = new();
        using FlatTestContainer recording = CreateContainer(new BatchRecordingPersistence(_persistence, batch));
        SaveRootAccountLeaf(recording.Resolve<FlatTreeSyncStore>(), kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.SelfDestructRangeCalls, Is.Not.Empty);
            Assert.That(batch.SelfDestructRangeCalls, Has.Count.EqualTo(batch.DeleteAccountRangeCalls.Count));
            Assert.That(batch.SelfDestructRangeCalls.Select(static call => call.KeptAccount), Is.All.EqualTo(Keccak.Compute(kept.Bytes).ValueHash256));
        }
    }

    public enum SavedBeforeTheLeaf
    {
        Nothing,
        StorageOfTheLeafAccount,
        LeafOfTheLeafAccountAbove,
        StorageOfAnotherAccount,
        StateNodeBelowTheLeaf,
        LeafOfAnotherAccountAbove,
        LeafOfTheLeafAccountBelow,
    }

    [TestCase(SavedBeforeTheLeaf.Nothing, false)]
    [TestCase(SavedBeforeTheLeaf.StorageOfTheLeafAccount, false)]
    [TestCase(SavedBeforeTheLeaf.LeafOfTheLeafAccountAbove, false)]
    [TestCase(SavedBeforeTheLeaf.StorageOfAnotherAccount, true)]
    [TestCase(SavedBeforeTheLeaf.StateNodeBelowTheLeaf, true)]
    [TestCase(SavedBeforeTheLeaf.LeafOfAnotherAccountAbove, true)]
    [TestCase(SavedBeforeTheLeaf.LeafOfTheLeafAccountBelow, true)]
    public void SaveNode_compares_a_leaf_with_the_one_RemoveMismatchedNode_took_out_until_a_state_node_or_another_account_is_saved_under_it(SavedBeforeTheLeaf saved, bool selfDestructsTheRange)
    {
        // Both accounts are under the leaf's path "a".
        Hash256 held = new("0xa000000000000000000000000000000000000000000000000000000000000001");
        Hash256 other = new("0xa000000000000000000000000000000000000000000000000000000000000002");
        TreePath leafPath = TreePath.FromHexString("a");
        using (FlatTestContainer container = CreateContainer(_persistence))
        {
            SaveAccountLeaf(container.Resolve<FlatTreeSyncStore>(), held, depth: 1);
        }

        FakeWriteBatch batch = new();
        using FlatTestContainer recording = CreateContainer(new BatchRecordingPersistence(_persistence, batch));
        FlatTreeSyncStore store = recording.Resolve<FlatTreeSyncStore>();
        (_, byte[] newLeaf) = Leaf(held, depth: 1, Rlp.Encode(new Account(1, 2)));
        store.RemoveMismatchedNode(null, leafPath, ValueKeccak.Compute(newLeaf));
        // The recording batch keeps the removal from the db.
        using (IPersistence.IWriteBatch dbBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            dbBatch.SetStateTrieNode(leafPath, default);
        }

        switch (saved)
        {
            case SavedBeforeTheLeaf.StorageOfTheLeafAccount: SaveSlotLeaf(store, held); break;
            case SavedBeforeTheLeaf.LeafOfTheLeafAccountAbove: SaveAccountLeaf(store, held, depth: 0); break;
            case SavedBeforeTheLeaf.StorageOfAnotherAccount: SaveSlotLeaf(store, other); break;
            case SavedBeforeTheLeaf.StateNodeBelowTheLeaf: SaveAccountLeaf(store, other, depth: 2); break;
            case SavedBeforeTheLeaf.LeafOfAnotherAccountAbove: SaveAccountLeaf(store, other, depth: 0); break;
            case SavedBeforeTheLeaf.LeafOfTheLeafAccountBelow: SaveAccountLeaf(store, held, depth: 2); break;
        }
        batch.SelfDestructRangeCalls.Clear();

        store.SaveNode(null, leafPath, ValueKeccak.Compute(newLeaf), newLeaf);

        Assert.That(batch.SelfDestructRangeCalls, selfDestructsTheRange ? Is.Not.Empty : Is.Empty);
    }

    private static readonly byte[] StorageNodeRlp = [0xc2, 0x80, 0x80];
    private static readonly TreePath[] StorageNodePaths = [TreePath.FromHexString("1"), TreePath.FromHexString("1111111111111111")];

    private static void SaveRootAccountLeaf(FlatTreeSyncStore store, Address address) =>
        SaveAccountLeaf(store, Keccak.Compute(address.Bytes), depth: 0);

    private static void SaveAccountLeaf(FlatTreeSyncStore store, Hash256 account, int depth)
    {
        (TreePath path, byte[] rlp) = Leaf(account, depth, Rlp.Encode(new Account(1, 1)));
        store.SaveNode(null, path, ValueKeccak.Compute(rlp), rlp);
    }

    private static void SaveSlotLeaf(FlatTreeSyncStore store, Hash256 account)
    {
        (_, byte[] rlp) = Leaf(TestItem.KeccakA, depth: 0, Rlp.Encode(new byte[] { 0x01 }));
        store.SaveNode(account, TreePath.Empty, ValueKeccak.Compute(rlp), rlp);
    }

    /// <summary>The leaf of the key at the depth, and its path.</summary>
    private static (TreePath Path, byte[] Rlp) Leaf(Hash256 key, int depth, byte[] value)
    {
        byte[] nibbles = Nibbles.BytesToNibbleBytes(key.Bytes);
        TreePath path = TreePath.Empty.Append(nibbles.AsSpan(0, depth));
        TrieNode leaf = TrieNodeFactory.CreateLeaf(nibbles.AsSpan(depth), value);
        TreePath resolvePath = path;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref resolvePath);
        return (path, leaf.FullRlp.ToArray()!);
    }

    [Test]
    public void FinalizeSync_flushes_data_before_advancing_the_state_pointer()
    {
        // #11457: the state pointer must advance only after the (DisableWAL) data is flushed.
        List<string> log = [];
        OrderRecordingPersistence spy = new(_persistence, log);
        using FlatTestContainer container = CreateContainer(spy);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(123).WithStateRoot(TestItem.KeccakA).TestObject;

        store.FinalizeSync(pivot);

        int firstFlush = log.IndexOf("flush");
        int firstAdvance = log.IndexOf("advance-pointer");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstFlush, Is.GreaterThanOrEqualTo(0), "data must be flushed during finalize");
            Assert.That(firstAdvance, Is.GreaterThan(firstFlush), "state pointer must advance only after the data flush");
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState.BlockNumber, Is.EqualTo(123), "state pointer should end at the pivot block");
    }

    [Test]
    public void FinalizeSync_seeds_the_history_pivot_with_the_pivot_block_and_root_before_advancing_the_state_pointer()
    {
        IHistoryPivotSeeder seeder = Substitute.For<IHistoryPivotSeeder>();
        List<string> log = [];
        seeder.When(s => s.SeedPivot(Arg.Any<ulong>(), Arg.Any<ValueHash256>()))
            .Do(_ => log.Add("seed-pivot"));
        OrderRecordingPersistence spy = new(_persistence, log);
        using FlatTestContainer container = CreateContainer(spy, seeder);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(123).WithStateRoot(TestItem.KeccakA).TestObject;

        store.FinalizeSync(pivot);

        using (Assert.EnterMultipleScope())
        {
            seeder.Received(1).SeedPivot(123, TestItem.KeccakA);

            int seedCallIndex = log.IndexOf("seed-pivot");
            int firstAdvance = log.IndexOf("advance-pointer");
            Assert.That(seedCallIndex, Is.GreaterThanOrEqualTo(0), "the seeder must actually be called");
            Assert.That(firstAdvance, Is.GreaterThan(seedCallIndex),
                "the pivot must be seeded while the reader still sees exactly the pivot's state, before any block is processed on top of it");
        }
    }

    [Test]
    public void FinalizeSync_withNoSeeder_neverTouchesTheHistoryPivotSeeder()
    {
        // No IHistoryPivotSeeder passed - the existing tests above already exercise this implicitly (omitting the
        // parameter), but this makes the "no-op when history capture/windowing is not configured" contract explicit.
        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        BlockHeader pivot = Build.A.BlockHeader.WithNumber(123).WithStateRoot(TestItem.KeccakA).TestObject;

        Assert.DoesNotThrow(() => store.FinalizeSync(pivot));
    }

    private sealed class BatchRecordingPersistence(IPersistence inner, FakeWriteBatch batch) : IPersistence
    {
        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => batch;

        public void Flush() => inner.Flush();

        public void Clear() => inner.Clear();
    }

    private sealed class OrderRecordingPersistence(IPersistence inner, List<string> log) : IPersistence
    {
        public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => inner.CreateReader(flags);

        public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
        {
            if (to != StateId.Sync) log.Add("advance-pointer");
            return inner.CreateWriteBatch(from, to, flags);
        }

        public void Flush()
        {
            log.Add("flush");
            inner.Flush();
        }

        public void Clear() => inner.Clear();
    }
}
