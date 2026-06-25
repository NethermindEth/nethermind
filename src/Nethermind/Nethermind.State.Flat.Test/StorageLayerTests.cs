// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class StorageLayerTests
{
    private string _testDir = null!;

    // Look up a catalog entry by (To, depth) over the loaded list — the catalog has no Find method
    // and no in-memory index; Load() reads the current state from the DB each call.
    private static CatalogEntry? FindEntry(SnapshotCatalog catalog, StateId to, long depth) =>
        catalog.Load().FirstOrDefault(e => e.To.Equals(to) && (long)(e.To.BlockNumber - e.From.BlockNumber) == depth);

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

        using (FileStream fs = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Write(data1);
            fs.Write(data2);
            fs.Flush();
        }

        // Read back via the raw mmap pointer — the same access path ArenaByteReader uses.
        Assert.That(new ReadOnlySpan<byte>(arena.BasePtr, data1.Length).ToArray(), Is.EqualTo(data1));
        Assert.That(new ReadOnlySpan<byte>(arena.BasePtr + data1.Length, data2.Length).ToArray(), Is.EqualTo(data2));
        Assert.That(arena.MappedSize, Is.EqualTo(1024 * 1024));
    }

    [Test]
    public void SnapshotCatalog_SaveLoad_RoundTrips()
    {
        MemDb catalogDb = new();
        // Same To across three entries with distinct depths (1 / 2 / 4) — mirrors the
        // runtime case where a base + sub-CompactSize compacted + CompactSized snapshot
        // all end at the same block. Pre-v7 catalog would collapse these to one entry on
        // disk; v7 keys by (To, depth) and round-trips all three.
        StateId s_base_from = new(99, Keccak.Compute("block99"));     // depth=1 source
        StateId s_compacted_from = new(98, Keccak.Compute("block98")); // depth=2 source
        StateId s_compactSized_from = new(96, Keccak.Compute("block96")); // depth=4 source
        StateId sharedTo = new(100, Keccak.Compute("block100"));
        StateId s2 = new(200, Keccak.Compute("block200"));

        SnapshotCatalog catalog = new(catalogDb);
        catalog.Add(new(s_base_from, sharedTo, new(0, 0, 1024), SnapshotTier.PersistedBase));
        catalog.Add(new(s_compacted_from, sharedTo, new(0, 1024, 2048), SnapshotTier.PersistedSmallCompacted));
        catalog.Add(new(s_compactSized_from, sharedTo, new(0, 3072, 4096), SnapshotTier.PersistedCompactSized));
        catalog.Add(new(sharedTo, s2, new(0, 7168, 2048), SnapshotTier.PersistedCompactSized));

        SnapshotCatalog loaded = new(catalogDb);

        Assert.That(loaded.Load().Count, Is.EqualTo(4));

        CatalogEntry? loadedBase = FindEntry(loaded, sharedTo, depth: 1);
        CatalogEntry? loadedCompacted = FindEntry(loaded, sharedTo, depth: 2);
        CatalogEntry? loadedCompactSized = FindEntry(loaded, sharedTo, depth: 4);
        Assert.That(loadedBase, Is.Not.Null);
        Assert.That(loadedBase!.From, Is.EqualTo(s_base_from));
        Assert.That(loadedBase.Location, Is.EqualTo(new SnapshotLocation(0, 0, 1024)));
        Assert.That(loadedBase.Tier, Is.EqualTo(SnapshotTier.PersistedBase));
        Assert.That(loadedCompacted, Is.Not.Null);
        Assert.That(loadedCompacted!.From, Is.EqualTo(s_compacted_from));
        Assert.That(loadedCompacted.Location, Is.EqualTo(new SnapshotLocation(0, 1024, 2048)));
        Assert.That(loadedCompacted.Tier, Is.EqualTo(SnapshotTier.PersistedSmallCompacted));
        Assert.That(loadedCompactSized, Is.Not.Null);
        Assert.That(loadedCompactSized!.From, Is.EqualTo(s_compactSized_from));
        Assert.That(loadedCompactSized.Location, Is.EqualTo(new SnapshotLocation(0, 3072, 4096)));
        Assert.That(loadedCompactSized.Tier, Is.EqualTo(SnapshotTier.PersistedCompactSized));

        CatalogEntry? loadedTail = FindEntry(loaded, s2, depth: 100);
        Assert.That(loadedTail, Is.Not.Null);
        Assert.That(loadedTail!.From, Is.EqualTo(sharedTo));
        Assert.That(loadedTail.Location, Is.EqualTo(new SnapshotLocation(0, 7168, 2048)));
        Assert.That(loadedTail.Tier, Is.EqualTo(SnapshotTier.PersistedCompactSized));
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
        catalog.Add(new(s0, s1, new(0, 0, 100), SnapshotTier.PersistedBase));
        catalog.Add(new(s1, s2, new(0, 100, 200), SnapshotTier.PersistedBase));
        // Same To (s2), different depth (s_compactedFrom→s2 has depth=2 vs s1→s2 depth=1).
        catalog.Add(new(s_compactedFrom, s2, new(0, 200, 100), SnapshotTier.PersistedSmallCompacted));

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
        using ArenaManager manager = new(arenaDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = 4096,
        }, LimboLogs.Instance);
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

        using (WholeReadSession session = manager.Open(location).BeginWholeReadSession())
            Assert.That(TestFixtureHelpers.ReadAll(session), Is.EqualTo(data));
        Assert.That(location.Size, Is.EqualTo(data.Length));
    }

    // Both pools (non-small and small) share the same reserve / cancel / re-add lifecycle, so the
    // cancelled-write reuse must hold for each independently.
    [TestCase(false)]
    [TestCase(true)]
    public void ArenaManager_CancelWrite_AllowsReuse(bool small)
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        // 64 KiB so two page-aligned reservations fit in one shared arena file.
        using ArenaManager manager = new(arenaDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = 64 * 1024,
        }, LimboLogs.Instance);
        manager.Initialize([]);

        byte[] baseline = [0xAA];
        SnapshotLocation baselineLoc;
        using (ArenaWriter bw = manager.CreateWriter(baseline.Length, small))
        {
            Span<byte> span = bw.GetWriter().GetSpan(baseline.Length);
            baseline.CopyTo(span);
            bw.GetWriter().Advance(baseline.Length);
            (baselineLoc, _) = bw.Complete();
        }

        using (ArenaWriter arenaWriter = manager.CreateWriter(0, small))
        {
            // Don't call Complete — Dispose will cancel the write and return the file to its pool.
        }

        byte[] data = new byte[50];
        SnapshotLocation loc;
        using (ArenaWriter w = manager.CreateWriter(data.Length, small))
        {
            Span<byte> span = w.GetWriter().GetSpan(data.Length);
            data.CopyTo(span);
            w.GetWriter().Advance(data.Length);
            (loc, _) = w.Complete();
        }
        // The reused write starts at the page-aligned frontier after the baseline reservation —
        // i.e. it landed in the same file, proving the cancelled write returned to the right pool.
        Assert.That(loc.ArenaId, Is.EqualTo(baselineLoc.ArenaId));
        Assert.That(loc.Offset, Is.EqualTo(PageLayout.RoundUpToOsPage(baselineLoc.Offset + baselineLoc.Size)));
    }

    [Test]
    public void ArenaManager_CreateWriter_NextReservationIsPageAligned()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        // 64 KiB so two page-aligned reservations fit in one shared arena file.
        using ArenaManager manager = new(arenaDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = 64 * 1024,
        }, LimboLogs.Instance);
        manager.Initialize([]);

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
        using ArenaManager manager = new(arenaDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = 4096,
            PersistedSnapshotDedicatedArenaThresholdBytes = 64 * 1024,
        }, LimboLogs.Instance);
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
        using ArenaManager manager = new(arenaDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = 200,
        }, LimboLogs.Instance);
        manager.Initialize([]);

        byte[] data = [1, 2, 3];

        using ArenaWriter w1 = manager.CreateWriter(data.Length);
        // w1 holds the first arena; w2 must be assigned a different one while w1 is open.
        using ArenaWriter w2 = manager.CreateWriter(data.Length);
        data.CopyTo(w1.GetWriter().GetSpan(data.Length));
        w1.GetWriter().Advance(data.Length);
        data.CopyTo(w2.GetWriter().GetSpan(data.Length));
        w2.GetWriter().Advance(data.Length);

        (SnapshotLocation loc1, _) = w1.Complete();
        (SnapshotLocation loc2, _) = w2.Complete();

        Assert.That(loc1.ArenaId, Is.Not.EqualTo(loc2.ArenaId));
    }

    [Test]
    public void ArenaManager_SmallAndNonSmallWrites_UseSeparateFiles()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        // Ample headroom: without pool separation all three writes would pack into one file.
        using ArenaManager manager = new(arenaDir, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = 64 * 1024,
        }, LimboLogs.Instance);
        manager.Initialize([]);

        byte[] data = [1, 2, 3];
        SnapshotLocation large = Write(manager, data, small: false);
        SnapshotLocation small = Write(manager, data, small: true);
        SnapshotLocation small2 = Write(manager, data, small: true);

        Assert.That(small.ArenaId, Is.Not.EqualTo(large.ArenaId), "small and non-small writes must not share a file");
        Assert.That(small2.ArenaId, Is.EqualTo(small.ArenaId), "consecutive small writes pack into the small pool's file");
        // The "arena_*" glob is prefix-anchored, so it must not catch the "small_arena_*" file.
        Assert.That(Directory.GetFiles(arenaDir, "small_arena_*.bin"), Has.Length.EqualTo(1));
        Assert.That(Directory.GetFiles(arenaDir, "arena_*.bin"), Has.Length.EqualTo(1));
    }

    [Test]
    public void ArenaManager_SmallArenaFile_SurvivesCatalogRoundTrip()
    {
        string arenaDir = Path.Combine(_testDir, "arenas");
        FlatDbConfig config = new()
        {
            PersistedSnapshotArenaPageCacheBytes = 0,
            ArenaFileSizeBytes = 64 * 1024,
        };
        byte[] data = [9, 8, 7, 6, 5];
        StateId from = new(0, Keccak.Compute("from"));
        StateId to = new(1, Keccak.Compute("to"));

        SnapshotLocation location;
        using (ArenaManager first = new(arenaDir, config, LimboLogs.Instance))
        {
            first.Initialize([]);
            using ArenaWriter writer = first.CreateWriter(data.Length, small: true);
            data.CopyTo(writer.GetWriter().GetSpan(data.Length));
            writer.GetWriter().Advance(data.Length);
            (location, ArenaReservation reservation) = writer.Complete();
            // Keep the small_arena_ file on disk past Dispose so the next session can reload it.
            reservation.PersistOnShutdown();
            reservation.Dispose();
        }

        // Fresh manager over the same dir, primed with the catalog entry referencing the small file.
        // Open succeeds only if Initialize recognized the small_arena_ prefix and loaded the file;
        // otherwise the entry is dropped and the arena left unregistered.
        CatalogEntry entry = new(from, to, location, SnapshotTier.PersistedBase);
        using ArenaManager second = new(arenaDir, config, LimboLogs.Instance);
        second.Initialize([entry]);

        using WholeReadSession session = second.Open(location).BeginWholeReadSession();
        Assert.That(TestFixtureHelpers.ReadAll(session), Is.EqualTo(data));
    }

    private static SnapshotLocation Write(ArenaManager manager, byte[] data, bool small)
    {
        using ArenaWriter writer = manager.CreateWriter(data.Length, small);
        data.CopyTo(writer.GetWriter().GetSpan(data.Length));
        writer.GetWriter().Advance(data.Length);
        (SnapshotLocation location, _) = writer.Complete();
        return location;
    }
}
