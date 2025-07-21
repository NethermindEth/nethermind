// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.AsyncProcessor;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.JsonRpcValidator.Eth;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.Metrics;
using Nethermind.Tools.Kute.ResponseTracer;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Tools.Kute.Test;

public class ApplicationTests
{
    private const string TestInput = """
    {"response": 0}
    {"response": 1}
    "a-string"
    {"method": null}
    {"method": "eth_getBlockByNumber"}
    {"method": "eth_getLogs"}
    {"method": "eth_syncing"}
    {"method": "engine_forkchoiceUpdatedV2"}
    {"method": "engine_exchangeTransitionConfigurationV1"}
    {"method": "eth_chainId"}
    {"method": "engine_newPayloadV3"}
    {"method": "engine_exchangeCapabilities"}
    {"method": "eth_getBlockByNumber"}
    {"method": "eth_getLogs"}
    {"method": "eth_syncing"}
    {"method": "engine_forkchoiceUpdatedV2"}
    {"method": "engine_exchangeTransitionConfigurationV1"}
    {"method": "eth_chainId"}
    {"method": "engine_newPayloadV2"}
    {"method": "engine_newPayloadV3"}
    {"method": "engine_exchangeCapabilities"}
    [{"method": "eth_getBlockByNumber"}, {"method": "eth_getLogs"}, {"method": "eth_syncing"}, {"method": "engine_forkchoiceUpdatedV2"}, {"method": "engine_exchangeTransitionConfigurationV1"}, {"method": "eth_chainId"}, {"method": "engine_newPayloadV3"}, {"method": "engine_exchangeCapabilities"}]
    [{"method": "eth_getBlockByNumber"}, {"method": "eth_getLogs"}, {"method": "eth_syncing"}, {"method": "engine_forkchoiceUpdatedV2"}, {"method": "engine_exchangeTransitionConfigurationV1"}, {"method": "eth_chainId"}, {"method": "engine_newPayloadV3"}, {"method": "engine_exchangeCapabilities"}]
    [{"method": "eth_getBlockByNumber"}, {"method": "eth_getLogs"}, {"method": "eth_syncing"}, {"method": "engine_forkchoiceUpdatedV2"}, {"method": "engine_exchangeTransitionConfigurationV1"}, {"method": "eth_chainId"}, {"method": "engine_newPayloadV3"}, {"method": "engine_exchangeCapabilities"}]
    [{"method": "eth_getBlockByNumber"}, {"method": "eth_getLogs"}, {"method": "eth_syncing"}, {"method": "engine_forkchoiceUpdatedV2"}, {"method": "engine_exchangeTransitionConfigurationV1"}, {"method": "eth_chainId"}, {"method": "engine_newPayloadV3"}, {"method": "engine_exchangeCapabilities"}]
    """;

    private const string ResponseOK = """{"jsonrpc":"2.0","id":1,"result":{"status":"VALID"}}""";
    private const string ResponseError = """{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Internal error"}}""";

    private static IMessageProvider<string> LinesProvider(string lines)
    {
        var stringProvider = Substitute.For<IMessageProvider<string>>();
        stringProvider.Messages().Returns(lines.Split('\n').ToAsyncEnumerable());

        return stringProvider;
    }

    private static IJsonRpcSubmitter ConstantSubmitter(string jsonResponse)
    {
        var submitter = Substitute.For<IJsonRpcSubmitter>();
        submitter.Submit(Arg.Any<JsonRpc.Request>()).Returns(async (_) =>
        {
            var content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json");
            var httpResponse = new HttpResponseMessage { Content = content };
            return await JsonRpc.Response.FromHttpResponseAsync(httpResponse);
        });

        return submitter;
    }

    private static IJsonRpcMethodFilter ConstantFilter(bool shouldSubmit)
    {
        var filter = Substitute.For<IJsonRpcMethodFilter>();
        filter.ShouldSubmit(Arg.Any<string>()).Returns(shouldSubmit);
        return filter;
    }

    private static IEnumerable<TestCaseData> Processors()
    {
        yield return new TestCaseData(new SequentialProcessor()).SetArgDisplayNames("Sequential");
        yield return new TestCaseData(new ConcurrentProcessor(8)).SetArgDisplayNames("Concurrent - 8");
    }

