// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class ConsoleProgressReporter : IMetricsReporter
{
    private readonly string _suffix;

    private int _messageCount = 1;

    public ConsoleProgressReporter(int total)
    {
        _suffix = $"/{total}";
    }

    public Task Message()
    {
        if (_messageCount == 1)
        {
            Console.Write($"Progress:  1{_suffix}");
        }

        var sb = new StringBuilder();

        sb.Append('\b', _messageCount.ToString().Length + _suffix.Length);
        sb.Append(_messageCount);
        sb.Append(_suffix);

        Console.Write(sb);
        Interlocked.Increment(ref _messageCount);

        return Task.CompletedTask;
    }

    public Task Total()
    {
        Console.Write('\n');
        return Task.CompletedTask;
    }
}
