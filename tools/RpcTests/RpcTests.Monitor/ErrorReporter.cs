// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal class ErrorReporter(INotifier notifier, IMonitorStats stats)
{
    public void Report(string details, Exception ex) => Report($"{details}: {ex}");

    public void Report(string error)
    {
        stats.RecordError();
        Console.WriteLine(error);
        _ = notifier.NotifyErrorAsync(error);
    }
}
