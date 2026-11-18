// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Test.Threading;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[Parallelizable(ParallelScope.Self)]
public class RateLimiterTests
{
    [Test]
    public void Refused_reservations_do_not_consume_future_slots()
    {
        RateLimiter limiter = new(1);
        using IDisposable assertions = Assert.EnterMultipleScope();
        long now = TimeProvider.System.GetTimestamp();
        Assert.That(limiter.TryReserve(now, TimeSpan.Zero, out TimeSpan delay), Is.True);
        Assert.That(delay, Is.EqualTo(TimeSpan.Zero));
        for (int i = 0; i < 100; i++)
        {
            Assert.That(limiter.TryReserve(now, TimeSpan.Zero, out _), Is.False);
        }
        Assert.That(limiter.TryReserve(now + Stopwatch.Frequency, TimeSpan.Zero, out delay), Is.True);
        Assert.That(delay, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Idle_time_does_not_accumulate_burst_credit()
    {
        RateLimiter limiter = new(1);
        using IDisposable assertions = Assert.EnterMultipleScope();
        long now = TimeProvider.System.GetTimestamp() + 100 * Stopwatch.Frequency;
        Assert.That(limiter.TryReserve(now, TimeSpan.Zero, out _), Is.True);
        Assert.That(limiter.TryReserve(now, TimeSpan.Zero, out _), Is.False);
    }

    [Test]
    public void Concurrent_reservations_have_a_bounded_backlog()
    {
        RateLimiter limiter = new(10);
        using IDisposable assertions = Assert.EnterMultipleScope();
        long now = TimeProvider.System.GetTimestamp();
        int accepted = 0;
        Parallel.For(0, 1000, index =>
        {
            if (limiter.TryReserve(now, TimeSpan.FromSeconds(1), out _)) Interlocked.Increment(ref accepted);
        });
        Assert.That(accepted, Is.EqualTo(11));
    }

    [Test]
    public async Task Cancelling_reserved_delays_cannot_create_unbounded_debt()
    {
        RateLimiter limiter = new(1);
        using IDisposable assertions = Assert.EnterMultipleScope();
        long now = TimeProvider.System.GetTimestamp();
        int accepted = 0;
        for (int i = 0; i < 100; i++)
        {
            if (!limiter.TryReserve(now, TimeSpan.FromSeconds(1), out TimeSpan delay)) continue;
            accepted++;
            using CancellationTokenSource cancellation = new();
            Task wait = Task.Delay(delay, cancellation.Token);
            cancellation.Cancel();
            await wait.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        Assert.That(accepted, Is.EqualTo(2));
        Assert.That(limiter.TryReserve(now + 2 * Stopwatch.Frequency, TimeSpan.Zero, out _), Is.True);
    }

    [TestCase(100, 1, 1000)]
    [TestCase(100, 1, 100)]
    [TestCase(1000, 1, 100)]
    [TestCase(100, 4, 1000)]
    [TestCase(100, 4, 100)]
    [TestCase(1000, 4, 100)]
    public async Task RateLimiter_should_delay_wait_to_rate_limit(int eventPerSec, int concurrency, int durationMs)
    {
        RateLimiter rateLimiter = new(eventPerSec);

        long startTime = Environment.TickCount64;
        long deadline = startTime + durationMs;
        long counter = 0;
        TimeSpan[] maxBacklog = new TimeSpan[concurrency];

        Task[] tasks = Enumerable.Range(0, concurrency).Select(async caller =>
        {
            while (Environment.TickCount64 < deadline)
            {
                Interlocked.Increment(ref counter);
                Task wait = rateLimiter.WaitAsync(CancellationToken.None);
                TimeSpan backlog = rateLimiter.Backlog;
                if (backlog > maxBacklog[caller]) maxBacklog[caller] = backlog;
                await wait;
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        long elapsedMs = Environment.TickCount64 - startTime;
        int effectivePerSec = (int)(counter / (elapsedMs / 1000.0));
        // A stalled runner lowers the rate, so only the backlog shows that the limiter does not over-delay:
        // each caller holds at most one slot, and the backlog includes the slot the reader just took.
        // Task.Delay truncates to whole milliseconds, and timers run on a coarse clock (4 ms steps on Linux with HZ=250,
        // 15.6 ms on Windows), so a caller can wake up to one clock step plus late clock updates before its slot.
        TimeSpan backlogLimit = TimeSpan.FromSeconds((double)(concurrency + 1) / eventPerSec) + TimeSpan.FromMilliseconds(OperatingSystem.IsWindows() ? 17 : 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(effectivePerSec, Is.LessThanOrEqualTo((int)(eventPerSec * 1.5)));
            Assert.That(maxBacklog.Max(), Is.LessThanOrEqualTo(backlogLimit));
            // The backlog does not show how long Task.Delay sleeps. The last wait ends a few intervals after the deadline,
            // so this loose bound catches a gross over-sleep but not a runner stall.
            Assert.That(elapsedMs, Is.LessThan(durationMs + 2000));
        }
    }

    [Test]
    public async Task RateLimiter_should_throw_when_cancelled()
    {
        RateLimiter rateLimiter = new(1);
        await rateLimiter.WaitAsync(CancellationToken.None);
        CancellationTokenSource cts = new();
        Task waitTask = rateLimiter.WaitAsync(cts.Token);
        cts.Cancel();

        Func<Task> act = async () => await waitTask;
        Assert.That(async () => await act(), Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task RateLimiter_should_return_true_on_is_throttled_if_throttled()
    {
        RateLimiter rateLimiter = new(1);
        await rateLimiter.WaitAsync(CancellationToken.None);
        Assert.That(rateLimiter.IsThrottled(), Is.True);
    }

    [Test]
    public async Task Idle_time_does_not_bank_a_burst()
    {
        RateLimiter rateLimiter = new(10);
        await Task.Delay(500);

        // Only the first call is free after an idle period; the next one is delayed by a full interval.
        Assert.That(rateLimiter.WaitAsync(CancellationToken.None).IsCompleted, Is.True);
        Assert.That(rateLimiter.WaitAsync(CancellationToken.None).IsCompleted, Is.False);
    }

    [Test]
    public void Backlog_reports_the_delay_of_the_next_caller()
    {
        RateLimiter rateLimiter = new(10);
        Assert.That(rateLimiter.Backlog, Is.EqualTo(TimeSpan.Zero));

        for (int i = 0; i < 11; i++)
        {
            _ = rateLimiter.WaitAsync(CancellationToken.None);
        }

        Assert.That(rateLimiter.Backlog, Is.InRange(TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1100)));
    }

    [Test]
    public void Rate_below_one_event_per_second_spaces_the_slots_by_the_whole_interval([Values(0.125, 0.5, 0.75)] double eventsPerSecond)
    {
        ManualTimeProvider time = new();
        RateLimiter rateLimiter = new(eventsPerSecond, time);

        Task first = rateLimiter.WaitAsync(CancellationToken.None);
        Task second = rateLimiter.WaitAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IsCompleted, Is.True);
            Assert.That(second.IsCompleted, Is.False);
            Assert.That(rateLimiter.Backlog, Is.EqualTo(TimeSpan.FromSeconds(2 / eventsPerSecond)).Within(TimeSpan.FromMilliseconds(1)));
        }
    }

    [Test]
    public void Slots_follow_the_time_provider()
    {
        ManualTimeProvider time = new();
        RateLimiter rateLimiter = new(1, time);
        _ = rateLimiter.WaitAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMilliseconds(400));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rateLimiter.Backlog, Is.EqualTo(TimeSpan.FromMilliseconds(600)).Within(TimeSpan.FromMilliseconds(1)));
            time.Advance(TimeSpan.FromMilliseconds(600));
            Assert.That(rateLimiter.IsThrottled(), Is.False);
        }
    }
}
