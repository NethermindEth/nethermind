// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Prometheus.Client;
using Prometheus.Client.Collectors;
using Prometheus.Client.MetricPusher;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class PrometheusPushGatewayMetricsReporter
    : IMetricsReporter
    , IDisposable
{
    private readonly string _endpoint;
    private readonly MetricPusher _pusher;
    private readonly MetricPushServer _server;

    private readonly ICounter _messageCounter;
    private readonly ICounter _succeededCounter;
    private readonly ICounter _failedCounter;
    private readonly ICounter _ignoredCounter;
    private readonly ICounter _responseCounter;
    private readonly IMetricFamily<IHistogram, ValueTuple<string>> _singleDuration;
    private readonly IHistogram _batchDuration;

    public PrometheusPushGatewayMetricsReporter(string endpoint)
    {
        var registry = new CollectorRegistry();
        var factory = new MetricFactory(registry);

        _messageCounter = factory.CreateCounter("messages", "");
        _succeededCounter = factory.CreateCounter("succeeded", "");
        _failedCounter = factory.CreateCounter("failed", "");
        _ignoredCounter = factory.CreateCounter("ignored", "");
        _responseCounter = factory.CreateCounter("responses", "");
        _singleDuration = factory.CreateHistogram("single_duration", "", labelName: "method_name");
        _batchDuration = factory.CreateHistogram("batch_duration", "");

        _endpoint = endpoint;
        _pusher = new MetricPusher(new MetricPusherOptions
        {
            CollectorRegistry = registry,
            Endpoint = _endpoint,
            Instance = "kute",
            Job = $"{Guid.NewGuid()}",
        });

        _server = new MetricPushServer(_pusher);
        _server.Start();
    }

    public async Task Message(CancellationToken token = default)
    {
        _messageCounter.Inc();
        await _pusher.PushAsync();
    }

    public Task Response(CancellationToken token = default)
    {
        _responseCounter.Inc();
        return _pusher.PushAsync();
    }

    public Task Succeeded(CancellationToken token = default)
    {
        _succeededCounter.Inc();
        return _pusher.PushAsync();
    }

    public Task Failed(CancellationToken token = default)
    {
        _failedCounter.Inc();
        return _pusher.PushAsync();
    }

    public Task Ignored(CancellationToken token = default)
    {
        _ignoredCounter.Inc();
        return _pusher.PushAsync();
    }

    public async Task Batch(int requestId, TimeSpan elapsed, CancellationToken token = default)
    {
        _batchDuration.Observe(elapsed.TotalSeconds);
        await _pusher.PushAsync();
    }

    public Task Single(int requestId, TimeSpan elapsed, CancellationToken token = default)
    {
        _singleDuration
            .WithLabels(requestId.ToString())
            .Observe(elapsed.TotalSeconds);
        return _pusher.PushAsync();
    }

    public void Dispose()
    {
        _server.Stop();
    }
}
