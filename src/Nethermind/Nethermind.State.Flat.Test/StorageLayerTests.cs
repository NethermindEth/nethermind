// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Core.Crypto;
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
    public void ArenaFile_WriteViaStreamAndRead_RoundTrips()
    {
        string path = Path.Combine(_testDir, "arena.bin");
        byte[] data1 = [1, 2, 3, 4, 5];
        byte[] data2 = new byte[1000];
        Random.Shared.NextBytes(data2);

        using ArenaFile arena = new(0, path, 1024 * 1024);

        // Write via FileStream, read via mmap
        using (FileStream fs = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Write(data1);
            fs.Write(data2);
            fs.Flush();
        }

        Assert.That(arena.Read(0, data1.Length), Is.EqualTo(data1));
        Assert.That(arena.Read(data1.Length, data2.Length), Is.EqualTo(data2));
        Assert.That(arena.MappedSize, Is.EqualTo(1024 * 1024));
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
    public void ArenaManager_CreateWriterAndComplete_WritesToArena()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        SnapshotLocation location;
        using (ArenaWriter arenaWriter = manager.CreateWriter(data.Length))
        {
            Span<byte> span = arenaWriter.GetWriter().GetSpan(data.Length);
            data.CopyTo(span);
            arenaWriter.GetWriter().Advance(data.Length);
            (location, _) = arenaWriter.Complete();
        }

        // Read back and verify
        Assert.That(manager.Open(location).GetSpan().ToArray(), Is.EqualTo(data));
        Assert.That(location.Size, Is.EqualTo(data.Length));
    }

    [Test]
    public void ArenaManager_CancelWrite_AllowsReuse()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        // First write some data to establish a baseline
        byte[] baseline = [0xAA];
        SnapshotLocation baselineLoc;
        using (ArenaWriter bw = manager.CreateWriter(baseline.Length))
        {
            Span<byte> span = bw.GetWriter().GetSpan(baseline.Length);
            baseline.CopyTo(span);
            bw.GetWriter().Advance(baseline.Length);
            (baselineLoc, _) = bw.Complete();
        }

        // Create writer and then dispose without completing (cancel)
        using (ArenaWriter arenaWriter = manager.CreateWriter(0))
        {
            // Don't call Complete — Dispose will call CancelWrite
        }

        // Write again — should reuse from the baseline offset
        byte[] data = new byte[50];
        SnapshotLocation loc;
        using (ArenaWriter w = manager.CreateWriter(data.Length))
        {
            Span<byte> span = w.GetWriter().GetSpan(data.Length);
            data.CopyTo(span);
            w.GetWriter().Advance(data.Length);
            (loc, _) = w.Complete();
        }
        Assert.That(loc.Offset, Is.EqualTo(baselineLoc.Offset + baselineLoc.Size));
    }

    [Test]
    public void ArenaManager_CreateWriter_FrontierAdvancesExactly()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 4096);
        manager.Initialize([]);

        // Write small data via ArenaWriter
        byte[] data = [1, 2, 3];
        SnapshotLocation location;
        using (ArenaWriter arenaWriter = manager.CreateWriter(data.Length))
        {
            Span<byte> span = arenaWriter.GetWriter().GetSpan(data.Length);
            data.CopyTo(span);
            arenaWriter.GetWriter().Advance(data.Length);
            (location, _) = arenaWriter.Complete();
        }

        Assert.That(location.Size, Is.EqualTo(3));

        // Next write should start right after the written data
        byte[] next = [4, 5];
        SnapshotLocation nextLoc;
        using (ArenaWriter w = manager.CreateWriter(next.Length))
        {
            Span<byte> span = w.GetWriter().GetSpan(next.Length);
            next.CopyTo(span);
            w.GetWriter().Advance(next.Length);
            (nextLoc, _) = w.Complete();
        }
        Assert.That(nextLoc.Offset, Is.EqualTo(location.Offset + location.Size));
    }

    [Test]
    public void ArenaManager_ConcurrentWriters_UseDifferentArenas()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = new(arenaDir, maxArenaSize: 200);
        manager.Initialize([]);

        // Write some data
        byte[] data = [1, 2, 3];

        // First writer takes the arena
        using ArenaWriter w1 = manager.CreateWriter(data.Length);
        // Second writer should use a different arena since the first arena is reserved
        using ArenaWriter w2 = manager.CreateWriter(data.Length);
        data.CopyTo(w1.GetWriter().GetSpan(data.Length));
        w1.GetWriter().Advance(data.Length);
        data.CopyTo(w2.GetWriter().GetSpan(data.Length));
        w2.GetWriter().Advance(data.Length);

        (SnapshotLocation loc1, _) = w1.Complete();
        (SnapshotLocation loc2, _) = w2.Complete();

        Assert.That(loc1.ArenaId, Is.Not.EqualTo(loc2.ArenaId));
    }
}
