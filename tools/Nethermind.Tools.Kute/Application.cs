// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json;
using Nethermind.Tools.Kute.JsonRpcMessageProvider;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.MetricsConsumer;

namespace Nethermind.Tools.Kute;

class Application
{
    private readonly Metrics _metrics = new();

    private readonly IJsonRpcMessageProvider _msgProvider;
    private readonly IJsonRpcSubmitter _submitter;
    private readonly IMetricsConsumer _metricsConsumer;
    private readonly IJsonRpcMethodFilter _methodFilter;

    public Application(
        IJsonRpcMessageProvider msgProvider,
        IJsonRpcSubmitter submitter,
        IMetricsConsumer metricsConsumer,
        IJsonRpcMethodFilter methodFilter
    )
    {
        _msgProvider = msgProvider;
        _submitter = submitter;
        _metricsConsumer = metricsConsumer;
        _methodFilter = methodFilter;
    }

    public async Task Run()
    {
        var start = Stopwatch.GetTimestamp();

        await foreach (var msg in _msgProvider.Messages)
        {
            _metrics.TickMessages();

            var rpc = JsonSerializer.Deserialize<JsonDocument>(msg);
            if (rpc is null)
            {
                _metrics.TickFailed();
                continue;
            }

            if (rpc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _metrics.TickFailed();
                continue;
            }

            if (rpc.RootElement.TryGetProperty("response", out _))
            {
                _metrics.TickResponses();
                continue;
            }

            if (!rpc.RootElement.TryGetProperty("method", out var jsonMethodField))
            {
                _metrics.TickFailed();
                continue;
            }

            var methodName = jsonMethodField.GetString();
            if (methodName is null)
            {
                _metrics.TickFailed();
                continue;
            }

            if (_methodFilter.ShouldIgnore(methodName))
            {
                _metrics.TickIgnoredRequests();
                continue;
            }

            var startMethod = Stopwatch.GetTimestamp();
            await _submitter.Submit(msg);

            _metrics.TickRequest(methodName, Stopwatch.GetElapsedTime(startMethod));
        }

        _metrics.TotalRunningTime = Stopwatch.GetElapsedTime(start);

        _metricsConsumer.ConsumeMetrics(_metrics);
    }
}
