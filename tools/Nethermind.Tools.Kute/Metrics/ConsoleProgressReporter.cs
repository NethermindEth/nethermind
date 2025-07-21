// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class ConsoleProgressReporter : IMetricsReporter
{
    private readonly string _suffix;
    private readonly SemaphoreSlim _semaphore = new(1);

    private int _messageCount = 1;

    public ConsoleProgressReporter(int total)
    {
        _suffix = $"/{total}";
    }

    public async Task Message(CancellationToken token = default)
    {
        try
        {
            await _semaphore.WaitAsync(token);

            if (_messageCount == 1)
            {
                Console.Write($"Progress: 1{_suffix}");
            }

            var sb = new StringBuilder();

            sb.Append('\b', (_messageCount - 1).ToString().Length + _suffix.Length);
            sb.Append(_messageCount);
            sb.Append(_suffix);

            Console.Write(sb);
            _messageCount++;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        Console.Write('\n');
        return Task.CompletedTask;
    }
}
