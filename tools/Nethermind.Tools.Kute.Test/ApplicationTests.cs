// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.JsonRpcValidator.Eth;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.MetricsConsumer;
using Nethermind.Tools.Kute.ProgressReporter;
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

    private const string ResponseValid = """{"jsonrpc":"2.0","id":1,"result":{"status":"VALID"}}""";

    private IMessageProvider<string> LinesProvider()
    {
        var stringProvider = Substitute.For<IMessageProvider<string>>();
        stringProvider.Messages.Returns(TestInput.Split('\n').ToAsyncEnumerable());

        return stringProvider;
    }

    private IJsonRpcSubmitter ConstantSubmitter(string jsonResponse)
    {
        var submitter = Substitute.For<IJsonRpcSubmitter>();
        submitter.Submit(Arg.Any<JsonRpc>()).Returns((_) =>
        {
            var content = new StringContent(jsonResponse, System.Text.Encoding.UTF8, "application/json");
            return new HttpResponseMessage { Content = content };
        });

        return submitter;
    }

    [Test]
    public async Task ProcessesMultipleJSONRpcMessages_NoFiltering()
    {
        var messageProvider = new JsonRpcMessageProvider(LinesProvider());
        var jsonRpcSubmitter = ConstantSubmitter(ResponseValid);
        var validator = new ComposedJsonRpcValidator([new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator()]);
        var responseTracer = Substitute.For<IResponseTracer>();
        var reporter = Substitute.For<IProgressReporter>();
        var consumer = Substitute.For<IMetricsConsumer>();
        var filter = Substitute.For<IJsonRpcMethodFilter>();

        var app = new Application(
            messageProvider,
            jsonRpcSubmitter,
            validator,
            responseTracer,
            reporter,
            consumer,
            filter
        );

        await app.Run();

        await jsonRpcSubmitter.Received(17).Submit(Arg.Any<JsonRpc.SingleJsonRpc>());
        await jsonRpcSubmitter.Received(4).Submit(Arg.Any<JsonRpc.BatchJsonRpc>());
    }

    [Test]
    public async Task ProcessesMultipleJSONRpcMessages_NoFiltering_UnwrapBatches()
    {
        var messageProvider = new UnwrapBatchJsonRpcMessageProvider(new JsonRpcMessageProvider(LinesProvider()));
        var jsonRpcSubmitter = ConstantSubmitter(ResponseValid);
        var validator = new ComposedJsonRpcValidator([new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator()]);
        var responseTracer = Substitute.For<IResponseTracer>();
        var reporter = Substitute.For<IProgressReporter>();
        var consumer = Substitute.For<IMetricsConsumer>();
        var filter = Substitute.For<IJsonRpcMethodFilter>();

        var app = new Application(
            messageProvider,
            jsonRpcSubmitter,
            validator,
            responseTracer,
            reporter,
            consumer,
            filter
        );

        await app.Run();

        await jsonRpcSubmitter.Received(49).Submit(Arg.Any<JsonRpc.SingleJsonRpc>());
        await jsonRpcSubmitter.Received(0).Submit(Arg.Any<JsonRpc.BatchJsonRpc>());
    }
}
