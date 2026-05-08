// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using Nethermind.State.Flat.Storage;
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
        new(Path.Combine(_testDir, "arenas"), pageCacheBytes, maxArenaSize: 64 * 1024);

    [Test]
    public void DisabledTracker_NoQueueOrDrain_QueueEvictionIsNoOp()
    {
        using ArenaManager manager = NewManager(pageCacheBytes: 0);
        manager.PageTracker.MaxCapacity.Should().Be(0);
        // No exception, no counters move.
        manager.QueueEviction(0, 0);
        manager.EvictionsQueued.Should().Be(0);
        manager.EvictionsInlineFallback.Should().Be(0);
        manager.EvictionsDispatched.Should().Be(0);
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
        manager.EvictionsQueued.Should().Be(1);
        manager.EvictionsInlineFallback.Should().Be(0);
        manager.EvictionsDispatched.Should().Be(1);
        manager.EvictionsSkippedRetouched.Should().Be(0);
    }

    [Test]
    public void QueueEviction_SkipsDispatchWhenPageBackInTracker()
    {
        long budget = 1024L * Environment.SystemPageSize;
        using ArenaManager manager = NewManager(budget);

        // Pre-touch (42, 7) so ContainsPage returns true. The drain must skip the dispatch
        // and bump EvictionsSkippedRetouched instead of EvictionsDispatched.
        manager.PageTracker.TryTouch(42, 7, out _, out _);
        manager.PageTracker.ContainsPage(42, 7).Should().BeTrue();

        manager.QueueEviction(arenaId: 42, pageIdx: 7);
        WaitFor(() => manager.EvictionsSkippedRetouched == 1);
        manager.EvictionsDispatched.Should().Be(0);
    }

    [Test]
    public void Dispose_DrainsRemainingEntries()
    {
        long budget = 1024L * Environment.SystemPageSize;
        ArenaManager manager = NewManager(budget);

        const int batch = 16;
        for (int i = 0; i < batch; i++)
            manager.QueueEviction(arenaId: 42, pageIdx: i);

        manager.Dispose();
        // Every queued (or inline-fallback) eviction must have been resolved — either dispatched
        // or skipped — by the time Dispose returns.
        manager.EvictionsQueued.Should().Be(batch);
        (manager.EvictionsDispatched + manager.EvictionsSkippedRetouched).Should().Be(
            manager.EvictionsQueued + manager.EvictionsInlineFallback);
    }
}