    [TestCaseSource(nameof(Processors))]
    public async Task NoFiltering(IAsyncProcessor processor)
    {
        var messageProvider = new JsonRpcMessageProvider(LinesProvider(TestInput));
        var jsonRpcSubmitter = ConstantSubmitter(ResponseOK);
        var validator = new ComposedJsonRpcValidator([new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator()]);
        var responseTracer = Substitute.For<IResponseTracer>();
        var reporter = Substitute.For<IMetricsReporter>();
        var filter = ConstantFilter(shouldSubmit: true);

        var app = new Application(
            processor,
            messageProvider,
            jsonRpcSubmitter,
            validator,
            responseTracer,
            reporter,
            filter
        );

        await app.Run();

        await jsonRpcSubmitter.Received(17).Submit(Arg.Any<JsonRpc.Request.Single>());
        await jsonRpcSubmitter.Received(4).Submit(Arg.Any<JsonRpc.Request.Batch>());
        await responseTracer.Received(21).TraceResponse(Arg.Any<JsonRpc.Response>());
        await reporter.Received(1).Total(Arg.Any<TimeSpan>());
    }

    [TestCaseSource(nameof(Processors))]
    public async Task NoFiltering_UnwrapBatches(IAsyncProcessor processor)
    {
        var messageProvider = new UnwrapBatchJsonRpcMessageProvider(new JsonRpcMessageProvider(LinesProvider(TestInput)));
        var jsonRpcSubmitter = ConstantSubmitter(ResponseOK);
        var validator = new ComposedJsonRpcValidator([new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator()]);
        var responseTracer = Substitute.For<IResponseTracer>();
        var reporter = Substitute.For<IMetricsReporter>();
        var filter = ConstantFilter(shouldSubmit: true);

        var app = new Application(
            processor,
            messageProvider,
            jsonRpcSubmitter,
            validator,
            responseTracer,
            reporter,
            filter
        );

        await app.Run();

        await jsonRpcSubmitter.Received(49).Submit(Arg.Any<JsonRpc.Request.Single>());
        await jsonRpcSubmitter.Received(0).Submit(Arg.Any<JsonRpc.Request.Batch>());
        await responseTracer.Received(49).TraceResponse(Arg.Any<JsonRpc.Response>());
        await reporter.Received(1).Total(Arg.Any<TimeSpan>());
    }

    [TestCaseSource(nameof(Processors))]
    public async Task WithFiltering_InvalidResponses(IAsyncProcessor processor)
    {
        var lines = """
        {"method": "engine_exchangeTransitionConfigurationV1"}
        {"method": "eth_chainId"}
        [{"method": "eth_getBlockByNumber"}, {"method": "engine_exchangeCapabilities"}]
        """;

        var messageProvider = new JsonRpcMessageProvider(LinesProvider(lines));
        var jsonRpcSubmitter = ConstantSubmitter(ResponseError);
        var validator = new ComposedJsonRpcValidator([new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator()]);
        var responseTracer = Substitute.For<IResponseTracer>();
        var reporter = Substitute.For<IMetricsReporter>();
        var filter = new ComposedJsonRpcMethodFilter([new PatternJsonRpcMethodFilter("eth_.*")]);

        var app = new Application(
            processor,
            messageProvider,
            jsonRpcSubmitter,
            validator,
            responseTracer,
            reporter,
            filter
        );

        await app.Run();

        await jsonRpcSubmitter.Received(1).Submit(Arg.Any<JsonRpc.Request.Single>());
        await jsonRpcSubmitter.Received(1).Submit(Arg.Any<JsonRpc.Request.Batch>());
        await responseTracer.Received(2).TraceResponse(Arg.Any<JsonRpc.Response>());
        await reporter.Received(1).Total(Arg.Any<TimeSpan>());
    }

    [TestCaseSource(nameof(Processors))]
    public async Task WithFiltering_UnwrapBatches_InvalidResponses(IAsyncProcessor processor)
    {
        var lines = """
        {"method": "engine_exchangeTransitionConfigurationV1"}
        {"method": "eth_chainId"}
        [{"method": "eth_getBlockByNumber"}, {"method": "engine_exchangeCapabilities"}]
        """;

        var messageProvider = new UnwrapBatchJsonRpcMessageProvider(new JsonRpcMessageProvider(LinesProvider(lines)));
        var jsonRpcSubmitter = ConstantSubmitter(ResponseError);
        var validator = new ComposedJsonRpcValidator([new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator()]);
        var responseTracer = Substitute.For<IResponseTracer>();
        var reporter = Substitute.For<IMetricsReporter>();
        var filter = new ComposedJsonRpcMethodFilter([new PatternJsonRpcMethodFilter("engine_.*")]);

        var app = new Application(
            processor,
            messageProvider,
            jsonRpcSubmitter,
            validator,
            responseTracer,
            reporter,
            filter
        );

        await app.Run();

        await jsonRpcSubmitter.Received(2).Submit(Arg.Any<JsonRpc.Request.Single>());
        await jsonRpcSubmitter.Received(0).Submit(Arg.Any<JsonRpc.Request.Batch>());
        await responseTracer.Received(2).TraceResponse(Arg.Any<JsonRpc.Response>());
        await reporter.Received(1).Total(Arg.Any<TimeSpan>());
    }
}
