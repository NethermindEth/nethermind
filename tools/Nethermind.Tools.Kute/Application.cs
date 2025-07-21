// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.Extensions;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.ResponseTracer;
using Nethermind.Tools.Kute.Metrics;

namespace Nethermind.Tools.Kute;

public sealed class Application
{
    private readonly IMessageProvider<JsonRpc> _msgProvider;
    private readonly IJsonRpcSubmitter _submitter;
    private readonly IJsonRpcValidator _validator;
    private readonly IResponseTracer _responseTracer;
    private readonly IMetricsReporter _reporter;
    private readonly IJsonRpcMethodFilter _methodFilter;

    public Application(
        IMessageProvider<JsonRpc> msgProvider,
        IJsonRpcSubmitter submitter,
        IJsonRpcValidator validator,
        IResponseTracer responseTracer,
        IMetricsReporter reporter,
        IJsonRpcMethodFilter methodFilter
    )
    {
        _msgProvider = msgProvider;
        _submitter = submitter;
        _validator = validator;
        _responseTracer = responseTracer;
        _reporter = reporter;
        _methodFilter = methodFilter;
    }

    public async Task Run(CancellationToken token = default)
    {
        var totalTimer = new Timer();
        using (totalTimer.Time())
        {
            await foreach (var (jsonRpc, n) in _msgProvider.Messages(token).Indexed(startingFrom: 1))
            {
                await _reporter.Message(token);

                switch (jsonRpc)
                {
                    case JsonRpc.Response:
                        {
                            await _reporter.Response(token);
                            continue;
                        }
                    case JsonRpc.Request.Batch batch:
                        {
                            JsonRpc.Response response;

                            var timer = new Timer();
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
                            await _reporter.Batch(n, timer.Elapsed, token);
                            await _responseTracer.TraceResponse(response, token);

                            break;
                        }
                    case JsonRpc.Request.Single single:
                        {
                            if (single.MethodName is null)
                            {
                                await _reporter.Failed(token);
                                continue;
                            }

                            if (_methodFilter.ShouldIgnore(single.MethodName))
                            {
                                await _reporter.Ignored(token);
                                continue;
                            }

                            JsonRpc.Response response;

                            var timer = new Timer();
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

                            await _reporter.Single(n, timer.Elapsed, token);
                            await _responseTracer.TraceResponse(response, token);

                            break;
                        }
                }
            }
        }

        await _reporter.Total(totalTimer.Elapsed, token);
    }
}
