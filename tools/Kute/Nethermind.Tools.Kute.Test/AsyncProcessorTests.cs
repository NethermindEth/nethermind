// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.AsyncProcessor;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class AsyncProcessorTests
{
    [Test]
    public async Task SequentialProcessor_SequentialTasks()
    {
        SequentialProcessor processor = new();
        int taskCount = 4;
        IAsyncEnumerable<int> source = Enumerable.Range(1, taskCount).ToAsyncEnumerable();

        int counter = 0;
        Timer t = new();
        using (t.Time())
        {
            await processor.Process(source, async (item) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
                Interlocked.Increment(ref counter);
            });
        }

        Assert.That(counter, Is.EqualTo(taskCount));
        Assert.That(t.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(90)));
        Assert.That(t.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(110)));
    }

    [Test]
    public async Task ConcurrentProcessor_ConcurrentTasks()
    {
        ConcurrentProcessor processor = new(maxDegreeOfParallelism: 5);
        int taskCount = 10;
        IAsyncEnumerable<int> source = Enumerable.Range(1, taskCount).ToAsyncEnumerable();

        int counter = 0;
        Timer t = new();
        using (t.Time())
        {
            await processor.Process(source, async (item) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                Interlocked.Increment(ref counter);
            });
        }

        Assert.That(counter, Is.EqualTo(taskCount));
        Assert.That(t.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(90)));
        Assert.That(t.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(110)));
    }
}
