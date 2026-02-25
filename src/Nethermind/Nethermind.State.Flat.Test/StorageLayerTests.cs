// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class StorageLayerTests
{
    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    [Test]
    public void ArenaFile_WriteAndRead_RoundTrips()
    {
        string path = Path.Combine(_testDir, "arena.bin");
        byte[] data1 = [1, 2, 3, 4, 5];
        byte[] data2 = new byte[1000];
        Random.Shared.NextBytes(data2);

        using ArenaFile arena = new(0, path, 1024 * 1024);

        // Write at offset 0
        arena.Write(0, data1);
        Assert.That(arena.Read(0, data1.Length), Is.EqualTo(data1));

        // Write at offset after first data
        arena.Write(data1.Length, data2);
        Assert.That(arena.Read(data1.Length, data2.Length), Is.EqualTo(data2));

        // First data still intact
        Assert.That(arena.Read(0, data1.Length), Is.EqualTo(data1));
        Assert.That(arena.MappedSize, Is.EqualTo(1024 * 1024));
    }

    [Test]
    public void ArenaManager_AllocateAndRead([Values(1, 10, 100)] int count)
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        SnapshotLocation[] locations = new SnapshotLocation[count];
        byte[][] expectedData = new byte[count][];

        for (int i = 0; i < count; i++)
        {
            expectedData[i] = new byte[32];
            Random.Shared.NextBytes(expectedData[i]);
            locations[i] = manager.Allocate(expectedData[i]);
        }

        // Read back all allocations
        for (int i = 0; i < count; i++)
        {
            Assert.That(manager.Open(locations[i]).GetSpan().ToArray(), Is.EqualTo(expectedData[i]),
                $"Data mismatch at index {i}");
        }
    }

    [Test]
    public void ArenaManager_CreatesNewArena_WhenFull()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 100);
        manager.Initialize([]);

        byte[] data = new byte[60];

        SnapshotLocation loc1 = manager.Allocate(data);
        SnapshotLocation loc2 = manager.Allocate(data); // Won't fit in first arena

        Assert.That(loc1.ArenaId, Is.Not.EqualTo(loc2.ArenaId), "Should use different arenas");
        Assert.That(manager.Open(loc1).GetSpan().ToArray(), Is.EqualTo(data));
        Assert.That(manager.Open(loc2).GetSpan().ToArray(), Is.EqualTo(data));
    }

    [Test]
    public void ArenaManager_MarkDead_TracksDeadSpace()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        byte[] data = new byte[100];
        SnapshotLocation loc1 = manager.Allocate(data);
        SnapshotLocation loc2 = manager.Allocate(data);
        SnapshotLocation loc3 = manager.Allocate(data);

        Assert.That(manager.ShouldCompact(loc1.ArenaId), Is.False);

        // Mark two of three as dead (>50%)
        manager.MarkDead(loc1);
        manager.MarkDead(loc2);
        Assert.That(manager.ShouldCompact(loc1.ArenaId), Is.True);
    }

    [Test]
    public void ArenaManager_Compact_MovesLiveData()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        byte[] data1 = [1, 2, 3];
        byte[] data2 = [4, 5, 6, 7];
        byte[] data3 = [8, 9];

        SnapshotLocation loc1 = manager.Allocate(data1);
        SnapshotLocation loc2 = manager.Allocate(data2);
        SnapshotLocation loc3 = manager.Allocate(data3);
        int origArena = loc1.ArenaId;

        // Compact with only loc2 and loc3 live (loc1 is dead)
        var mapping = manager.Compact(origArena, [loc2, loc3]);

        Assert.That(mapping.Count, Is.EqualTo(2));
        Assert.That(manager.Open(mapping[loc2]).GetSpan().ToArray(), Is.EqualTo(data2));
        Assert.That(manager.Open(mapping[loc3]).GetSpan().ToArray(), Is.EqualTo(data3));
    }

    [Test]
    public void ArenaManager_Compact_EmptyArena_DeletesFile()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        byte[] data = [1, 2, 3];
        SnapshotLocation loc = manager.Allocate(data);
        int arenaId = loc.ArenaId;

        // Compact with no live data
        var mapping = manager.Compact(arenaId, []);

        Assert.That(mapping, Is.Empty);
        Assert.That(manager.GetArenaIds(), Does.Not.Contain(arenaId));
    }

    [Test]
    public void ArenaManager_Initialize_RestoresFromCatalog()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        byte[] data1 = [1, 2, 3];
        byte[] data2 = [4, 5, 6, 7, 8];
        SnapshotLocation loc1, loc2;

        // First session: write data
        using (ArenaManager manager = new(arenaDir, maxArenaSize: 4096))
        {
            manager.Initialize([]);
            loc1 = manager.Allocate(data1);
            loc2 = manager.Allocate(data2);
        }

        // Second session: restore from catalog entries
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));
        SnapshotCatalog.CatalogEntry[] entries =
        [
            new(1, s0, s1, PersistedSnapshotType.Full, loc1),
            new(2, s0, s1, PersistedSnapshotType.Full, loc2),
        ];

        using (ArenaManager manager = new(arenaDir, maxArenaSize: 4096))
        {
            manager.Initialize(entries);
            Assert.That(manager.Open(loc1).GetSpan().ToArray(), Is.EqualTo(data1));
            Assert.That(manager.Open(loc2).GetSpan().ToArray(), Is.EqualTo(data2));

            // New allocation starts after existing data
            byte[] data3 = [9, 10];
            SnapshotLocation loc3 = manager.Allocate(data3);
            Assert.That(loc3.Offset, Is.GreaterThanOrEqualTo(loc2.Offset + loc2.Size));
            Assert.That(manager.Open(loc3).GetSpan().ToArray(), Is.EqualTo(data3));
        }
    }

    [Test]
    public void SnapshotCatalog_SaveLoad_RoundTrips()
    {
        string catalogPath = Path.Combine(_testDir, "catalog.bin");
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(100, Keccak.Compute("block100"));
        StateId s2 = new(200, Keccak.Compute("block200"));

        SnapshotCatalog catalog = new(catalogPath);
        int id1 = catalog.NextId();
        int id2 = catalog.NextId();
        catalog.Add(new(id1, s0, s1, PersistedSnapshotType.Full, new(0, 0, 1024)));
        catalog.Add(new(id2, s1, s2, PersistedSnapshotType.Linked, new(0, 1024, 2048)));
        catalog.Save();

        // Load in new instance
        SnapshotCatalog loaded = new(catalogPath);
        loaded.Load();

        Assert.That(loaded.Entries.Count, Is.EqualTo(2));

        SnapshotCatalog.CatalogEntry e1 = loaded.Entries[0];
        Assert.That(e1.Id, Is.EqualTo(id1));
        Assert.That(e1.From.BlockNumber, Is.EqualTo(0));
        Assert.That(e1.To.BlockNumber, Is.EqualTo(100));
        Assert.That(e1.Type, Is.EqualTo(PersistedSnapshotType.Full));
        Assert.That(e1.Location, Is.EqualTo(new SnapshotLocation(0, 0, 1024)));

        SnapshotCatalog.CatalogEntry e2 = loaded.Entries[1];
        Assert.That(e2.Id, Is.EqualTo(id2));
        Assert.That(e2.From.BlockNumber, Is.EqualTo(100));
        Assert.That(e2.To.BlockNumber, Is.EqualTo(200));
        Assert.That(e2.Type, Is.EqualTo(PersistedSnapshotType.Linked));
        Assert.That(e2.Location, Is.EqualTo(new SnapshotLocation(0, 1024, 2048)));

        // NextId should be preserved
        Assert.That(loaded.NextId(), Is.EqualTo(id2 + 1));
    }

    [Test]
    public void SnapshotCatalog_Remove_And_Find()
    {
        string catalogPath = Path.Combine(_testDir, "catalog.bin");
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        SnapshotCatalog catalog = new(catalogPath);
        int id1 = catalog.NextId();
        int id2 = catalog.NextId();
        catalog.Add(new(id1, s0, s1, PersistedSnapshotType.Full, new(0, 0, 100)));
        catalog.Add(new(id2, s0, s1, PersistedSnapshotType.Full, new(0, 100, 200)));

        Assert.That(catalog.Find(id1), Is.Not.Null);
        Assert.That(catalog.Remove(id1), Is.True);
        Assert.That(catalog.Find(id1), Is.Null);
        Assert.That(catalog.Entries.Count, Is.EqualTo(1));
        Assert.That(catalog.Remove(999), Is.False);
    }

    [Test]
    public void SnapshotCatalog_UpdateLocation()
    {
        string catalogPath = Path.Combine(_testDir, "catalog.bin");
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s1 = new(1, Keccak.Compute("1"));

        SnapshotCatalog catalog = new(catalogPath);
        int id = catalog.NextId();
        SnapshotLocation origLoc = new(0, 0, 100);
        SnapshotLocation newLoc = new(1, 500, 100);
        catalog.Add(new(id, s0, s1, PersistedSnapshotType.Full, origLoc));

        catalog.UpdateLocation(id, newLoc);

        Assert.That(catalog.Find(id)!.Location, Is.EqualTo(newLoc));
    }

    [Test]
    public void SnapshotCatalog_Load_EmptyOrMissing_ReturnsEmpty()
    {
        string catalogPath = Path.Combine(_testDir, "nonexistent.bin");
        SnapshotCatalog catalog = new(catalogPath);
        catalog.Load();

        Assert.That(catalog.Entries, Is.Empty);
    }

    [Test]
    public void EndToEnd_BuildHsst_StoreInArena_ReadBack()
    {
        // Build HSST data from a snapshot
        StateId from = new(0, Keccak.EmptyTreeHash);
        StateId to = new(1, Keccak.Compute("1"));

        SnapshotContent content = new();
        content.Accounts[Core.Test.Builders.TestItem.AddressA] =
            Core.Test.Builders.Build.An.Account.WithBalance(42).TestObject;
        ResourcePool pool = new(new FlatDbConfig());
        Snapshot snapshot = new(from, to, content, pool, ResourcePool.Usage.MainBlockProcessing);

        byte[] hsstData = PersistedSnapshotBuilder.Build(snapshot);

        // Store in arena
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);
        SnapshotLocation location = manager.Allocate(hsstData);

        // Store in catalog
        string catalogPath = Path.Combine(_testDir, "catalog.bin");
        SnapshotCatalog catalog = new(catalogPath);
        int id = catalog.NextId();
        catalog.Add(new(id, from, to, PersistedSnapshotType.Full, location));
        catalog.Save();

        // Read back from arena and use as PersistedSnapshot
        ArenaReservation reservation = manager.Open(location);
        PersistedSnapshot persisted = new(id, from, to, PersistedSnapshotType.Full, reservation);

        bool hasAccount = persisted.TryGetAccount(Core.Test.Builders.TestItem.AddressA, out ReadOnlySpan<byte> rlp);
        Assert.That(hasAccount, Is.True);

        Serialization.Rlp.Rlp.ValueDecoderContext ctx = new(rlp);
        Core.Account decoded = Serialization.Rlp.AccountDecoder.Slim.Decode(ref ctx)!;
        Assert.That(decoded.Balance, Is.EqualTo((Int256.UInt256)42));
    }

    [Test]
    public void ArenaManager_ReserveAndFinalize_WritesToArena()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        ArenaReservation reservation = manager.ReserveForWrite(64);

        // Write into the reserved mmap span
        data.CopyTo(reservation.GetSpan());

        SnapshotLocation location = reservation.FinalizedWrite(data.Length);

        // Read back and verify
        Assert.That(manager.Open(location).GetSpan().ToArray(), Is.EqualTo(data));
        Assert.That(location.Size, Is.EqualTo(data.Length));
    }

    [Test]
    public void ArenaManager_Return_RollsBackFrontier()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        // First allocate some data to establish a baseline
        byte[] baseline = [0xAA];
        SnapshotLocation baselineLoc = manager.Allocate(baseline);

        // Reserve and then return
        ArenaReservation reservation = manager.ReserveForWrite(100);
        int arenaId = reservation.ArenaId;
        reservation.Return();

        // Allocate again — should reuse the space that was returned
        byte[] data = new byte[50];
        SnapshotLocation loc = manager.Allocate(data);
        Assert.That(loc.Offset, Is.EqualTo(baselineLoc.Offset + baselineLoc.Size));
    }

    [Test]
    public void ArenaManager_ReserveFinalize_TrimsFrontierOnPartialUse()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        // Reserve large, finalize with smaller actualSize
        ArenaReservation reservation = manager.ReserveForWrite(1000);
        byte[] data = [1, 2, 3];
        data.CopyTo(reservation.GetSpan());
        SnapshotLocation location = reservation.FinalizedWrite(data.Length);

        Assert.That(location.Size, Is.EqualTo(3));

        // Next allocation should start right after the finalized data, not after the full reservation
        byte[] next = [4, 5];
        SnapshotLocation nextLoc = manager.Allocate(next);
        Assert.That(nextLoc.Offset, Is.EqualTo(location.Offset + location.Size));
    }

    [Test]
    public void ArenaManager_ConcurrentReserves_UseDifferentArenas()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 200);
        manager.Initialize([]);

        // First reserve takes the arena
        ArenaReservation r1 = manager.ReserveForWrite(100);
        // Second reserve should use a different arena since the first arena is reserved
        ArenaReservation r2 = manager.ReserveForWrite(100);

        Assert.That(r1.ArenaId, Is.Not.EqualTo(r2.ArenaId));

        r1.FinalizedWrite(50);
        r2.FinalizedWrite(50);
    }

    [Test]
    public void ArenaManager_Reserve_LargeSize_CreatesStandaloneFile()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        // Use a small maxArenaSize but reserve more than DedicatedArenaThreshold
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        // First allocate something in the shared arena
        byte[] small = [1, 2, 3];
        SnapshotLocation smallLoc = manager.Allocate(small);

        // Reserve a large standalone file (>512MB threshold)
        int largeSize = 512 * 1024 * 1024 + 1;
        ArenaReservation reservation = manager.ReserveForWrite(largeSize);

        // The standalone arena should be different from the shared one
        Assert.That(reservation.ArenaId, Is.Not.EqualTo(smallLoc.ArenaId));

        // Return the large reservation — standalone file should be deleted
        reservation.Return();

        // The shared arena should still work
        byte[] more = [4, 5, 6];
        SnapshotLocation moreLoc = manager.Allocate(more);
        Assert.That(moreLoc.ArenaId, Is.EqualTo(smallLoc.ArenaId));
        Assert.That(manager.Open(moreLoc).GetSpan().ToArray(), Is.EqualTo(more));
    }
}
