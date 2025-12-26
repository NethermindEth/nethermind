// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Tools.Kute.AsyncProcessor;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class AsyncProcessorTests
{
    [Test]
    public async Task SequentialProcessor_SequentialTasks()
    {
        var processor = new SequentialProcessor();
        var taskCount = 4;
        var source = Enumerable.Range(1, taskCount).ToAsyncEnumerable();

        var counter = 0;
        var t = new Timer();
        using (t.Time())
        {
            await processor.Process(source, async (item) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
                Interlocked.Increment(ref counter);
            });
        }

        counter.Should().Be(taskCount);
        t.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(90));
        t.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(110));
    }

    [Test]
    public async Task ConcurrentProcessor_ConcurrentTasks()
    {
        var processor = new ConcurrentProcessor(maxDegreeOfParallelism: 5);
        var taskCount = 10;
        var source = Enumerable.Range(1, taskCount).ToAsyncEnumerable();

        var counter = 0;
        var t = new Timer();
        using (t.Time())
        {
            await processor.Process(source, async (item) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                Interlocked.Increment(ref counter);
            });
        }

        counter.Should().Be(taskCount);
        t.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(90));
        t.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(110));
    }
}
