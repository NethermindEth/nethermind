// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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

        Task[] tasks = Enumerable.Range(0, concurrency).Select(async (_) =>
        {
            while (Environment.TickCount64 < deadline)
            {
                Interlocked.Increment(ref counter);
                await rateLimiter.WaitAsync(CancellationToken.None);
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        int effectivePerSec = (int)(counter / ((Environment.TickCount64 - startTime) / 1000.0));
        effectivePerSec.Should().BeInRange((int)(eventPerSec * 0.5), (int)(eventPerSec * 1.1));
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
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task RateLimiter_should_return_true_on_is_throttled_if_throttled()
    {
        RateLimiter rateLimiter = new(1);
        await rateLimiter.WaitAsync(CancellationToken.None);
        rateLimiter.IsThrottled().Should().BeTrue();
    }
}
