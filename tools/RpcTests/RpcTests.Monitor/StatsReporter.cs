// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal class StatsReporter(INotifier notifier, TimeSpan reportAt, ReorgTracker reorgTracker, EmptyTestsTracker emptyTests) : IStatsReporter
{
    private static readonly TimeSpan ReorgsPeriod = TimeSpan.FromDays(1);

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
    private long _headUpdates;
    private long _reorgs;
    private long _testRuns;
    private long _targetRequests;
    private long _referenceRequests;
    private long _testFailures;
    private long _errors;

    public void RecordHeadUpdate() => Interlocked.Increment(ref _headUpdates);
    public void RecordTestRun() => Interlocked.Increment(ref _testRuns);
    public void RecordTargetRequest() => Interlocked.Increment(ref _targetRequests);
    public void RecordReferenceRequest() => Interlocked.Increment(ref _referenceRequests);
    public void RecordTestFailure() => Interlocked.Increment(ref _testFailures);
    public void RecordError() => Interlocked.Increment(ref _errors);
    public void RecordReorg() => Interlocked.Increment(ref _reorgs);

    private MonitorStats GetAndReset() => new()
    {
        Since = FromUnix(Interlocked.Exchange(ref _since, UnixNow())),
        HeadUpdates = Interlocked.Exchange(ref _headUpdates, 0),
        Reorgs = Interlocked.Exchange(ref _reorgs, 0),
        TestRuns = Interlocked.Exchange(ref _testRuns, 0),
        TargetRequests = Interlocked.Exchange(ref _targetRequests, 0),
        ReferenceRequests = Interlocked.Exchange(ref _referenceRequests, 0),
        TestFailures = Interlocked.Exchange(ref _testFailures, 0),
        Errors = Interlocked.Exchange(ref _errors, 0)
    };

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DelayUntilNext(reportAt), ct);
                    MonitorStats stats = GetAndReset();

                    stats = stats with
                    {
                        RecentReorgs = reorgTracker.GetReorgs(ReorgsPeriod),
                        EmptyTests = emptyTests.GetTestIdsEmptySince(stats.Since)
                    };

                    await notifier.NotifyStatsAsync(stats, ct);
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
