// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    private ArenaManager _memArena = null!;
    private string _memArenaDir = null!;
    private BlobArenaManager _blobs = null!;
    private string _blobsDir = null!;

    [SetUp]
    public void SetUp()
    {
        _resourcePool = new ResourcePool(new FlatDbConfig());
        _memArenaDir = Path.Combine(Path.GetTempPath(), $"nm-pstest-arena-{Guid.NewGuid():N}");
        _memArena = TestFixtureHelpers.CreateArenaManager(_memArenaDir);
        _blobsDir = Path.Combine(Path.GetTempPath(), $"nm-pstest-blobs-{Guid.NewGuid():N}");
        _blobs = new BlobArenaManager(_blobsDir, 4L * 1024 * 1024);
    }

    [TearDown]
    public void TearDown()
    {
        _blobs.Dispose();
        _memArena.Dispose();
        try { Directory.Delete(_blobsDir, recursive: true); } catch { /* best-effort */ }
        try { Directory.Delete(_memArenaDir, recursive: true); } catch { /* best-effort */ }
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

    // Regression: a storage HSST node can land within <12 bytes of a 4 KiB boundary in a
    // region-relative (SpanByteReader-scoped) read; TryLoadNode used to clamp the speculative
    // window to that short page remainder and overrun the 12-byte header. A single account with
    // ~280 spread-out slots places such a node; reading every slot back must not throw.
    [Test]
    public void StorageNode_NearPageBoundary_RoundTrips()
    {
        Address a = TestItem.AddressA;
        const int slotCount = 280;

        SnapshotContent content = new();
        content.Accounts[a] = Build.An.Account.WithBalance(1).TestObject;
        SlotValue[] expected = new SlotValue[slotCount];
        UInt256[] keys = new UInt256[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            keys[i] = new UInt256(Keccak.Compute(i.ToString()).Bytes, isBigEndian: true);
            byte[] v = new byte[32];
            v[31] = (byte)((i % 255) + 1);
            expected[i] = new SlotValue(v);
            content.Storages[(a, keys[i])] = expected[i];
        }

        StateId from = new(0, Keccak.EmptyTreeHash), to = new(1, Keccak.Compute("to"));
        string arenaDir = Path.Combine(Path.GetTempPath(), $"nm-regr-arena-{Guid.NewGuid():N}");
        using ArenaManager arena = TestFixtureHelpers.CreateArenaManager(arenaDir, 64 * 1024 * 1024);
        string blobsDir = Path.Combine(Path.GetTempPath(), $"nm-regr-{Guid.NewGuid():N}");
        using BlobArenaManager blobs = new(blobsDir, 64L * 1024 * 1024);
        try
        {
            Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
            byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, blobs);
            using PersistedSnapshot persisted = TestFixtureHelpers.CreatePersistedSnapshot(arena, blobs, from, to, data);

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < slotCount; i++)
                {
                    SlotValue got = default;
                    Assert.That(persisted.TryGetSlot(a, keys[i], ref got), Is.True, $"slot {i} missing");
                    Assert.That(got.AsReadOnlySpan.SequenceEqual(expected[i].AsReadOnlySpan), Is.True, $"slot {i} mismatch");
                }
            });
        }
        finally
        {
            try { Directory.Delete(blobsDir, recursive: true); } catch { /* best-effort */ }
            try { Directory.Delete(arenaDir, recursive: true); } catch { /* best-effort */ }
        }
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
        content.Storages[(TestItem.AddressA, (UInt256)3)] = null;
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
        content.Storages[(TestItem.AddressA, (UInt256)2)] = null;
        content.SelfDestructedStorageAddresses[TestItem.AddressD] = false;   // 0x00 destructed
        content.SelfDestructedStorageAddresses[TestItem.AddressE] = true;    // 0x01 new-account
        TreePath stTop = new(Keccak.Compute("st-top"), 3);
        TreePath stMid = new(Keccak.Compute("st-mid"), 8);
        TreePath stLong = new(Keccak.Compute("st-long"), 20);
        content.StateNodes[stTop] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        content.StateNodes[stMid] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]);
        content.StateNodes[stLong] = new TrieNode(NodeType.Extension, [0xC2, 0x80, 0x81]);
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

    // Exercises the read-path miss branches: a present snapshot queried for keys that are
    // absent at every level — unknown address, present-address/absent-slot, present-address/
    // no-self-destruct, absent state node, absent storage addressHash, and present-addressHash/
    // absent-path (same and different sub-tag tier).
    [Test]
    public void Queries_ForAbsentKeys_ReturnMisses()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("miss"));

        byte[] slotVal = new byte[32]; slotVal[31] = 0x07;
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(5).TestObject;
        content.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(9).TestObject; // 2nd address → real address BTree
        content.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(slotVal);
        content.SelfDestructedStorageAddresses[TestItem.AddressA] = true;
        TreePath statePath = new(Keccak.Compute("sp"), 4);
        content.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        Hash256 storageHashObj = Keccak.Compute("sh");
        TreePath storagePath = new(Keccak.Compute("stp"), 4);
        content.StorageNodes[(storageHashObj, storagePath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x81]);

        Snapshot snapshot = new(from, to, content, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        SlotValue sv = default;
        // Unknown address: BTree seek misses.
        Assert.That(persisted.TryGetAccount(TestItem.AddressB, out Account? accB), Is.False);
        Assert.That(accB, Is.Null);
        Assert.That(persisted.TryGetSlot(TestItem.AddressB, (UInt256)1, ref sv), Is.False);
        Assert.That(persisted.TryGetSelfDestructFlag(TestItem.AddressB), Is.Null);

        // Present address, absent slot index; present address with no slot/self-destruct sub-tag.
        Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)999, ref sv), Is.False);
        Assert.That(persisted.TryGetSlot(TestItem.AddressC, (UInt256)1, ref sv), Is.False);
        Assert.That(persisted.TryGetSelfDestructFlag(TestItem.AddressC), Is.Null);

        // Absent state node.
        Assert.That(persisted.TryLoadStateNodeRlp(new TreePath(Keccak.Compute("absent"), 4), out byte[]? sn), Is.False);
        Assert.That(sn, Is.Null);

        // Storage node: absent addressHash; present addressHash with absent path in the same
        // sub-tag tier and in a different (absent) tier.
        ValueHash256 storageHash = new(storageHashObj.Bytes);
        Assert.That(persisted.TryLoadStorageNodeRlp(new ValueHash256(Keccak.Compute("nope").Bytes), storagePath, out _), Is.False);
        Assert.That(persisted.TryLoadStorageNodeRlp(storageHash, new TreePath(Keccak.Compute("absentSameTier"), 4), out _), Is.False);
        Assert.That(persisted.TryLoadStorageNodeRlp(storageHash, new TreePath(Keccak.Compute("absentDeep"), 18), out _), Is.False);

        Assert.That(persisted.TryGetAccount(TestItem.AddressA, out _), Is.True);
        Assert.That(persisted.TryLoadStorageNodeRlp(storageHash, storagePath, out _), Is.True);
    }

    // An empty snapshot has no address column (cached BTree bound is empty) and no node
    // columns, so every read returns a miss without faulting.
    [Test]
    public void Queries_OnEmptySnapshot_ReturnMisses()
    {
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("empty-reads"));
        Snapshot snapshot = new(from, to, new SnapshotContent(), _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilderTestExtensions.Build(snapshot, _blobs);
        using PersistedSnapshot persisted = CreatePersistedSnapshot(from, to, data);

        SlotValue sv = default;
        Assert.That(persisted.TryGetAccount(TestItem.AddressA, out _), Is.False);
        Assert.That(persisted.TryGetSlot(TestItem.AddressA, (UInt256)1, ref sv), Is.False);
        Assert.That(persisted.TryGetSelfDestructFlag(TestItem.AddressA), Is.Null);
        Assert.That(persisted.TryLoadStateNodeRlp(new TreePath(Keccak.Compute("p"), 4), out _), Is.False);
        Assert.That(persisted.TryLoadStorageNodeRlp(new ValueHash256(Keccak.Compute("h").Bytes), new TreePath(Keccak.Compute("p"), 4), out _), Is.False);

        // Build-based snapshots carry no blob_range metadata → BlobRange.None → advise is a no-op.
        Assert.DoesNotThrow(() => persisted.AdviseWillNeedBlobRange());
        Assert.DoesNotThrow(() => persisted.AdviseDontNeedBlobRange());
    }

    // Drives PersistedSnapshotStack's newest-first probe loops over a two-snapshot stack:
    // hits in the newer and (after a newer miss) the older snapshot, full misses, the
    // self-destruct slot boundary, and the detailed-metrics observations.
    [Test]
    public void Stack_ProbesNewestFirst_AcrossAllKinds()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("st1"));
        StateId s2 = new(2, Keccak.Compute("st2"));

        byte[] v1 = new byte[32]; v1[31] = 0x11;
        byte[] v2 = new byte[32]; v2[31] = 0x22;

        SnapshotContent older = new();
        older.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
        older.Accounts[TestItem.AddressD] = Build.An.Account.WithBalance(40).TestObject;
        older.Storages[(TestItem.AddressA, (UInt256)1)] = new SlotValue(v1);
        older.SelfDestructedStorageAddresses[TestItem.AddressA] = false;
        TreePath statePath = new(Keccak.Compute("st-p"), 4);
        older.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        Hash256 storageHashObj = Keccak.Compute("st-sh");
        TreePath storagePath = new(Keccak.Compute("st-sp"), 4);
        older.StorageNodes[(storageHashObj, storagePath)] = new TrieNode(NodeType.Leaf, [0xC1, 0x81]);

        SnapshotContent newer = new();
        newer.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(200).TestObject;
        newer.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(7).TestObject;
        newer.Storages[(TestItem.AddressA, (UInt256)2)] = new SlotValue(v2);

        byte[] olderData = PersistedSnapshotBuilderTestExtensions.Build(
            new Snapshot(s0, s1, older, _resourcePool, ResourcePool.Usage.MainBlockProcessing), _blobs);
        byte[] newerData = PersistedSnapshotBuilderTestExtensions.Build(
            new Snapshot(s1, s2, newer, _resourcePool, ResourcePool.Usage.MainBlockProcessing), _blobs);

        PersistedSnapshotList list = new(2) { CreatePersistedSnapshot(s0, s1, olderData), CreatePersistedSnapshot(s1, s2, newerData) };
        using PersistedSnapshotStack stack = new(list, recordDetailedMetrics: true);

        // Account: newest wins; older-only address resolves after the newer miss; full miss.
        Assert.That(stack.TryGetAccount(TestItem.AddressA, out Account? a), Is.True);
        Assert.That(a!.Balance, Is.EqualTo((UInt256)200), "newest snapshot wins");
        Assert.That(stack.TryGetAccount(TestItem.AddressD, out Account? d), Is.True);
        Assert.That(d!.Balance, Is.EqualTo((UInt256)40), "older-only address resolves after newer miss");
        Assert.That(stack.TryGetAccount(TestItem.AddressF, out _), Is.False);

        // Self-destruct: only the older snapshot carries it.
        Assert.That(stack.TryGetSelfDestruct(TestItem.AddressA, out int sdIdx), Is.True);
        Assert.That(sdIdx, Is.EqualTo(0));
        Assert.That(stack.TryGetSelfDestruct(TestItem.AddressF, out _), Is.False);

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        // Slot: newer holds slot 2, older holds slot 1; both resolve.
        Assert.That(stack.TryGetSlot(TestItem.AddressA, (UInt256)2, -1, start, out byte[]? sv2), Is.True);
        Assert.That(sv2![^1], Is.EqualTo((byte)0x22)); // ToEvmBytes strips leading zeros
        Assert.That(stack.TryGetSlot(TestItem.AddressA, (UInt256)1, -1, start, out byte[]? sv1), Is.True);
        Assert.That(sv1![^1], Is.EqualTo((byte)0x11));
        // Slot below the self-destruct boundary resolves to null (storage wiped).
        Assert.That(stack.TryGetSlot(TestItem.AddressA, (UInt256)999, 0, start, out byte[]? svNull), Is.True);
        Assert.That(svNull, Is.Null);
        // Slot fully absent (no boundary) falls through.
        Assert.That(stack.TryGetSlot(TestItem.AddressF, (UInt256)1, -1, start, out _), Is.False);

        // State / storage node RLP: present (in older) and absent.
        Assert.That(stack.TryLoadStateRlp(statePath, out byte[]? srlp), Is.True);
        Assert.That(srlp, Is.Not.Null);
        Assert.That(stack.TryLoadStateRlp(new TreePath(Keccak.Compute("nope-st"), 4), out _), Is.False);
        Assert.That(stack.TryLoadStorageRlp(storageHashObj, storagePath, out byte[]? strlp), Is.True);
        Assert.That(strlp, Is.Not.Null);
        Assert.That(stack.TryLoadStorageRlp(storageHashObj, new TreePath(Keccak.Compute("nope-sp"), 4), out _), Is.False);
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

        SnapshotContent content1 = new();
        content1.Storages[(addrA, (UInt256)1)] = new SlotValue(val1);
        content1.Storages[(addrB, (UInt256)5)] = new SlotValue(val2);
        Snapshot snap1 = new(s0, s1, content1, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1, _blobs);

        SnapshotContent content2 = new();
        content2.Storages[(addrA, (UInt256)1)] = new SlotValue(val3);
        content2.Storages[(addrA, (UInt256)2)] = new SlotValue(val2);
        Snapshot snap2 = new(s1, s2, content2, _resourcePool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2, _blobs);

        PersistedSnapshotList toMerge = new(2) { CreatePersistedSnapshot(s0, s1, data1), CreatePersistedSnapshot(s1, s2, data2) };
        byte[] merged = PersistedSnapshotBuilderTestExtensions.NWayMergeSnapshots(toMerge);
        PersistedSnapshot persisted = CreatePersistedSnapshot(s0, s2, merged);

        SlotValue slot1 = default;
        Assert.That(persisted.TryGetSlot(addrA, (UInt256)1, ref slot1), Is.True);
        Assert.That(slot1.ToEvmBytes()[0], Is.EqualTo(0x03));

        SlotValue slot2 = default;
        Assert.That(persisted.TryGetSlot(addrA, (UInt256)2, ref slot2), Is.True);
        Assert.That(slot2.ToEvmBytes()[0], Is.EqualTo(0x02));

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

    // Round-trips account / self-destruct / slot / storage-node across a range of slot counts,
    // including a multi-page snapshot, then re-reads after AdviseDontNeed drops the kernel pages.
    [TestCase(4)]
    [TestCase(400)]
    [TestCase(4000)]
    public void RoundTrips_AcrossSlotCounts(int slotCount)
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
        // The flat sorted table materialises a full record per slot, so a large slot count exceeds
        // the shared 64 KiB fixture arena — use a roomier local arena for this case.
        string arenaDir = Path.Combine(Path.GetTempPath(), $"nm-pstest-rt-{Guid.NewGuid():N}");
        using ArenaManager arena = TestFixtureHelpers.CreateArenaManager(arenaDir, 64 * 1024 * 1024);
        using PersistedSnapshot persisted = TestFixtureHelpers.CreatePersistedSnapshot(arena, _blobs, from, to, data);

        // Per-address entries are keyed by raw Address; storage-trie reads take the addressHash.
        ValueHash256 addrHash = addr.ToAccountPath;

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

        // Second pass: results must match.
        Assert.That(persisted.TryGetAccount(addr, out Account? acc2), Is.True);
        Assert.That(acc2!.Balance, Is.EqualTo(expectedAccount.Balance));
        SlotValue slot2 = default;
        Assert.That(persisted.TryGetSlot(addr, probeIndex, ref slot2), Is.True);
        Assert.That(slot2.AsReadOnlySpan.SequenceEqual(expectedSlotVal), Is.True);

        // AdviseDontNeed advises the mmap range cold; the next reads re-fault any dropped page
        // and the binary search must still resolve correctly.
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
