// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json;

namespace Nethermind.Tools.Kute;

class Application
{
    private readonly Metrics _metrics = new();

    private readonly IJsonRpcMessageProvider _msgProvider;
    private readonly IJsonRpcSubmitter _submitter;
    private readonly IMetricsConsumer _metricsConsumer;

    public Application(
        IJsonRpcMessageProvider msgProvider,
        IJsonRpcSubmitter submitter,
        IMetricsConsumer metricsConsumer
    )
    {
        _msgProvider = msgProvider;
        _submitter = submitter;
        _metricsConsumer = metricsConsumer;
    }

    public async Task Run()
    {
        var start = Stopwatch.GetTimestamp();

        await foreach (var msg in _msgProvider.Messages)
        {
            _metrics.TickTotal();

            var rpc = JsonSerializer.Deserialize<JsonDocument>(msg);
            if (rpc is null)
            {
                _metrics.TickFailed();
                continue;
            }

            if (rpc.RootElement.TryGetProperty("response", out _))
            {
                _metrics.TickResponses();
            }

            if (rpc.RootElement.TryGetProperty("method", out var jsonMethodField))
            {
                var methodName = jsonMethodField.GetString();
                if (methodName is not null)
                {
                    _metrics.TickMethod(methodName);
                }


                await _submitter.Submit(msg);
            }
        }

        _metrics.TotalRunningTime = Stopwatch.GetElapsedTime(start);

        _metricsConsumer.ConsumeMetrics(_metrics);
    }
}
