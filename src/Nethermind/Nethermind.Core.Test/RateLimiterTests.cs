// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[Parallelizable(ParallelScope.Self)]
public class RateLimiterTests
{
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
        // Task.Delay truncates to whole milliseconds, and timers run on a clock that moves in 4 ms steps on Linux with HZ=250,
        // so a caller can wake up to 5 ms before its slot.
        TimeSpan backlogLimit = TimeSpan.FromSeconds((double)(concurrency + 1) / eventPerSec) + TimeSpan.FromMilliseconds(5);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(effectivePerSec, Is.LessThanOrEqualTo((int)(eventPerSec * 1.5)));
            Assert.That(maxBacklog.Max(), Is.LessThanOrEqualTo(backlogLimit));
            // The backlog does not show how long Task.Delay sleeps. The last wait ends a few intervals after the deadline,
            // so this loose bound catches a gross over-sleep but not a runner stall.
            Assert.That(elapsedMs, Is.LessThan(durationMs + 1000));
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
}
