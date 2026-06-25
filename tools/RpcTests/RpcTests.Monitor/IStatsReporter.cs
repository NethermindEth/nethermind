// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.RpcTests.Monitor;

internal record MonitorStats
{
    public required DateTime Since { get; init; }
    public required long HeadUpdates { get; init; }
    public required long Reorgs { get; init; }
    public required long TestRuns { get; init; }
    public required long RequestRuns { get; init; }
    public required long TestFailures { get; init; }
    public required long Errors { get; init; }

    public IReadOnlyList<ReorgEntry> RecentReorgs { get; init; } = [];
}

internal interface IStatsReporter
{
    void RecordHeadUpdate();
    void RecordReorg();
    void RecordTestRun();
    void RecordRequestRun(int count);
    void RecordTestFailure();
    void RecordError();

    Task RunAsync(CancellationToken ct);
}

internal sealed class NullStatsReporter : IStatsReporter
{
    public static readonly NullStatsReporter Instance = new();

    public void RecordHeadUpdate() { }
    public void RecordTestRun() { }
    public void RecordRequestRun(int count) { }
    public void RecordTestFailure() { }
    public void RecordError() { }
    public void RecordReorg() { }

    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}
