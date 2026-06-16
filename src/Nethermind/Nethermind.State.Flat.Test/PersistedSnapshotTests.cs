// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using NUnit.Framework;
using WholeReadScanner = Nethermind.State.Flat.PersistedSnapshots.PersistedSnapshotScanner<
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSession,
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSessionReader,
    Nethermind.State.Flat.Hsst.NoOpPin>;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotTests
{
    private ResourcePool _resourcePool = null!;
    private TempDirArenaManager _memArena = null!;
    private BlobArenaManager _blobs = null!;
    private string _blobsDir = null!;

    [SetUp]
    public void SetUp()
    {
        _resourcePool = new ResourcePool(new FlatDbConfig());
        _memArena = new TempDirArenaManager();
        _blobsDir = Path.Combine(Path.GetTempPath(), $"nm-pstest-blobs-{Guid.NewGuid():N}");
        _blobs = new BlobArenaManager(_blobsDir, 4L * 1024 * 1024);
    }

    [TearDown]
    public void TearDown()
    {
        _blobs.Dispose();
        _memArena.Dispose();
        try { Directory.Delete(_blobsDir, recursive: true); } catch { /* best-effort */ }
    }

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, byte[] data) =>
        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _blobs, from, to, data);

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
            c.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);
        })).SetName("StateNode_TopPath");

        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            TreePath path = new(Keccak.Compute("path"), 8);
            c.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);
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

        // Single significant byte < 0x80: RLP wraps it to the byte itself (1 byte), so the
        // stored length is still 1 — distinct from the length-0 absent sentinel.
        yield return new TestCaseData((Action<SnapshotContent>)(c =>
        {
            byte[] value = new byte[32];
            value[31] = 0x05;
            c.Storages[(TestItem.AddressA, (UInt256)9)] = new SlotValue(value);
        })).SetName("Storage_SmallSingleByteSlot");

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
            TreePath path = new(Keccak.Compute("path"), 4);
            c.StorageNodes[(address, path)] = new TrieNode(NodeType.Branch, [0xC1, 0x80]);
        })).SetName("StorageNode_TopPath");

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
            c.StateNodes[topStatePath] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);

            TreePath shortStatePath = new(Keccak.Compute("sp"), 8);
            c.StateNodes[shortStatePath] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);

            TreePath longStatePath = new(Keccak.Compute("lp"), 20);
            c.StateNodes[longStatePath] = new TrieNode(NodeType.Extension, [0xC2, 0x80, 0x81]);

            Hash256 storageAddr = Keccak.Compute("storageAddr");
            TreePath topStoragePath = new(Keccak.Compute("tsp"), 3);
            c.StorageNodes[(storageAddr, topStoragePath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);

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
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Assert.DoesNotThrow(() => PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted));
    }

    // Covers the scanner slot-decode path (PersistedSnapshotScanner.SlotEntry.Value), which
    // PersistPersistedSnapshot uses to flush slots back into the flat DB. Slot values are now
    // RLP-wrapped; this asserts varied widths (1-byte < 0x80, 1-byte >= 0x80, full 32 bytes)
    // decode correctly and that a null/deleted slot is surfaced as null (length-0 sentinel).
    [Test]
    public void Slot_scanner_round_trips_rlp_wrapped_values()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("scan"));

        byte[] small = new byte[32]; small[31] = 0x05;      // RLP(0x05) = 0x05
        byte[] high = new byte[32]; high[31] = 0xFF;         // RLP(0xff) = 0x81 0xff
        byte[] full = new byte[32];
        for (int i = 0; i < 32; i++) full[i] = (byte)(i + 1); // RLP = 0xa0 + 32 bytes

        SnapshotContent content = new();
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(small);
        content.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(high);
        content.Storages[(TestItem.AddressA, (UInt256)3)] = null;          // deleted slot
        content.Storages[(TestItem.AddressB, (UInt256)4)] = new SlotValue(full);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Dictionary<(Address, UInt256), SlotValue?> scanned = [];
        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
                foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
                    scanned[(entry.Address, slot.Slot)] = slot.Value;
        }

        Assert.That(scanned[(TestItem.AddressA, (UInt256)1)]!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(small));
        Assert.That(scanned[(TestItem.AddressA, (UInt256)2)]!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(high));
        Assert.That(scanned[(TestItem.AddressA, (UInt256)3)], Is.Null, "deleted slot must surface as null");
        Assert.That(scanned[(TestItem.AddressB, (UInt256)4)]!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(full));
    }

    // Drives the scanner across every entry kind in one pass: normal vs deleted account,
    // self-destruct false (0x00) vs true (0x01), present vs deleted slot, and state/storage
    // trie nodes spread across all three depth tiers (top/compact/fallback).
    [Test]
    public void FullScan_DecodesAccounts_SelfDestruct_Slots_StateAndStorageNodes()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("fullscan"));

        byte[] slotVal = new byte[32]; slotVal[31] = 0x11;

        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1000).WithNonce(3).TestObject;
        content.Accounts[TestItem.AddressC] = null;                          // deleted marker
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(slotVal);
        content.Storages[(TestItem.AddressA, (UInt256)2)] = null;            // deleted slot
        content.SelfDestructedStorageAddresses[TestItem.AddressD] = false;   // 0x00 destructed
        content.SelfDestructedStorageAddresses[TestItem.AddressE] = true;    // 0x01 new-account
        // State nodes across the three depth tiers.
        TreePath stTop = new(Keccak.Compute("st-top"), 3);
        TreePath stMid = new(Keccak.Compute("st-mid"), 8);
        TreePath stLong = new(Keccak.Compute("st-long"), 20);
        content.StateNodes[stTop] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        content.StateNodes[stMid] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);
        content.StateNodes[stLong] = new TrieNode(NodeType.Extension, [0xC2, 0x80, 0x81]);
        // Storage nodes for one address across the three tiers.
        Hash256 storageAddr = Keccak.Compute("storage-addr");
        TreePath snTop = new(Keccak.Compute("sn-top"), 3);
        TreePath snMid = new(Keccak.Compute("sn-mid"), 6);
        TreePath snLong = new(Keccak.Compute("sn-long"), 18);
        content.StorageNodes[(storageAddr, snTop)] = new TrieNode(NodeType.Leaf, [0xC1, 0x81]);
        content.StorageNodes[(storageAddr, snMid)] = new TrieNode(NodeType.Branch, [0xC1, 0x82]);
        content.StorageNodes[(storageAddr, snLong)] = new TrieNode(NodeType.Leaf, [0xC3, 0x80, 0x81, 0x82]);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        Dictionary<Address, (bool HasAccount, UInt256? Balance, bool? Sd)> perAddr = [];
        Dictionary<(Address, UInt256), SlotValue?> slots = [];
        int stateNodes = 0, storageNodes = 0;

        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry e in scanner.PerAddresses)
            {
                perAddr[e.Address] = (e.HasAccount, e.Account?.Balance, e.SelfDestructFlag);
                foreach (WholeReadScanner.SlotEntry s in e.Slots)
                    slots[(e.Address, s.Slot)] = s.Value;
            }
            foreach (WholeReadScanner.StateNodeEntry n in scanner.StateNodes)
            {
                _ = n.Path;          // exercise the stage-specific path decode
                Assert.That(n.Rlp.Length, Is.GreaterThan(0));
                stateNodes++;
            }
            foreach (WholeReadScanner.StorageNodeEntry n in scanner.StorageNodes)
            {
                _ = n.Path;
                _ = n.AddressHash;
                Assert.That(n.Rlp.Length, Is.GreaterThan(0));
                storageNodes++;
            }
        }

        Assert.That(perAddr[TestItem.AddressA].HasAccount, Is.True);
        Assert.That(perAddr[TestItem.AddressA].Balance, Is.EqualTo((UInt256)1000));
        Assert.That(perAddr[TestItem.AddressA].Sd, Is.Null, "address with no self-destruct sub-tag → null flag");
        Assert.That(perAddr[TestItem.AddressC].HasAccount, Is.True, "deleted account still has a (marker) sub-tag");
        Assert.That(perAddr[TestItem.AddressC].Balance, Is.Null, "deleted account decodes to null");
        Assert.That(perAddr[TestItem.AddressD].HasAccount, Is.False, "self-destruct-only address has no account sub-tag");
        Assert.That(perAddr[TestItem.AddressD].Sd, Is.False, "0x00 marker → destructed");
        Assert.That(perAddr[TestItem.AddressE].Sd, Is.True, "0x01 marker → new account");

        Assert.That(slots[(TestItem.AddressA, (UInt256)1)]!.Value.AsReadOnlySpan.ToArray(), Is.EqualTo(slotVal));
        Assert.That(slots[(TestItem.AddressA, (UInt256)2)], Is.Null, "deleted slot surfaces as null");

        Assert.That(stateNodes, Is.EqualTo(3), "one state node per depth tier");
        Assert.That(storageNodes, Is.EqualTo(3), "one storage node per depth tier");
    }

    // When a column / sub-tag tier is absent, the enumerators must seek past it gracefully:
    // state nodes only in the top tier, storage nodes only in the fallback tier, and no
    // per-address column at all.
    [Test]
    public void Scan_AbsentTiers_SkipMissingColumnsAndSubTags()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("absent"));

        SnapshotContent content = new();
        TreePath onlyTop = new(Keccak.Compute("only-top"), 3);
        content.StateNodes[onlyTop] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        Hash256 storageAddr = Keccak.Compute("absent-storage");
        TreePath onlyFallback = new(Keccak.Compute("only-fallback"), 18);
        content.StorageNodes[(storageAddr, onlyFallback)] = new TrieNode(NodeType.Leaf, [0xC3, 0x80, 0x81, 0x82]);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        int perAddrCount = 0, stateNodes = 0, storageNodes = 0;
        using (WholeReadSession session = persisted.BeginWholeReadSession())
        {
            WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, persisted);
            foreach (WholeReadScanner.PerAddressEntry _ in scanner.PerAddresses) perAddrCount++;
            foreach (WholeReadScanner.StateNodeEntry n in scanner.StateNodes) { _ = n.Path; stateNodes++; }
            foreach (WholeReadScanner.StorageNodeEntry n in scanner.StorageNodes) { _ = n.Path; storageNodes++; }
        }

        Assert.That(perAddrCount, Is.EqualTo(0), "no per-address column → empty enumeration");
        Assert.That(stateNodes, Is.EqualTo(1), "only the top-tier state node, compact/fallback columns absent");
        Assert.That(storageNodes, Is.EqualTo(1), "only the fallback-tier storage node, top/compact sub-tags absent");
    }

    [Test]
    public void ActivePersistedSnapshotCount_TracksConstructionAndDisposal()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to1 = new(1, Keccak.Compute("one"));
        StateId to2 = new(2, Keccak.Compute("two"));

        Snapshot inMem1 = new(from, to1, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        Snapshot inMem2 = new(from, to2, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(inMem1, _blobs);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(inMem2, _blobs);

        long baseline = Active();

        PersistedSnapshot s1 = CreatePersistedSnapshot(from, to1, data1);
        PersistedSnapshot s2 = CreatePersistedSnapshot(from, to2, data2);

        Assert.That(Active(), Is.EqualTo(baseline + 2));

        s1.Dispose();
        Assert.That(Active(), Is.EqualTo(baseline + 1));

        s2.Dispose();
        Assert.That(Active(), Is.EqualTo(baseline));

        static long Active()
        {
            long total = 0;
            foreach (KeyValuePair<PersistedSnapshotLabel, long> kv in Metrics.ActivePersistedSnapshotCount)
                total += kv.Value;
            return total;
        }
    }

    [Test]
    public void BlobArena_FrontierResets_WhenLastPersistedSnapshotDisposes()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("reset"));

        Snapshot inMem = new(from, to, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        TreePath path = new(Keccak.Compute("p"), 8);
        inMem.Content.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);

        long baselineBytes = Metrics.BlobAllocatedBytes;
        // Build writes the trie-node RLPs into _blobs; afterBuild captures that growth.
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(inMem, _blobs);
        long afterBuild = Metrics.BlobAllocatedBytes;
        Assert.That(afterBuild, Is.GreaterThan(baselineBytes), "Building a snapshot with trie nodes should grow blob-allocated bytes");

        // Skip LeaseBlobIdsFromHsst: it acquires an extra lease per blob id that other
        // tests rely on but that this test must not leave dangling, otherwise the
        // orphan-reset would correctly refuse to fire.
        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _blobs, from, to, data, leaseBlobIds: false)
            .Dispose();

        // After the last external lease drops, the manager's TryResetOrphanedFrontier
        // should have reset the file's frontier and pushed the delta back to the gauge.
        Assert.That(Metrics.BlobAllocatedBytes, Is.EqualTo(baselineBytes),
            "Blob-allocated bytes must drop back to baseline once the last referencing snapshot is disposed");
    }

    [TestCase((ushort)0, 0)]
    [TestCase((ushort)42, 12345)]
    [TestCase(ushort.MaxValue, int.MaxValue)]
    public void NodeRef_ReadWrite_RoundTrip(ushort id, int offset)
    {
        Assert.That(NodeRef.Size, Is.EqualTo(6));
        NodeRef original = new(id, offset);
        byte[] buffer = new byte[NodeRef.Size];
        NodeRef.Write(buffer, original);
        NodeRef decoded = NodeRef.Read(buffer);

        Assert.That(decoded.BlobArenaId, Is.EqualTo(id));
        Assert.That(decoded.RlpDataOffset, Is.EqualTo(offset));
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

        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1, _blobs);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2, _blobs);

        PersistedSnapshot p1 = CreatePersistedSnapshot(s0, s1, data1);
        PersistedSnapshot p2 = CreatePersistedSnapshot(s1, s2, data2);

        // Ordered oldest-first; query newest-first via indexer
        PersistedSnapshotList list = new(2) { p1, p2 };
        byte[]? result = null;
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
        Assert.That(result, Is.EqualTo(rlp2));
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
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1, _blobs);

        // Newer: addrA slot 1 = val3 (override), addrA slot 2 = val2 (new)
        SnapshotContent content2 = new();
        content2.Storages[(addrA, (UInt256)1)] = new SlotValue(val3);
        content2.Storages[(addrA, (UInt256)2)] = new SlotValue(val2);
        Snapshot snap2 = new(s1, s2, content2, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2, _blobs);

        PersistedSnapshotList toMerge = new(2) { CreatePersistedSnapshot(s0, s1, data1), CreatePersistedSnapshot(s1, s2, data2) };
        byte[] merged = PersistedSnapshotBuilderTestExtensions.NWayMergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s2, merged);

        // addrA slot 1 should be overridden to val3
        SlotValue slot1 = default;
        Assert.That(persisted.TryGetSlot(addrA, (UInt256)1, ref slot1), Is.True);
        Assert.That(slot1.ToEvmBytes()[0], Is.EqualTo(0x03));

        // addrA slot 2 should be val2 (from newer)
        SlotValue slot2 = default;
        Assert.That(persisted.TryGetSlot(addrA, (UInt256)2, ref slot2), Is.True);
        Assert.That(slot2.ToEvmBytes()[0], Is.EqualTo(0x02));

        // addrB slot 5 should be val2 (from older, carried through)
        SlotValue slot5 = default;
        Assert.That(persisted.TryGetSlot(addrB, (UInt256)5, ref slot5), Is.True);
        Assert.That(slot5.ToEvmBytes()[0], Is.EqualTo(0x02));
    }

    private static IEnumerable<TestCaseData> NullSlotMergeCases()
    {
        byte[] nonZero = new byte[32];
        nonZero[31] = 0xFF;

        yield return new TestCaseData(
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(nonZero)),
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = null),
            (Action<PersistedSnapshot>)(persisted =>
            {
                SlotValue slot = default;
                Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, ref slot), Is.True);
                Assert.That(slot.AsReadOnlySpan.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "Null slot should override value after merge");
            })).SetName("NullOverridesValue");

        yield return new TestCaseData(
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = null),
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(nonZero)),
            (Action<PersistedSnapshot>)(persisted =>
            {
                SlotValue slot = default;
                Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, ref slot), Is.True);
                Assert.That(slot.ToEvmBytes().Length, Is.GreaterThan(0), "Value should override null slot after merge");
            })).SetName("ValueOverridesNull");

        yield return new TestCaseData(
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)1)] = null),
            (Action<SnapshotContent>)(c => c.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(nonZero)),
            (Action<PersistedSnapshot>)(persisted =>
            {
                SlotValue slot1 = default;
                Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, ref slot1), Is.True);
                Assert.That(slot1.AsReadOnlySpan.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "Null slot from older should be preserved");

                SlotValue slot2 = default;
                Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)2, ref slot2), Is.True);
                Assert.That(slot2.AsReadOnlySpan.IndexOfAnyExcept((byte)0), Is.GreaterThanOrEqualTo(0), "Value from newer should be present");
            })).SetName("NullPreservedAndValueCarried");
    }

    [TestCaseSource(nameof(NullSlotMergeCases))]
    public void Storage_NullSlot_Merge(
        Action<SnapshotContent> populateOlder,
        Action<SnapshotContent> populateNewer,
        Action<PersistedSnapshot> verify)
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        SnapshotContent olderContent = new();
        populateOlder(olderContent);
        Snapshot older = new(s0, s1, olderContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataOlder = PersistedSnapshotBuilderTestExtensions.Build(older, _blobs);

        SnapshotContent newerContent = new();
        populateNewer(newerContent);
        Snapshot newer = new(s1, s2, newerContent, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] dataNewer = PersistedSnapshotBuilderTestExtensions.Build(newer, _blobs);

        PersistedSnapshotList toMerge = new(2) { CreatePersistedSnapshot(s0, s1, dataOlder), CreatePersistedSnapshot(s1, s2, dataNewer) };
        byte[] merged = PersistedSnapshotBuilderTestExtensions.NWayMergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s2, merged);

        verify(persisted);
    }

    // Cross-size coverage for the address-bound warmup path added to <see cref="PersistedSnapshot.TryGetAddressBound"/>.
    // Three regimes:
    //   - 4 slots: inner HSST is tiny → warmedWholeBound = true → sub-tag walk goes via SpanByteReader.
    //   - 400 slots: inner HSST is a few KiB → still under the 32 KiB warmup window → SpanByteReader path.
    //   - 4000 slots: inner HSST exceeds 32 KiB → warmedWholeBound = false → sub-tag walk stays on ArenaByteReader.
    // Each case asserts: account/self-destruct/slot/storage-node round-trip on first lookup (cache miss → warmup),
    // a second lookup (cache hit, no warmup), and a third lookup after Demote() drops kernel pages.
    [TestCase(4)]
    [TestCase(400)]
    [TestCase(4000)]
    public void AddressBoundWarmup_RoundTripsAcrossInnerHsstSizes(int slotCount)
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("warmup"));

        Address addr = TestItem.AddressA;
        Hash256 addrHashKey = new(addr.ToAccountPath.Bytes);
        Account expectedAccount = Build.An.Account.WithBalance(987654321).WithNonce(11).TestObject;
        TreePath storagePath = new(Keccak.Compute("warmup-spath"), 6);
        TrieNode storageNode = new(NodeType.Branch, [0xC3, 0x80, 0x81, 0x82]);

        SnapshotContent content = new();
        content.Accounts[addr] = expectedAccount;
        content.SelfDestructedStorageAddresses[addr] = true;
        content.StorageNodes[(addrHashKey, storagePath)] = storageNode;
        for (int i = 0; i < slotCount; i++)
        {
            byte[] val = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(val.AsSpan(28, 4), i + 1);
            content.Storages[(addr, (UInt256)i + 1)] = new SlotValue(val);
        }

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        // Spot-check the sub-tags that the address-bound warmup path serves. The per-address
        // column is keyed by raw Address; storage-trie reads still take the addressHash.
        ValueHash256 addrHash = addr.ToAccountPath;

        // First pass: cache miss → warmup runs.
        Assert.That(persisted.TryGetAccount(addr, out Account? acc1), Is.True);
        Assert.That(acc1, Is.Not.Null);
        Assert.That(acc1!.Balance, Is.EqualTo(expectedAccount.Balance));
        Assert.That(acc1.Nonce, Is.EqualTo(expectedAccount.Nonce));

        Assert.That(persisted.TryGetSelfDestructFlag(addr), Is.EqualTo((bool?)true));

        UInt256 probeIndex = (UInt256)(Math.Min(slotCount, 3));
        SlotValue slot1 = default;
        Assert.That(persisted.TryGetSlot(addr, probeIndex, ref slot1), Is.True);
        byte[] expectedSlotVal = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(expectedSlotVal.AsSpan(28, 4), (int)probeIndex);
        Assert.That(slot1.AsReadOnlySpan.SequenceEqual(expectedSlotVal), Is.True);

        Assert.That(persisted.TryLoadStorageNodeRlp(addrHash, storagePath, out byte[]? nodeRlp1), Is.True);
        Assert.That(nodeRlp1, Is.EqualTo(storageNode.FullRlp.ToArray()));

        // Second pass: cache hit → no warmup, results must match.
        Assert.That(persisted.TryGetAccount(addr, out Account? acc2), Is.True);
        Assert.That(acc2!.Balance, Is.EqualTo(expectedAccount.Balance));
        SlotValue slot2 = default;
        Assert.That(persisted.TryGetSlot(addr, probeIndex, ref slot2), Is.True);
        Assert.That(slot2.AsReadOnlySpan.SequenceEqual(expectedSlotVal), Is.True);

        // AdviseDontNeed: the per-arena tracker entries are forgotten and the mmap range
        // is advised cold. The inline address-bound cache slot is unaffected (it holds an
        // arena offset, not page-residency state) so the *next* TryGetAccount call hits the
        // cache. For a small bound this exercises the cache-hit-with-cold-pages branch:
        // TryGetAddressBound's hit path now also calls TouchRangePopulate on the whole bound
        // when bound.Length <= AddressBoundWarmupBytes, re-arming the tracker and (on a real
        // mmap) re-faulting any cold page in one syscall. With TempDirArenaManager the kernel
        // side is a no-op; the assertion below just proves the lookup path remains correct.
        persisted.AdviseDontNeed();
        Assert.That(persisted.TryGetAccount(addr, out Account? acc3), Is.True);
        Assert.That(acc3!.Nonce, Is.EqualTo(expectedAccount.Nonce));
        SlotValue slot3 = default;
        Assert.That(persisted.TryGetSlot(addr, probeIndex, ref slot3), Is.True);
        Assert.That(slot3.AsReadOnlySpan.SequenceEqual(expectedSlotVal), Is.True);

        // Fresh miss for an unrelated address still works after AdviseDontNeed.
        Assert.That(persisted.TryGetAccount(TestItem.AddressB, out _), Is.False);
    }
}
