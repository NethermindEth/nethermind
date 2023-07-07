// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.MetricsConsumer;

namespace Nethermind.Tools.Kute;

class Application
{
    private readonly Metrics _metrics = new();

    private readonly IMessageProvider<JsonRpc?> _msgProvider;
    private readonly IJsonRpcSubmitter _submitter;
    private readonly IMetricsConsumer _metricsConsumer;
    private readonly IJsonRpcMethodFilter _methodFilter;

    public Application(
        IMessageProvider<JsonRpc?> msgProvider,
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
        using (_metrics.TimeTotal())
        {
            await foreach (var jsonRpc in _msgProvider.Messages)
            {
                _metrics.TickMessages();

                switch (jsonRpc)
                {
                    case null:
                        _metrics.TickFailed();

                        break;

                    case JsonRpc.BatchJsonRpc batch:
                        using (_metrics.TimeBatch())
                        {
                            await _submitter.Submit(batch);
                        }

                        break;

                    case JsonRpc.SingleJsonRpc single:
                        if (single.IsResponse)
                        {
                            _metrics.TickResponses();
                            continue;
                        }

                        if (single.MethodName is null)
                        {
                            _metrics.TickFailed();
                            continue;
                        }

                        if (_methodFilter.ShouldIgnore(single.MethodName))
                        {
                            _metrics.TickIgnoredRequests();
                            continue;
                        }

                        using (_metrics.TimeMethod(single.MethodName))
                        {
                            await _submitter.Submit(single);
                        }

                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(jsonRpc));
                }
            }
        }

        await _metricsConsumer.ConsumeMetrics(_metrics);
    }
}
