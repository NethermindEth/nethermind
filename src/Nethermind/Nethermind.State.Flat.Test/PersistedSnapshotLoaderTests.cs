// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

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
}
