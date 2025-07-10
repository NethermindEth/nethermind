// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Timer;
using Nethermind.Tools.Kute.Extensions;
using System.Collections.Concurrent;

namespace Nethermind.Tools.Kute;

public class Metrics
{
    private readonly IMetrics _metrics;
    private readonly MetricTags _defaultMetricTags;

    private readonly TimerOptions _totalRunningTime;
    private readonly CounterOptions _messages;
    private readonly CounterOptions _failed;
    private readonly CounterOptions _succeeded;
    private readonly CounterOptions _ignoredRequests;
    private readonly CounterOptions _responses;
    private readonly TimerOptions _batches;
    private readonly ConcurrentDictionary<string, TimerOptions> _processedRequests = new ConcurrentDictionary<string, TimerOptions>();

    public Metrics(
        IEnumerable<(string, string)>? tags = null
    )
    {
        _metrics = new MetricsBuilder()
            .SampleWith.Reservoir<CompleteReservoir>()
            .Build();
        _defaultMetricTags = tags is null ? MetricTags.Empty : new MetricTags(
            tags.Select(t => t.Item1).ToArray(),
            tags.Select(t => t.Item2).ToArray()
        );

        _totalRunningTime = new()
        {
            Name = "Total Running Time",
            DurationUnit = TimeUnit.Milliseconds,
            Tags = _defaultMetricTags
        };
        _messages = new()
        {
            Name = "Messages",
            MeasurementUnit = Unit.Items,
            Tags = _defaultMetricTags
        };
        _failed = new()
        {
            Name = "Failed",
            MeasurementUnit = Unit.Items,
            Tags = _defaultMetricTags
        };
        _succeeded = new()
        {
            Name = "Succeeded",
            MeasurementUnit = Unit.Items,
            Tags = _defaultMetricTags
        };
        _ignoredRequests = new()
        {
            Name = "Ignored Requests",
            MeasurementUnit = Unit.Items,
            Tags = _defaultMetricTags
        };
        _responses = new()
        {
            Name = "Responses",
            MeasurementUnit = Unit.Items,
            Tags = _defaultMetricTags
        };
        _batches = new()
        {
            Name = "Batches",
            DurationUnit = TimeUnit.Milliseconds,
            Tags = _defaultMetricTags
        };
    }

    public MetricsDataValueSource Snapshot => _metrics.Snapshot.Get();

    public void TickMessages() => _metrics.Measure.Counter.Increment(_messages);
    public void TickFailed() => _metrics.Measure.Counter.Increment(_failed);
    public void TickSucceeded() => _metrics.Measure.Counter.Increment(_succeeded);
    public void TickIgnoredRequests() => _metrics.Measure.Counter.Increment(_ignoredRequests);
    public void TickResponses() => _metrics.Measure.Counter.Increment(_responses);

    public TimerContext TimeTotal() => _metrics.Measure.Timer.Time(_totalRunningTime);
    public TimerContext TimeBatch() => _metrics.Measure.Timer.Time(_batches);
    public TimerContext TimeMethod(string methodName)
    {
        var timerOptions = _processedRequests.GetOrAdd(methodName, new TimerOptions
        {
            Name = methodName,
            MeasurementUnit = Unit.Requests,
            DurationUnit = TimeUnit.Milliseconds,
            RateUnit = TimeUnit.Milliseconds,
            Tags = _defaultMetricTags
        });

        return _metrics.Measure.Timer.Time(timerOptions);
    }
}
