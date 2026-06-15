// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor;

internal record MonitorStats(DateTime Since, long TestRuns, long RequestRuns, long TestFailures, long Errors) { }

internal interface IStatsReporter
{
    void RecordTestRun();
    void RecordRequestRun();
    void RecordTestFailure();
    void RecordError();

    Task RunAsync(CancellationToken ct);
}

internal sealed class NullStatsReporter : IStatsReporter
{
    public static readonly NullStatsReporter Instance = new();

    public void RecordTestRun() { }
    public void RecordRequestRun() { }
    public void RecordTestFailure() { }
    public void RecordError() { }

    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}
