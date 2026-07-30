// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.ResponseTracer;
using Nethermind.Tools.Kute.Metrics;
using Nethermind.Tools.Kute.AsyncProcessor;

namespace Nethermind.Tools.Kute;

public sealed class Application(
    IAsyncProcessor processor,
    IMessageProvider<JsonRpc> msgProvider,
    IJsonRpcSubmitter submitter,
    IJsonRpcValidator validator,
    IResponseTracer responseTracer,
    IMetricsReporter reporter,
    IJsonRpcMethodFilter methodFilter
    )
{
    private readonly IAsyncProcessor _processor = processor;
    private readonly IMessageProvider<JsonRpc> _msgProvider = msgProvider;
    private readonly IJsonRpcSubmitter _submitter = submitter;
    private readonly IJsonRpcValidator _validator = validator;
    private readonly IResponseTracer _responseTracer = responseTracer;
    private readonly IMetricsReporter _reporter = reporter;
    private readonly IJsonRpcMethodFilter _methodFilter = methodFilter;

    public async Task Run(CancellationToken token = default)
    {
        Timer totalTimer = new();
        using (totalTimer.Time())
        {
            await _processor.Process(_msgProvider.Messages(token), async jsonRpc =>
            {
                await ProcessRpc(jsonRpc, token);
            }, token);
        }

        await _reporter.Total(totalTimer.Elapsed, token);
    }

    private async Task ProcessRpc(JsonRpc jsonRpc, CancellationToken token)
    {
        await _reporter.Message(token);

        switch (jsonRpc)
        {
            case JsonRpc.Response:
                {
                    await _reporter.Response(token);
                    break;
                }
            case JsonRpc.Request.Batch batch:
                {
                    JsonRpc.Response response;

                    Timer timer = new();
                    using (timer.Time())
                    {
                        response = await _submitter.Submit(batch, token);
                    }

                    if (_validator.IsInvalid(batch, response))
                    {
                        await _reporter.Failed(token);
                    }
                    else
                    {
                        await _reporter.Succeeded(token);
                    }
                    await _reporter.Batch(batch, timer.Elapsed, token);
                    await _responseTracer.TraceResponse(response, token);

                    break;
                }
            case JsonRpc.Request.Single single:
                {
                    if (single.MethodName is null)
                    {
                        await _reporter.Failed(token);
                        break;
                    }

                    if (_methodFilter.ShouldIgnore(single.MethodName))
                    {
                        await _reporter.Ignored(token);
                        break;
                    }

                    JsonRpc.Response response;

                    Timer timer = new();
                    using (timer.Time())
                    {
                        response = await _submitter.Submit(single, token);
                    }

                    if (_validator.IsInvalid(single, response))
                    {
                        await _reporter.Failed(token);
                    }
                    else
                    {
                        await _reporter.Succeeded(token);
                    }

                    await _reporter.Single(single, timer.Elapsed, token);
                    await _responseTracer.TraceResponse(response, token);

                    break;
                }
        }
    }
}
