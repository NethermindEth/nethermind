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
/// Tests for the per-<see cref="ArenaManager"/> MPSC eviction queue: the producer hot path
/// enqueues displaced pages, a background drain task does the dictionary lookup +
/// <c>madvise</c>, and the drain re-checks the tracker so re-touched pages are not punished.
/// Uses the manager's internal counters for observability (see InternalsVisibleTo on the
/// production assembly).
/// </summary>
public class ArenaManagerEvictionQueueTests
{
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
    public void DisabledTracker_NoQueueOrDrain_QueueEvictionIsNoOp()
    {
        using ArenaManager manager = NewManager(pageCacheBytes: 0);
        Assert.That(manager.PageTracker.MaxCapacity, Is.EqualTo(0));
        manager.QueueEviction(0, 0);
        Assert.That(manager.EvictionsQueued, Is.EqualTo(0));
        Assert.That(manager.EvictionsInlineFallback, Is.EqualTo(0));
        Assert.That(manager.EvictionsDispatched, Is.EqualTo(0));
    }

    [Test]
    public void QueueEviction_EnqueuesAndDrainsEventually()
    {
        long budget = 1024L * Environment.SystemPageSize;
        using ArenaManager manager = NewManager(budget);

        // Use an arenaId that won't exist in _arenas — DispatchEvictionInline silently no-ops
        // on the dictionary miss. We're testing the queue mechanics, not the syscall.
        manager.QueueEviction(arenaId: 42, pageIdx: 3);
        WaitFor(() => manager.EvictionsDispatched + manager.EvictionsSkippedRetouched == 1);
        Assert.That(manager.EvictionsQueued, Is.EqualTo(1));
        Assert.That(manager.EvictionsInlineFallback, Is.EqualTo(0));
        Assert.That(manager.EvictionsDispatched, Is.EqualTo(1));
        Assert.That(manager.EvictionsSkippedRetouched, Is.EqualTo(0));
    }

    [Test]
    public void QueueEviction_SkipsDispatchWhenPageBackInTracker()
    {
        long budget = 1024L * Environment.SystemPageSize;
        using ArenaManager manager = NewManager(budget);

        // Pre-touch (42, 7) so ContainsPage returns true. The drain must skip the dispatch
        // and bump EvictionsSkippedRetouched instead of EvictionsDispatched.
        manager.PageTracker.TryTouch(42, 7, out _, out _);
        Assert.That(manager.PageTracker.ContainsPage(42, 7), Is.True);

        manager.QueueEviction(arenaId: 42, pageIdx: 7);
        WaitFor(() => manager.EvictionsSkippedRetouched == 1);
        Assert.That(manager.EvictionsDispatched, Is.EqualTo(0));
    }

    [Test]
    public void WarmTouch_FiresOnDispatch_WithStaleArenaIdsDoesNotThrow()
    {
        // Touch a couple of pages so the tracker has VALID slots for the warm-hand to pick;
        // their arenaIds (777, 778) are NOT in _arenas — TouchWarmPages must skip them via
        // TryGetValue and not crash. Pair with a queue eviction whose arenaId is also stale,
        // exercising the full DispatchEvictionInline → TouchWarmPages path.
        long budget = 1024L * Environment.SystemPageSize;
        using ArenaManager manager = NewManager(budget);
        manager.PageTracker.TryTouch(arenaId: 777, pageIdx: 0, out _, out _);
        manager.PageTracker.TryTouch(arenaId: 778, pageIdx: 1, out _, out _);

        for (int i = 0; i < 8; i++)
            manager.QueueEviction(arenaId: 42, pageIdx: (uint)i);

        WaitFor(() => manager.EvictionsDispatched + manager.EvictionsSkippedRetouched == 8);
        // The point is that no crash occurred — warm-touch tolerated the missing arenas.
        Assert.That(manager.EvictionsDispatched, Is.EqualTo(8));
    }

    [Test]
    public void WarmTouch_FiresOnForgetTrackerRange_WithEmptyTrackerDoesNotThrow()
    {
        long budget = 1024L * Environment.SystemPageSize;
        using ArenaManager manager = NewManager(budget);

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
        using ArenaManager manager = NewManager(8L * Environment.SystemPageSize);
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
        manager.PageTracker.TryTouch(location.ArenaId, (uint)firstPage, out _, out _);
        manager.PageTracker.TryTouch(location.ArenaId, (uint)(firstPage + 1), out _, out _);
        Assert.That(manager.PagesRefreshed, Is.EqualTo(0));

        // Forget just the first page's range: it is actually dropped (forgotten == 1), which fires the
        // keep-warm hand; it picks the still-resident second page and TouchByte-refreshes it.
        manager.ForgetTrackerRange(location.ArenaId, location.Offset, byteSize: pageSize);
        Assert.That(manager.PagesRefreshed, Is.GreaterThan(0));
    }

    [Test]
    public void ForgetUntrackedRange_DoesNotWarm()
    {
        using ArenaManager manager = NewManager(8L * Environment.SystemPageSize);
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
        manager.PageTracker.TryTouch(location.ArenaId, (uint)(location.Offset / Environment.SystemPageSize), out _, out _);

        // Forget a large, fully-untracked range: nothing is actually dropped, so the warm count must scale
        // to actual drops (0) — not over-warm proportional to the cold range size.
        manager.ForgetTrackerRange(location.ArenaId + 1000, byteOffset: 0, byteSize: 1000L * Environment.SystemPageSize);
        Assert.That(manager.PagesRefreshed, Is.EqualTo(0));
    }

    [Test]
    public void Dispose_DrainsRemainingEntries()
    {
        long budget = 1024L * Environment.SystemPageSize;
        ArenaManager manager = NewManager(budget);

        const int batch = 16;
        for (int i = 0; i < batch; i++)
            manager.QueueEviction(arenaId: 42, pageIdx: (uint)i);

        manager.Dispose();
        // Every queued (or inline-fallback) eviction must have been resolved — either dispatched
        // or skipped — by the time Dispose returns.
        Assert.That(manager.EvictionsQueued, Is.EqualTo(batch));
        Assert.That(
            manager.EvictionsDispatched + manager.EvictionsSkippedRetouched,
            Is.EqualTo(manager.EvictionsQueued + manager.EvictionsInlineFallback));
    }
}
