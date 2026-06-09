// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal class MonitorStats(INotifier notifier, TimeSpan period) : IMonitorStats
{
    private long _testRuns;
    private long _requestRuns;
    private long _testFailures;
    private long _errors;
    private readonly DateTime _since = DateTime.UtcNow;

    public void RecordTestRun() => Interlocked.Increment(ref _testRuns);
    public void RecordRequestRun() => Interlocked.Increment(ref _requestRuns);
    public void RecordTestFailure() => Interlocked.Increment(ref _testFailures);
    public void RecordError() => Interlocked.Increment(ref _errors);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, ct);
                    await notifier.NotifyInfoAsync(
                        $"""
                          *RPC Monitor statistic since `{_since:u}`*:
                          - `{Interlocked.Exchange(ref _testRuns, 0)}` tests executed ()
                          - `{Interlocked.Exchange(ref _requestRuns, 0)}` requests sent
                          - `{Interlocked.Exchange(ref _testFailures, 0)}` test failed
                          - `{Interlocked.Exchange(ref _errors, 0)}` errors occured
                         """);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"Monitor stats error: {ex}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
