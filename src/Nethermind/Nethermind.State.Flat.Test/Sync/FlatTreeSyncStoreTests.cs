// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Sync;

[TestFixture]
public class FlatTreeSyncStoreTests
{
    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        // WriteStorageDirectToDb seeds the Storage column with raw (un-wrapped) bytes after the persistence
        // is built, so slot-presence detection can't kick in — pin the raw encoding up front.
        BasePersistence.SetSlotEncoding(_columnsDb.GetColumnDb(FlatDbColumns.Metadata), BasePersistence.SlotEncodingRaw);
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    private static FlatTestContainer CreateContainer(IPersistence persistence, IHistoryPivotSeeder? seeder = null) =>
        new(loadPersistedTier: false, configure: builder =>
        {
            builder
                .AddSingleton<IPersistence>(persistence)
                .AddSingleton<IPersistenceManager>(Substitute.For<IPersistenceManager>());
            if (seeder is not null) builder.AddSingleton<IHistoryPivotSeeder>(seeder);
        });

    private void WriteStorageDirectToDb(Address address, UInt256 slot, byte[] value) =>
        _columnsDb.GetColumnDb(FlatDbColumns.Storage).PutSpan(StorageKey(address, slot), ((ReadOnlySpan<byte>)value).WithoutLeadingZeros());

    private bool HasStorageSlot(Address address, UInt256 slot) =>
        _columnsDb.GetColumnDb(FlatDbColumns.Storage).KeyExists(StorageKey(address, slot));

