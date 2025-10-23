// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Text;
using Prometheus.Client;
using Prometheus.Client.Collectors;
using Prometheus.Client.MetricPusher;

namespace Nethermind.Tools.Kute.Metrics;

public sealed class PrometheusPushGatewayMetricsReporter : IMetricsReporter
{
    private const string JobName = "kute";

    private readonly MetricPusher _pusher;
    private readonly MetricPushServer _server;

    private readonly ICounter _messageCounter;
    private readonly ICounter _succeededCounter;
    private readonly ICounter _failedCounter;
    private readonly ICounter _ignoredCounter;
    private readonly ICounter _responseCounter;
    private readonly IMetricFamily<IHistogram> _singleDuration;
    private readonly IMetricFamily<IHistogram> _batchDuration;

    public PrometheusPushGatewayMetricsReporter(
        string endpoint,
        Dictionary<string, string> labels,
        string? user,
        string? password)
    {
        var registry = new CollectorRegistry();
        var factory = new MetricFactory(registry);

        _messageCounter = factory.CreateCounter(GetMetricName("messages_total"), "");
        _succeededCounter = factory.CreateCounter(GetMetricName("messages_succeeded"), "");
        _failedCounter = factory.CreateCounter(GetMetricName("messages_failed"), "");
        _ignoredCounter = factory.CreateCounter(GetMetricName("messages_ignored"), "");
        _responseCounter = factory.CreateCounter(GetMetricName("responses_total"), "");
        _singleDuration = factory.CreateHistogram(GetMetricName("single_duration_seconds"), "", labelNames: ["jsonrpc_id", "method"]);
        _batchDuration = factory.CreateHistogram(GetMetricName("batch_duration_seconds"), "", labelNames: ["jsonrpc_id"]);

        string instanceLabel = labels.TryGetValue("instance", out var instance) ? instance : Guid.NewGuid().ToString();
        labels.Remove("instance");

        var httpClient = new HttpClient();
        if (user is not null && password is not null)
        {
            var authParameter = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authParameter);
        }

        _pusher = new MetricPusher(new MetricPusherOptions
        {
            CollectorRegistry = registry,
            Endpoint = endpoint,
            Job = JobName,
            Instance = instanceLabel,
            AdditionalLabels = labels,
            HttpClient = httpClient,
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
            .WithLabels(single.Id, single.MethodName)
            .Observe(elapsed.TotalSeconds);
        return Task.CompletedTask;
    }

    public async Task Total(TimeSpan elapsed, CancellationToken token = default)
    {
        await _pusher.PushAsync();
        _server.Stop();
    }

    private static string GetMetricName(string name)
    {
        var lowerName = name.ToLowerInvariant();
        var sanitizedName = lowerName.Replace(" ", "_").Replace("-", "_");
        return $"{JobName}_{sanitizedName}";
    }
}
