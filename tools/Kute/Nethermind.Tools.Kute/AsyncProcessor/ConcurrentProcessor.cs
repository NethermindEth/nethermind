// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.AsyncProcessor;

public sealed class ConcurrentProcessor : IAsyncProcessor
{
    private readonly int _maxDegreeOfParallelism;

    public ConcurrentProcessor(int maxDegreeOfParallelism)
    {
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    public async Task Process<T>(IAsyncEnumerable<T> source, Func<T, Task> process, CancellationToken token = default)
    {
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(_maxDegreeOfParallelism);
        await foreach (var t in source)
        {
            await semaphore.WaitAsync(token);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await process(t);
                }
                finally
                {
                    semaphore.Release();
                }
            }, token));
        }
        await Task.WhenAll(tasks);
    }
}
