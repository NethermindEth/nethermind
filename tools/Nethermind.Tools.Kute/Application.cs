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
using System.Collections.Concurrent;
using Nethermind.Tools.Kute.FlowManager;
using System.Threading.Tasks;

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
    private readonly IJsonRpcFlowManager _flowManager;

    public Application(
        IMessageProvider<JsonRpc?> msgProvider,
        IJsonRpcSubmitter submitter,
        IJsonRpcValidator validator,
        IResponseTracer responseTracer,
        IProgressReporter progressReporter,
        IMetricsConsumer metricsConsumer,
        IJsonRpcMethodFilter methodFilter,
        IJsonRpcFlowManager flowManager
    )
    {
        _msgProvider = msgProvider;
        _submitter = submitter;
        _validator = validator;
        _responseTracer = responseTracer;
        _progressReporter = progressReporter;
        _metricsConsumer = metricsConsumer;
        _methodFilter = methodFilter;
        _flowManager = flowManager;
    }

    public async Task Run()
    {
        _progressReporter.ReportStart();

        BlockingCollection<(Task<HttpResponseMessage>, JsonRpc)> responseTasks = new BlockingCollection<(Task<HttpResponseMessage>, JsonRpc)>();
        var responseHandlingTask = Task.Run(async () =>
        {
            await Parallel.ForEachAsync(responseTasks.GetConsumingEnumerable(), async (task, ct) =>
            {
                await AnalyzeRequest(task);
            });
        });

        bool isSequentionalExecution = _flowManager.RequestsPerSecond <= 0;
        using (_metrics.TimeTotal())
        {
            await foreach (var (jsonRpc, n) in _msgProvider.Messages.Indexed(startingFrom: 1))
            {
                _progressReporter.ReportProgress(n);
                _metrics.TickMessages();

                switch (jsonRpc)
                {
                    case JsonRpc.BatchJsonRpc batch:
                        {
                            var requestTask = _submitter.Submit(batch);
                            if (isSequentionalExecution)
                                await AnalyzeRequest((requestTask, batch));
                            else
                                responseTasks.Add((requestTask, batch));

                            break;
                        }
                    case JsonRpc.SingleJsonRpc single:
                        {
                            if (single.IsResponse)
                            {
                                _metrics.TickResponses();
                                continue;
                            }
                            var requestTask = _submitter.Submit(single);
                            if (isSequentionalExecution)
                                await AnalyzeRequest((requestTask, single));
                            else
                                responseTasks.Add((requestTask, single));

                            break;
                        }
                    case null:
                        {
                            _metrics.TickFailed();

                            break;
                        }
                }

                if (_flowManager.RequestsPerSecond > 0)
                {
                    var delay = 1000 / _flowManager.RequestsPerSecond;
                    await Task.Delay(delay);
                }
            }

            responseTasks.CompleteAdding();
            await responseHandlingTask;
        }

        _progressReporter.ReportComplete();
        await _metricsConsumer.ConsumeMetrics(_metrics);
    }

    public async Task AnalyzeRequest((Task<HttpResponseMessage>, JsonRpc) task)
    {
        switch (task.Item2)
        {
            case null:
                {
                    _metrics.TickFailed();

                    break;
                }
            case JsonRpc.BatchJsonRpc batch:
                {
                    HttpResponseMessage content;
                    using (_metrics.TimeBatch())
                    {
                        content = await task.Item1;
                    }
                    var deserialized = JsonSerializer.Deserialize<JsonDocument>(content.Content.ReadAsStream());

                    if (_validator.IsInvalid(batch, deserialized))
                    {
                        _metrics.TickFailed();
                    }
                    else
                    {
                        _metrics.TickSucceeded();
                    }

                    await _responseTracer.TraceResponse(deserialized);

                    break;
                }
            case JsonRpc.SingleJsonRpc single:
                {
                    HttpResponseMessage content;
                    using (_metrics.TimeMethod(single.MethodName))
                    {
                        content = await task.Item1;
                    }
                    var deserialized = JsonSerializer.Deserialize<JsonDocument>(content.Content.ReadAsStream());

                    if (single.MethodName is null)
                    {
                        _metrics.TickFailed();
                        return;
                    }

                    if (_methodFilter.ShouldIgnore(single.MethodName))
                    {
                        _metrics.TickIgnoredRequests();
                        return;
                    }

                    if (_validator.IsInvalid(single, deserialized))
                    {
                        _metrics.TickFailed();
                    }
                    else
                    {
                        _metrics.TickSucceeded();
                    }

                    await _responseTracer.TraceResponse(deserialized);

                    break;
                }
            default:
                {
                    throw new ArgumentOutOfRangeException(nameof(task.Item2));
                }
        }
    }
}
