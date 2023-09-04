// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Timer;

namespace Nethermind.Tools.Kute;

public class Metrics
{
    private readonly IMetrics _metrics;

    private readonly TimerOptions _totalRunningTime = new()
    {
        Name = "Total Running Time", DurationUnit = TimeUnit.Milliseconds,
    };
    private readonly CounterOptions _messages = new()
    {
        Name = "Messages", MeasurementUnit = Unit.Items,
    };
    private readonly CounterOptions _failed = new()
    {
        Name = "Failed", MeasurementUnit = Unit.Items,
    };
    private readonly CounterOptions _ignoredRequests = new()
    {
        Name = "Ignored Requests", MeasurementUnit = Unit.Items
    };
    private readonly CounterOptions _responses = new()
    {
        Name = "Responses", MeasurementUnit = Unit.Items
    };
    private readonly TimerOptions _batches = new()
    {
        Name = "Batches", DurationUnit = TimeUnit.Milliseconds
    };
    private readonly IDictionary<string, TimerOptions> _processedRequests = new Dictionary<string, TimerOptions>();

    public Metrics()
    {
        _metrics = new MetricsBuilder().Build();
    }

    public MetricsDataValueSource Snapshot => _metrics.Snapshot.Get();

    public void TickMessages() => _metrics.Measure.Counter.Increment(_messages);
    public void TickFailed() => _metrics.Measure.Counter.Increment(_failed);
    public void TickIgnoredRequests() => _metrics.Measure.Counter.Increment(_ignoredRequests);
    public void TickResponses() => _metrics.Measure.Counter.Increment(_responses);

    public TimerContext TimeTotal() => _metrics.Measure.Timer.Time(_totalRunningTime);
    public TimerContext TimeBatch() => _metrics.Measure.Timer.Time(_batches);
    public TimerContext TimeMethod(string methodName)
    {
        if (!_processedRequests.ContainsKey(methodName))
        {
            _processedRequests[methodName] = new TimerOptions
            {
                Name = methodName, MeasurementUnit = Unit.Requests, DurationUnit = TimeUnit.Milliseconds, RateUnit = TimeUnit.Milliseconds
            };
        }
        return _metrics.Measure.Timer.Time(_processedRequests[methodName]);
    }
}
