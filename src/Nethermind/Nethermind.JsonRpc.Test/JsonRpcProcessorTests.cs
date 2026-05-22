// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Config;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

[Parallelizable(ParallelScope.Self)]
[TestFixture(true)]
[TestFixture(false)]
public class JsonRpcProcessorTests(bool returnErrors)
{
    private const string TransactionCountAddress = "0x7f01d9b227593e033bf8d6fc86e634d27aa85568";
    private const string TransactionCountBlock = "0x668c24";
    private const string TransactionCountParamsJson = "[\"" + TransactionCountAddress + "\",\"" + TransactionCountBlock + "\"]";
    private const string TransactionCountObjectParamsJson = "[{\"a\":\"" + TransactionCountAddress + "\",\"b\":\"" + TransactionCountBlock + "\"}]";
    private const string TransactionCountNestedArrayParamsJson = "[" + TransactionCountObjectParamsJson + "]";
    private const string TransactionCountNestedArrayWithValueParamsJson = "[[{\"a\":\"" + TransactionCountAddress + "\",\"b\":\"" + TransactionCountBlock + "\"}, 1]]";
    private const string TransactionCountAddressParamJson = "\"" + TransactionCountAddress + "\"";
    private const string TransactionCountBlockParamJson = "\"" + TransactionCountBlock + "\"";
    private const string TransactionCountInvalidObjectParamsJson = "{\"a\":\"" + TransactionCountAddress + "\",\"" + TransactionCountBlock + "\"}";

    private readonly JsonRpcErrorResponse _errorResponse = new();
    private static readonly object[] CachedMethodNameCases =
    [
        new object[] { "engine_newPayloadV4", false, true },
        new object[] { "engine_newPayloadV4", true, true },
        new object[] { "engine_getBlobsV2", false, true },
        new object[] { "engine_getBlobsV2", true, true },
        new object[] { "eth_call", false, true },
        new object[] { "eth_call", true, true },
        new object[] { "eth_getBlockByNumber", false, true },
        new object[] { "eth_getBlockByNumber", true, true },
        new object[] { "eth_chainId", false, true },
        new object[] { "eth_chainId", true, true },
        new object[] { "eth_unknown", false, false },
        new object[] { "eth_unknown", true, false },
    ];
    private static readonly object[] JsonRpcIdCases =
    [
        new object[] { "12345678901234567890", new JsonRpcId(decimal.Parse("12345678901234567890")) },
        new object[] { "\"0xa1aa12434\"", new JsonRpcId("0xa1aa12434") },
        new object[] { "67", new JsonRpcId(67) },
        new object[] { "9223372036854775807", new JsonRpcId(long.MaxValue) },
        new object[] { "\";\\\\\\\"\"", new JsonRpcId(";\\\"") },
        new object[] { "null", JsonRpcId.Null },
    ];

