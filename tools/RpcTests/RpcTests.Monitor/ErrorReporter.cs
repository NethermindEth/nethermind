// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal class ErrorReporter(INotifier notifier, IStatsReporter stats)
{
    public void Report(string error, Exception ex)
    {
        stats.RecordError();

        Console.Error.WriteLine($"{error}: {ex}");
        _ = notifier.NotifyErrorAsync(error, ex);
    }
}
