// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal class StatsReporter(INotifier notifier, TimeSpan period) : IStatsReporter
{
    private DateTime _since = DateTime.UtcNow;
    private long _testRuns;
    private long _requestRuns;
    private long _testFailures;
    private long _errors;

    public void RecordTestRun() => Interlocked.Increment(ref _testRuns);
    public void RecordRequestRun() => Interlocked.Increment(ref _requestRuns);
    public void RecordTestFailure() => Interlocked.Increment(ref _testFailures);
    public void RecordError() => Interlocked.Increment(ref _errors);

    private MonitorStats GetAndReset()
    {
        DateTime since = _since;
        _since = DateTime.UtcNow;

        return new MonitorStats(since,
            Interlocked.Exchange(ref _testRuns, 0),
            Interlocked.Exchange(ref _requestRuns, 0),
            Interlocked.Exchange(ref _testFailures, 0),
            Interlocked.Exchange(ref _errors, 0)
        );
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(period, ct);
                    await notifier.NotifyStatsAsync(GetAndReset());
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
