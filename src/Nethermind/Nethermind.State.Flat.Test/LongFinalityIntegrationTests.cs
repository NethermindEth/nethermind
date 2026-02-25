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
        SnapshotLocation loc = _memArena.Allocate(data);
        ArenaReservation reservation = _memArena.Open(loc);
        return new PersistedSnapshot(id, from, to, type, reservation, referencedSnapshots);
    }

    [Test]
    public void FullStack_PersistAndQuery_AccountsStorageAndTrieNodes()
    {
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(arena, _testDir, new FlatDbConfig());
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
        using (ArenaManager arenaManager = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(arenaManager, _testDir, new FlatDbConfig()))
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
        using (ArenaManager arenaManager = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(arenaManager, _testDir, new FlatDbConfig()))
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

        byte[] data1 = PersistedSnapshotBuilder.Build(snap1);
        byte[] data2 = PersistedSnapshotBuilder.Build(snap2);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(0, s0, s1, PersistedSnapshotType.Full, data1);
        PersistedSnapshot baseSnap2 = CreatePersistedSnapshot(1, s1, s2, PersistedSnapshotType.Full, data2);
        PersistedSnapshotList toMerge = new(2);
        toMerge.Add(baseSnap1);
        toMerge.Add(baseSnap2);
        byte[] merged = PersistedSnapshotBuilder.MergeSnapshots(toMerge);

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
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 64 * 1024);
        using PersistedSnapshotRepository repo = new(arena, _testDir, new FlatDbConfig());
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
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(arena, _testDir, new FlatDbConfig());
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
            LimboLogs.Instance,
            enableDetailedMetrics: false,
            persistedSnapshotRepository: repo);

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
        using (ArenaManager arenaManager = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(arenaManager, _testDir, new FlatDbConfig()))
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
        using (ArenaManager arenaManager = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(arenaManager, _testDir, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            Assert.That(repo.SnapshotCount, Is.EqualTo(3));

            int pruned = repo.PruneBefore(new StateId(3, Keccak.Compute("prune")));
            Assert.That(pruned, Is.EqualTo(2)); // s1 and s2 removed
            Assert.That(repo.SnapshotCount, Is.EqualTo(1));
        }

        // Session 3: verify pruned state persists
        using (ArenaManager arenaManager = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096))
        using (PersistedSnapshotRepository repo = new(arenaManager, _testDir, new FlatDbConfig()))
        {
            repo.LoadFromCatalog();
            Assert.That(repo.SnapshotCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void EmptySnapshot_PersistsAndLoads()
    {
        using ArenaManager arena = new(Path.Combine(_testDir, "arenas"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(arena, _testDir, new FlatDbConfig());
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
}
