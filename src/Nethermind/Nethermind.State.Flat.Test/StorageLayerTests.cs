// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class StorageLayerTests
{
    private string _testDir = null!;

    // Look up a catalog entry by (To, depth) over the loaded list — the catalog has no Find method
    // and no in-memory index; Load() reads the current state from the DB each call.
    private static SnapshotCatalog.CatalogEntry? FindEntry(SnapshotCatalog catalog, StateId to, long depth) =>
        catalog.Load().FirstOrDefault(e => e.To.Equals(to) && e.To.BlockNumber - e.From.BlockNumber == depth);

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
    public unsafe void ArenaFile_WriteViaStreamAndRead_RoundTrips()
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

        // Read back through the mmap base pointer (the same primitive ArenaByteReader uses).
        Assert.That(new ReadOnlySpan<byte>(arena.BasePtr, data1.Length).ToArray(), Is.EqualTo(data1));
        Assert.That(new ReadOnlySpan<byte>(arena.BasePtr + data1.Length, data2.Length).ToArray(), Is.EqualTo(data2));
        Assert.That(arena.MappedSize, Is.EqualTo(1024 * 1024));
    }

    [Test]
    public void SnapshotCatalog_SaveLoad_RoundTrips()
    {
        MemDb catalogDb = new();
        // Same To across three entries with distinct depths (1 / 2 / 4) — mirrors the
        // runtime case where a base + sub-CompactSize compacted + CompactSize persistable
        // all end at the same block. Pre-v7 catalog would collapse these to one entry on
        // disk; v7 keys by (To, depth) and round-trips all three.
        StateId s_base_from = new(99, Keccak.Compute("block99"));     // depth=1 source
        StateId s_compacted_from = new(98, Keccak.Compute("block98")); // depth=2 source
        StateId s_persistable_from = new(96, Keccak.Compute("block96")); // depth=4 source
        StateId sharedTo = new(100, Keccak.Compute("block100"));
        StateId s2 = new(200, Keccak.Compute("block200"));

        SnapshotCatalog catalog = new(catalogDb);
        catalog.Add(new(s_base_from, sharedTo, new(0, 0, 1024), SnapshotKind.Base));
        catalog.Add(new(s_compacted_from, sharedTo, new(0, 1024, 2048), SnapshotKind.Compacted));
        catalog.Add(new(s_persistable_from, sharedTo, new(0, 3072, 4096), SnapshotKind.Persistable));
        catalog.Add(new(sharedTo, s2, new(0, 7168, 2048), SnapshotKind.Persistable));

        // Load in new instance
        SnapshotCatalog loaded = new(catalogDb);

        Assert.That(loaded.Load().Count, Is.EqualTo(4));

        // All three entries at sharedTo must survive distinct.
        SnapshotCatalog.CatalogEntry? loadedBase = FindEntry(loaded, sharedTo, depth: 1);
        SnapshotCatalog.CatalogEntry? loadedCompacted = FindEntry(loaded, sharedTo, depth: 2);
        SnapshotCatalog.CatalogEntry? loadedPersistable = FindEntry(loaded, sharedTo, depth: 4);
        Assert.That(loadedBase, Is.Not.Null);
        Assert.That(loadedBase!.From, Is.EqualTo(s_base_from));
        Assert.That(loadedBase.Location, Is.EqualTo(new SnapshotLocation(0, 0, 1024)));
        Assert.That(loadedBase.Kind, Is.EqualTo(SnapshotKind.Base));
        Assert.That(loadedCompacted, Is.Not.Null);
        Assert.That(loadedCompacted!.From, Is.EqualTo(s_compacted_from));
        Assert.That(loadedCompacted.Location, Is.EqualTo(new SnapshotLocation(0, 1024, 2048)));
        Assert.That(loadedCompacted.Kind, Is.EqualTo(SnapshotKind.Compacted));
        Assert.That(loadedPersistable, Is.Not.Null);
        Assert.That(loadedPersistable!.From, Is.EqualTo(s_persistable_from));
        Assert.That(loadedPersistable.Location, Is.EqualTo(new SnapshotLocation(0, 3072, 4096)));
        Assert.That(loadedPersistable.Kind, Is.EqualTo(SnapshotKind.Persistable));

        SnapshotCatalog.CatalogEntry? loadedTail = FindEntry(loaded, s2, depth: 100);
        Assert.That(loadedTail, Is.Not.Null);
        Assert.That(loadedTail!.From, Is.EqualTo(sharedTo));
        Assert.That(loadedTail.Location, Is.EqualTo(new SnapshotLocation(0, 7168, 2048)));
        Assert.That(loadedTail.Kind, Is.EqualTo(SnapshotKind.Persistable));
    }

    [Test]
    public void SnapshotCatalog_Remove_And_Find()
    {
        StateId s0 = new(0, Keccak.EmptyTreeHash);
        StateId s_compactedFrom = new(0, Keccak.Compute("compactedFrom"));
        StateId s1 = new(1, Keccak.Compute("1"));
        StateId s2 = new(2, Keccak.Compute("2"));
        StateId missing = new(999, Keccak.Compute("missing"));

        SnapshotCatalog catalog = new(new MemDb());
        catalog.Add(new(s0, s1, new(0, 0, 100), SnapshotKind.Base));
        catalog.Add(new(s1, s2, new(0, 100, 200), SnapshotKind.Base));
        // Same To (s2), different depth (s_compactedFrom→s2 has depth=2 vs s1→s2 depth=1).
        catalog.Add(new(s_compactedFrom, s2, new(0, 200, 100), SnapshotKind.Compacted));

        Assert.That(FindEntry(catalog, s1, depth: 1), Is.Not.Null);
        Assert.That(catalog.Remove(s1, depth: 1), Is.True);
        Assert.That(FindEntry(catalog, s1, depth: 1), Is.Null);
        Assert.That(catalog.Load().Count(), Is.EqualTo(2));
        Assert.That(catalog.Remove(missing, depth: 1), Is.False);

        // Removing one (To, depth) leaves the sibling at the same To intact.
        Assert.That(FindEntry(catalog, s2, depth: 1), Is.Not.Null);
        Assert.That(FindEntry(catalog, s2, depth: 2), Is.Not.Null);
        Assert.That(catalog.Remove(s2, depth: 1), Is.True);
        Assert.That(FindEntry(catalog, s2, depth: 1), Is.Null);
        Assert.That(FindEntry(catalog, s2, depth: 2), Is.Not.Null);
    }


    [Test]
    public void SnapshotCatalog_Load_EmptyOrMissing_ReturnsEmpty()
    {
        SnapshotCatalog catalog = new(new MemDb());

        Assert.That(catalog.Load(), Is.Empty);
    }

    [Test]
    public void ArenaManager_CreateWriterAndComplete_WritesToArena()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = ArenaManagerTestFactory.Create(arenaDir, 0, maxArenaSize: 4096);
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
        using (WholeReadSession session = manager.Open(location).BeginWholeReadSession())
            Assert.That(TestFixtureHelpers.ReadAll(session), Is.EqualTo(data));
        Assert.That(location.Size, Is.EqualTo(data.Length));
    }

    [Test]
    public void ArenaManager_CancelWrite_AllowsReuse()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        // 64 KiB so two page-aligned reservations fit in one shared arena file.
        using ArenaManager manager = ArenaManagerTestFactory.Create(arenaDir, 0, maxArenaSize: 64 * 1024);
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
        // The reused write starts at the page-aligned frontier after the baseline reservation.
        Assert.That(loc.Offset, Is.EqualTo(PageLayout.RoundUpToOsPage(baselineLoc.Offset + baselineLoc.Size)));
    }

    [Test]
    public void ArenaManager_CreateWriter_NextReservationIsPageAligned()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        // 64 KiB so two page-aligned reservations fit in one shared arena file.
        using ArenaManager manager = ArenaManagerTestFactory.Create(arenaDir, 0, maxArenaSize: 64 * 1024);
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

        // Size stays the exact byte count; only the frontier is page-padded.
        Assert.That(location.Size, Is.EqualTo(3));

        // Next reservation starts at the page-aligned frontier, not right after the data.
        byte[] next = [4, 5];
        SnapshotLocation nextLoc;
        using (ArenaWriter w = manager.CreateWriter(next.Length))
        {
            Span<byte> span = w.GetWriter().GetSpan(next.Length);
            next.CopyTo(span);
            w.GetWriter().Advance(next.Length);
            (nextLoc, _) = w.Complete();
        }
        Assert.That(nextLoc.Offset, Is.EqualTo(PageLayout.RoundUpToOsPage(location.Offset + location.Size)));
    }

    [Test]
    public void ArenaManager_DedicatedArena_ShrinksToActualSizeOnComplete()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        // Lower the dedicated threshold so the test doesn't need to allocate 512 MiB.
        using ArenaManager manager = ArenaManagerTestFactory.Create(arenaDir, 0, maxArenaSize: 4096, dedicatedArenaThreshold: 64 * 1024);
        manager.Initialize([]);

        const long estimate = 256 * 1024;
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        SnapshotLocation location;
        string dedicatedFile;
        using (ArenaWriter writer = manager.CreateWriter(estimate))
        {
            data.CopyTo(writer.GetWriter().GetSpan(data.Length));
            writer.GetWriter().Advance(data.Length);
            (location, _) = writer.Complete();
            dedicatedFile = Directory.GetFiles(arenaDir, "dedicated_*.bin")[0];
        }

        Assert.That(new FileInfo(dedicatedFile).Length, Is.EqualTo(data.Length));
        using WholeReadSession session = manager.Open(location).BeginWholeReadSession();
        Assert.That(TestFixtureHelpers.ReadAll(session), Is.EqualTo(data));
    }

    [Test]
    public void ArenaManager_ConcurrentWriters_UseDifferentArenas()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        using ArenaManager manager = ArenaManagerTestFactory.Create(arenaDir, 0, maxArenaSize: 200);
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
