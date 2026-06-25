// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Tests for the per-<see cref="ArenaManager"/> touch ring: the producer hot path enqueues
/// <c>(arenaId, pageIdx)</c> touches, a single background worker drains them and runs the residency
/// clock, dispatching any displaced page's <c>madvise</c> off the producer thread. On ring-full the
/// producer runs the clock inline. Uses the manager's internal counters for observability (see
/// InternalsVisibleTo on the production assembly).
/// </summary>
public class ArenaManagerEvictionQueueTests
{
    // A page-cache budget of N OS pages yields an N-slot tracker; 8 slots is exactly one 8-way set,
    // so every key collides and the clock outcome is fully determined.
    private static long OneSetBudget => 8L * Environment.SystemPageSize;

    private string _testDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nethermind_evictq_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private static void WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met within timeout");
            Thread.Sleep(5);
        }
    }

    private ArenaManager NewManager(long pageCacheBytes) =>
        new(Path.Combine(_testDir, "arenas"), new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = pageCacheBytes,
            ArenaFileSizeBytes = 64 * 1024,
        }, LimboLogs.Instance);

    [Test]
    public void DisabledTracker_TouchIsNoOp()
    {
        using ArenaManager manager = NewManager(pageCacheBytes: 0);
        Assert.That(manager.PageTracker.MaxCapacity, Is.EqualTo(0));

        manager.Touch(0, 0, inline: false);
        Assert.That(manager.Touch(0, 0, inline: true), Is.EqualTo(PageResidencyTracker.TouchOutcome.Hit));

        Assert.That(manager.EvictionsDispatched, Is.EqualTo(0));
        Assert.That(manager.TouchesProcessedInline, Is.EqualTo(0));
        Assert.That(manager.PendingTouches, Is.EqualTo(0));
    }

    [Test]
    public void Touch_Background_DrainsAndDispatchesEviction()
    {
        using ArenaManager manager = NewManager(OneSetBudget);

        // Fill the single 8-way set, then one more touch forces a clock eviction. The arenaId is not in
        // _arenas, so the dispatch's madvise no-ops on the dictionary miss — we're testing the drain
        // mechanics, not the syscall.
        for (int p = 0; p <= 8; p++)
            manager.Touch(arenaId: 42, pageIdx: p, inline: false);

        WaitFor(() => manager.EvictionsDispatched == 1);
        Assert.That(manager.TouchesProcessedInline, Is.EqualTo(0));
        Assert.That(manager.PendingTouches, Is.EqualTo(0));
    }

    [Test]
    public void Touch_Inline_RunsClockSynchronously()
    {
        using ArenaManager manager = NewManager(OneSetBudget);

        // First touch inserts, repeat hits (no eviction), distinct keys fill the set, and the 9th distinct
        // key evicts — all synchronously on the calling thread.
        Assert.That(manager.Touch(1, 0, inline: true), Is.EqualTo(PageResidencyTracker.TouchOutcome.Inserted));
        Assert.That(manager.Touch(1, 0, inline: true), Is.EqualTo(PageResidencyTracker.TouchOutcome.Hit));
        for (int p = 1; p < 8; p++)
            Assert.That(manager.Touch(1, p, inline: true), Is.EqualTo(PageResidencyTracker.TouchOutcome.Inserted));
        Assert.That(manager.Touch(1, 8, inline: true), Is.EqualTo(PageResidencyTracker.TouchOutcome.Evicted));

        Assert.That(manager.EvictionsDispatched, Is.EqualTo(1));
        // The inline touch did not go through the ring.
        Assert.That(manager.PendingTouches, Is.EqualTo(0));
    }

    [Test]
    public void Dispatch_WithStaleArenaIds_DoesNotThrow()
    {
        using ArenaManager manager = NewManager(OneSetBudget);

        // Seed a couple of resident slots whose arenaIds are NOT in _arenas; forcing evictions against
        // them must not crash (the dispatch's dictionary lookup skips the missing arena).
        manager.PageTracker.TryTouch(arenaId: 777, pageIdx: 0, out _, out _);
        manager.PageTracker.TryTouch(arenaId: 778, pageIdx: 1, out _, out _);

        for (int p = 0; p < 8; p++)
            manager.Touch(arenaId: 42, pageIdx: p, inline: true);

        // Filling the rest of the set and beyond forces at least one eviction, all against stale arenas.
        Assert.That(manager.EvictionsDispatched, Is.GreaterThan(0));
    }

    [Test]
    public void WarmTouch_FiresOnForgetTrackerRange_WithEmptyTrackerDoesNotThrow()
    {
        using ArenaManager manager = NewManager(OneSetBudget);

        // Empty tracker → warm-hand probe budget runs out → TouchWarmPages early-returns.
        // ForgetTrackerRange's per-page Forget is a no-op on an empty tracker.
        manager.ForgetTrackerRange(arenaId: 5, byteOffset: 0, byteSize: 16L * Environment.SystemPageSize);

        // Now populate the tracker and Forget the range again — warm-hand picks must skip the
        // stale arena id (no entry in _arenas) and not crash.
        manager.PageTracker.TryTouch(arenaId: 9, pageIdx: 0, out _, out _);
        manager.ForgetTrackerRange(arenaId: 5, byteOffset: 0, byteSize: 16L * Environment.SystemPageSize);

        // Zero-byte / non-positive ranges are a no-op.
        manager.ForgetTrackerRange(arenaId: 5, byteOffset: 0, byteSize: 0);
        manager.ForgetTrackerRange(arenaId: 5, byteOffset: 0, byteSize: -1);
    }

    [Test]
    public void WarmTouch_RefreshesRegisteredArenaPage_BumpsPagesRefreshed()
    {
        // One 8-way set (capacity 8) so the bounded keep-warm probe is guaranteed to find a resident slot.
        using ArenaManager manager = NewManager(OneSetBudget);
        manager.Initialize([]);

        // A two-page reservation so we can drop one tracked page and still leave one resident for the
        // keep-warm hand to pick and TouchByte-refresh.
        int pageSize = Environment.SystemPageSize;
        byte[] data = new byte[2 * pageSize];
        SnapshotLocation location;
        using (ArenaWriter writer = manager.CreateWriter(data.Length))
        {
            data.CopyTo(writer.GetWriter().GetSpan(data.Length));
            writer.GetWriter().Advance(data.Length);
            (location, _) = writer.Complete();
        }

        // Seed both of the reservation's pages as resident.
        int firstPage = (int)(location.Offset / pageSize);
        manager.PageTracker.TryTouch(location.ArenaId, firstPage, out _, out _);
        manager.PageTracker.TryTouch(location.ArenaId, firstPage + 1, out _, out _);
        Assert.That(manager.PagesRefreshed, Is.EqualTo(0));

        // Forget just the first page's range: it is actually dropped (forgotten == 1), which fires the
        // keep-warm hand; it picks the still-resident second page and TouchByte-refreshes it.
        manager.ForgetTrackerRange(location.ArenaId, location.Offset, byteSize: pageSize);
        Assert.That(manager.PagesRefreshed, Is.GreaterThan(0));
    }

    [Test]
    public void ForgetUntrackedRange_DoesNotWarm()
    {
        using ArenaManager manager = NewManager(OneSetBudget);
        manager.Initialize([]);

        // Seed a resident page so the keep-warm hand WOULD have a target if it fired.
        byte[] data = new byte[Environment.SystemPageSize];
        SnapshotLocation location;
        using (ArenaWriter writer = manager.CreateWriter(data.Length))
        {
            data.CopyTo(writer.GetWriter().GetSpan(data.Length));
            writer.GetWriter().Advance(data.Length);
            (location, _) = writer.Complete();
        }
        manager.PageTracker.TryTouch(location.ArenaId, (int)(location.Offset / Environment.SystemPageSize), out _, out _);

        // Forget a large, fully-untracked range: nothing is actually dropped, so the warm count must scale
        // to actual drops (0) — not over-warm proportional to the cold range size.
        manager.ForgetTrackerRange(location.ArenaId + 1000, byteOffset: 0, byteSize: 1000L * Environment.SystemPageSize);
        Assert.That(manager.PagesRefreshed, Is.EqualTo(0));
    }

    [Test]
    public void Dispose_DrainsRemainingTouches()
    {
        ArenaManager manager = NewManager(OneSetBudget);

        // 16 touches into the single set: 8 fill it, the next 8 each evict. Some are drained by the
        // background worker, the rest by Dispose's synchronous flush.
        const int batch = 16;
        for (int i = 0; i < batch; i++)
            manager.Touch(arenaId: 42, pageIdx: i, inline: false);

        manager.Dispose();

        // The ring is fully drained and every eviction the clock produced was dispatched.
        Assert.That(manager.PendingTouches, Is.EqualTo(0));
        Assert.That(manager.EvictionsDispatched, Is.EqualTo(8));
    }
}
