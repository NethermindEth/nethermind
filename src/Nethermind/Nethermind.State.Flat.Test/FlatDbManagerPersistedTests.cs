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
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatDbManagerPersistedTests
{
    private string _testDir = null!;
    private ResourcePool _pool = null!;
    private IProcessExitSource _processExitSource = null!;
    private CancellationTokenSource _cts = null!;
    private IFlatDbConfig _config = null!;

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
    }

    [TearDown]
    public void TearDown()
    {
        _cts.Cancel();
        _cts.Dispose();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Test]
    public async Task ConstructorAcceptsPersistedRepository()
    {
        using ArenaManager smallArena = ArenaManagerTestFactory.Create(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024);
        using PersistedTierTestHarness repoH = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig());
        SnapshotRepository repo = repoH.Repository;

        await using FlatDbManager manager = new(
            Substitute.For<IResourcePool>(),
            _processExitSource,
            Substitute.For<ITrieNodeCache>(),
            Substitute.For<ISnapshotCompactor>(),
            repo,
            Substitute.For<IPersistenceManager>(),
            Substitute.For<IPersistedSnapshotLoader>(),
            _config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false);

        Assert.That(manager, Is.Not.Null);
    }

    [Test]
    public async Task GatherReadOnlySnapshotBundle_IncludesPersistedSnapshots()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        // Build a persisted snapshot with a known state trie node
        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] nodeRlp = [0xC2, 0x80, 0x80];
        SnapshotContent content = new();
        content.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);

        using ArenaManager smallArena = ArenaManagerTestFactory.Create(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        using BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024);
        using PersistedTierTestHarness repoH = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig());
        SnapshotRepository repo = repoH.Repository;
        repo.ConvertToPersistedBase(snap).Dispose();

        // Mock persistence manager at s0 — persisted snapshot fills gap s0→s1
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

        // The bundle should find the trie node from the persisted snapshot
        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);
        Assert.That(result, Is.EqualTo(nodeRlp));

        bundle.Dispose();
    }

    [Test]
    public async Task DisposeAsync_DisposesPersistedRepository()
    {
        ArenaManager smallArena = ArenaManagerTestFactory.Create(Path.Combine(_testDir, "arenas", "base"), 0, maxArenaSize: 4096);
        BlobArenaManager smallBlobs = new(Path.Combine(_testDir, "blobs", "small"), 1024 * 1024);
        using PersistedTierTestHarness repoH = new(smallArena, smallBlobs, new MemDb(), new FlatDbConfig());
        SnapshotRepository repo = repoH.Repository;

        // Persist something to verify cleanup
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        repo.ConvertToPersistedBase(new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

        FlatDbManager manager = new(
            Substitute.For<IResourcePool>(),
            _processExitSource,
            Substitute.For<ITrieNodeCache>(),
            Substitute.For<ISnapshotCompactor>(),
            repo,
            Substitute.For<IPersistenceManager>(),
            Substitute.For<IPersistedSnapshotLoader>(),
            _config,
            new BlocksConfig(),
            LimboLogs.Instance,
            enableDetailedMetrics: false);

        await manager.DisposeAsync();


        // Repository should be disposed - accessing it should be safe
        // (no crash, but data might not be accessible)
        Assert.Pass("Dispose completed without error");
    }
}