    private static ValueHash256 SlotHash(UInt256 slot)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        return ValueKeccak.Compute(slotBytes);
    }

    private static UInt256 SlotWithFirstNibble(int nibble)
    {
        UInt256 slot = 0;
        while (SlotHash(slot).Bytes[0] >> 4 != nibble) slot++;
        return slot;
    }

    private static byte[] StorageKey(Address address, UInt256 slot)
    {
        ValueHash256 addrHash = ValueKeccak.Compute(address.Bytes);
        ValueHash256 slotHash = SlotHash(slot);

        byte[] storageKey = new byte[52];
        addrHash.Bytes[..4].CopyTo(storageKey.AsSpan()[..4]);
        slotHash.Bytes.CopyTo(storageKey.AsSpan()[4..36]);
        addrHash.Bytes[4..20].CopyTo(storageKey.AsSpan()[36..52]);
        return storageKey;
    }

    private bool HasStorageEntries(Address address)
    {
        IDb storageDb = _columnsDb.GetColumnDb(FlatDbColumns.Storage);
        ValueHash256 addrHash = ValueKeccak.Compute(address.Bytes);
        byte[] prefix = addrHash.Bytes[..4].ToArray();

        foreach (byte[] key in storageDb.GetAllKeys())
        {
            if (key.AsSpan()[..4].SequenceEqual(prefix))
                return true;
        }
        return false;
    }

    [Test]
    public void EnsureStorageEmpty_deletes_all_storage_entries()
    {
        Address address = TestItem.AddressA;
        WriteStorageDirectToDb(address, 0, [0x01, 0x15, 0xe8]);
        WriteStorageDirectToDb(address, 1, [0xab, 0xcd]);
        WriteStorageDirectToDb(address, 42, [0xff]);

        Assert.That(HasStorageEntries(address), Is.True, "Storage entries should exist before cleanup");

        using FlatTestContainer container = CreateContainer(_persistence);
        FlatTreeSyncStore store = container.Resolve<FlatTreeSyncStore>();
        store.EnsureStorageEmpty(Keccak.Compute(address.Bytes));

        Assert.That(HasStorageEntries(address), Is.False, "Storage entries should be deleted after EnsureStorageEmpty");
    }

    private static readonly byte[] Leaf = Bytes.FromHexString("f838a120290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e5639594654f75e491acf8c380d2a6906e67e2e56813665e");

    private static byte[] ExtensionWithKey123() => Bytes.FromHexString("e4821123a0" + new string('1', 64));

    private static byte[] BranchWithChildren(params int[] nibbles)
    {
        string items = string.Concat(Enumerable.Range(0, 17).Select(i => nibbles.Contains(i) ? "a0" + new string('1', 64) : "80"));
        int length = items.Length / 2;
        return Bytes.FromHexString((length < 56 ? $"{0xc0 + length:x2}" : $"f8{length:x2}") + items);
    }

    private void WriteStorageNodes(Hash256 address, params string[] paths) => WriteStorageNodes(address, Leaf, paths);

    private void WriteStorageNodes(Hash256 address, byte[] rlp, params string[] paths)
    {
        using IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync);
        foreach (string path in paths)
        {
            writeBatch.SetStorageTrieNode(address, TreePath.FromHexString(path), rlp);
        }
    }

    [Test]
    public void InvalidateNode_drops_the_stored_node_at_its_path_and_keeps_its_children()
    {
        Hash256 address = Keccak.Compute(TestItem.AddressA.Bytes);
        WriteStorageNodes(address, "", "0a");
        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        ValueHash256 leafHash = ValueKeccak.Compute(Leaf);
        byte[] branch = BranchWithChildren(0, 1);

        Invalidate(store, address, "", branch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.NodeExists(address, TreePath.Empty, leafHash), Is.False);
            Assert.That(store.NodeExists(address, TreePath.FromHexString("0a"), leafHash), Is.True);
        }
    }

    [Test]
    public void InvalidateNode_keeps_the_stored_node_when_it_is_the_received_node()
    {
        Hash256 address = Keccak.Compute(TestItem.AddressA.Bytes);
        WriteStorageNodes(address, "");
        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        ValueHash256 leafHash = ValueKeccak.Compute(Leaf);

        Invalidate(store, address, "", Leaf);

        Assert.That(store.NodeExists(address, TreePath.Empty, leafHash), Is.True);
    }

    [Test]
    public void SaveNode_after_InvalidateNode_deletes_only_below_children_the_replaced_node_had()
    {
        Address account = TestItem.AddressA;
        Hash256 address = Keccak.Compute(account.Bytes);
        WriteStorageNodes(address, BranchWithChildren(0, 1), "");
        UInt256 droppedSlot = SlotWithFirstNibble(1);
        UInt256 untouchedSlot = SlotWithFirstNibble(5);
        WriteStorageDirectToDb(account, droppedSlot, [0x01]);
        WriteStorageDirectToDb(account, untouchedSlot, [0x02]);
        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        byte[] branch = BranchWithChildren(0);
        ValueHash256 branchHash = ValueKeccak.Compute(branch);

        Invalidate(store, address, "", branch);
        store.SaveNode(address, TreePath.Empty, branchHash, branch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasStorageSlot(account, droppedSlot), Is.False);
            Assert.That(HasStorageSlot(account, untouchedSlot), Is.True);
            Assert.That(store.NodeExists(address, TreePath.Empty, branchHash), Is.True);
        }
    }

    [Test]
    public void SaveNode_after_two_different_received_nodes_deletes_below_every_empty_child()
    {
        Address account = TestItem.AddressA;
        Hash256 address = Keccak.Compute(account.Bytes);
        WriteStorageNodes(address, BranchWithChildren(0, 1), "");
        UInt256 slot = SlotWithFirstNibble(5);
        WriteStorageDirectToDb(account, slot, [0x02]);
        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        byte[] interrupted = BranchWithChildren(0, 5);
        byte[] branch = BranchWithChildren(0);
        ValueHash256 branchHash = ValueKeccak.Compute(branch);

        Invalidate(store, address, "", interrupted);
        Invalidate(store, address, "", branch);
        store.SaveNode(address, TreePath.Empty, branchHash, branch);

        Assert.That(HasStorageSlot(account, slot), Is.False);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Extension_drops_the_stored_nodes_inside_its_key(bool invalidate)
    {
        Hash256 address = Keccak.Compute(TestItem.AddressA.Bytes);
        WriteStorageNodes(address, "1", "12", "123");
        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        byte[] extension = ExtensionWithKey123();
        ValueHash256 leafHash = ValueKeccak.Compute(Leaf);

        if (invalidate) Invalidate(store, address, "", extension);
        else store.SaveNode(address, TreePath.Empty, ValueKeccak.Compute(extension), extension);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.NodeExists(address, TreePath.FromHexString("1"), leafHash), Is.False);
            Assert.That(store.NodeExists(address, TreePath.FromHexString("12"), leafHash), Is.False);
            Assert.That(store.NodeExists(address, TreePath.FromHexString("123"), leafHash), Is.True);
        }
    }

    private static readonly ValueHash256 KeyUnderP7 = new("0x17abaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    private static byte[] RlpList(byte[] first, byte[] second)
    {
        int contentLength = first.Length + second.Length;
        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(result, 0, contentLength);
        first.CopyTo(result, position);
        second.CopyTo(result, position + first.Length);
        return result;
    }

    private static byte[] LeafRlp(byte[] hexPrefixedKey, byte[] value) => RlpList(Rlp.Encode(hexPrefixedKey), Rlp.Encode(value));

    private static byte[] RootLeaf(in ValueHash256 key, byte[] value) => LeafRlp([0x20, .. key.Bytes], value);

    private static byte[] LeafBelow17(in ValueHash256 key, byte[] value) => LeafRlp([0x20, .. key.Bytes[1..]], value);

    private static byte[] ExtensionWithKey17(in ValueHash256 child) => RlpList(Rlp.Encode(new byte[] { 0x00, 0x17 }), Rlp.Encode(child.Bytes));

    private void UseRlpSlotEncoding()
    {
        _columnsDb.Dispose();
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        BasePersistence.SetSlotEncoding(_columnsDb.GetColumnDb(FlatDbColumns.Metadata), BasePersistence.SlotEncodingRlp);
        _persistence = new RocksDbPersistence(_columnsDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
    }

    private bool HasFlatEntry(Hash256? address, in ValueHash256 key)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        UInt256 value = default;
        return address is null ? reader.GetAccountRaw(key) is not null : reader.TryGetStorageRaw(address.ValueHash256, key, ref value);
    }

    private static void Save(FlatTreeSyncStore store, Hash256? address, string path, byte[] rlp) =>
        store.SaveNode(address, path.Length == 0 ? TreePath.Empty : TreePath.FromHexString(path), ValueKeccak.Compute(rlp), rlp);

    private static void Invalidate(FlatTreeSyncStore store, Hash256? address, string path, byte[] rlp)
    {
        TreePath treePath = path.Length == 0 ? TreePath.Empty : TreePath.FromHexString(path);
        TrieNode node = new(NodeType.Unknown, rlp);
        node.ResolveNode(NullTrieNodeResolver.Instance, treePath);
        store.InvalidateNode(address, treePath, ValueKeccak.Compute(rlp), node);
    }

    public enum StaleEntryWriter { StorageLeaf, AccountLeaf, StorageExtension }

    [Test]
    public void Received_node_seen_again_after_its_path_was_emptied_deletes_below_every_child_it_lacks([Values] StaleEntryWriter writer)
    {
        UseRlpSlotEncoding();
        Hash256? address = writer == StaleEntryWriter.AccountLeaf ? null : Keccak.Compute(TestItem.AddressA.Bytes);
        byte[] stored = BranchWithChildren(0, 1);
        using (IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            if (address is null) writeBatch.SetStateTrieNode(TreePath.FromHexString("1"), stored);
            else writeBatch.SetStorageTrieNode(address, TreePath.FromHexString("1"), stored);
        }

        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        byte[] received = BranchWithChildren(0);
        Invalidate(store, address, "1", received);

        byte[] value = address is null ? AccountDecoder.Instance.Encode(Build.An.Account.WithBalance(1).TestObject) : Rlp.Encode(new byte[] { 0x01 });
        switch (writer)
        {
            case StaleEntryWriter.StorageExtension:
                byte[] leaf = LeafBelow17(KeyUnderP7, value);
                Save(store, address, "17", leaf);
                Save(store, address, "", ExtensionWithKey17(ValueKeccak.Compute(leaf)));
                break;
            default:
                Save(store, address, "", RootLeaf(KeyUnderP7, value));
                break;
        }

        Assert.That(HasFlatEntry(address, KeyUnderP7), Is.True, "guards the premise: the interrupted round wrote the entry under 1.7");

        byte[] root = BranchWithChildren(1);
        Invalidate(store, address, "", root);
        Invalidate(store, address, "1", received);
        Save(store, address, "1", received);
        Save(store, address, "", root);

        Assert.That(HasFlatEntry(address, KeyUnderP7), Is.False);
    }

    [Test]
    public void InvalidateNode_with_a_leaf_drops_the_stored_subtree_at_its_path([Values] bool storage, [Values] bool storedIsSameLeaf)
    {
        Hash256? address = storage ? Keccak.Compute(TestItem.AddressA.Bytes) : null;
        byte[] key = [0x20, .. KeyUnderP7.Bytes[1..]];
        byte[] stored = storedIsSameLeaf ? LeafRlp(key, Rlp.Encode(new byte[] { 0x02 })) : BranchWithChildren(0xa);
        using (IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            foreach ((string path, byte[] rlp) in new[] { ("1", stored), ("1a", Leaf) })
            {
                if (address is null) writeBatch.SetStateTrieNode(TreePath.FromHexString(path), rlp);
                else writeBatch.SetStorageTrieNode(address, TreePath.FromHexString(path), rlp);
            }
        }

        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        byte[] leaf = LeafRlp(key, Rlp.Encode(new byte[] { 0x01 }));
        Invalidate(store, address, "1", leaf);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.NodeExists(address, TreePath.FromHexString("1"), ValueKeccak.Compute(stored)), Is.False);
            Assert.That(store.NodeExists(address, TreePath.FromHexString("1a"), ValueKeccak.Compute(Leaf)), Is.EqualTo(storedIsSameLeaf));
        }
    }

    [Test]
    public void InvalidateNode_with_a_leaf_drops_stored_descendants_when_its_own_path_is_empty([Values] bool storage)
    {
        Hash256? address = storage ? Keccak.Compute(TestItem.AddressA.Bytes) : null;
        using (IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            if (address is null) writeBatch.SetStateTrieNode(TreePath.FromHexString("1a"), Leaf);
            else writeBatch.SetStorageTrieNode(address, TreePath.FromHexString("1a"), Leaf);
        }

        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        byte[] key = [0x20, .. KeyUnderP7.Bytes[1..]];
        Invalidate(store, address, "1", LeafRlp(key, Rlp.Encode(new byte[] { 0x01 })));

        Assert.That(store.NodeExists(address, TreePath.FromHexString("1a"), ValueKeccak.Compute(Leaf)), Is.False);
    }

    [Test]
    public void Replaced_nodes_stay_bounded_over_interrupted_rounds()
    {
        UseRlpSlotEncoding();
        Hash256 address = Keccak.Compute(TestItem.AddressA.Bytes);
        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance) { MaxReplacedNodes = 4 };
        byte[] stored = BranchWithChildren(0, 1);
        byte[] received = BranchWithChildren(0);

        for (int round = 0; round < 10; round++)
        {
            string path = round.ToString("x");
            WriteStorageNodes(address, stored, path);
            Invalidate(store, address, path, received);
            Assert.That(store.ReplacedNodeCount, Is.LessThanOrEqualTo(4));
        }

        ValueHash256 slot = new("0x91abaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        using (IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            writeBatch.SetStorageRawEncoded(address.ValueHash256, slot, Rlp.Encode(new byte[] { 0x01 }));
        }

        Assert.That(HasFlatEntry(address, slot), Is.True, "guards the premise");
        Save(store, address, "9", received);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.ReplacedNodeCount, Is.EqualTo(4));
            Assert.That(HasFlatEntry(address, slot), Is.False, "a path past the bound falls back to deleting outside the saved children");
        }
    }

    [Test]
    public void EnsureStorageEmpty_deletes_storage_trie_nodes()
    {
        Hash256 address = Keccak.Compute(TestItem.AddressA.Bytes);
        byte[] node = [0xc2, 0x80, 0x80];
        ValueHash256 nodeHash = ValueKeccak.Compute(node);
        using (IPersistence.IWriteBatch writeBatch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync))
        {
            writeBatch.SetStorageTrieNode(address, TreePath.Empty, node);
            writeBatch.SetStorageTrieNode(address, TreePath.FromHexString("0a"), node);
            writeBatch.SetStorageTrieNode(address, TreePath.FromHexString("0a1b2c3d4e5f60718293"), node);
        }

        FlatTreeSyncStore store = new(_persistence, Substitute.For<IPersistenceManager>(), LimboLogs.Instance);
        Assert.That(store.NodeExists(address, TreePath.FromHexString("0a1b2c3d4e5f60718293"), nodeHash), Is.True);

        store.EnsureStorageEmpty(address);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.NodeExists(address, TreePath.Empty, nodeHash), Is.False);
            Assert.That(store.NodeExists(address, TreePath.FromHexString("0a"), nodeHash), Is.False);
            Assert.That(store.NodeExists(address, TreePath.FromHexString("0a1b2c3d4e5f60718293"), nodeHash), Is.False);
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
        WriteStorageDirectToDb(removed, 1, [0x01]);
        WriteStorageDirectToDb(kept, 1, [0x02]);

        SaveRootAccountLeaf(store, kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasStorageEntries(removed), Is.False);
            Assert.That(HasStorageEntries(kept), Is.True);
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
        WriteStorageDirectToDb(held, 1, [0x01]);
        WriteStorageDirectToDb(removed, 1, [0x02]);

        SaveRootAccountLeaf(store, held);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasStorageEntries(held), Is.True);
            Assert.That(HasStorageEntries(removed), Is.False);
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
        WriteStorageDirectToDb(orphaned, 1, [0x01]);
        WriteStorageDirectToDb(kept, 1, [0x02]);

        SaveRootAccountLeaf(store, kept);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(HasStorageEntries(orphaned), Is.False);
            Assert.That(HasStorageEntries(kept), Is.True);
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

    private static readonly byte[] StorageNodeRlp = [0xc2, 0x80, 0x80];
    private static readonly TreePath[] StorageNodePaths = [TreePath.FromHexString("1"), TreePath.FromHexString("1111111111111111")];

    private static void SaveRootAccountLeaf(FlatTreeSyncStore store, Address address)
    {
        TrieNode leaf = TrieNodeFactory.CreateLeaf(Nibbles.BytesToNibbleBytes(Keccak.Compute(address.Bytes).Bytes), Rlp.Encode(new Account(1, 1)));
        TreePath path = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        byte[] rlp = leaf.FullRlp.ToArray()!;
        store.SaveNode(null, TreePath.Empty, ValueKeccak.Compute(rlp), rlp);
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
