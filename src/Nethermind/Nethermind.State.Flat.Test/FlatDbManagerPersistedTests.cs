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
using Nethermind.Monitoring.Config;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatDbManagerPersistedTests
{
    private static readonly TimeSpan DisposeWaitLimit = TimeSpan.FromSeconds(10);

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
    public async Task GatherReadOnlySnapshotBundle_IncludesPersistedSnapshots()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        TreePath path = new(Keccak.Compute("path"), 4);
        byte[] nodeRlp = [0xC2, 0x80, 0x80];
        SnapshotContent content = new();
        content.StateNodes[path] = new TrieNode(NodeType.Leaf, nodeRlp);
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);

        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;
        tier.ConvertToPersistedBase(snap).Dispose();

        // Persisted snapshot covers s0→s1; mock reader anchored at s0 so the manager sees it as the persisted base.
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
            enableDetailedMetrics: false,
            tier.Resolve<SnapshotRetention>());

        ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(s1);

        byte[]? result = bundle.TryLoadStateRlp(path, Keccak.Compute("hash"), ReadFlags.None);
        Assert.That(result, Is.EqualTo(nodeRlp));

        bundle.Dispose();
    }

    [Test]
    public async Task GatherReadOnlySnapshotBundle_RetainsHeadUntilCacheAndReadersRelease([Values] bool persisted)
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("retained"));
        using FlatTestContainer tier = CreateRetentionContainer(s0);
        await using FlatDbManager manager = (FlatDbManager)tier.Resolve<IFlatDbManager>();
        SnapshotRetention retention = tier.Resolve<SnapshotRetention>();
        Snapshot snapshot = tier.ResourcePool.CreateSnapshot(s0, s1, ResourcePool.Usage.MainBlockProcessing);
        if (persisted)
        {
            using (snapshot) tier.ConvertToPersistedBase(snapshot).Dispose();
        }
        else
        {
            Assert.That(tier.Repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True);
            tier.Repository.AddStateId(s1);
        }

        using (ReadOnlySnapshotBundle first = manager.GatherReadOnlySnapshotBundle(s1))
        {
            using (ReadOnlySnapshotBundle second = manager.GatherReadOnlySnapshotBundle(s1))
                Assert.That(second, Is.SameAs(first));
            AssertRetained(retention, s1, true);

            manager.FlushCache(CancellationToken.None);
            AssertRetained(retention, s1, true);
        }
        AssertRetained(retention, s1, false);
    }

    [Test]
    public async Task RemoveOrphanedStates_ActiveCompactedBundle_PreservesUnleasedBaseAncestry()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId canonicalFirst = new(1, Keccak.Compute("canonical1"));
        StateId canonicalSecond = new(2, Keccak.Compute("canonical2"));
        StateId canonical = new(3, Keccak.Compute("canonical"));
        StateId first = new(1, Keccak.Compute("orphan1"));
        StateId second = new(2, Keccak.Compute("orphan2"));
        StateId head = new(3, Keccak.Compute("orphan3"));
        StateId unused = new(2, Keccak.Compute("unused"));
        using FlatTestContainer tier = CreateRetentionContainer(s0);
        await using FlatDbManager manager = (FlatDbManager)tier.Resolve<IFlatDbManager>();
        SnapshotRepository repository = tier.Repository;
        AddEmptySnapshot(tier, s0, canonicalFirst);
        AddEmptySnapshot(tier, canonicalFirst, canonicalSecond);
        AddEmptySnapshot(tier, canonicalSecond, canonical);
        AddEmptySnapshot(tier, s0, first);
        AddEmptySnapshot(tier, first, second);
        AddEmptySnapshot(tier, second, head);
        AddEmptySnapshot(tier, s0, head, compacted: true);
        AddEmptySnapshot(tier, s0, unused);
        repository.SetLastCommittedStateId(canonical);

        using (ReadOnlySnapshotBundle bundle = manager.GatherReadOnlySnapshotBundle(head))
        {
            Assert.That(bundle.SnapshotCount, Is.EqualTo(1), "The compacted edge bypasses the base snapshots.");
            manager.FlushCache(CancellationToken.None);

            Assert.That(repository.RemoveOrphanedStates(canonical, canonical), Is.EqualTo(1));
            using AssembledSnapshotResult ancestry = repository.AssembleSnapshots(second, s0, 2);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(repository.HasState(unused), Is.False);
                Assert.That(repository.HasState(first), Is.True);
                Assert.That(repository.HasState(second), Is.True);
                Assert.That(ancestry.SnapshotCount, Is.EqualTo(2));
            }
        }

        Assert.That(repository.RemoveOrphanedStates(canonical, canonical), Is.EqualTo(4));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.HasState(first), Is.False);
            Assert.That(repository.HasState(second), Is.False);
            Assert.That(repository.HasState(head), Is.False);
            Assert.That(repository.HasState(canonical), Is.True);
        }
    }

    private static void AddEmptySnapshot(FlatTestContainer tier, StateId from, StateId to, bool compacted = false)
    {
        Snapshot snapshot = tier.ResourcePool.CreateSnapshot(from, to, ResourcePool.Usage.MainBlockProcessing);
        Assert.That(tier.Repository.TryAdd(snapshot, compacted ? SnapshotTier.InMemoryCompacted : SnapshotTier.InMemoryBase), Is.True);
        if (!compacted) tier.Repository.AddStateId(to);
    }

    [Test]
    public async Task GatherReadOnlySnapshotBundle_UnavailableState_ReleasesRetention()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId missing = new(1, Keccak.Compute("missing"));
        using FlatTestContainer tier = CreateRetentionContainer(s0);
        await using FlatDbManager manager = (FlatDbManager)tier.Resolve<IFlatDbManager>();

        Assert.That(() => manager.GatherReadOnlySnapshotBundle(missing), Throws.TypeOf<StateUnavailableException>());
        AssertRetained(tier.Resolve<SnapshotRetention>(), missing, false);
    }

    private static FlatTestContainer CreateRetentionContainer(StateId persistedState)
    {
        IPersistenceManager persistenceManager = Substitute.For<IPersistenceManager>();
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.CurrentState.Returns(persistedState);
        persistenceManager.LeaseReader().Returns(reader);
        persistenceManager.GetCurrentPersistedStateId().Returns(persistedState);
        persistenceManager.FlushToPersistence(Arg.Any<CancellationToken>()).Returns(persistedState);
        return new FlatTestContainer(
            new FlatDbConfig { TrieCacheMemoryBudget = 0 },
            arenaFileSizeBytes: 4096,
            configure: builder => builder
                .AddSingleton<IPersistenceManager>(persistenceManager)
                .AddSingleton<IBlocksConfig>(new BlocksConfig())
                .AddSingleton<IMetricsConfig>(new MetricsConfig()));
    }

    private static void AssertRetained(SnapshotRetention retention, StateId head, bool expected)
    {
        using Lock.Scope scope = retention.Sync.EnterScope();
        Assert.That(retention.ActiveHeads.Contains(head), Is.EqualTo(expected));
    }

    [Test]
    public void DisposeAsync_CompletesPromptlyAndIsIdempotent()
    {
        using FlatTestContainer tier = new(arenaFileSizeBytes: 4096);
        SnapshotRepository repo = tier.Repository;

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        tier.ConvertToPersistedBase(new Snapshot(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing)).Dispose();

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
            enableDetailedMetrics: false,
            tier.Resolve<SnapshotRetention>());

        // WaitAsync bounds only the wait, not the drain. A wedged worker-channel drain
        // causes a fast TimeoutException here instead of a stalled test host.
        Assert.DoesNotThrowAsync(async () => await manager.DisposeAsync().AsTask().WaitAsync(DisposeWaitLimit));

        // A second disposal must be a no-op. A completed channel throws on Complete().
        Assert.DoesNotThrowAsync(async () => await manager.DisposeAsync().AsTask().WaitAsync(DisposeWaitLimit));
    }
}
