// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class ConsoleProgressReporter : IMetricsReporter
{
    private readonly SemaphoreSlim _semaphore;
    private int _messageCount = 1;

    public ConsoleProgressReporter()
    {
        _semaphore = new SemaphoreSlim(1);
    }

    public async Task Message(CancellationToken token = default)
    {
        try
        {
            await _semaphore.WaitAsync(token);

            if (_messageCount == 1)
            {
                Console.Error.Write($"Progress: 1");
            }

            var sb = new StringBuilder();

            sb.Append('\b', (_messageCount - 1).ToString().Length);
            sb.Append(_messageCount);

            Console.Error.Write(sb);
            _messageCount++;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        Console.Error.Write('\n');
        return Task.CompletedTask;
    }
}
