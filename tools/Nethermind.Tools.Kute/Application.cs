// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Tools.Kute.Extensions;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.MetricsConsumer;
using Nethermind.Tools.Kute.ProgressReporter;
using Nethermind.Tools.Kute.ResponseTracer;

namespace Nethermind.Tools.Kute;

class Application
{
    private readonly Metrics _metrics = new();

    private readonly IMessageProvider<JsonRpc?> _msgProvider;
    private readonly IJsonRpcSubmitter _submitter;
    private readonly IJsonRpcValidator _validator;
    private readonly IResponseTracer _responseTracer;
    private readonly IProgressReporter _progressReporter;
    private readonly IMetricsConsumer _metricsConsumer;
    private readonly IJsonRpcMethodFilter _methodFilter;

    public Application(
        IMessageProvider<JsonRpc?> msgProvider,
        IJsonRpcSubmitter submitter,
        IJsonRpcValidator validator,
        IResponseTracer responseTracer,
        IProgressReporter progressReporter,
        IMetricsConsumer metricsConsumer,
        IJsonRpcMethodFilter methodFilter
    )
    {
        _msgProvider = msgProvider;
        _submitter = submitter;
        _validator = validator;
        _responseTracer = responseTracer;
        _progressReporter = progressReporter;
        _metricsConsumer = metricsConsumer;
        _methodFilter = methodFilter;
    }

    public async Task Run()
    {
        _progressReporter.ReportStart();

        using (_metrics.TimeTotal())
        {
            await foreach (var (jsonRpc, n) in _msgProvider.Messages.Indexed(startingFrom: 1))
            {
                _progressReporter.ReportProgress(n);

                _metrics.TickMessages();

                switch (jsonRpc)
                {
                    case null:
                    {
                        _metrics.TickFailed();

                        break;
                    }
                    case JsonRpc.BatchJsonRpc batch:
                    {
                        JsonDocument? result;
                        using (_metrics.TimeBatch())
                        {
                            result = await _submitter.Submit(batch);
                        }

                        if (_validator.IsInvalid(batch, result))
                        {
                            _metrics.TickFailed();
                        }

                        await _responseTracer.TraceResponse(result);

                        break;
                    }
                    case JsonRpc.SingleJsonRpc single:
                    {
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

                        JsonDocument? result;
                        using (_metrics.TimeMethod(single.MethodName))
                        {
                            result = await _submitter.Submit(single);
                        }

                        if (_validator.IsInvalid(single, result))
                        {
                            _metrics.TickFailed();
                        }

                        await _responseTracer.TraceResponse(result);

                        break;
                    }
                    default:
                    {
                        throw new ArgumentOutOfRangeException(nameof(jsonRpc));
                    }
                }
            }
        }

        _progressReporter.ReportComplete();
        await _metricsConsumer.ConsumeMetrics(_metrics);
    }
}
