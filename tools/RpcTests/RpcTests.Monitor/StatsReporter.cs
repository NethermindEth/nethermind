// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal class StatsReporter(INotifier notifier, TimeSpan reportAt) : IStatsReporter
{
    private static TimeSpan DelayUntilNext(TimeSpan timeOfDay)
    {
        DateTime now = DateTime.UtcNow;
        DateTime next = now.Date.Add(timeOfDay);
        if (now >= next)
            next = next.AddDays(1);
        return next - now;
    }

    private static ulong UnixNow() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static DateTime FromUnix(ulong seconds) => DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime;

    private ulong _since = UnixNow();
    private long _testRuns;
    private long _requestRuns;
    private long _testFailures;
    private long _errors;

    public void RecordTestRun() => Interlocked.Increment(ref _testRuns);
    public void RecordRequestRun() => Interlocked.Increment(ref _requestRuns);
    public void RecordTestFailure() => Interlocked.Increment(ref _testFailures);
    public void RecordError() => Interlocked.Increment(ref _errors);

    private MonitorStats GetAndReset() => new(
        FromUnix(Interlocked.Exchange(ref _since, UnixNow())),
        Interlocked.Exchange(ref _testRuns, 0),
        Interlocked.Exchange(ref _requestRuns, 0),
        Interlocked.Exchange(ref _testFailures, 0),
        Interlocked.Exchange(ref _errors, 0)
    );

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DelayUntilNext(reportAt), ct);
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
