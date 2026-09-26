// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Verifies that whole-range <c>madvise(MADV_DONTNEED)</c> paths on
/// <see cref="ArenaReservation"/> — <see cref="ArenaReservation.AdviseDontNeed"/> and
/// the disposal path — clear the corresponding entries from the per-arena
/// <see cref="PageResidencyTracker"/>, keeping the tracker in sync with actual page
/// residency after the kernel drops the pages.
/// </summary>
public class ArenaManagerForgetOnAdviseTests
{
    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_forget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private ArenaManager NewManager() =>
        new(Path.Combine(_testDir, "arenas"), new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = 1024L * Environment.SystemPageSize,
            ArenaFileSizeBytes = 1L << 20,
        }, LimboLogs.Instance);

    // Throwaway file backing — the manager's `_arenas` dict doesn't know about this id,
    // so ForgetTrackerRange runs on the tracker only; when the reservation is disposed the
    // subsequent MarkDead TryRemove is a harmless no-op. The reservation requires a non-null
    // ArenaFile to satisfy its constructor.
    private ArenaFile NewSyntheticFile(int id, long size) =>
        new(id, Path.Combine(_testDir, $"synthetic_{id}.bin"), size);

    [Test]
    public void AdviseDontNeed_OnReservation_ClearsTrackerEntries_ForFullyCoveredPages()
    {
        using ArenaManager manager = NewManager();
        const int arenaId = 7;
        int pageSize = Environment.SystemPageSize;

        for (uint p = 0; p < 10; p++)
            manager.PageTracker.TryTouch(arenaId, p, out _, out _);
        for (uint p = 0; p < 10; p++)
            Assert.That(manager.PageTracker.ContainsPage(arenaId, p), Is.True);

        // Reservation covering [0, 10*pageSize) — 10 fully-covered pages.
        using ArenaFile syntheticFile = NewSyntheticFile(arenaId, 10L * pageSize);
        using ArenaReservation reservation = new(manager, syntheticFile, arenaId,
            offset: 0, size: 10L * pageSize);

        reservation.AdviseDontNeed();

        for (uint p = 0; p < 10; p++)
            Assert.That(manager.PageTracker.ContainsPage(arenaId, p), Is.False, $"page {p} should have been Forgotten");
    }

    [Test]
    public void AdviseDontNeed_OnUnalignedReservation_OnlyClearsFullyCoveredPages()
    {
        using ArenaManager manager = NewManager();
        const int arenaId = 7;
        int pageSize = Environment.SystemPageSize;

        for (uint p = 0; p < 5; p++)
            manager.PageTracker.TryTouch(arenaId, p, out _, out _);

        // Reservation [pageSize/2, pageSize/2 + 3*pageSize). Page-aligned start = page 1,
        // page-aligned end = page 3 (exclusive). So pages 1, 2 are fully covered; pages 0 and 3
        // straddle the boundary and must remain.
        using ArenaFile syntheticFile = NewSyntheticFile(arenaId, 5L * pageSize);
        using ArenaReservation reservation = new(manager, syntheticFile, arenaId,
            offset: pageSize / 2, size: 3L * pageSize);

        reservation.AdviseDontNeed();

        Assert.That(manager.PageTracker.ContainsPage(arenaId, 0), Is.True, "page 0 partially covered");
        Assert.That(manager.PageTracker.ContainsPage(arenaId, 1), Is.False);
        Assert.That(manager.PageTracker.ContainsPage(arenaId, 2), Is.False);
        Assert.That(manager.PageTracker.ContainsPage(arenaId, 3), Is.True, "page 3 partially covered");
        Assert.That(manager.PageTracker.ContainsPage(arenaId, 4), Is.True, "page 4 outside range");
    }

    [Test]
    public void ReservationDispose_ClearsTrackerRange()
    {
        using ArenaManager manager = NewManager();
        int pageSize = Environment.SystemPageSize;

        // Materialise a real arena via a writer so the dispose-driven MarkDead has the dict
        // entry it expects to mutate. Write 4 pages of zeros.
        const int pages = 4;
        ArenaWriter writer = manager.CreateWriter(estimatedSize: pages * pageSize);
        ref ArenaBufferWriter buf = ref writer.GetWriter();
        Span<byte> sink = buf.GetSpan(pages * pageSize);
        sink[..(pages * pageSize)].Clear();
        buf.Advance(pages * pageSize);
        (SnapshotLocation location, ArenaReservation reservation) = writer.Complete();

        uint firstPage = (uint)(location.Offset / pageSize);
        for (uint i = 0; i < pages; i++)
            manager.PageTracker.TryTouch(location.ArenaId, firstPage + i, out _, out _);

        // CleanUp calls ForgetTrackerRange over the reservation's footprint after MarkDead.
        reservation.Dispose();

        for (uint i = 0; i < pages; i++)
            Assert.That(manager.PageTracker.ContainsPage(location.ArenaId, firstPage + i),
                Is.False, $"page {firstPage + i} should have been Forgotten on reservation dispose");
    }
}
