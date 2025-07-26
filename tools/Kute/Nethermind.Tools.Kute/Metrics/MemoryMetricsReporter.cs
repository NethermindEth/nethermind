// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class MemoryMetricsReporter
    : IMetricsReporter
    , IMetricsReportProvider
{
    private readonly ConcurrentDictionary<int, TimeSpan> _singles = new();
    private readonly ConcurrentDictionary<int, TimeSpan> _batches = new();

    private TimeSpan _totalRunningTime;
    private long _messages;
    private long _failed;
    private long _succeeded;
    private long _ignored;
    private long _responses;

    public Task Message(CancellationToken token = default) => Task.FromResult(Interlocked.Increment(ref _messages));
    public Task Response(CancellationToken token = default) => Task.FromResult(Interlocked.Increment(ref _responses));
    public Task Succeeded(CancellationToken token = default) => Task.FromResult(Interlocked.Increment(ref _succeeded));
    public Task Failed(CancellationToken token = default) => Task.FromResult(Interlocked.Increment(ref _failed));
    public Task Ignored(CancellationToken token = default) => Task.FromResult(Interlocked.Increment(ref _ignored));
    public Task Batch(int requestId, TimeSpan elapsed, CancellationToken token = default)
    {
        _batches[requestId] = elapsed;
        return Task.CompletedTask;
    }
    public Task Single(int requestId, TimeSpan elapsed, CancellationToken token = default)
    {
        _singles[requestId] = elapsed;
        return Task.CompletedTask;
    }
    public Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        _totalRunningTime = elapsed;
        return Task.CompletedTask;
    }

    public MetricsReport Report()
    {
        return new MetricsReport
        {
            TotalMessages = _messages,
            Failed = _failed,
            Succeeded = _succeeded,
            Ignored = _ignored,
            Responses = _responses,
            TotalTime = _totalRunningTime,
            Singles = _singles.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Batches = _batches.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };
    }
}
