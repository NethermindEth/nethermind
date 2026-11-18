// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test.Threading;

/// <summary>
/// Verifies deterministic one-shot scheduling and explicit timer firing.
/// </summary>
public class ManualTimeProviderTests
{
    /// <summary>
    /// Checks one-shot deadlines and disposal when timers overlap.
    /// </summary>
    [Test]
    public void Due_timers_preserve_an_older_refresh_deadline_after_a_startup_retry([Values] bool disposed)
    {
        ManualTimeProvider clock = new();
        int refreshes = 0;
        int retries = 0;
        using ITimer refresh = clock.CreateTimer(_ => refreshes++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        using ITimer retry = clock.CreateTimer(_ => retries++, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        refresh.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        if (disposed) refresh.Dispose();

        clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshes, Is.Zero);
            Assert.That(retries, Is.Zero);
        }
        clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshes, Is.Zero);
            Assert.That(retries, Is.EqualTo(1));
        }
        clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(3));
        clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshes, Is.EqualTo(disposed ? 0 : 1));
            Assert.That(retries, Is.EqualTo(1));
        }
    }

    /// <summary>
    /// Checks that explicit firing invokes the latest timer before its deadline.
    /// </summary>
    [Test]
    public void Explicit_latest_timer_firing_keeps_its_original_before_deadline_contract()
    {
        ManualTimeProvider clock = new();
        int first = 0;
        int second = 0;
        using ITimer original = clock.CreateTimer(_ => first++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        using ITimer latest = clock.CreateTimer(_ => second++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Zero);
            Assert.That(second, Is.Zero);
        }
        clock.AdvanceAndFireTimer(TimeSpan.Zero);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.Zero);
            Assert.That(second, Is.EqualTo(1));
        }
    }

    [Test]
    public void Overdue_periodic_timer_fires_once_then_waits_a_full_period()
    {
        ManualTimeProvider clock = new();
        int callbacks = 0;
        TimeSpan period = TimeSpan.FromSeconds(1);
        using ITimer timer = clock.CreateTimer(_ => callbacks++, null, period, period);
        clock.Advance(TimeSpan.FromSeconds(10));
        clock.AdvanceAndFireDueTimers(TimeSpan.Zero);
        Assert.That(callbacks, Is.EqualTo(1));
        clock.AdvanceAndFireDueTimers(period);
        Assert.That(callbacks, Is.EqualTo(2));
    }

    [Test]
    public void Deadline_outside_the_clock_range_is_rejected()
    {
        ManualTimeProvider clock = new();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.CreateTimer(_ => { }, null, TimeSpan.MaxValue, Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Due_callbacks_follow_deadlines_and_include_nested_timers()
    {
        ManualTimeProvider clock = new();
        List<long> callbacks = [];
        ITimer? child = null;
        using ITimer later = clock.CreateTimer(_ => callbacks.Add(clock.GetTimestamp()), null, TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
        using ITimer earlier = clock.CreateTimer(_ =>
        {
            callbacks.Add(clock.GetTimestamp());
            child = clock.CreateTimer(_ => callbacks.Add(clock.GetTimestamp()), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        try
        {
            clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(5));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(callbacks, Is.EqualTo(new[] { TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(2).Ticks, TimeSpan.FromSeconds(4).Ticks }));
                Assert.That(clock.GetTimestamp(), Is.EqualTo(TimeSpan.FromSeconds(5).Ticks));
            }
        }
        finally { child?.Dispose(); }
    }

    [Test]
    public void Periodic_callbacks_respect_change_and_dispose([Values("repeat", "change", "dispose")] string action)
    {
        ManualTimeProvider clock = new();
        List<long> calls = [];
        ITimer? timer = null;
        timer = clock.CreateTimer(_ =>
        {
            calls.Add(clock.GetTimestamp());
            if (calls.Count != 1) return;
            if (action == "change") timer!.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
            if (action == "dispose") timer!.Dispose();
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        using (timer)
        {
            clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(2));
            Assert.That(calls, Is.EqualTo(action == "repeat" ? new[] { TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(2).Ticks } : new[] { TimeSpan.FromSeconds(1).Ticks }));
            if (action == "dispose") Assert.That(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan), Is.False);
            if (action == "repeat") timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(2));
            Assert.That(calls.Count, Is.EqualTo(action == "dispose" ? 1 : 2));
            if (action == "change") Assert.That(calls[1], Is.EqualTo(TimeSpan.FromSeconds(4).Ticks));
        }
    }

    [Test]
    public void Due_callbacks_never_rewind_plain_or_callback_advancement([Values] bool advanceInCallback)
    {
        ManualTimeProvider clock = new();
        long observed = 0;
        using ITimer timer = clock.CreateTimer(_ =>
        {
            observed = clock.GetTimestamp();
            if (advanceInCallback) clock.Advance(TimeSpan.FromSeconds(10));
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        if (!advanceInCallback) clock.Advance(TimeSpan.FromSeconds(3));
        clock.AdvanceAndFireDueTimers(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(observed, Is.EqualTo(TimeSpan.FromSeconds(advanceInCallback ? 1 : 3).Ticks));
            Assert.That(clock.GetTimestamp(), Is.EqualTo(TimeSpan.FromSeconds(advanceInCallback ? 11 : 5).Ticks));
            Assert.That(clock.GetUtcNow(), Is.EqualTo(DateTimeOffset.UnixEpoch.AddSeconds(advanceInCallback ? 11 : 5)));
        }
    }

    [Test]
    public void Disposed_latest_timer_does_not_force_an_older_timer()
    {
        ManualTimeProvider clock = new();
        int calls = 0;
        using ITimer older = clock.CreateTimer(_ => calls++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        using ITimer latest = clock.CreateTimer(_ => calls++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        latest.Dispose();
        clock.AdvanceAndFireTimer(TimeSpan.Zero);
        Assert.That(calls, Is.Zero);
    }

    [Test]
    public void Disposed_timer_releases_its_callback_state()
    {
        ManualTimeProvider clock = new();
        (WeakReference state, WeakReference timer) = CreateDisposedTimer(clock);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.IsAlive, Is.False);
            Assert.That(timer.IsAlive, Is.False);
        }
        GC.KeepAlive(clock);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference State, WeakReference Timer) CreateDisposedTimer(ManualTimeProvider clock)
    {
        object state = new();
        WeakReference reference = new(state);
        using ITimer timer = clock.CreateTimer(static _ => { }, state, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        return (reference, new WeakReference(timer));
    }
}
