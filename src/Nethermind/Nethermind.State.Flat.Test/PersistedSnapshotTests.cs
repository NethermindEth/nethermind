// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotTests
{
    private ResourcePool _resourcePool = null!;
    private MemoryArenaManager _memArena = null!;

    [SetUp]
    public void SetUp()
    {
        _resourcePool = new ResourcePool(new FlatDbConfig());
        _memArena = new MemoryArenaManager();
    }

    [TearDown]
    public void TearDown() => _memArena.Dispose();

    private PersistedSnapshot CreatePersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, byte[] data)
    {
        using ArenaWriter writer = _memArena.CreateWriter();
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        return new PersistedSnapshot(id, from, to, type, reservation);
    }

    private static IEnumerable<TestCaseData> RoundTripTestCases()
    {
        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1000).TestObject;
        })).SetName("Account");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            c.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
        })).SetName("SelfDestruct");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            TreePath path = new(Keccak.Compute("path"), 4);
            c.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC0, 0x80, 0x80]);
        })).SetName("StateNode_TopPath");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            TreePath path = new(Keccak.Compute("path"), 8);
            c.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC0, 0x80, 0x80]);
        })).SetName("StateNode_CompactPath");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            TreePath longPath = new(Keccak.Compute("longpath"), 20);
            c.StateNodes[longPath] = new TrieNode(NodeType.Extension, [0xC2, 0x80, 0x81]);
        })).SetName("StateNode_LongPath");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            byte[] value = new byte[32];
            value[31] = 0xFF;
            c.Storages[(TestItem.AddressA, (UInt256)42)] = new SlotValue(value);
        })).SetName("Storage_SingleSlot");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            byte[] value = new byte[32];
            value[31] = 0xAB;
            c.Storages[(TestItem.AddressA, UInt256.Zero)] = new SlotValue(value);
        })).SetName("Storage_ZeroSlot");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            c.Storages[(TestItem.AddressA, (UInt256)1)] = null;
            byte[] val = new byte[32];
            val[31] = 0xFF;
            c.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(val);
        })).SetName("Storage_NullSlot");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            byte[] val1 = new byte[32]; val1[31] = 0x01;
            byte[] val2 = new byte[32]; val2[31] = 0x02;
            byte[] val3 = new byte[32]; val3[31] = 0x03;
            c.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(val1);
            c.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(val2);
            c.Storages[(TestItem.AddressB, (UInt256)5)] = new SlotValue(val3);
        })).SetName("Storage_MultipleAddresses");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            Hash256 address = Keccak.Compute("address");
            TreePath path = new(Keccak.Compute("path"), 6);
            c.StorageNodes[(address, path)] = new TrieNode(NodeType.Branch, [0xC1, 0x80]);
        })).SetName("StorageNode_CompactPath");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            Hash256 address = Keccak.Compute("address");
            TreePath longPath = new(Keccak.Compute("longpath"), 18);
            c.StorageNodes[(address, longPath)] = new TrieNode(NodeType.Branch, [0xC3, 0x80, 0x81, 0x82]);
        })).SetName("StorageNode_LongPath");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            c.Accounts[TestItem.AddressA] = Build.An.Account
                .WithBalance(12345).WithNonce(7).TestObject;
            c.Accounts[TestItem.AddressB] = Build.An.Account
                .WithBalance(0).WithNonce(0)
                .WithCode([0x60, 0x00])
                .WithStorageRoot(Keccak.Compute("storage")).TestObject;
            c.Accounts[TestItem.AddressC] = null;

            byte[] slotVal1 = new byte[32]; slotVal1[31] = 0xFF;
            byte[] slotVal2 = new byte[32]; slotVal2[0] = 0x01; slotVal2[31] = 0x02;
            c.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(slotVal1);
            c.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(slotVal2);
            c.Storages[(TestItem.AddressB, (UInt256)42)] = null;

            c.SelfDestructedStorageAddresses[TestItem.AddressD] = false;
            c.SelfDestructedStorageAddresses[TestItem.AddressE] = true;

            TreePath topStatePath = new(Keccak.Compute("tp"), 3);
            c.StateNodes[topStatePath] = new TrieNode(NodeType.Leaf, [0xBF, 0x80]);

            TreePath shortStatePath = new(Keccak.Compute("sp"), 8);
            c.StateNodes[shortStatePath] = new TrieNode(NodeType.Leaf, [0xC0, 0x80, 0x80]);

            TreePath longStatePath = new(Keccak.Compute("lp"), 20);
            c.StateNodes[longStatePath] = new TrieNode(NodeType.Extension, [0xC2, 0x80, 0x81]);

            Hash256 storageAddr = Keccak.Compute("storageAddr");
            TreePath shortStoragePath = new(Keccak.Compute("ssp"), 6);
            c.StorageNodes[(storageAddr, shortStoragePath)] = new TrieNode(NodeType.Branch, [0xC1, 0x80]);

            TreePath longStoragePath = new(Keccak.Compute("lsp"), 18);
            c.StorageNodes[(storageAddr, longStoragePath)] = new TrieNode(NodeType.Leaf, [0xC3, 0x80, 0x81, 0x82]);
        })).SetName("AllDataTypes");
    }

    [TestCaseSource(nameof(RoundTripTestCases))]
    public void RoundTrip(Action<SnapshotContent> populateContent)
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("1"));

        SnapshotContent content = new();
        populateContent(content);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot);
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, from, to, PersistedSnapshotType.Full, data);

        Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted));
    }

    [Test]
    public void NodeRef_ReadWrite_RoundTrip()
    {
        NodeRef original = new(42, 12345);
        byte[] buffer = new byte[NodeRef.Size];
        NodeRef.Write(buffer, original);
        NodeRef decoded = NodeRef.Read(buffer);

        Assert.That(decoded.SnapshotId, Is.EqualTo(42));
        Assert.That(decoded.ValueLengthOffset, Is.EqualTo(12345));
    }

    [Test]
    public void PersistedSnapshotList_Queries_NewestFirst()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        // path length 4 → StateTopNodes column
        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] rlp1 = [0xC0];
        byte[] rlp2 = [0xC1, 0x80];

        SnapshotContent content1 = new();
        content1.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp1);
        Snapshot snap1 = new(s0, s1, content1, _resourcePool, ResourcePool.Usage.MainBlockProcessing);

        SnapshotContent content2 = new();
        content2.StateNodes[path] = new TrieNode(NodeType.Leaf, rlp2);
        Snapshot snap2 = new(s1, s2, content2, _resourcePool, ResourcePool.Usage.MainBlockProcessing);

        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2);

        PersistedSnapshot p1 = CreatePersistedSnapshot(1, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot p2 = CreatePersistedSnapshot(2, s1, s2, PersistedSnapshotType.Full, data2);

        // Ordered oldest-first; query newest-first via indexer
        PersistedSnapshotList list = new(2);
        list.Add(p1);
        list.Add(p2);
        ReadOnlySpan<byte> result = default;
        bool found = false;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].TryLoadStateNodeRlp(path, out result))
            {
                found = true;
                break;
            }
        }

        // Should return the newest (p2) value
        Assert.That(found, Is.True);
        Assert.That(result.ToArray(), Is.EqualTo(rlp2));
    }

    [Test]
    [Explicit]
    public void DiagnosticJsonFile_RoundTrip_ViaHsst()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(100, Keccak.Compute("100"));

        // Dump to JSON using the DumpSnapshotToJson method
        string jsonPath = "/home/amirul/repo/nethermind/broken.23447047.23447048.json";
        SnapshotContent content = PersistedSnapshotUtils.ReadSnapshotFromJson(jsonPath);

        // Build HSST from original snapshot
        Snapshot snapshot = new Snapshot(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot);
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, from, to, PersistedSnapshotType.Full, data);

        PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, dumpWhenFailed: false);
    }

    [Test]
    public void Storage_NestedMerge_OverlappingAddresses()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        Address addrA = TestItem.AddressA;
        Address addrB = TestItem.AddressB;
        byte[] val1 = new byte[32]; val1[31] = 0x01;
        byte[] val2 = new byte[32]; val2[31] = 0x02;
        byte[] val3 = new byte[32]; val3[31] = 0x03;

        // Older: addrA slot 1 = val1, addrB slot 5 = val2
        SnapshotContent content1 = new();
        content1.Storages[(addrA, (UInt256)1)] = new SlotValue(val1);
        content1.Storages[(addrB, (UInt256)5)] = new SlotValue(val2);
        Snapshot snap1 = new(s0, s1, content1, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1);

        // Newer: addrA slot 1 = val3 (override), addrA slot 2 = val2 (new)
        SnapshotContent content2 = new();
        content2.Storages[(addrA, (UInt256)1)] = new SlotValue(val3);
        content2.Storages[(addrA, (UInt256)2)] = new SlotValue(val2);
        Snapshot snap2 = new(s1, s2, content2, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2);

        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1));
        toMerge.Add(CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2));
        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(1, s0, s2, PersistedSnapshotType.Full, merged);

        // addrA slot 1 should be overridden to val3
        Assert.That(persisted.TryGetSlot(addrA, (UInt256)1, out ReadOnlySpan<byte> slot1), Is.True);
        Assert.That(slot1[0], Is.EqualTo(0x03));

        // addrA slot 2 should be val2 (from newer)
        Assert.That(persisted.TryGetSlot(addrA, (UInt256)2, out ReadOnlySpan<byte> slot2), Is.True);
        Assert.That(slot2[0], Is.EqualTo(0x02));

        // addrB slot 5 should be val2 (from older, carried through)
        Assert.That(persisted.TryGetSlot(addrB, (UInt256)5, out ReadOnlySpan<byte> slot5), Is.True);
        Assert.That(slot5[0], Is.EqualTo(0x02));
    }

    [Test]
    public void Storage_NullSlot_Merge_OverridesValue()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        Address addr = TestItem.AddressA;

        // Older: slot 1 has a value
        byte[] val = new byte[32]; val[31] = 0xFF;
        SnapshotContent olderContent = new();
        olderContent.Storages[(addr, (UInt256)1)] = new SlotValue(val);
        Snapshot older = new(s0, s1, olderContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataOlder = PersistedSnapshotBuilderTestExtensions.Build(older);

        // Newer: slot 1 set to null (deleted)
        SnapshotContent newerContent = new();
        newerContent.Storages[(addr, (UInt256)1)] = null;
        Snapshot newer = new(s1, s2, newerContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataNewer = PersistedSnapshotBuilderTestExtensions.Build(newer);

        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, dataOlder));
        toMerge.Add(CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, dataNewer));
        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Full, merged);

        Assert.That(persisted.TryGetSlot(addr, (UInt256)1, out ReadOnlySpan<byte> slot), Is.True);
        Assert.That(slot.Length, Is.EqualTo(0), "Null slot should override value after merge");
    }

    [Test]
    public void Storage_NullSlot_Merge_ValueOverridesNull()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        Address addr = TestItem.AddressA;

        // Older: slot 1 is null (deleted)
        SnapshotContent olderContent = new();
        olderContent.Storages[(addr, (UInt256)1)] = null;
        Snapshot older = new(s0, s1, olderContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataOlder = PersistedSnapshotBuilderTestExtensions.Build(older);

        // Newer: slot 1 has a value
        byte[] val = new byte[32]; val[31] = 0xFF;
        SnapshotContent newerContent = new();
        newerContent.Storages[(addr, (UInt256)1)] = new SlotValue(val);
        Snapshot newer = new(s1, s2, newerContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataNewer = PersistedSnapshotBuilderTestExtensions.Build(newer);

        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, dataOlder));
        toMerge.Add(CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, dataNewer));
        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Full, merged);

        Assert.That(persisted.TryGetSlot(addr, (UInt256)1, out ReadOnlySpan<byte> slot), Is.True);
        Assert.That(slot.Length, Is.GreaterThan(0), "Value should override null slot after merge");
    }

    [Test]
    public void Storage_NullSlot_Merge_PreservesFromOlder()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        Address addr = TestItem.AddressA;

        // Older: slot 1 is null (deleted)
        SnapshotContent olderContent = new();
        olderContent.Storages[(addr, (UInt256)1)] = null;
        Snapshot older = new(s0, s1, olderContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataOlder = PersistedSnapshotBuilderTestExtensions.Build(older);

        // Newer: slot 2 has a value (different slot, doesn't touch slot 1)
        byte[] val = new byte[32]; val[31] = 0xFF;
        SnapshotContent newerContent = new();
        newerContent.Storages[(addr, (UInt256)2)] = new SlotValue(val);
        Snapshot newer = new(s1, s2, newerContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataNewer = PersistedSnapshotBuilderTestExtensions.Build(newer);

        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, dataOlder));
        toMerge.Add(CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, dataNewer));
        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Full, merged);

        Assert.That(persisted.TryGetSlot(addr, (UInt256)1, out ReadOnlySpan<byte> slot1), Is.True);
        Assert.That(slot1.Length, Is.EqualTo(0), "Null slot from older should be preserved");

        Assert.That(persisted.TryGetSlot(addr, (UInt256)2, out ReadOnlySpan<byte> slot2), Is.True);
        Assert.That(slot2.Length, Is.GreaterThan(0), "Value from newer should be present");
    }

    [Test]
    [Explicit]
    public void DiagnosticCompactedJsonFile()
    {
        string jsonPath = "/home/amirul/repo/nethermind/broken.compacted.23447048.23447052.json";
        List<string> base64List = System.Text.Json.JsonSerializer.Deserialize<List<string>>(System.IO.File.ReadAllText(jsonPath))!;

        PersistedSnapshotList snapshots = new(base64List.Count);
        for (int i = 0; i < base64List.Count; i++)
        {
            byte[] data = Convert.FromBase64String(base64List[i]);
            StateId snapFrom = new(23447048 + i, Keccak.Compute($"{i}"));
            StateId snapTo = new(23447048 + i + 1, Keccak.Compute($"{i + 1}"));
            snapshots.Add(CreatePersistedSnapshot(i, snapFrom, snapTo, PersistedSnapshotType.Full, data));
        }

        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(snapshots);

        StateId compFrom = snapshots[0].From;
        StateId compTo = snapshots[snapshots.Count - 1].To;
        PersistedSnapshot compacted = CreatePersistedSnapshot(100, compFrom, compTo,
            PersistedSnapshotType.Linked, merged);
        PersistedSnapshotUtils.ValidateCompactedPersistedSnapshot(compacted, snapshots, true);
    }

}
