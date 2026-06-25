// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Config;
using Nethermind.Init.Modules;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
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
    private ArenaManager _memArena = null!;
    private BlobArenaManager _helperBlobs = null!;

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
        _memArena = TestFixtureHelpers.CreateArenaManager(Path.Combine(_testDir, "mem-arena"));
        _helperBlobs = new BlobArenaManager(Path.Combine(_testDir, "helper-blobs"), 4L * 1024 * 1024);
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _cts.Dispose();
        _helperBlobs.Dispose();
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

    private PersistedSnapshot CreatePersistedSnapshot(StateId from, StateId to, byte[] data) =>
        TestFixtureHelpers.CreatePersistedSnapshot(_memArena, _helperBlobs, from, to, data);

    [Test]
    public void FullStack_PersistAndQuery_AccountsStorageAndTrieNodes()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath statePath = new(Keccak.Compute("state_path"), 4);
        Hash256 storageAddr = Keccak.Compute("storage_address");
        TreePath storagePath = new(Keccak.Compute("storage_path"), 6);
        byte[] stateRlp = [0xC2, 0x80, 0x80];
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

        tier.ConvertToPersistedBase(snap).Dispose();
        Assert.That(repo.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? persisted), Is.True);

        Assert.That(persisted!.TryLoadStateNodeRlp(statePath, out byte[]? stateResult), Is.True);
        Assert.That(stateResult, Is.EqualTo(stateRlp));
        Assert.That(persisted.TryLoadStorageNodeRlp(storageAddr.ValueHash256, storagePath, out byte[]? storageResult), Is.True);
        Assert.That(storageResult, Is.EqualTo(storageRlp));
        persisted.Dispose();
    }

    // 4 KiB — each snapshot's metadata reservation page-rounds to fill the whole arena
    // file, so the file fully-dies on the sole reservation's MarkDead and the punch path
    // is short-circuited. 1 MiB — both snapshots' reservations pack into one arena file,
    // so snap1's dispose finds snap2 still live, MarkDead returns true, and the bare
    // ArenaReservation.CleanUp would (without the PersistOnShutdown-aware fix) punch the
    // dead range in a live preserve-flagged file, zeroing snap1's metadata for session 2.
    [TestCase(4096L, TestName = "Repository_Restart_PreservesAllData_PerSnapshotArenaFiles")]
    [TestCase(1L * 1024 * 1024, TestName = "Repository_Restart_PreservesAllData_SharedArenaAcrossSnapshots")]
    public void Repository_Restart_PreservesAllData(long maxArenaSize)
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));

        // Per-snapshot trie nodes are capped at 568 bytes (MaxTrieNodeRlpBytes), so use
        // many smaller RLPs per snapshot to push the cumulative blob frontier well past
        // 1 OS page (4 KiB). Without enough total blob bytes, a stray
        // BlobArenaManager.TryResetOrphanedFrontier punch over [0, frontier) is a no-op
        // on tmpfs (sub-page punches are dropped), letting the test silently pass with
        // the bug present. 10 × ~500 bytes per snap = ~5 KiB per snap = ~10 KiB shared
        // blob frontier → punch reliably zeros page 0.
        const int nodesPerSnap = 10;
        byte[] body1 = new byte[500]; Array.Fill(body1, (byte)0xAA);
        byte[] body2 = new byte[500]; Array.Fill(body2, (byte)0xBB);
        byte[] rlp1 = Rlp.Encode(body1).Bytes;   // ~503 bytes — under MaxTrieNodeRlpBytes
        byte[] rlp2 = Rlp.Encode(body2).Bytes;
        TreePath[] paths1 = new TreePath[nodesPerSnap];
        TreePath[] paths2 = new TreePath[nodesPerSnap];
        for (int i = 0; i < nodesPerSnap; i++)
        {
            paths1[i] = new TreePath(Keccak.Compute($"path1_{i}"), 4);
            paths2[i] = new TreePath(Keccak.Compute($"path2_{i}"), 4);
        }
        MemDb catalogDb = new();

        using (FlatTestContainer tier1 = new(arenaFileSizeBytes: maxArenaSize, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo = tier1.Repository;

            tier1.ConvertToPersistedBase(CreateSnapshot(s0, s1, c =>
            {
                foreach (TreePath p in paths1) c.StateNodes[p] = new TrieNode(NodeType.Leaf, rlp1);
                c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(100).TestObject;
            })).Dispose();

            tier1.ConvertToPersistedBase(CreateSnapshot(s1, s2, c =>
            {
                foreach (TreePath p in paths2) c.StateNodes[p] = new TrieNode(NodeType.Leaf, rlp2);
                c.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(200).TestObject;
            })).Dispose();
        }

        // Repository.Dispose flags every loaded snapshot's arena reservation AND every
        // referenced blob file with PersistOnShutdown before tearing down the managers,
        // so both file kinds must survive on disk for the catalog to re-bind in session 2.
        // Split assertions so a missing flag on one side fingerprints which side regressed.
        string arenaDir = Path.Combine(_testDir, "persisted_snapshot", "arena");
        string blobDir = Path.Combine(_testDir, "persisted_snapshot", "blob");
        // PersistedBase metadata lives in the small-arena pool (sub-CompactSize tier).
        Assert.That(Directory.GetFiles(arenaDir, "small_arena_*.bin"), Is.Not.Empty,
            "arena files were deleted on Dispose — PersistOnShutdown flag did not propagate to ArenaFile");
        string[] blobFiles = Directory.GetFiles(blobDir, "blob_*.bin");
        Assert.That(blobFiles, Is.Not.Empty,
            "blob files were deleted on Dispose — PersistOnShutdown flag did not propagate to BlobArenaFile");
        // No pre-extension: blob length tracks the actual data extent. If we ever drift
        // back into pre-extending or punch-zero-on-shutdown, a preserve-flagged file ends
        // up with length 0 (truncated) or length MaxSize (pre-extended sparse) — neither
        // matches the snapshot's written extent. Either symptom would be caught here.
        foreach (string blobFile in blobFiles)
        {
            long len = new FileInfo(blobFile).Length;
            Assert.That(len, Is.GreaterThan(0),
                $"{blobFile} truncated on Dispose — preserve flag did not protect a referenced blob");
            Assert.That(len, Is.LessThanOrEqualTo(1024 * 1024),
                $"{blobFile} length {len} > 1 MiB cap — pre-extension regressed");
        }

        using (FlatTestContainer tier2 = new(arenaFileSizeBytes: 4096, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo = tier2.Repository;
            Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(2));

            // s0→s1 carries paths1[] + AddressA; s1→s2 carries paths2[] + AddressB. Every
            // state node round-trips intact — a stray BlobArenaManager.TryResetOrphanedFrontier
            // punch during the session-1 dispose would zero at least the first 4 KiB of the
            // blob, so the early-index nodes' RLPs would either not decode or read as zeros.
            // The cross-snapshot misses verify the snapshot boundary survives reload (i.e.
            // AddressB does NOT bleed into snap1's view, and vice versa).
            Assert.That(repo.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? snap1), Is.True);
            foreach (TreePath p in paths1)
            {
                Assert.That(snap1!.TryLoadStateNodeRlp(p, out byte[]? r), Is.True, $"snap1 missing {p}");
                Assert.That(r, Is.EqualTo(rlp1), $"snap1 state node at {p} read back corrupted");
            }
            Assert.That(snap1!.TryGetAccount(TestItem.AddressA, out Account? a1), Is.True);
            Assert.That(snap1.TryGetAccount(TestItem.AddressB, out Account? snap1MissB), Is.False);
            snap1.Dispose();

            Assert.That(repo.TryLeasePersistedState(s2, SnapshotTier.PersistedBase, out PersistedSnapshot? snap2), Is.True);
            foreach (TreePath p in paths2)
            {
                Assert.That(snap2!.TryLoadStateNodeRlp(p, out byte[]? r), Is.True, $"snap2 missing {p}");
                Assert.That(r, Is.EqualTo(rlp2), $"snap2 state node at {p} read back corrupted");
            }
            Assert.That(snap2!.TryGetAccount(TestItem.AddressB, out Account? a2), Is.True);
            Assert.That(snap2.TryGetAccount(TestItem.AddressA, out Account? snap2MissA), Is.False);
            snap2.Dispose();

            Assert.That(a1!.Balance, Is.EqualTo((UInt256)100));
            Assert.That(a2!.Balance, Is.EqualTo((UInt256)200));
            Assert.That(snap1MissB, Is.Null);
            Assert.That(snap2MissA, Is.Null);
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
            c.StateNodes[statePath] = new TrieNode(NodeType.Leaf, [0xC2, 0x80, 0x80]); // Override
        });

        byte[] data1 = PersistedSnapshotBuilderTestExtensions.Build(snap1, _helperBlobs);
        byte[] data2 = PersistedSnapshotBuilderTestExtensions.Build(snap2, _helperBlobs);
        PersistedSnapshot baseSnap1 = CreatePersistedSnapshot(s0, s1, data1);
        PersistedSnapshot baseSnap2 = CreatePersistedSnapshot(s1, s2, data2);
        PersistedSnapshotList toMerge = new(2) { baseSnap1, baseSnap2 };
        byte[] merged = PersistedSnapshotBuilderTestExtensions.NWayMergeSnapshots(toMerge);

        PersistedSnapshot mergedSnap = CreatePersistedSnapshot(s0, s2, merged);

        // State node should have newer value
        Assert.That(mergedSnap.TryLoadStateNodeRlp(statePath, out byte[]? stateRlpResult), Is.True);
        Assert.That(stateRlpResult, Is.EqualTo(new byte[] { 0xC2, 0x80, 0x80 }));

        // Storage node from older should be preserved
        Assert.That(mergedSnap.TryLoadStorageNodeRlp(storageAddr.ValueHash256, storagePath, out byte[]? storageRlpResult), Is.True);
        Assert.That(storageRlpResult, Is.EqualTo(new byte[] { 0xC1, 0x80 }));

        Assert.That(mergedSnap.TryGetAccount(TestItem.AddressA, out _), Is.True);
        Assert.That(mergedSnap.TryGetAccount(TestItem.AddressB, out _), Is.True);
    }

    [TestCase(10)]
    [TestCase(100)]
    public void ManySnapshots_PersistAndQuery(int snapshotCount)
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 64 * 1024);
        SnapshotRepository repo = tier.Repository;

        StateId prev = new(0, Keccak.EmptyTreeHash);
        for (int i = 1; i <= snapshotCount; i++)
        {
            StateId current = new((ulong)i, Keccak.Compute(i.ToString()));
            tier.ConvertToPersistedBase(CreateSnapshot(prev, current, c =>
                c.Accounts[new Address(Keccak.Compute(i.ToString()))] =
                    Build.An.Account.WithBalance((UInt256)i).TestObject)).Dispose();
            prev = current;
        }

        Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(snapshotCount));
    }


    [Test]
    public async Task FlatDbManager_EndToEnd_WithPersistedSnapshots()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        TreePath path = new(Keccak.Compute("e2e_path"), 4);
        byte[] nodeRlp = [0xC1, 0x80];

        tier.ConvertToPersistedBase(CreateSnapshot(s0, s1, c =>
            c.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp))).Dispose();

        // Set up persistence reader at s0 — persisted snapshot fills gap s0→s1
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(s0);
        persistenceManager.LeaseReader().Returns(reader);
        persistenceManager.GetCurrentPersistedStateId().Returns(s0);

        await using FlatDbManager manager = new(
            Substitute.For<IResourcePool>(),
            _processExitSource,
            Substitute.For<ITrieNodeCache>(),
            Substitute.For<ISnapshotCompactor>(),
            repo,
            persistenceManager,
            Substitute.For<IPersistedSnapshotLoader>(),
            _config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false);

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
        MemDb catalogDb = new();

        using (FlatTestContainer tier1 = new(arenaFileSizeBytes: 4096, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo = tier1.Repository;
            tier1.ConvertToPersistedBase(CreateSnapshot(s0, s1, c =>
                c.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject)).Dispose();
            tier1.ConvertToPersistedBase(CreateSnapshot(s1, s2, c =>
                c.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(2).TestObject)).Dispose();
            tier1.ConvertToPersistedBase(CreateSnapshot(s2, s5, c =>
                c.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(5).TestObject)).Dispose();
        }

        using (FlatTestContainer tier2 = new(arenaFileSizeBytes: 4096, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo = tier2.Repository;
            Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(3));

            repo.RemovePersistedStatesUntil(3); // s1 and s2 removed
            Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(1));
        }

        using (FlatTestContainer tier3 = new(arenaFileSizeBytes: 4096, baseDbPath: _testDir, catalogDb: catalogDb))
        {
            SnapshotRepository repo = tier3.Repository;
            Assert.That(repo.PersistedSnapshotCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void EmptySnapshot_PersistsAndLoads()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        Snapshot empty = CreateSnapshot(s0, s1, _ => { });
        tier.ConvertToPersistedBase(empty).Dispose();

        Assert.That(repo.TryLeasePersistedState(s1, SnapshotTier.PersistedBase, out PersistedSnapshot? persisted), Is.True);
        Assert.That(persisted!.TryGetAccount(TestItem.AddressA, out _), Is.False);
        Assert.That(persisted.TryLoadStateNodeRlp(new TreePath(Keccak.Compute("any"), 4), out _), Is.False);
        persisted.Dispose();
    }

    [Test]
    public void Configuration_DefaultValues()
    {
        FlatDbConfig config = new();
        Assert.That(config.EnableLongFinality, Is.False);
        Assert.That(config.MaxReorgDepth, Is.EqualTo(256));
        Assert.That(config.LongFinalityMaxReorgDepth, Is.EqualTo(90000));
        Assert.That(config.ArenaFileSizeBytes, Is.EqualTo(1L * 1024 * 1024 * 1024));
    }

    [Test]
    public void DisabledLongFinality_WiresInertPersistedTier()
    {
        FlatDbConfig config = new() { EnableLongFinality = false };
        using IContainer container = new ContainerBuilder()
            .AddModule(new FlatWorldStateModule(config))
            .AddSingleton<IFlatDbConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .Build();

        Assert.That(container.Resolve<ISnapshotCatalog>(), Is.SameAs(NullSnapshotCatalog.Instance));
        Assert.That(container.Resolve<IPersistedSnapshotLoader>(), Is.SameAs(NullPersistedSnapshotLoader.Instance));
        Assert.That(container.Resolve<IPersistedSnapshotCompactor>(), Is.SameAs(NullPersistedSnapshotCompactor.Instance));

        // The Null loader/catalog keep the tier inert: loading is a no-op and nothing is ever recorded.
        container.Resolve<IPersistedSnapshotLoader>().Load();
        Assert.That(container.Resolve<ISnapshotCatalog>().Load(), Is.Empty);
    }
}
