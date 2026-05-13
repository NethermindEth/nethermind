// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using FluentAssertions;
using Nethermind.State.Flat.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Verifies that whole-range <c>madvise(MADV_DONTNEED)</c> paths driven from
/// <see cref="ArenaReservation"/> (its <see cref="ArenaReservation.AdviseDontNeed"/> entry
/// point and its disposal path through <see cref="ArenaManager.MarkDead(ArenaFile, long)"/>)
/// clear the corresponding page entries from the per-arena
/// <see cref="PageResidencyTracker"/>. Without this, stale entries would make the next
/// reader's <c>TryTouch</c> return <c>Hit</c> and skip the <c>PopulateRead</c> pre-fault.
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
        new(Path.Combine(_testDir, "arenas"), pageCacheBytes: 1024L * Environment.SystemPageSize, maxArenaSize: 1L << 20);

    // Throwaway file backing — the manager's `_arenas` dict still doesn't know about the
    // synthesised reservation's id, so the file-level madvise path operates on the synthetic
    // file directly and the manager's MarkDead path harmlessly fails to find the id in its
    // dict (TryRemove returns false). The reservation just needs a non-null ArenaFile to
    // satisfy the constructor.
    private ArenaFile NewSyntheticFile(int id, long size) =>
        new(id, Path.Combine(_testDir, $"synthetic_{id}.bin"), size);

    [Test]
    public void AdviseDontNeed_OnReservation_ClearsTrackerEntries_ForFullyCoveredPages()
    {
        using ArenaManager manager = NewManager();
        const int arenaId = 7;
        int pageSize = Environment.SystemPageSize;

        // Populate tracker for pages 0..9 of arena 7.
        for (int p = 0; p < 10; p++)
            manager.PageTracker.TryTouch(arenaId, p, out _, out _);
        for (int p = 0; p < 10; p++)
            manager.PageTracker.ContainsPage(arenaId, p).Should().BeTrue();

        // Reservation covering [0, 10*pageSize) — 10 fully-covered pages.
        using ArenaFile syntheticFile = NewSyntheticFile(arenaId, 10L * pageSize);
        using ArenaReservation reservation = new(manager, syntheticFile, arenaId,
            offset: 0, size: 10L * pageSize, tag: "test");

        reservation.AdviseDontNeed();

        for (int p = 0; p < 10; p++)
            manager.PageTracker.ContainsPage(arenaId, p).Should().BeFalse($"page {p} should have been Forgotten");
    }

    [Test]
    public void AdviseDontNeed_OnUnalignedReservation_OnlyClearsFullyCoveredPages()
    {
        using ArenaManager manager = NewManager();
        const int arenaId = 7;
        int pageSize = Environment.SystemPageSize;

        // Pages 0..4 in tracker.
        for (int p = 0; p < 5; p++)
            manager.PageTracker.TryTouch(arenaId, p, out _, out _);

        // Reservation [pageSize/2, pageSize/2 + 3*pageSize). Page-aligned start = page 1,
        // page-aligned end = page 3 (exclusive). So pages 1, 2 are fully covered; pages 0 and 3
        // straddle the boundary and must remain.
        using ArenaFile syntheticFile = NewSyntheticFile(arenaId, 5L * pageSize);
        using ArenaReservation reservation = new(manager, syntheticFile, arenaId,
            offset: pageSize / 2, size: 3L * pageSize, tag: "test");

        reservation.AdviseDontNeed();

        manager.PageTracker.ContainsPage(arenaId, 0).Should().BeTrue("page 0 partially covered");
        manager.PageTracker.ContainsPage(arenaId, 1).Should().BeFalse();
        manager.PageTracker.ContainsPage(arenaId, 2).Should().BeFalse();
        manager.PageTracker.ContainsPage(arenaId, 3).Should().BeTrue("page 3 partially covered");
        manager.PageTracker.ContainsPage(arenaId, 4).Should().BeTrue("page 4 outside range");
    }

    [Test]
    public void ReservationDispose_ClearsTrackerRange()
    {
        using ArenaManager manager = NewManager();
        int pageSize = Environment.SystemPageSize;

        // Materialise a real arena via a writer so the dispose-driven MarkDead has the dict
        // entry it expects to mutate. Write 4 pages of zeros.
        const int pages = 4;
        ArenaWriter writer = manager.CreateWriter(estimatedSize: pages * pageSize, tag: "test");
        ref ArenaBufferWriter buf = ref writer.GetWriter();
        Span<byte> sink = buf.GetSpan(pages * pageSize);
        sink[..(pages * pageSize)].Clear();
        buf.Advance(pages * pageSize);
        (SnapshotLocation location, ArenaReservation reservation) = writer.Complete();

        int firstPage = (int)(location.Offset / pageSize);
        for (int i = 0; i < pages; i++)
            manager.PageTracker.TryTouch(location.ArenaId, firstPage + i, out _, out _);

        // Disposing the reservation runs its CleanUp path, which calls
        // manager.ForgetTrackerRange(...) on the same byte range MarkDead used to handle.
        reservation.Dispose();

        for (int i = 0; i < pages; i++)
            manager.PageTracker.ContainsPage(location.ArenaId, firstPage + i)
                .Should().BeFalse($"page {firstPage + i} should have been Forgotten on reservation dispose");
    }
}