    static JsonRpcProcessorTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(KnownRpcMethodNames).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(Nethermind.Merge.Plugin.IEngineRpcModule).Module.ModuleHandle);
    }

    private JsonRpcProcessor Initialize(JsonRpcConfig? config = null, RpcRecorderState recorderState = RpcRecorderState.All)
    {
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>()).Returns(ci => returnErrors ? new JsonRpcErrorResponse { Id = ci.Arg<JsonRpcRequest>().Id } : new JsonRpcSuccessResponse { Id = ci.Arg<JsonRpcRequest>().Id });
        service.GetErrorResponse(0, null!).ReturnsForAnyArgs(_errorResponse);
        service.GetErrorResponse(0, null!, null!, null!).ReturnsForAnyArgs(_errorResponse);

        // we enable recorder always to have an easy smoke test for recording and this is fine because recorder is a non-critical component
        config ??= new JsonRpcConfig();
        config.RpcRecorderState = recorderState;

        return CreateProcessor(service, config);
    }

    private static JsonRpcProcessor CreateProcessor(IJsonRpcService service, IJsonRpcConfig? config = null, IFileSystem? fileSystem = null, IProcessExitSource? processExitSource = null) =>
        new(service, config ?? new JsonRpcConfig(), fileSystem ?? Substitute.For<IFileSystem>(), LimboLogs.Instance, processExitSource);

    [Test]
    public async Task Can_process_guid_ids()
    {
        using CollectedJsonRpcResponses result = await ProcessAsync("{\"id\":\"840b55c4-18b0-431c-be1d-6d22198b53f2\",\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[\"0x7f01d9b227593e033bf8d6fc86e634d27aa85568\",\"0x668c24\"]}");
        result.Should().HaveCount(1);
        Assert.That(result[0].Response!.Id, Is.EqualTo(new JsonRpcId("840b55c4-18b0-431c-be1d-6d22198b53f2")));
    }

    [Test]
    public async Task Http_engine_newPayloadV4_keeps_envelope_and_params_on_direct_utf8_path()
    {
        string? capturedMethod = null;
        bool capturedRawParams = false;
        JsonValueKind capturedParamsKind = JsonValueKind.Undefined;
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>()).Returns(callInfo =>
        {
            JsonRpcRequest request = callInfo.Arg<JsonRpcRequest>();
            capturedMethod = request.Method;
            capturedRawParams = !request.ParamsUtf8.IsEmpty;
            capturedParamsKind = request.ParamsKind;
            return new JsonRpcSuccessResponse { Id = request.Id };
        });

        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None });

        await ProcessAsync(
            processor,
            CreateReader("{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"engine_newPayloadV4\",\"params\":[{\"parentHash\":\"0x0\"},[],null,null]}"),
            new JsonRpcContext(RpcEndpoint.Http));

        capturedMethod.Should().Be("engine_newPayloadV4");
        capturedRawParams.Should().BeTrue();
        capturedParamsKind.Should().Be(JsonValueKind.Array);
    }

    [TestCaseSource(nameof(CachedMethodNameCases))]
    public async Task Http_generated_method_names_use_cached_instances(string methodName, bool inBatch, bool expectedCached)
    {
        string? capturedMethod = null;
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>()).Returns(callInfo =>
        {
            JsonRpcRequest request = callInfo.Arg<JsonRpcRequest>();
            capturedMethod = request.Method;
            return new JsonRpcSuccessResponse { Id = request.Id };
        });

        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None });

        string request = inBatch
            ? $$"""[{"id":1,"jsonrpc":"2.0","method":"{{methodName}}","params":[]}]"""
            : $$"""{"id":1,"jsonrpc":"2.0","method":"{{methodName}}","params":[]}""";

        await ProcessAsync(
            processor,
            CreateReader(request),
            new JsonRpcContext(RpcEndpoint.Http));

        capturedMethod.Should().Be(methodName);
        string? knownMethodName = TryGetKnownMethodName(methodName);
        if (expectedCached)
        {
            knownMethodName.Should().NotBeNull();
            capturedMethod.Should().BeSameAs(knownMethodName);
        }
        else
        {
            knownMethodName.Should().BeNull();
            capturedMethod.Should().NotBeSameAs(methodName);
        }
    }

    [Test]
    public void KnownRpcMethodNames_uses_full_value_length_for_multi_segment_reader()
    {
        ReadOnlySequence<byte> methodSequence = CreateSequence("\"engine_", "newPayloadV4\"");
        Utf8JsonReader reader = new(methodSequence);

        reader.Read().Should().BeTrue();
        reader.TokenType.Should().Be(JsonTokenType.String);
        reader.HasValueSequence.Should().BeTrue();

        string? methodName = KnownRpcMethodNames.Intern(ref reader);

        methodName.Should().Be("engine_newPayloadV4");
        methodName.Should().BeSameAs(GetKnownMethodName("engine_newPayloadV4"));
    }

    [Test]
    public void Generated_known_method_names_cover_rpc_module_interfaces()
    {
        HashSet<string> knownMethods = KnownRpcMethodNames.All.ToHashSet(StringComparer.Ordinal);
        Assembly[] assemblies =
        [
            typeof(IRpcModule).Assembly,
            typeof(Nethermind.Merge.Plugin.IEngineRpcModule).Assembly,
        ];

        foreach (Assembly assembly in assemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                if (!type.IsInterface || !typeof(IRpcModule).IsAssignableFrom(type) || type == typeof(IRpcModule))
                {
                    continue;
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<JsonRpcMethodAttribute>() is not null)
                    {
                        knownMethods.Should().Contain(method.Name);
                    }
                }
            }
        }
    }

    private static string? TryGetKnownMethodName(string methodName)
    {
        IReadOnlyList<string> methods = KnownRpcMethodNames.All;
        for (int i = 0; i < methods.Count; i++)
        {
            if (methods[i] == methodName)
            {
                return methods[i];
            }
        }

        return null;
    }

    private static string GetKnownMethodName(string methodName) =>
        TryGetKnownMethodName(methodName) ?? throw new InvalidOperationException($"Missing generated method name {methodName}.");

    private static IEnumerable<TestCaseData> MultipleDocumentRequestCases()
    {
        yield return new TestCaseData(CreateTransactionCountRequest("67") + "\r\n" + CreateTransactionCountRequest("68"), false, false).SetName("Two single requests");
        yield return new TestCaseData(CreateTransactionCountRequest("67") + CreateTransactionCountBatchRequest(2), true, false).SetName("Single request and batch");
        yield return new TestCaseData(CreateTransactionCountRequest("67") + CreateTransactionCountRequest("68")[..^1], false, true).SetName("Second request not closed");
        yield return new TestCaseData(CreateTransactionCountRequest("67") + "{aaa}", false, true).SetName("Second request invalid");
    }

    private ValueTask<CollectedJsonRpcResponses> ProcessAsync(string request, JsonRpcContext? context = null, JsonRpcConfig? config = null) =>
        ProcessAsync(Initialize(config), CreateReader(request), context ?? new JsonRpcContext(RpcEndpoint.Http));

    private static async ValueTask<CollectedJsonRpcResponses> ProcessAsync(
        JsonRpcProcessor processor,
        PipeReader reader,
        JsonRpcContext context,
        CollectingJsonRpcResponseSink? sink = null)
    {
        sink ??= new CollectingJsonRpcResponseSink();
        JsonRpcInputMode inputMode = context.RpcEndpoint == RpcEndpoint.Http
            ? JsonRpcInputMode.SingleDocument
            : JsonRpcInputMode.MultipleDocuments;

        await processor.ProcessAsync(reader, context, sink, new JsonRpcProcessingOptions(inputMode));
        return sink.Responses;
    }

    [Test]
    public async Task Sink_processor_entry_point_propagates_stop_requested_to_inline_batch_processing()
    {
        IJsonRpcService service = CreateEchoService();
        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None });
        CollectingJsonRpcResponseSink sink = new() { StopAfterBatchItems = 1 };

        await processor.ProcessAsync(
            CreateReader("[{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[]},{\"id\":2,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[]},{\"id\":3,\"jsonrpc\":\"2.0\",\"method\":\"net_version\",\"params\":[]}]"),
            new JsonRpcContext(RpcEndpoint.Http),
            sink,
            new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument));

        sink.BatchItems.Should().HaveCount(3);
        sink.BatchItems[0].Should().BeOfType<JsonRpcSuccessResponse>();
        JsonRpcErrorResponse second = sink.BatchItems[1].Should().BeOfType<JsonRpcErrorResponse>().Subject;
        JsonRpcErrorResponse third = sink.BatchItems[2].Should().BeOfType<JsonRpcErrorResponse>().Subject;
        second.Id.Should().Be(2);
        third.Id.Should().Be(3);
        await service.Received(1).SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>());
    }

    [Test]
    public async Task Sink_processor_entry_point_writes_to_sink()
    {
        CollectingJsonRpcResponseSink sink = new();
        JsonRpcProcessor processor = Initialize(recorderState: RpcRecorderState.None);

        await processor.ProcessAsync(
            CreateReader("{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[]}"),
            new JsonRpcContext(RpcEndpoint.Http),
            sink,
            new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument));

        sink.Singles.Should().HaveCount(1);
        sink.Singles[0].Id.Should().Be(67);
    }

    [Test]
    public async Task Sink_processor_entry_point_reads_params_through_envelope_reader()
    {
        bool inspected = false;
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>())
            .Returns(ci =>
            {
                JsonRpcRequest request = ci.Arg<JsonRpcRequest>();
                request.Params.ValueKind.Should().Be(JsonValueKind.Array);
                request.Params[0].GetProperty("a").GetInt32().Should().Be(2);
                inspected = true;
                return new JsonRpcSuccessResponse { Id = request.Id };
            });

        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None });
        CollectingJsonRpcResponseSink sink = new();

        await processor.ProcessAsync(
            CreateReader(" \r\n{\"id\":67,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{\"a\":2}]}\t "),
            new JsonRpcContext(RpcEndpoint.Http),
            sink,
            new JsonRpcProcessingOptions(JsonRpcInputMode.SingleDocument));

        inspected.Should().BeTrue();
        sink.Singles.Should().HaveCount(1);
        sink.Singles[0].Id.Should().Be(67);
    }

    private static PipeReader CreateReader(string request) =>
        PipeReader.Create(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(request)));

    private static string CreateTransactionCountRequest(string idJson, string? paramsName = "params", string paramsJson = TransactionCountParamsJson) =>
        paramsName is null
            ? $$"""{"id":{{idJson}},"jsonrpc":"2.0","method":"eth_getTransactionCount"}"""
            : $$"""{"id":{{idJson}},"jsonrpc":"2.0","method":"eth_getTransactionCount","{{paramsName}}":{{paramsJson}}}""";

    private static string CreateTransactionCountBatchRequest(int count, bool omitLastParams = false)
    {
        StringBuilder request = new("[");
        for (int i = 0; i < count; i++)
        {
            if (i != 0) request.Append(',');
            request.Append(CreateTransactionCountRequest("67", omitLastParams && i == count - 1 ? null : "params"));
        }

        request.Append(']');
        return request.ToString();
    }

    private static string CreateTransactionCountBatchRequest(params string[] paramsJsons)
    {
        StringBuilder request = new("[");
        for (int i = 0; i < paramsJsons.Length; i++)
        {
            if (i != 0) request.Append(',');
            request.Append(CreateTransactionCountRequest("67", paramsJson: paramsJsons[i]));
        }

        request.Append(']');
        return request.ToString();
    }

    private static ReadOnlySequence<byte> CreateSequence(string first, string second)
    {
        BufferSegment start = new(Encoding.UTF8.GetBytes(first));
        BufferSegment end = start.Append(Encoding.UTF8.GetBytes(second));
        return new ReadOnlySequence<byte>(start, 0, end, end.Memory.Length);
    }

    [Test]
    public void JsonRpcEnvelopeReader_reads_envelope_and_params_range()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_newPayloadV3\",\"params\":[1,{\"a\":2}],\"extra\":{\"ignored\":true}}");
        JsonRpcEnvelopeReader reader = new(body);

        reader.TryRead(out JsonRpcEnvelope envelope).Should().BeTrue();

        envelope.JsonRpc.Should().Be("2.0");
        envelope.Id.Should().Be(new JsonRpcId(1));
        ReferenceEquals(envelope.Method, "engine_newPayloadV3").Should().BeTrue();
        envelope.HasParams.Should().BeTrue();
        envelope.ParamsKind.Should().Be(JsonValueKind.Array);
        Encoding.UTF8.GetString(body, envelope.ParamsStart, envelope.ParamsLength).Should().Be("[1,{\"a\":2}]");
    }

    [Test]
    public void JsonRpcEnvelopeReader_reads_unknown_method_and_missing_params()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":12345678901234567890,\"method\":\"eth_unknown\"}");
        JsonRpcEnvelopeReader reader = new(body);

        reader.TryRead(out JsonRpcEnvelope envelope).Should().BeTrue();

        envelope.Id.Should().Be(new JsonRpcId(decimal.Parse("12345678901234567890")));
        envelope.Method.Should().Be("eth_unknown");
        envelope.HasParams.Should().BeFalse();
        envelope.ParamsKind.Should().Be(JsonValueKind.Undefined);
    }

    [Test]
    public void JsonRpcEnvelopeReader_echoes_validated_raw_string_id_token()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"\\u0041\\n\",\"method\":\"eth_blockNumber\"}");
        JsonRpcEnvelopeReader reader = new(body);

        reader.TryRead(out JsonRpcEnvelope envelope).Should().BeTrue();

        JsonRpcId expectedId = new("A\n");
        envelope.Id.Should().Be(expectedId);
        envelope.Id.GetHashCode().Should().Be(expectedId.GetHashCode());
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            envelope.Id.WriteTo(writer);
        }

        Encoding.UTF8.GetString(buffer.WrittenSpan).Should().Be("\"\\u0041\\n\"");
    }

    [Test]
    public void JsonRpcEnvelopeReader_keeps_numeric_ids_typed()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":1e2,\"method\":\"eth_blockNumber\"}");
        JsonRpcEnvelopeReader reader = new(body);

        reader.TryRead(out JsonRpcEnvelope envelope).Should().BeTrue();

        envelope.Id.TryGetDecimal(out decimal id).Should().BeTrue();
        id.Should().Be(100m);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            envelope.Id.WriteTo(writer);
        }

        Encoding.UTF8.GetString(buffer.WrittenSpan).Should().Be("100");
    }

    [Test]
    public void JsonRpcEnvelopeReader_returns_false_for_non_object_root()
    {
        byte[] body = Encoding.UTF8.GetBytes("[{\"id\":1}]");
        JsonRpcEnvelopeReader reader = new(body);

        reader.TryRead(out JsonRpcEnvelope envelope).Should().BeFalse();
        envelope.Should().Be(default(JsonRpcEnvelope));
    }

    [Test]
    public void JsonRpcEnvelopeReader_rejects_fractional_numeric_ids()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"id\":1.1,\"method\":\"eth_blockNumber\"}");

        Action read = () =>
        {
            JsonRpcEnvelopeReader reader = new(body);
            reader.TryRead(out _);
        };

        read.Should().Throw<JsonException>();
    }

    private static IJsonRpcService CreateEchoService()
    {
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>())
            .Returns(static ci => new JsonRpcSuccessResponse { Id = ci.Arg<JsonRpcRequest>().Id });
        service.GetErrorResponse(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<JsonRpcId?>(), Arg.Any<string?>())
            .Returns(static ci => new JsonRpcErrorResponse
            {
                Id = JsonRpcId.FromObject(ci.ArgAt<JsonRpcId?>(2)),
                Error = new Error { Code = ci.ArgAt<int>(0), Message = ci.ArgAt<string>(1) }
            });

        return service;
    }

    [TestCase(RpcEndpoint.Http)]
    [TestCase(RpcEndpoint.Ws)]
    [TestCase(RpcEndpoint.IPC)]
    public async Task Request_recorder_captures_payload(RpcEndpoint endpoint)
    {
        List<string> records = [];
        JsonRpcProcessor processor = CreateRecordingProcessor(RpcRecorderState.Request, records);

        string request = endpoint == RpcEndpoint.Http
            ? "{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[]}"
            : "{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[]}{\"id\":2,\"jsonrpc\":\"2.0\",\"method\":\"net_version\",\"params\":[]}";

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, CreateReader(request), new JsonRpcContext(endpoint));

        records.Should().ContainSingle(record => record.Contains("\"method\":\"eth_blockNumber\""));
        if (endpoint != RpcEndpoint.Http)
        {
            records[0].Should().Contain("\"method\":\"net_version\"");
        }
    }

    [TestCase(false, 1)]
    [TestCase(true, 2)]
    public async Task Response_recorder_captures_responses(bool isBatch, int expectedRecordCount)
    {
        List<string> records = [];
        JsonRpcProcessor processor = CreateRecordingProcessor(RpcRecorderState.Response, records);
        string request = isBatch
            ? "[{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[]},{\"id\":2,\"jsonrpc\":\"2.0\",\"method\":\"net_version\",\"params\":[]}]"
            : "{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[]}";

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, CreateReader(request), new JsonRpcContext(RpcEndpoint.Http));

        records.Should().HaveCount(expectedRecordCount);
        records.Should().Contain(record => record.Contains("eth_blockNumber"));
        if (isBatch)
        {
            records.Should().Contain(record => record.Contains("net_version"));
        }
    }

    [Test]
    public async Task Single_request_params_document_is_disposed_after_sink_write()
    {
        JsonElement capturedParams = default;
        IJsonRpcService service = CreateService(capturedRequest =>
        {
            capturedParams = capturedRequest.Params;
            return new JsonRpcSuccessResponse { Id = capturedRequest.Id };
        });
        CollectingJsonRpcResponseSink sink = new()
        {
            OnSingleWrite = (_, _) => capturedParams.ValueKind.Should().Be(JsonValueKind.Array)
        };
        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None });

        await ProcessAsync(
            processor,
            CreateReader("{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[{\"a\":1}]}"),
            new JsonRpcContext(RpcEndpoint.Http),
            sink);

        Action readAfterProcessing = () => _ = capturedParams.ValueKind;
        readAfterProcessing.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task Batch_request_document_is_disposed_after_last_sink_write()
    {
        JsonElement capturedParams = default;
        IJsonRpcService service = CreateService(capturedRequest =>
        {
            capturedParams = capturedRequest.Params;
            return new JsonRpcSuccessResponse { Id = capturedRequest.Id };
        });
        CollectingJsonRpcResponseSink sink = new()
        {
            OnEndBatch = () => capturedParams.ValueKind.Should().Be(JsonValueKind.Array)
        };
        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None });

        await ProcessAsync(
            processor,
            CreateReader("[{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[1]},{\"id\":2,\"jsonrpc\":\"2.0\",\"method\":\"net_version\",\"params\":[2]}]"),
            new JsonRpcContext(RpcEndpoint.Http),
            sink);

        Action readAfterProcessing = () => _ = capturedParams.ValueKind;
        readAfterProcessing.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public async Task Response_disposables_run_after_sink_write()
    {
        bool disposed = false;
        bool disposedDuringWrite = true;
        IJsonRpcService service = CreateService(capturedRequest => new JsonRpcSuccessResponse(() => disposed = true) { Id = capturedRequest.Id });
        CollectingJsonRpcResponseSink sink = new()
        {
            OnSingleWrite = (_, _) => disposedDuringWrite = disposed
        };
        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None });

        await ProcessAsync(
            processor,
            CreateReader("{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[]}"),
            new JsonRpcContext(RpcEndpoint.Http),
            sink);

        disposedDuringWrite.Should().BeFalse();
        disposed.Should().BeTrue();
    }

    private static IJsonRpcService CreateService(Func<JsonRpcRequest, JsonRpcResponse> responseFactory)
    {
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>())
            .Returns(callInfo => responseFactory(callInfo.Arg<JsonRpcRequest>()));
        service.GetErrorResponse(0, null!).ReturnsForAnyArgs(new JsonRpcErrorResponse());
        service.GetErrorResponse(0, null!, null!, null!).ReturnsForAnyArgs(new JsonRpcErrorResponse());
        return service;
    }

    private static JsonRpcProcessor CreateShutdownProcessor(out IJsonRpcService service)
    {
        service = Substitute.For<IJsonRpcService>();
        service.GetErrorResponse(Arg.Any<int>(), Arg.Any<string>())
            .Returns(new JsonRpcErrorResponse { Error = new Error { Code = ErrorCodes.ResourceUnavailable, Message = "Shutting down" } });

        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        processExitSource.Token.Returns(new CancellationToken(canceled: true));
        return CreateProcessor(service, processExitSource: processExitSource);
    }

    private static JsonRpcConfig CreateRecorderConfig(RpcRecorderState recorderState) =>
        new()
        {
            RpcRecorderState = recorderState,
            RpcRecorderBaseFilePath = "rpc.{counter}.txt"
        };

    private static JsonRpcProcessor CreateRecordingProcessor(RpcRecorderState recorderState, List<string> records) =>
        CreateProcessor(CreateEchoService(), CreateRecorderConfig(recorderState), CreateRecordingFileSystem(records));

    private static IFileSystem CreateRecordingFileSystem(List<string> records)
    {
        IFile file = Substitute.For<IFile>();
        file.Create(Arg.Any<string>()).Returns((FileSystemStream)null!);
        file.When(static file => file.AppendAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(callInfo => records.Add(callInfo.ArgAt<string>(1)));

        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);
        return fileSystem;
    }

    private void AssertBatchItemsTypeMatchesFixtureMode(IReadOnlyList<JsonRpcResponse>? batchItems)
    {
        batchItems.Should().NotBeNull();
        batchItems.Should().AllSatisfy(AssertResponseTypeMatchesFixtureMode);
    }

    private void AssertResponseTypeMatchesFixtureMode(JsonRpcResponse response) =>
        response.Should().BeOfType(returnErrors ? typeof(JsonRpcErrorResponse) : typeof(JsonRpcSuccessResponse));

    private void AssertSingleResponse(CollectedJsonRpcResult result, bool shouldBeParseError = false)
    {
        result.Response.Should().NotBeNull();
        result.BatchItems.Should().BeNull();
        if (shouldBeParseError)
        {
            result.Response.Should().BeSameAs(_errorResponse);
        }
        else
        {
            result.Response.Should().NotBeSameAs(_errorResponse);
        }
    }

    [TestCaseSource(nameof(JsonRpcIdCases))]
    public async Task Can_process_ids(string idJson, JsonRpcId expectedId)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountRequest(idJson));
        result.Should().HaveCount(1);
        Assert.That(result[0].Response!.Id, Is.EqualTo(expectedId));
    }

    [Test]
    public async Task Can_process_uppercase_params()
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountRequest("67", "Params"));
        result.Should().HaveCount(1);
        Assert.That(result[0].Response!.Id, Is.EqualTo(new JsonRpcId(67)));
        AssertResponseTypeMatchesFixtureMode(result[0].Response!);
    }

    [TestCase(TransactionCountObjectParamsJson, TransactionCountObjectParamsJson, TestName = "Nested object params")]
    [TestCase(TransactionCountNestedArrayParamsJson, TransactionCountNestedArrayWithValueParamsJson, TestName = "Nested array params")]
    [TestCase(TransactionCountAddressParamJson, TransactionCountBlockParamJson, TestName = "Value params")]
    public async Task Can_process_batch_request_with_nonstandard_params(string firstParamsJson, string secondParamsJson)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountBatchRequest(firstParamsJson, secondParamsJson));
        result.Should().HaveCount(1);
        result[0].Response.Should().BeNull();
        AssertBatchItemsTypeMatchesFixtureMode(result[0].BatchItems);
        result[0].BatchItems.Should().HaveCount(2);
        result[0].BatchItems.Should().NotContain(_errorResponse);
    }

    [Test]
    public async Task Can_process_batch_request_with_invalid_object_params()
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountBatchRequest(TransactionCountInvalidObjectParamsJson, TransactionCountInvalidObjectParamsJson));
        result.Should().HaveCount(1);
        result[0].Response.Should().NotBeNull();
        result[0].Response.Should().BeOfType<JsonRpcErrorResponse>();
    }

    [TestCase(false, TestName = "All params present")]
    [TestCase(true, TestName = "Last params omitted")]
    public async Task Can_process_batch_request(bool omitLastParams)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountBatchRequest(4, omitLastParams));
        result.Should().HaveCount(1);
        result[0].BatchItems.Should().NotBeNull();
        result[0].Response.Should().BeNull();
    }

    [TestCaseSource(nameof(MultipleDocumentRequestCases))]
    public async Task Can_process_multiple_document_requests(string request, bool secondIsBatch, bool secondIsParseError)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(request, new JsonRpcContext(RpcEndpoint.Ws));
        result.Should().HaveCount(2);
        AssertSingleResponse(result[0]);
        if (secondIsBatch)
        {
            result[1].Response.Should().BeNull();
            AssertBatchItemsTypeMatchesFixtureMode(result[1].BatchItems);
            result[1].BatchItems.Should().HaveCount(2);
            result[1].BatchItems.Should().NotContain(_errorResponse);
        }
        else
        {
            AssertSingleResponse(result[1], secondIsParseError);
        }
    }

    [TestCase(false, 0, TestName = "Unauthenticated batch over limit is rejected")]
    [TestCase(true, 2, TestName = "Authenticated batch over limit is processed")]
    public async Task Batch_size_limit_respects_authentication(bool isAuthenticated, int expectedDispatchCount)
    {
        IJsonRpcService service = CreateEchoService();
        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { RpcRecorderState = RpcRecorderState.None, MaxBatchSize = 1 });
        using JsonRpcContext context = CreateHttpContext(isAuthenticated);

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, CreateReader(CreateTransactionCountBatchRequest(2)), context);

        result.Should().HaveCount(1);
        if (!isAuthenticated)
        {
            JsonRpcErrorResponse response = result[0].Response.Should().BeOfType<JsonRpcErrorResponse>().Subject;
            response.Error!.Code.Should().Be(ErrorCodes.LimitExceeded);
            result[0].BatchItems.Should().BeNull();
            await service.DidNotReceive().SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>());
            return;
        }

        result[0].Response.Should().BeNull();
        List<JsonRpcResponse> batchItems = result[0].BatchItems!;
        batchItems.Should().HaveCount(expectedDispatchCount);
        batchItems[0].Id.Should().Be(67);
        batchItems[1].Id.Should().Be(67);
        await service.Received(expectedDispatchCount).SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>());
    }

    [Test]
    public async Task Can_process_batch_request_with_result_limit([Values(false, true)] bool limit)
    {
        CollectingJsonRpcResponseSink sink = new() { StopAfterBatchItems = limit ? 1 : int.MaxValue };
        using CollectedJsonRpcResponses result = await ProcessAsync(
            Initialize(recorderState: RpcRecorderState.None),
            CreateReader(CreateTransactionCountBatchRequest(TransactionCountParamsJson, TransactionCountParamsJson)),
            new JsonRpcContext(RpcEndpoint.Http),
            sink);
        result[0].IsCollection.Should().BeTrue();
        result[0].BatchItems.Should().NotBeNull();
        IReadOnlyList<JsonRpcResponse> batchItems = result[0].BatchItems!;
        batchItems[0].Should().BeOfType(returnErrors ? typeof(JsonRpcErrorResponse) : typeof(JsonRpcSuccessResponse));
        batchItems[1].Should().BeOfType(limit || returnErrors ? typeof(JsonRpcErrorResponse) : typeof(JsonRpcSuccessResponse));
    }

    [TestCase("invalid", TestName = "Invalid JSON")]
    [TestCase("\"aaa\"", TestName = "String root")]
    [TestCase("null", TestName = "Null root")]
    public async Task Can_handle_invalid_or_value_request(string request)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(request);
        result.Should().HaveCount(1);
        result[0].Response.Should().BeSameAs(_errorResponse);
    }

    [TestCase("[]", 0, TestName = "Empty array")]
    [TestCase("[{},{},{}]", 3, TestName = "Array of empty requests")]
    public async Task Can_handle_empty_batch_requests(string request, int expectedBatchItems)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(request);
        result.Should().HaveCount(1);
        result[0].Response.Should().BeNull();
        result[0].BatchItems.Should().NotBeNull();
        result[0].BatchItems.Should().HaveCount(expectedBatchItems);
        Assert.That(result[0].BatchItems!.All(r => r != _errorResponse), Is.True);
    }

    [Test]
    public async Task Can_handle_empty_object_request()
    {
        using CollectedJsonRpcResponses result = await ProcessAsync("{}");
        result.Should().HaveCount(1);
        result[0].Response.Should().NotBeNull();
        result[0].BatchItems.Should().BeNull();
        result[0].Response.Should().NotBeSameAs(_errorResponse);
    }

    [Test]
    public async Task Should_stop_processing_when_shutdown_requested()
    {
        JsonRpcProcessor processor = CreateShutdownProcessor(out IJsonRpcService service);
        string request = CreateTransactionCountRequest("67");
        using CollectedJsonRpcResponses results = await ProcessAsync(processor, CreateReader(request), new JsonRpcContext(RpcEndpoint.Http));

        results.Should().HaveCount(1);
        results[0].Response.Should().BeOfType<JsonRpcErrorResponse>();
        ((JsonRpcErrorResponse)results[0].Response!).Error!.Code.Should().Be(ErrorCodes.ResourceUnavailable);
        await service.DidNotReceive().SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>());
    }

    [Test]
    public async Task Should_complete_pipe_reader_when_shutdown_requested()
    {
        JsonRpcProcessor processor = CreateShutdownProcessor(out _);
        Pipe pipe = new();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[]}"));

        using CollectedJsonRpcResponses results = await ProcessAsync(processor, pipe.Reader, new JsonRpcContext(RpcEndpoint.Http));

        results.Should().HaveCount(1);
        results[0].Response.Should().BeOfType<JsonRpcErrorResponse>();

        await FluentActions.Invoking(async () => await pipe.Reader.ReadAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public void Cannot_accept_null_file_system() => Assert.Throws<ArgumentNullException>(static () => new JsonRpcProcessor(Substitute.For<IJsonRpcService>(),
                                                             Substitute.For<IJsonRpcConfig>(),
                                                             null!, LimboLogs.Instance));

    [Test]
    public async Task Can_process_multiple_large_requests_arriving_in_chunks()
    {
        Pipe pipe = new();
        JsonRpcProcessor processor = Initialize(recorderState: RpcRecorderState.None);
        JsonRpcContext context = new(RpcEndpoint.Ws);

        List<string> requests = Enumerable.Range(0, 5)
            .Select(i => CreateLargeRequest(i, targetSize: 10_000))
            .ToList();

        string allRequestsJson = string.Join("\n", requests);
        byte[] bytes = Encoding.UTF8.GetBytes(allRequestsJson);

        ValueTask<CollectedJsonRpcResponses> processTask = ProcessAsync(processor, pipe.Reader, context);

        const int chunkSize = 1024;
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            int size = Math.Min(chunkSize, bytes.Length - i);
            await pipe.Writer.WriteAsync(new ReadOnlyMemory<byte>(bytes, i, size));
            await Task.Yield();
        }
        await pipe.Writer.CompleteAsync();

        // Verify all 5 requests processed
        using CollectedJsonRpcResponses results = await processTask;
        results.Should().HaveCount(5);
        for (int i = 0; i < 5; i++)
        {
            results[i].Response.Should().NotBeNull();
        }
    }

    private static string CreateLargeRequest(int id, int targetSize)
    {
        StringBuilder sb = new();
        sb.Append($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"test_method\",\"params\":[");

        int currentSize = sb.Length + 2; // account for closing ]}
        bool first = true;
        int paramIndex = 0;
        while (currentSize < targetSize)
        {
            string param = $"\"param_{paramIndex++}_padding\"";
            if (!first) sb.Append(',');
            sb.Append(param);
            currentSize += param.Length + (first ? 0 : 1);
            first = false;
        }

        sb.Append("]}");
        return sb.ToString();
    }

    [TestCase("foo_unregistered", true, RpcReport.UnknownMethod, false, TestName = "Unknown method")]
    [TestCase("eth_getTransactionCount", false, "eth_getTransactionCount", true, TestName = "Resolved method")]
    public async Task Response_report_keeps_expected_method_label(string methodName, bool methodNotFound, string expectedReportMethod, bool expectedSuccess)
    {
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>())
            .Returns(ci =>
            {
                JsonRpcRequest request = ci.Arg<JsonRpcRequest>();
                return methodNotFound
                    ? new JsonRpcErrorResponse { Id = request.Id, Error = new Error { Code = ErrorCodes.MethodNotFound, Message = "Method not found" } }
                    : new JsonRpcSuccessResponse { Id = request.Id };
            });

        JsonRpcProcessor processor = CreateProcessor(service);
        using CollectedJsonRpcResponses result = await ProcessAsync(processor, CreateReader($$"""{"id":1,"jsonrpc":"2.0","method":"{{methodName}}","params":[]}"""), new JsonRpcContext(RpcEndpoint.Http));

        result.Should().HaveCount(1);
        result[0].Report.Should().NotBeNull();
        result[0].Report!.Value.Method.Should().Be(expectedReportMethod);
        result[0].Report!.Value.Success.Should().Be(expectedSuccess);
    }

    [TestCase(50, false, TestName = "Input below the 64-depth limit is accepted")]
    [TestCase(65, true, TestName = "Input above the 64-depth limit is rejected as parse error")]
    public async Task Input_depth_is_bounded_by_reader_default_max_depth(int paramNestingDepth, bool expectParseError)
    {
        bool requestCaptured = false;
        string? capturedMethod = null;
        int observedDepth = 0;
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>())
            .Returns(ci =>
            {
                JsonRpcRequest request = ci.Arg<JsonRpcRequest>();
                requestCaptured = true;
                capturedMethod = request.Method;

                JsonElement paramsArr = request.Params;
                paramsArr.ValueKind.Should().Be(JsonValueKind.Array);
                paramsArr.GetArrayLength().Should().Be(1);

                observedDepth = 1;
                JsonElement node = paramsArr[0];
                while (node.ValueKind == JsonValueKind.Array && node.GetArrayLength() > 0)
                {
                    node = node[0];
                    observedDepth++;
                }
                node.ValueKind.Should().Be(JsonValueKind.Array);
                node.GetArrayLength().Should().Be(0, "innermost array of the constructed chain is empty");

                return new JsonRpcSuccessResponse { Id = request.Id };
            });
        service.GetErrorResponse(0, null!).ReturnsForAnyArgs(_errorResponse);
        service.GetErrorResponse(0, null!, null!, null!).ReturnsForAnyArgs(_errorResponse);

        JsonRpcConfig config = new() { RpcRecorderState = RpcRecorderState.None };
        JsonRpcProcessor processor = CreateProcessor(service, config);

        string nested = BuildNestedArrayParams(paramNestingDepth);
        string request = $"{{\"id\":1,\"jsonrpc\":\"2.0\",\"method\":\"eth_getTransactionCount\",\"params\":[{nested}]}}";

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, CreateReader(request), new JsonRpcContext(RpcEndpoint.Http));

        result.Should().HaveCount(1);

        if (expectParseError)
        {
            result[0].Response.Should().BeSameAs(_errorResponse);
            requestCaptured.Should().BeFalse("a depth-rejected request must never reach the service");
        }
        else
        {
            result[0].Response.Should().BeOfType<JsonRpcSuccessResponse>();
            result[0].Response!.Id.Should().Be(1);
            requestCaptured.Should().BeTrue();
            capturedMethod.Should().Be("eth_getTransactionCount");
            observedDepth.Should().Be(paramNestingDepth);
        }
    }

    private static string BuildNestedArrayParams(int depth)
    {
        StringBuilder sb = new(depth * 2);
        for (int i = 0; i < depth; i++) sb.Append('[');
        for (int i = 0; i < depth; i++) sb.Append(']');
        return sb.ToString();
    }

    private static JsonRpcContext CreateHttpContext(bool isAuthenticated = false) =>
        isAuthenticated
            ? new JsonRpcContext(RpcEndpoint.Http, url: new JsonRpcUrl(string.Empty, string.Empty, 0, RpcEndpoint.Http, true, []))
            : new JsonRpcContext(RpcEndpoint.Http);

    private sealed class CollectingJsonRpcResponseSink : IJsonRpcResponseSink
    {
        private CollectedJsonRpcResult? _currentBatch;

        public CollectedJsonRpcResponses Responses { get; } = new();
        public List<JsonRpcResponse> Singles { get; } = [];
        public List<JsonRpcResponse> BatchItems { get; } = [];
        public List<string> BatchEvents { get; } = [];
        public Action<JsonRpcResponse, RpcReport>? OnSingleWrite { get; init; }
        public Action<JsonRpcResponse, RpcReport>? OnBatchItemWrite { get; init; }
        public Action? OnEndBatch { get; init; }
        public int StopAfterBatchItems { get; init; } = int.MaxValue;
        public long BytesWritten { get; private set; }
        public bool StopRequested { get; private set; }

        public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            OnSingleWrite?.Invoke(response, report);
            Singles.Add(response);
            Responses.AddSingle(response, report);
            BytesWritten++;
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginBatchAsync(CancellationToken cancellationToken)
        {
            BatchEvents.Add("begin");
            _currentBatch = Responses.AddBatch();
            BytesWritten++;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            OnBatchItemWrite?.Invoke(response, report);
            BatchEvents.Add("item");
            BatchItems.Add(response);
            _currentBatch!.AddBatchItem(response, report);
            BytesWritten++;
            StopRequested = BatchItems.Count >= StopAfterBatchItems;
            return ValueTask.CompletedTask;
        }

        public ValueTask EndBatchAsync(CancellationToken cancellationToken)
        {
            OnEndBatch?.Invoke();
            BatchEvents.Add("end");
            _currentBatch = null;
            BytesWritten++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CollectedJsonRpcResponses : IReadOnlyList<CollectedJsonRpcResult>, IDisposable
    {
        private readonly List<CollectedJsonRpcResult> _results = [];

        public int Count => _results.Count;
        public CollectedJsonRpcResult this[int index] => _results[index];

        public void AddSingle(JsonRpcResponse response, RpcReport report) =>
            _results.Add(CollectedJsonRpcResult.Single(response, report));

        public CollectedJsonRpcResult AddBatch()
        {
            CollectedJsonRpcResult batch = CollectedJsonRpcResult.Batch();
            _results.Add(batch);
            return batch;
        }

        public IEnumerator<CollectedJsonRpcResult> GetEnumerator() => _results.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose()
        {
            foreach (CollectedJsonRpcResult result in _results)
            {
                result.Dispose();
            }
        }
    }

    private sealed class CollectedJsonRpcResult : IDisposable
    {
        private CollectedJsonRpcResult(JsonRpcResponse? response, RpcReport? report, List<JsonRpcResponse>? batchItems, List<RpcReport>? batchReports)
        {
            Response = response;
            Report = report;
            BatchItems = batchItems;
            BatchReports = batchReports;
        }

        public JsonRpcResponse? Response { get; }
        public RpcReport? Report { get; }
        public List<JsonRpcResponse>? BatchItems { get; }
        public List<RpcReport>? BatchReports { get; }
        public bool IsCollection => BatchItems is not null;

        public static CollectedJsonRpcResult Single(JsonRpcResponse response, RpcReport report) =>
            new(response, report, null, null);

        public static CollectedJsonRpcResult Batch() =>
            new(null, null, [], []);

        public void AddBatchItem(JsonRpcResponse response, RpcReport report)
        {
            BatchItems!.Add(response);
            BatchReports!.Add(report);
        }

        public void Dispose()
        {
            Response?.Dispose();
            if (BatchItems is null)
            {
                return;
            }

            foreach (JsonRpcResponse response in BatchItems)
            {
                response.Dispose();
            }
        }
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public BufferSegment Append(ReadOnlyMemory<byte> memory)
        {
            BufferSegment segment = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
