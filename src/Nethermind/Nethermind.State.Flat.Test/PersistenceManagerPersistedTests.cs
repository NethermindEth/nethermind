// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class PersistenceManagerPersistedTests
{
    private string _testDir = null!;
    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _pool = new ResourcePool(new FlatDbConfig());
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Test]
    public void ConvertToPersistedSnapshot_PersistsViaManager()
    {
        using ArenaManager baseArena = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096);
        using ArenaManager compactedArena = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(baseArena, compactedArena, _testDir, new FlatDbConfig());
        repo.LoadFromCatalog();

        IFlatDbConfig config = new FlatDbConfig();
        PersistedSnapshotCompactor compactor = new(repo, compactedArena, config, LimboLogs.Instance);

        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotContent content = new();
        content.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(500).TestObject;
        Snapshot snap = new(s0, s1, content, _pool, ResourcePool.Usage.MainBlockProcessing);

        repo.ConvertSnapshotToPersistedSnapshot(snap);

        Assert.That(repo.SnapshotCount, Is.EqualTo(1));
        Assert.That(repo.TryLeaseSnapshotTo(s1, out PersistedSnapshot? snapshot), Is.True);
        snapshot!.Dispose();
    }

    [Test]
    public void PrunePersistedSnapshots_RemovesOldSnapshots()
    {
        using ArenaManager baseArena = new(Path.Combine(_testDir, "arenas", "base"), maxArenaSize: 4096);
        using ArenaManager compactedArena = new(Path.Combine(_testDir, "arenas", "compacted"), maxArenaSize: 4096);
        using PersistedSnapshotRepository repo = new(baseArena, compactedArena, _testDir, new FlatDbConfig());
        repo.LoadFromCatalog();

        IFlatDbConfig config = new FlatDbConfig();
        PersistedSnapshotCompactor compactor = new(repo, compactedArena, config, LimboLogs.Instance);

        // Persist snapshots at various block heights
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s3 = new(3, Keccak.Compute("3"));
        StateId s6 = new(6, Keccak.Compute("6"));

        SnapshotContent c1 = new();
        c1.Accounts[TestItem.AddressA] = Build.An.Account.WithBalance(1).TestObject;
        repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s0, s1, c1, _pool, ResourcePool.Usage.MainBlockProcessing));

        SnapshotContent c2 = new();
        c2.Accounts[TestItem.AddressB] = Build.An.Account.WithBalance(2).TestObject;
        repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s1, s3, c2, _pool, ResourcePool.Usage.MainBlockProcessing));

        SnapshotContent c3 = new();
        c3.Accounts[TestItem.AddressC] = Build.An.Account.WithBalance(3).TestObject;
        repo.ConvertSnapshotToPersistedSnapshot(new Snapshot(s3, s6, c3, _pool, ResourcePool.Usage.MainBlockProcessing));

        Assert.That(repo.SnapshotCount, Is.EqualTo(3));

        // Prune before block 5 (removes snapshots with To < 5, i.e., s1 and s3)
        repo.PruneBefore(new StateId(5, Keccak.Compute("5")));

        Assert.That(repo.SnapshotCount, Is.EqualTo(1)); // Only s6 remains
    }
}
