// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

using static SharedBloomScenarios;

[TestFixture]
public class PersistedSnapshotLoaderTests
{
    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nm_persisted_loader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { /* best-effort */ }
    }

    [Test]
    public void ConversionRegistersOnlyWhenTheArenaSucceeds([Values] bool failArena)
    {
        using ForwardingNodeRecordBuffer buffer = new(FlatTestHelpers.NewTestBuffer());
        ResourcePool pool = new(new FlatDbConfig());
        using Snapshot snapshot = FlatTestHelpers.MakeSnapshot(pool, buffer, content =>
            content.SetStateNodeRlp(buffer, TreePath.Empty, FlatTestHelpers.LeafRlpA));
        using ArenaManager realArena = TestFixtureHelpers.CreateArenaManager(Path.Combine(_testDir, "arena"));
        IArenaManager arena = failArena ? Substitute.For<IArenaManager>() : realArena;
        if (failArena)
        {
            arena.CreateWriter(Arg.Any<long>(), small: true).Returns(_ => throw new IOException("Injected arena allocation failure."));
        }
        ISnapshotCatalog catalog = Substitute.For<ISnapshotCatalog>();
        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs"), 64 * 1024);
        using PersistedSnapshotLoader loader = new(Substitute.For<ISnapshotRepository>(), arena, blobs, catalog,
            new FlatDbConfig { PersistedSnapshotBloomBitsPerKey = 0 }, LimboLogs.Instance);

        if (failArena) Assert.That(() => loader.ConvertAndRegister(snapshot), Throws.TypeOf<IOException>());
        else loader.ConvertAndRegister(snapshot);

        if (failArena) catalog.DidNotReceive().Add(Arg.Any<CatalogEntry>());
        else catalog.Received(1).Add(Arg.Any<CatalogEntry>());
    }

    [Test]
    public void Load_FiltersEntriesReferencingMissingArenasBeforeOpen()
    {
        CatalogEntry orphan = new(default, default, new SnapshotLocation(65, 0, 4096), SnapshotTier.PersistedBase);
        IArenaManager arena = Substitute.For<IArenaManager>();
        arena.Initialize(Arg.Any<IReadOnlyList<CatalogEntry>>()).Returns(new HashSet<int> { 65 });

        ISnapshotCatalog catalog = Substitute.For<ISnapshotCatalog>();
        catalog.Load().Returns(new[] { orphan });

        using BlobArenaManager blobs = new(Path.Combine(_testDir, "blobs"), 64 * 1024);
        using PersistedSnapshotLoader loader = new(
            Substitute.For<ISnapshotRepository>(),
            arena,
            blobs,
            catalog,
            new FlatDbConfig { PersistedSnapshotBloomBitsPerKey = 0 },
            LimboLogs.Instance);

        loader.Load();

        arena.DidNotReceive().Open(Arg.Any<SnapshotLocation>());
    }

    /// <summary>
    /// After a restart, <see cref="PersistedSnapshotLoader"/> builds one bloom per snapshot of the widest chain from that
    /// snapshot's own content and shares it with every persisted snapshot inside its range.
    /// </summary>
    /// <remarks>
    /// The first run leaves the CompactSized (0, 4] and the two-block (4, 6] merges over their one-block snapshots, as
    /// compaction does while finality lags. No large boundary is reached, so nothing is shared before the restart and the
    /// same reads are right there. The widest chain after the restart is [(0, 4], (4, 6]]. The slots are written and
    /// cleared either inside (0, 4] or inside (4, 6].
    /// </remarks>
    [TestCase(2ul, 4ul, false)]
    [TestCase(2ul, 4ul, true)]
    [TestCase(5ul, 6ul, false)]
    [TestCase(5ul, 6ul, true)]
    public async Task Reload_keeps_slots_cleared_later_in_the_widest_range_readable(ulong written, ulong cleared, bool recreated)
    {
        Scenario scenario = new(written, cleared, recreated);
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        using (FlatTestContainer run = new(Config(), baseDbPath: dir.Path, catalogDb: catalog))
        {
            for (ulong block = 1; block <= 6; block++) ConvertAndCompact(run, block, scenario, largeMerge: true);

            await using ILifetimeScope before = BeginManagerScope(run);
            AssertReads(before.Resolve<FlatDbManager>(), scenario, lastBlock: 6, " before the restart");
        }

        // The restart: building the container loads the catalog, then rebuilds and shares the blooms.
        using FlatTestContainer restarted = new(Config(), baseDbPath: dir.Path, catalogDb: catalog);
        await using ILifetimeScope managerScope = BeginManagerScope(restarted);
        AssertReads(managerScope.Resolve<FlatDbManager>(), scenario, lastBlock: 6, " after the restart");
    }

    /// <summary>
    /// A restart after compaction crossed a large boundary: before it the clear-free CompactSized (4, 8] shared its bloom
    /// and the (0, 8] merge, whose range holds the clear, did not. After it the widest (0, 8] keeps its rebuilt bloom to
    /// itself, and the catalog is not rewritten.
    /// </summary>
    [Test]
    public async Task Restart_after_a_large_merge_keeps_slots_cleared_later_in_its_range_readable()
    {
        Scenario scenario = new(Written: 2, Cleared: 3, Recreated: false);
        using TempPath dir = TempPath.GetTempDirectory();
        MemDb catalog = new();
        CatalogEntry[] before;
        using (FlatTestContainer run = new(Config(), baseDbPath: dir.Path, catalogDb: catalog))
        {
            for (ulong block = 1; block <= 8; block++) ConvertAndCompact(run, block, scenario, largeMerge: true);
            AssertSharing(run.Repository, State(8), SnapshotTier.PersistedCompactSized, shares: true);
            before = [.. run.Resolve<ISnapshotCatalog>().Load()];
        }

        using FlatTestContainer restarted = new(Config(), baseDbPath: dir.Path, catalogDb: catalog);
        await using ILifetimeScope managerScope = BeginManagerScope(restarted);
        using (Assert.EnterMultipleScope())
        {
            AssertReads(managerScope.Resolve<FlatDbManager>(), scenario, lastBlock: 8, " after the restart");
            // After the loader has returned: a bloom it released too early fails the owner's probes.
            AssertSharing(restarted.Repository, State(8), SnapshotTier.PersistedLargeCompacted, shares: false);
            Assert.That(restarted.Resolve<ISnapshotCatalog>().Load(), Is.EqualTo(before), "catalog entries, locations and tiers");
            AssertEveryBloomCoversItsTable(restarted.Repository);
        }
    }
}
