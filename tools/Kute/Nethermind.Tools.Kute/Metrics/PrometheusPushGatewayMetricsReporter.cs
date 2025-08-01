// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Prometheus.Client;
using Prometheus.Client.Collectors;
using Prometheus.Client.MetricPusher;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class PrometheusPushGatewayMetricsReporter : IMetricsReporter
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
    private readonly IMetricFamily<IHistogram, ValueTuple<string>> _batchDuration;

    public PrometheusPushGatewayMetricsReporter(
        string endpoint,
        Dictionary<string, string> labels
    )
    {
        var registry = new CollectorRegistry();
        var factory = new MetricFactory(registry);

        _messageCounter = factory.CreateCounter("messages", "");
        _succeededCounter = factory.CreateCounter("succeeded", "");
        _failedCounter = factory.CreateCounter("failed", "");
        _ignoredCounter = factory.CreateCounter("ignored", "");
        _responseCounter = factory.CreateCounter("responses", "");
        _singleDuration = factory.CreateHistogram("single_duration", "", labelName: "jsonrpc_id");
        _batchDuration = factory.CreateHistogram("batch_duration", "", labelName: "jsonrpc_id");

        _endpoint = endpoint;
        string instanceLabel = labels.TryGetValue("instance", out var instance) ? instance : Guid.NewGuid().ToString();
        labels.Remove("instance");
        _pusher = new MetricPusher(new MetricPusherOptions
        {
            CollectorRegistry = registry,
            Endpoint = _endpoint,
            Job = "kute",
            Instance = instanceLabel,
            AdditionalLabels = labels,
        });

        _server = new MetricPushServer(_pusher);
        _server.Start();
    }

    public Task Message(CancellationToken token = default)
    {
        _messageCounter.Inc();
        return Task.CompletedTask;
    }

    public Task Response(CancellationToken token = default)
    {
        _responseCounter.Inc();
        return Task.CompletedTask;
    }

    public Task Succeeded(CancellationToken token = default)
    {
        _succeededCounter.Inc();
        return Task.CompletedTask;
    }

    public Task Failed(CancellationToken token = default)
    {
        _failedCounter.Inc();
        return Task.CompletedTask;
    }

    public Task Ignored(CancellationToken token = default)
    {
        _ignoredCounter.Inc();
        return Task.CompletedTask;
    }

    public Task Batch(JsonRpc.Request.Batch batch, TimeSpan elapsed, CancellationToken token = default)
    {
        _batchDuration
            .WithLabels(batch.Id)
            .Observe(elapsed.TotalSeconds);
        return Task.CompletedTask;
    }

    public Task Single(JsonRpc.Request.Single single, TimeSpan elapsed, CancellationToken token = default)
    {
        _singleDuration
            .WithLabels(single.Id)
            .Observe(elapsed.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        await _pusher.PushAsync();
        _server.Stop();
    }
}
