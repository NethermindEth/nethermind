// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.BlockRangeTrieForest;
using ForestImpl = Nethermind.State.Flat.BlockRangeTrieForest.BlockRangeTrieForest;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class LongFinalityIntegrationTests
{
    private string _testDir = null!;
    private ResourcePool _pool = null!;
    private IProcessExitSource _processExitSource = null!;
    private CancellationTokenSource _cts = null!;
    private IFlatDbConfig _config = null!;
    private MemoryArenaManager _memArena = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _pool = new ResourcePool(new FlatDbConfig());
        _cts = new CancellationTokenSource();
        _processExitSource = Substitute.For<IProcessExitSource>();
        _processExitSource.Token.Returns(_cts.Token);
        _config = new FlatDbConfig { CompactSize = 16, MaxInFlightCompactJob = 4, InlineCompaction = true };
        _memArena = new MemoryArenaManager();
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _cts.Dispose();
        _memArena.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private Snapshot CreateSnapshot(StateId from, StateId to, Action<SnapshotContent> configure)
    {
        SnapshotContent content = new();
        configure(content);
        return new Snapshot(from, to, content, _pool, ResourcePool.Usage.MainBlockProcessing);
    }

    private PersistedSnapshot CreatePersistedSnapshot(int id, StateId from, StateId to, PersistedSnapshotType type, byte[] data,
        PersistedSnapshot[]? referencedSnapshots = null)
    {
        using ArenaWriter writer = _memArena.CreateWriter(data.Length);
        Span<byte> span = writer.GetWriter().GetSpan(data.Length);
        data.CopyTo(span);
        writer.GetWriter().Advance(data.Length);
        (_, ArenaReservation reservation) = writer.Complete();
        return new PersistedSnapshot(id, from, to, type, reservation, referencedSnapshots);
    }

    [Test]
    public void FullStack_PersistAndQuery_AccountsStorageAndTrieNodes()
    {
        using ArenaManager baseArena = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096);
        using ArenaManager compactedArena = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(baseArena, compactedArena, _testDir, new FlatDbConfig());
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath statePath = new(Keccak.Compute("state_path"), 4);
        Hash256 storageAddr = Keccak.Compute("storage_address");
        TreePath storagePath = new(Keccak.Compute("storage_path"), 6);
        byte[] stateRlp = [0xC0, 0x80, 0x80];
        byte[] storageRlp = [0xC1, 0x80];

        Snapshot snap = CreateSnapshot(s0, s1, c =>
        {
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(500).TestObject;
            byte[] slotVal = new byte[32]; slotVal[31] = 0xFF;
            c.Storages[(TestItem.AddressA, (UInt256)42)] = new SlotValue(slotVal);
            c.SelfDestructedStorageAddresses[TestItem.AddressB] = false;
            c.StateNodes[statePath] = new TrieNode(NodeType.Leaf, stateRlp);
            c.StorageNodes[(storageAddr, storagePath)] = new TrieNode(NodeType.Branch, storageRlp);
        });

        repo.ConvertSnapshotToPersistedSnapshot(snap);
        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? persisted), Is.True);

        // Query all types through the individual persisted snapshot
        Assert.That(persisted!.TryLoadStateNodeRlp(statePath, out ReadOnlySpan<byte> stateResult), Is.True);
        Assert.That(stateResult.ToArray(), Is.EqualTo(stateRlp));
        Assert.That(persisted.TryLoadStorageNodeRlp(storageAddr, storagePath, out ReadOnlySpan<byte> storageResult), Is.True);
        Assert.That(storageResult.ToArray(), Is.EqualTo(storageRlp));
        persisted.Dispose();
    }

    [Test]
    public void Repository_Restart_PreservesAllData()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath path1 = new(Keccak.Compute("path1"), 4);
        TreePath path2 = new(Keccak.Compute("path2"), 4);
        byte[] rlp1 = [0xC0];
        byte[] rlp2 = [0xC1, 0x80];

        // Session 1: persist two snapshots
        using (ArenaManager baseArena1 = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096))
        using (ArenaManager compactedArena1 = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(baseArena1, compactedArena1, _testDir, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();

            repo.ConvertSnapshotToPersistedSnapshot(CreateSnapshot(s0, s1, c =>
            {
                c.StateNodes[path1] = new TrieNode(NodeType.Leaf, rlp1);
                c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            }));

            repo.ConvertSnapshotToPersistedSnapshot(CreateSnapshot(s1, s2, c =>
            {
                c.StateNodes[path2] = new TrieNode(NodeType.Leaf, rlp2);
                c.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            }));
        }

        // Session 2: reload and verify
        using (ArenaManager baseArena2 = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096))
        using (ArenaManager compactedArena2 = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(baseArena2, compactedArena2, _testDir, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            Assert.That(repo.SnapshotCount, Is.EqualTo(2));

            // path1 is in s0→s1, path2 is in s1→s2 — query each snapshot directly
            Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? snap1), Is.True);
            Assert.That(snap1!.TryLoadStateNodeRlp(path1, out ReadOnlySpan<byte> r1Span), Is.True);
            byte[] r1 = r1Span.ToArray();
            snap1.Dispose();

            Assert.That(repo.TryLeaseSnapshotTo(s2, out PersistedSnapshot? snap2), Is.True);
            Assert.That(snap2!.TryLoadStateNodeRlp(path2, out ReadOnlySpan<byte> r2Span), Is.True);
            byte[] r2 = r2Span.ToArray();
            snap2.Dispose();

            Assert.That(r1, Is.EqualTo(rlp1));
            Assert.That(r2, Is.EqualTo(rlp2));
        }
    }


    [Test]
    public void MergeSnapshotData_AllEntryTypes()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        TreePath statePath = new(Keccak.Compute("state"), 4);
        Hash256 storageAddr = Keccak.Compute("addr");
        TreePath storagePath = new(Keccak.Compute("stor_path"), 6);

        Snapshot snap1 = CreateSnapshot(s0, s1, c =>
        {
            c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            c.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC0]);
            c.StorageNodes[(storageAddr, storagePath)] = new TrieNode(NodeType.Branch, [0xC1, 0x80]);
        });

        Snapshot snap2 = CreateSnapshot(s1, s2, c =>
        {
            c.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            c.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC1, 0x80, 0x80]); // Override
        });

        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap2 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(baseSnap1);
        toMerge.Add(baseSnap2);
        byte[] merged = PersistedSnapshotBuilderTestExtensions.MergeSnapshots(toMerge);

        PersistedSnapshot mergedSnap = CreatePersistedSnapshot(2, s0, s2, PersistedSnapshotType.Linked, merged,
            [baseSnap1, baseSnap2]);

        // State node should have newer value
        Assert.That(mergedSnap.TryLoadStateNodeRlp(statePath, out ReadOnlySpan<byte> stateRlpResult), Is.True);
        Assert.That(stateRlpResult.ToArray(), Is.EqualTo(new byte[] { 0xC1, 0x80, 0x80 }));

        // Storage node from older should be preserved
        Assert.That(mergedSnap.TryLoadStorageNodeRlp(storageAddr, storagePath, out ReadOnlySpan<byte> storageRlpResult), Is.True);
        Assert.That(storageRlpResult.ToArray(), Is.EqualTo(new byte[] { 0xC1, 0x80 }));

        // Both accounts should be present
        Assert.That(mergedSnap.TryGetAccount(TestItem.AddressA, out _), Is.True);
        Assert.That(mergedSnap.TryGetAccount(TestItem.AddressB, out _), Is.True);
    }

    [TestCase(10)]
    [TestCase(100)]
    [TestCase(500)]
    public void ManySnapshots_PersistAndQuery(int snapshotCount)
    {
        using ArenaManager baseArena = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 64 * 1024);
        using ArenaManager compactedArena = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 64 * 1024);
        using PersistedSnapshotRepository repo = new(baseArena, compactedArena, _testDir, new FlatDbConfig());
        repo.LoadFromCatalog();

        StateId prev = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= snapshotCount; i++)
        {
            StateId current = new(i, Keccak.Compute(i.ToString()));
            repo.ConvertSnapshotToPersistedSnapshot(CreateSnapshot(prev, current, c =>
                c.Accounts[new Address(Keccak.Compute(i.ToString()))] =
                    Build.An.Account.WithBalance((UInt256)i).TestObject));
            prev = current;
        }

        Assert.That(repo.SnapshotCount, Is.EqualTo(snapshotCount));
    }


    [Test]
    public async Task FlatDbManager_EndToEnd_WithPersistedSnapshots()
    {
        using ArenaManager baseArena = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096);
        using ArenaManager compactedArena = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(baseArena, compactedArena, _testDir, new FlatDbConfig());
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        TreePath path = new(Keccak.Compute("e2e_path"), 4);
        byte[] nodeRlp = [0xC0, 0x80];

        // Persist a snapshot with a state node
        repo.ConvertSnapshotToPersistedSnapshot(CreateSnapshot(s0, s1, c =>
            c.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp)));

        // Set up persistence reader at s0 — persisted snapshot fills gap s0→s1
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(s0);
        persistenceManager.LeaseReader().Returns(reader);
        persistenceManager.GetCurrentPersistedStateId().Returns(s0);

        SnapshotRepository snapshotRepo = new(repo, LimboLogs.Instance);

        await using FlatDbManager manager = new(
            Substitute.For<IResourcePool>(),
            _processExitSource,
            Substitute.For<ITrieNodeCache>(),
            Substitute.For<ISnapshotCompactor>(),
            snapshotRepo,
            persistenceManager,
            _config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false,
            persistedSnapshotRepository: repo,
            blockRangeTrieForest: NullBlockRangeTrieForest.Instance);

        ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(s1);

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);
        Assert.That(result, Is.EqualTo(nodeRlp));

        bundle.Dispose();
    }

    [Test]
    public void Prune_AfterRestart_Works()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId s5 = new(5, Keccak.Compute("5"));

        // Session 1: persist snapshots
        using (ArenaManager baseArena1 = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096))
        using (ArenaManager compactedArena1 = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(baseArena1, compactedArena1, _testDir, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            repo.ConvertSnapshotToPersistedSnapshot(CreateSnapshot(s0, s1, c =>
                c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject));
            repo.ConvertSnapshotToPersistedSnapshot(CreateSnapshot(s1, s2, c =>
                c.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(2).TestObject));
            repo.ConvertSnapshotToPersistedSnapshot(CreateSnapshot(s2, s5, c =>
                c.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(5).TestObject));
        }

        // Session 2: reload and prune
        using (ArenaManager baseArena2 = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096))
        using (ArenaManager compactedArena2 = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(baseArena2, compactedArena2, _testDir, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            Assert.That(repo.SnapshotCount, Is.EqualTo(3));

            int pruned = repo.PruneBefore(new StateId(3, Keccak.Compute("prune")));
            Assert.That(pruned, Is.EqualTo(2)); // s1 and s2 removed
            Assert.That(repo.SnapshotCount, Is.EqualTo(1));
        }

        // Session 3: verify pruned state persists
        using (ArenaManager baseArena3 = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096))
        using (ArenaManager compactedArena3 = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(baseArena3, compactedArena3, _testDir, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            Assert.That(repo.SnapshotCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void EmptySnapshot_PersistsAndLoads()
    {
        using ArenaManager baseArena = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096);
        using ArenaManager compactedArena = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(baseArena, compactedArena, _testDir, new FlatDbConfig());
        repo.LoadFromCatalog();

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        // Persist an empty snapshot
        Snapshot empty = CreateSnapshot(s0, s1, _ => { });
        repo.ConvertSnapshotToPersistedSnapshot(empty);

        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? persisted), Is.True);
        Assert.That(persisted!.TryGetAccount(TestItem.AddressA, out _), Is.False);
        Assert.That(persisted.TryLoadStateNodeRlp(new TreePath(Keccak.Compute("any"), 4), out _), Is.False);
        persisted.Dispose();
    }

    [Test]
    public void Configuration_DefaultValues()
    {
        FlatDbConfig config = new();
        Assert.That(config.EnableLongFinality, Is.False);
        Assert.That(config.LongFinalityReorgDepth, Is.EqualTo(90000));
        Assert.That(config.PersistedSnapshotPath, Is.EqualTo("snapshots"));
        Assert.That(config.ArenaFileSizeBytes, Is.EqualTo(4L * 1024 * 1024 * 1024));
    }

    /// <summary>
    /// Verifies the full forest lifecycle across two forest ranges:
    /// 1. Compaction populates forest range 0 and range 1 with trie nodes.
    /// 2. The deletion driver drains range 0 (below range 1).
    /// 3. Range 1 entries remain intact after deletion.
    /// </summary>
    [Test]
    public void Forest_CompactionPopulates_DeletionDrains_AcrossRanges()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"nethermind_forest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            using ArenaManager baseArena = new(Path.Combine(testDir, "arenas", "base"), maxArenaSize: 64 * 1024);
            using ArenaManager compactedArena = new(Path.Combine(testDir, "arenas", "compacted"), maxArenaSize: 64 * 1024);
            using PersistedSnapshotRepository repo = new(baseArena, compactedArena, testDir, new FlatDbConfig());
            repo.LoadFromCatalog();

            // CompactSize=4, BlockRangePerForest=4 → range 0 = blocks 0-3, range 1 = blocks 4-7.
            // At block 8: compactSize=8 > CompactSize=4 → forest-spilled compaction.
            IFlatDbConfig config = new FlatDbConfig { CompactSize = 4, MinCompactSize = 2, BlockRangePerForest = 4 };
            using SnapshotableMemDb forestDb = new();
            ForestImpl forest = new(forestDb);
            PersistedSnapshotCompactor compactor = new(repo, compactedArena, config, forest, LimboLogs.Instance);

            TreePath pathRange0 = new(Keccak.Compute("nodeRange0"), 4);
            TreePath pathRange1 = new(Keccak.Compute("nodeRange1"), 4);
            byte[] rlpRange0 = [0xC0, 0x80];
            byte[] rlpRange1 = [0xC1, 0x80];

            // Create 8 snapshots: first has a node in range 0 (block 1), fifth has a node in range 1 (block 5).
            StateId prev = new(0, Keccak.EmptyTreeHash);
            for (int i = 1; i <= 8; i++)
            {
                StateId next = new(i, Keccak.Compute(i.ToString()));
                SnapshotContent content = new();
                if (i == 1) content.StateNodes[pathRange0] = new TrieNode(NodeType.Leaf, rlpRange0);
                if (i == 5) content.StateNodes[pathRange1] = new TrieNode(NodeType.Leaf, rlpRange1);
                content.Accounts[TestItem.Addresses[i - 1]] = Build.An.Account.WithBalance((UInt256)i * 100).TestObject;
                repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(prev, next, content, _pool, ResourcePool.Usage.MainBlockProcessing));
                prev = next;
            }

            StateId s8 = new(8, Keccak.Compute("8"));
            compactor.DoCompactSnapshot(s8);

            // Both ranges should be in the forest
            Assert.That(forest.TryGetState(0, pathRange0, Keccak.Compute(rlpRange0)), Is.EqualTo(rlpRange0));
            Assert.That(forest.TryGetState(1, pathRange1, Keccak.Compute(rlpRange1)), Is.EqualTo(rlpRange1));

            // The compacted snapshot should be forest-spilled and still have all accounts
            Assert.That(repo.TryLeaseCompactedSnapshotTo(s8, out PersistedSnapshot? compacted), Is.True);
            Assert.That(compacted!.IsForestSpilled, Is.True);
            for (int i = 0; i < 8; i++)
                Assert.That(compacted.TryGetAccount(TestItem.Addresses[i], out ReadOnlySpan<byte> _), Is.True);
            compacted.Dispose();

            // Deletion driver: drain range 0 (belowBlockRange=1 means delete range 0 entries)
            using MemColumnsDb<FlatDbColumns> metaDb = new();
            BlockRangeForestDeletionDriver driver = new(forest, metaDb);
            driver.DeleteBatch(belowBlockRange: 1, count: 100);

            // Range 0 drained, range 1 intact
            Assert.That(forest.TryGetState(0, pathRange0, Keccak.Compute(rlpRange0)), Is.Null);
            Assert.That(forest.TryGetState(1, pathRange1, Keccak.Compute(rlpRange1)), Is.EqualTo(rlpRange1));
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }
}
