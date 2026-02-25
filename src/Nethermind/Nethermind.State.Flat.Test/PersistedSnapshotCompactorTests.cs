// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistedSnapshotCompactorTests
{
    private ResourcePool _pool = null!;
    private MemoryArenaManager _memArena = null!;

    [SetUp]
    public void SetUp()
    {
        _pool = new ResourcePool(new FlatDbConfig());
        _memArena = new MemoryArenaManager();
    }

    [TearDown]
    public void TearDown() =>
        _memArena.Dispose();

    private PersistedSnapshot CreatePersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, byte[] data,
        PersistedSnapshot[]? referencedSnapshots = null)
    {
        SnapshotLocation loc = _memArena.Allocate(data);
        ArenaReservation reservation = _memArena.Open(loc);
        return new PersistedSnapshot(id, from, to, type, reservation, referencedSnapshots);
    }

    [Test]
    public void MergeSnapshotData_NewerOverridesOlder()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] rlpOlder = [0xC0];
        byte[] rlpNewer = [0xC1, 0x80];

        // Build older snapshot
        SnapshotContent content1 = new();
        content1.StateNodes[path] = new TrieNode(NodeType.Leaf, rlpOlder);
        content1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] olderData = PersistedSnapshotBuilder.Build(snap1);

        // Build newer snapshot with same path but different value, and a new account
        SnapshotContent content2 = new();
        content2.StateNodes[path] = new TrieNode(NodeType.Leaf, rlpNewer);
        content2.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] newerData = PersistedSnapshotBuilder.Build(snap2);

        // Merge
        PersistedSnapshot baseSnap0 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, olderData);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, newerData);
        PersistedSnapshotList toMerge0 = new(2);
        toMerge0.Add(baseSnap0);
        toMerge0.Add(baseSnap1);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge0);

        // Create PersistedSnapshot from merged data with references to base snapshots
        PersistedSnapshot mergedSnapshot = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged,
            [baseSnap0, baseSnap1]);

        // State node should have newer value
        Assert.That(mergedSnapshot.TryLoadStateNodeRlp(path, out ReadOnlySpan<byte> nodeRlp), Is.True);
        Assert.That(nodeRlp.ToArray(), Is.EqualTo(rlpNewer));

        // Both accounts should be present
        Assert.That(mergedSnapshot.TryGetAccount(TestItem.AddressA, out _), Is.True);
        Assert.That(mergedSnapshot.TryGetAccount(TestItem.AddressB, out _), Is.True);
    }

    [Test]
    public void MergeSnapshotData_PreservesNonOverlappingEntries()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path1 = new(Keccak.Compute("path1"), 4);
        TreePath path2 = new(Keccak.Compute("path2"), 4);
        byte[] rlp1 = [0xC0];
        byte[] rlp2 = [0xC1, 0x80];

        SnapshotContent content1 = new();
        content1.StateNodes[path1] = new TrieNode(NodeType.Leaf, rlp1);
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilder.Build(snap1);

        SnapshotContent content2 = new();
        content2.StateNodes[path2] = new TrieNode(NodeType.Leaf, rlp2);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilder.Build(snap2);

        PersistedSnapshot baseSnap1_1 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap1_2 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge1 = new(2);
        toMerge1.Add(baseSnap1_1);
        toMerge1.Add(baseSnap1_2);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge1);
        PersistedSnapshot mergedSnapshot = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged,
            [baseSnap1_1, baseSnap1_2]);

        // Both paths should be present
        Assert.That(mergedSnapshot.TryLoadStateNodeRlp(path1, out ReadOnlySpan<byte> rlp1Result), Is.True);
        Assert.That(rlp1Result.ToArray(), Is.EqualTo(rlp1));
        Assert.That(mergedSnapshot.TryLoadStateNodeRlp(path2, out ReadOnlySpan<byte> rlp2Result), Is.True);
        Assert.That(rlp2Result.ToArray(), Is.EqualTo(rlp2));
    }

    [Test]
    public void TryCompactPersistedSnapshots_MergesMultipleBaseSnapshots()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager arenaM = new(Path.Combine(testDir, "arenas"), maxArenaSize: 64 * 1024);
            using PersistedSnapshotRepository repo = new(arenaM, testDir, new FlatDbConfig());
            repo.LoadFromCatalog();

            // CompactSize=4, MinCompactSize=2 so compaction triggers at block 4
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2 };
            PersistedSnapshotCompactor compactor = new(repo, arenaM, config, Nethermind.Logging.LimboLogs.Instance);

            StateId s0 = new(0, Keccak.EmptyTreeHash);
            StateId s1 = new(1, Keccak.Compute("1"));
            StateId s2 = new(2, Keccak.Compute("2"));
            StateId s3 = new(3, Keccak.Compute("3"));
            StateId s4 = new(4, Keccak.Compute("4"));

            // Create 4 consecutive base snapshots with different accounts
            SnapshotContent c1 = new();
            c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c1, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c2 = new();
            c2.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s1, s2, c2, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c3 = new();
            c3.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(300).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s2, s3, c3, _pool, ResourcePool.Usage.MainBlockProcessing));

            SnapshotContent c4 = new();
            c4.Accounts[TestItem.AddressD] = Build.An.Account.WithBalance(400).TestObject;
            repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s3, s4, c4, _pool, ResourcePool.Usage.MainBlockProcessing));

            compactor.DoCompactSnapshot(s4);

            // Compaction should have been triggered at block 4 (4 & -4 == 4 >= MinCompactSize=2)
            // Verify compacted snapshot exists and contains all data
            Assert.That(repo.TryLeaseCompactedSnapshotTo(s4, out PersistedSnapshot? compacted), Is.True);
            Assert.That(compacted!.From, Is.EqualTo(s0));
            Assert.That(compacted.TryGetAccount(TestItem.AddressA, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressB, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressC, out _), Is.True);
            Assert.That(compacted.TryGetAccount(TestItem.AddressD, out _), Is.True);
            compacted.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Test]
    public void SelfDestructMerge_DestructedAddressClearsOlderStorage()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        // Older: storage for addrA slot 1
        SnapshotContent older = new();
        older.Storages[(TestItem.AddressA, 1)] = new SlotValue(new byte[] { 0x42 });
        byte[] olderData = PersistedSnapshotBuilder.Build(new Snapshot(s0, s1, older, _pool, ResourcePool.Usage.MainBlockProcessing));

        // Newer: self-destruct=false for addrA (destructed), new storage for addrA slot 2
        SnapshotContent newer = new();
        newer.SelfDestructedStorageAddresses[TestItem.AddressA] = false; // destructed
        newer.Storages[(TestItem.AddressA, 2)] = new SlotValue(new byte[] { 0x99 });
        byte[] newerData = PersistedSnapshotBuilder.Build(new Snapshot(s1, s2, newer, _pool, ResourcePool.Usage.MainBlockProcessing));

        PersistedSnapshotList toMerge2 = new(2);
        toMerge2.Add(CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, olderData));
        toMerge2.Add(CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, newerData));
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge2);
        PersistedSnapshot result = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged);

        // Slot 1 from older should be gone (address was destructed)
        Assert.That(result.TryGetSlot(TestItem.AddressA, 1, out _), Is.False);
        // Slot 2 from newer should be present (added after self-destruct)
        Assert.That(result.TryGetSlot(TestItem.AddressA, 2, out _), Is.True);
        // Self-destruct flag should be false (destructed)
        Assert.That(result.TryGetSelfDestructFlag(TestItem.AddressA), Is.EqualTo(false));
    }

    [Test]
    public void SelfDestructMerge_NewAccountDoesNotOverwriteDestructFlag()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        // Older: self-destruct=false for addrA (destructed)
        SnapshotContent older = new();
        older.SelfDestructedStorageAddresses[TestItem.AddressA] = false; // destructed
        byte[] olderData = PersistedSnapshotBuilder.Build(new Snapshot(s0, s1, older, _pool, ResourcePool.Usage.MainBlockProcessing));

        // Newer: self-destruct=true for addrA (new account, TryAdd should not overwrite)
        SnapshotContent newer = new();
        newer.SelfDestructedStorageAddresses[TestItem.AddressA] = true; // new account
        byte[] newerData = PersistedSnapshotBuilder.Build(new Snapshot(s1, s2, newer, _pool, ResourcePool.Usage.MainBlockProcessing));

        PersistedSnapshotList toMerge3 = new(2);
        toMerge3.Add(CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, olderData));
        toMerge3.Add(CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, newerData));
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge3);
        PersistedSnapshot result = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged);

        // TryAdd semantics: older (false/destructed) should be preserved
        Assert.That(result.TryGetSelfDestructFlag(TestItem.AddressA), Is.EqualTo(false));
    }

    [Test]
    public void SelfDestructMerge_StorageNodesNotAffected()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        Hash256 addrHash = Keccak.Compute(TestItem.AddressA.Bytes);
        TreePath storagePath = new(Keccak.Compute("storage_path"), 4);
        byte[] nodeRlp = [0xC1, 0x80];

        // Older: storage trie node for addrA
        SnapshotContent older = new();
        older.StorageNodes[(addrHash, storagePath)] = new TrieNode(NodeType.Leaf, nodeRlp);
        byte[] olderData = PersistedSnapshotBuilder.Build(new Snapshot(s0, s1, older, _pool, ResourcePool.Usage.MainBlockProcessing));

        // Newer: self-destruct=false for addrA (destructed), but no storage nodes changes
        SnapshotContent newer = new();
        newer.SelfDestructedStorageAddresses[TestItem.AddressA] = false; // destructed
        byte[] newerData = PersistedSnapshotBuilder.Build(new Snapshot(s1, s2, newer, _pool, ResourcePool.Usage.MainBlockProcessing));

        PersistedSnapshot baseSnap4_1 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, olderData);
        PersistedSnapshot baseSnap4_2 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, newerData);
        PersistedSnapshotList toMerge4 = new(2);
        toMerge4.Add(baseSnap4_1);
        toMerge4.Add(baseSnap4_2);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge4);
        PersistedSnapshot result = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged,
            [baseSnap4_1, baseSnap4_2]);

        // Storage trie nodes should still be present (not affected by self-destruct)
        Assert.That(result.TryLoadStorageNodeRlp(addrHash, storagePath, out ReadOnlySpan<byte> loadedRlp), Is.True);
        Assert.That(loadedRlp.ToArray(), Is.EqualTo(nodeRlp));
    }

    [Test]
    public void CompactedSnapshot_HasNodeRefsAndRefIds_InMetadata()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path = new(Keccak.Compute("path"), 4);

        SnapshotContent content1 = new();
        content1.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC0]);
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilder.Build(snap1);

        SnapshotContent content2 = new();
        content2.StateNodes[path] = new TrieNode(NodeType.Leaf, [0xC1, 0x80]);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilder.Build(snap2);

        PersistedSnapshot baseSnap0 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(baseSnap0);
        toMerge.Add(baseSnap1);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge);

        // Read merged bytes directly to verify metadata
        Hsst.Hsst outer = new(merged);
        Assert.That(outer.TryGet(PersistedSnapshot.MetadataTag, out ReadOnlySpan<byte> metaColumn), Is.True);
        Hsst.Hsst meta = new(metaColumn);

        // "noderefs" key with value [0x01]
        Assert.That(meta.TryGet("noderefs"u8, out ReadOnlySpan<byte> nodeRefsValue), Is.True);
        Assert.That(nodeRefsValue.ToArray(), Is.EqualTo(new byte[] { 0x01 }));

        // "ref_ids" key with both base snapshot IDs as LE int32s
        Assert.That(meta.TryGet("ref_ids"u8, out ReadOnlySpan<byte> refIdsValue), Is.True);
        Assert.That(refIdsValue.Length, Is.EqualTo(8)); // 2 IDs × 4 bytes

        // ReadRefIdsFromMetadata should return both IDs
        int[]? refIds = PersistedSnapshot.ReadRefIdsFromMetadata(merged);
        Assert.That(refIds, Is.Not.Null);
        Assert.That(refIds, Does.Contain(0));
        Assert.That(refIds, Does.Contain(1));
    }

    private static IEnumerable<TestCaseData> MergeValidationTestCases()
    {
        // Basic: two snapshots with overlapping accounts
        yield return new TestCaseData((object)new[]
        {
            "010100000000000000000809726F6D5F626C6F636B56E81F171BCC55A6FF8345E692C0F86E5B48E01B996CADC001622FB5E363B4212003617368010000000000000008076F5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE01000101C480648080050005000000030A0101040412125AE4C6F81B66CDB323C65F4E8133690FC09912000000B7700A0102040432000100000000040600010000000004060001000000000406000100000000040600010000000004060000000000350000003D000000450000004D000000550000005D000000000103050607082A070104AE0106",
            "010101000000000000000809726F6D5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368020000000000000008076F5F626C6F636BAD7C5BEF027816A800DA1736444FB58A807EF4C9603B7848673F7E3A68EB14A52003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE01000101C58081C88080060006000000030A0101040413125AE4C6F81B66CDB323C65F4E8133690FC09913000000B7700A0102040433000100000000040600010000000004060001000000000406000100000000040600010000000004060000000000360000003E000000460000004E000000560000005E000000000103050607082A070104AE0106",
        }).SetName("Merge_AccountOverride");

        // Regression: advance-corrupts-minKey bug in NWayStreamingMerge (StateTopNodes).
        // snapshot[0] has paths {A, B}, snapshot[1] has only {B} with different RLP.
        // Old bug: advancing enums[0] past A overwrites minKey to B (via _keyBuffer),
        // falsely advancing enums[1] and losing snapshot[1]'s newer value for B.
        yield return new TestCaseData((object)new[]
        {
            "010100000000000000000809726F6D5F626C6F636B56E81F171BCC55A6FF8345E692C0F86E5B48E01B996CADC001622FB5E363B4212003617368010000000000000008076F5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE01000100000000040600010000000004060001C0800200C080020000000000040000000000041000042A02030402051D0001000000000406000100000000040600010000000004060000000000090000001100000030000000380000004000000048000000000103050607082A070104AE0106",
            "010101000000000000000809726F6D5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368020000000000000008076F5F626C6F636BAD7C5BEF027816A800DA1736444FB58A807EF4C9603B7848673F7E3A68EB14A52003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE01000100000000040600010000000004060001C1800200020000001000040A010304041100010000000004060001000000000406000100000000040600000000000900000011000000240000002C000000340000003C000000000103050607082A070104AE0106",
        }).SetName("Merge_AdvanceOrder_StateTopNodes");

        // Regression: same bug in NWayInnerMerge (StorageNodes inner merge).
        // snapshot[0] has storage trie nodes for addressA at {pathA, pathB},
        // snapshot[1] has only {pathB} with different RLP.
        yield return new TestCaseData((object)new[]
        {
            "010100000000000000000809726F6D5F626C6F636B56E81F171BCC55A6FF8345E692C0F86E5B48E01B996CADC001622FB5E363B4212003617368010000000000000008076F5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE010001000000000406000100000000040600010000000004060001000000000406000101C1800200C18002000000000004000000000000000000000810000000000000082A020804020527120E1FC259CBC5E5ECE94C448B629248606C682700000024F30A010204044700010000000004060000000000090000001100000019000000210000006A00000072000000000103050607082A070104AE0106",
            "010101000000000000000809726F6D5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368020000000000000008076F5F626C6F636BAD7C5BEF027816A800DA1736444FB58A807EF4C9603B7848673F7E3A68EB14A52003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE010001000000000406000100000000040600010000000004060001000000000406000101C2808103000300000010000000000000080A0108040417120E1FC259CBC5E5ECE94C448B629248606C681700000024F30A010204043700010000000004060000000000090000001100000019000000210000005A00000062000000000103050607082A070104AE0106",
        }).SetName("Merge_AdvanceOrder_StorageNodes");

        // Mixed: all data types across two snapshots
        yield return new TestCaseData((object)new[]
        {
            "010100000000000000000809726F6D5F626C6F636B56E81F171BCC55A6FF8345E692C0F86E5B48E01B996CADC001622FB5E363B4212003617368010000000000000008076F5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE01000101000000000000020A010104040D1221B14F1B1C385CD7E0CC2EF7ABE5598C83580101810100010A01020104091C000000000000000000000000000000000000000000000000000000000900000000000A010204043300C4806480800500000000000700000001032A02010433054D125AE4C6F81B66CDB323C65F4E8133690FC09900000000610000009429B7702A0202040D0595010001C080800300030000005E7932E747B94D980A01080404170001C080020002000000A488730A0103040411000100000000040600010000000004060001000000000406000000000098000000B2000000C5000000CD000000D5000000DD000000000103050607082A070104AE0106",
            "010101000000000000000809726F6D5F626C6F636BC89EFDAA54C0F20C7ADF612882DF0950F5A951637E0307CDCB4C672F298B8BC62003617368020000000000000008076F5F626C6F636BAD7C5BEF027816A800DA1736444FB58A807EF4C9603B7848673F7E3A68EB14A52003617368010106657273696F6E000000002B0000003800000061000000670000000000020009000B00100001660666726F6D5F68017404746F5F68017628051C040805AE0100010101810200010A01020104091C000000000000000000000000000000000000000000000000000000000900000000000A010204043300C58081C880800600000000000800000001032A02010433054E125AE4C6F81B66CDB323C65F4E8133690FC0994E000000B7700A010204046E0001C1800200020000005E7932E747B94D980A01080404160001000000000406000100000000040600010000000004060001000000000406000000000071000000890000009100000099000000A1000000A9000000000103050607082A070104AE0106",
        }).SetName("Merge_MixedDataTypes");
    }

    [TestCaseSource(nameof(MergeValidationTestCases))]
    public void MergeSnapshots_ValidatesCorrectly(string[] snapshotHexData)
    {
        PersistedSnapshotList toMerge = new(snapshotHexData.Length);
        StateId prevState = new(0, Keccak.EmptyTreeHash);

        for (int i = 0; i < snapshotHexData.Length; i++)
        {
            StateId nextState = new(i + 1, Keccak.Compute($"{i + 1}"));
            byte[] data = Bytes.FromHexString(snapshotHexData[i]);
            toMerge.Add(CreatePersistedSnapshot(i, prevState, nextState, PersistedSnapshotType.Full, data));
            prevState = nextState;
        }

        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge);
        PersistedSnapshot compacted = CreatePersistedSnapshot(100, toMerge[0].From, toMerge[toMerge.Count - 1].To,
            PersistedSnapshotType.Linked, merged);
        PersistedSnapshotUtils.ValidateCompactedPersistedSnapshot(compacted, toMerge, true);
    }

    [Test]
    public void ReadRefIdsFromMetadata_ReturnsNull_ForBaseSnapshot()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        SnapshotContent content = new();
        content.StateNodes[new TreePath(Keccak.Compute("path"), 4)] = new TrieNode(NodeType.Leaf, [0xC0]);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data = PersistedSnapshotBuilder.Build(snap);

        int[]? refIds = PersistedSnapshot.ReadRefIdsFromMetadata(data);
        Assert.That(refIds, Is.Null);
    }

    [Test]
    public void CompactedSnapshot_NodeRefResolution_WorksWithMetadataFlag()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path1 = new(Keccak.Compute("path1"), 4);
        TreePath path2 = new(Keccak.Compute("path2"), 4);
        byte[] rlp1 = [0xC0];
        byte[] rlp2 = [0xC1, 0x80];

        SnapshotContent content1 = new();
        content1.StateNodes[path1] = new TrieNode(NodeType.Leaf, rlp1);
        Snapshot snap1 = new(s0, s1, content1, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data1 = PersistedSnapshotBuilder.Build(snap1);

        SnapshotContent content2 = new();
        content2.StateNodes[path2] = new TrieNode(NodeType.Leaf, rlp2);
        Snapshot snap2 = new(s1, s2, content2, _pool, ResourcePool.Usage.MainBlockProcessing);
        byte[] data2 = PersistedSnapshotBuilder.Build(snap2);

        PersistedSnapshot baseSnap0 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(baseSnap0);
        toMerge.Add(baseSnap1);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge);

        // With referenced snapshots: NodeRefs resolve to actual RLP
        PersistedSnapshot compactedWithRefs = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged,
            [baseSnap0, baseSnap1]);
        Assert.That(compactedWithRefs.TryLoadStateNodeRlp(path1, out ReadOnlySpan<byte> resolved1), Is.True);
        Assert.That(resolved1.ToArray(), Is.EqualTo(rlp1));
        Assert.That(compactedWithRefs.TryLoadStateNodeRlp(path2, out ReadOnlySpan<byte> resolved2), Is.True);
        Assert.That(resolved2.ToArray(), Is.EqualTo(rlp2));

        // Without referenced snapshots: returns raw NodeRef bytes (8 bytes)
        PersistedSnapshot compactedWithoutRefs = CreatePersistedSnapshot(3, s0, s2, PersistedSnapshotType.Linked, merged);
        Assert.That(compactedWithoutRefs.TryLoadStateNodeRlp(path1, out ReadOnlySpan<byte> raw1), Is.True);
        Assert.That(raw1.Length, Is.EqualTo(NodeRef.Size));
        Assert.That(compactedWithoutRefs.TryLoadStateNodeRlp(path2, out ReadOnlySpan<byte> raw2), Is.True);
        Assert.That(raw2.Length, Is.EqualTo(NodeRef.Size));
    }
}
