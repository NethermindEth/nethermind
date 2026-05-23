// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Pipelines;
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
    private static readonly object[][] JsonRpcIdCases =
    [
        ["\"840b55c4-18b0-431c-be1d-6d22198b53f2\"", new JsonRpcId("840b55c4-18b0-431c-be1d-6d22198b53f2")],
        ["12345678901234567890", new JsonRpcId(decimal.Parse("12345678901234567890"))],
        ["\"0xa1aa12434\"", new JsonRpcId("0xa1aa12434")],
        ["67", new JsonRpcId(67)],
        ["9223372036854775807", new JsonRpcId(long.MaxValue)],
        ["\";\\\\\\\"\"", new JsonRpcId(";\\\"")],
        ["null", JsonRpcId.Null],
    ];

    static JsonRpcProcessorTests()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(KnownRpcMethodNames).Module.ModuleHandle);
        RuntimeHelpers.RunModuleConstructor(typeof(Nethermind.Merge.Plugin.IEngineRpcModule).Module.ModuleHandle);
    }

    private JsonRpcProcessor CreateFixtureProcessor(IJsonRpcConfig? config = null) =>
        CreateProcessor(CreateService(request => returnErrors ? new JsonRpcErrorResponse { Id = request.Id } : new JsonRpcSuccessResponse { Id = request.Id }, _errorResponse), config);

    private static JsonRpcProcessor CreateProcessor(IJsonRpcService service, IJsonRpcConfig? config = null, IFileSystem? fileSystem = null, IProcessExitSource? processExitSource = null) =>
        new(service, config ?? new JsonRpcConfig(), fileSystem ?? Substitute.For<IFileSystem>(), LimboLogs.Instance, processExitSource);

    private static JsonRpcContext CreateHttpContext() => new(RpcEndpoint.Http);

    [Test]
    public async Task Http_engine_newPayloadV4_keeps_envelope_and_params_on_direct_utf8_path()
    {
        string? capturedMethod = null;
        bool capturedRawParams = false;
        JsonValueKind capturedParamsKind = JsonValueKind.Undefined;
        IJsonRpcService service = CreateService(request =>
        {
            capturedMethod = request.Method;
            capturedRawParams = !request.ParamsUtf8.IsEmpty;
            capturedParamsKind = request.ParamsKind;
            return new JsonRpcSuccessResponse { Id = request.Id };
        });

        JsonRpcProcessor processor = CreateProcessor(service);

        await ProcessAsync(processor, CreateRequest("1", "engine_newPayloadV4", "[{\"parentHash\":\"0x0\"},[],null,null]"), CreateHttpContext());

        capturedMethod.Should().Be("engine_newPayloadV4");
        capturedRawParams.Should().BeTrue();
        capturedParamsKind.Should().Be(JsonValueKind.Array);
    }

    [Test]
    public async Task Http_generated_method_names_use_cached_instances(
        [Values("engine_newPayloadV4", "engine_getBlobsV2", "eth_call", "eth_getBlockByNumber", "eth_chainId", "eth_unknown")] string methodName,
        [Values(false, true)] bool inBatch)
    {
        bool expectedCached = methodName != "eth_unknown";
        string? capturedMethod = null;
        IJsonRpcService service = CreateService(request =>
        {
            capturedMethod = request.Method;
            return new JsonRpcSuccessResponse { Id = request.Id };
        });

        JsonRpcProcessor processor = CreateProcessor(service);

        string request = inBatch ? CreateBatchRequest(CreateRequest("1", methodName)) : CreateRequest("1", methodName);

        await ProcessAsync(processor, request, CreateHttpContext());

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
        methodName.Should().BeSameAs(TryGetKnownMethodName("engine_newPayloadV4"));
    }

    [Test]
    public void Generated_known_method_names_cover_rpc_module_interfaces()
    {
        HashSet<string> knownMethods = new(KnownRpcMethodNames.All, StringComparer.Ordinal);
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

    private static IEnumerable<TestCaseData> MultipleDocumentRequestCases()
    {
        yield return new TestCaseData(CreateTransactionCountRequest("67") + "\r\n" + CreateTransactionCountRequest("68"), false, false).SetName("Two single requests");
        yield return new TestCaseData(CreateTransactionCountRequest("67") + CreateTransactionCountBatchRequest(2), true, false).SetName("Single request and batch");
        yield return new TestCaseData(CreateTransactionCountRequest("67") + CreateTransactionCountRequest("68")[..^1], false, true).SetName("Second request not closed");
        yield return new TestCaseData(CreateTransactionCountRequest("67") + "{aaa}", false, true).SetName("Second request invalid");
    }

    private ValueTask<CollectedJsonRpcResponses> ProcessAsync(string request, JsonRpcContext? context = null, JsonRpcConfig? config = null) =>
        ProcessAsync(CreateFixtureProcessor(config), CreateReader(request), context ?? CreateHttpContext());

    private static ValueTask<CollectedJsonRpcResponses> ProcessAsync(JsonRpcProcessor processor, string request, JsonRpcContext context, CollectingJsonRpcResponseSink? sink = null) =>
        ProcessAsync(processor, CreateReader(request), context, sink);

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
        JsonRpcProcessor processor = CreateProcessor(service);
        CollectingJsonRpcResponseSink sink = new() { StopAfterBatchItems = 1 };

        await ProcessAsync(processor,
            CreateBatchRequest(CreateRequest("1", "eth_getTransactionCount"), CreateRequest("2", "eth_blockNumber"), CreateRequest("3", "net_version")),
            CreateHttpContext(),
            sink);

        List<JsonRpcResponse> batchItems = sink.Responses[0].BatchItems!;
        batchItems.Should().HaveCount(3);
        batchItems[0].Should().BeOfType<JsonRpcSuccessResponse>();
        JsonRpcErrorResponse second = batchItems[1].Should().BeOfType<JsonRpcErrorResponse>().Subject;
        JsonRpcErrorResponse third = batchItems[2].Should().BeOfType<JsonRpcErrorResponse>().Subject;
        second.Id.Should().Be(2);
        third.Id.Should().Be(3);
        await service.Received(1).SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>());
    }

    [Test]
    public async Task Sink_processor_entry_point_writes_to_sink()
    {
        CollectingJsonRpcResponseSink sink = new();
        JsonRpcProcessor processor = CreateFixtureProcessor();

        await ProcessAsync(processor, CreateTransactionCountRequest("67", paramsJson: "[]"), CreateHttpContext(), sink);

        AssertSingleResponse(sink.Responses).Response!.Id.Should().Be(67);
    }

    [Test]
    public async Task Sink_processor_entry_point_reads_params_through_envelope_reader()
    {
        bool inspected = false;
        IJsonRpcService service = CreateService(request =>
        {
            request.Params.ValueKind.Should().Be(JsonValueKind.Array);
            request.Params[0].GetProperty("a").GetInt32().Should().Be(2);
            inspected = true;
            return new JsonRpcSuccessResponse { Id = request.Id };
        });

        JsonRpcProcessor processor = CreateProcessor(service);
        CollectingJsonRpcResponseSink sink = new();

        await ProcessAsync(processor, " \r\n" + CreateTransactionCountRequest("67", paramsJson: "[{\"a\":2}]") + "\t ", CreateHttpContext(), sink);

        inspected.Should().BeTrue();
        AssertSingleResponse(sink.Responses).Response!.Id.Should().Be(67);
    }

    private static PipeReader CreateReader(string request) =>
        PipeReader.Create(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(request)));

    private static string CreateRequest(string idJson, string method, string paramsJson = "[]") => $$"""{"id":{{idJson}},"jsonrpc":"2.0","method":"{{method}}","params":{{paramsJson}}}""";

    private static string CreateBatchRequest(params string[] requests) => "[" + string.Join(",", requests) + "]";

    private static string CreateTransactionCountRequest(string idJson, string? paramsName = "params", string paramsJson = TransactionCountParamsJson) =>
        paramsName is null
            ? $$"""{"id":{{idJson}},"jsonrpc":"2.0","method":"eth_getTransactionCount"}"""
            : $$"""{"id":{{idJson}},"jsonrpc":"2.0","method":"eth_getTransactionCount","{{paramsName}}":{{paramsJson}}}""";

    private static string CreateTransactionCountBatchRequest(int count, bool omitLastParams = false)
    {
        string[] requests = new string[count];
        for (int i = 0; i < count; i++)
        {
            requests[i] = CreateTransactionCountRequest("67", omitLastParams && i == count - 1 ? null : "params");
        }

        return CreateBatchRequest(requests);
    }

    private static string CreateTransactionCountBatchRequest(params string[] paramsJsons)
    {
        string[] requests = new string[paramsJsons.Length];
        for (int i = 0; i < paramsJsons.Length; i++)
        {
            requests[i] = CreateTransactionCountRequest("67", paramsJson: paramsJsons[i]);
        }

        return CreateBatchRequest(requests);
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
        JsonRpcEnvelope envelope = ReadEnvelope("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_newPayloadV3\",\"params\":[1,{\"a\":2}],\"extra\":{\"ignored\":true}}", out byte[] body);

        envelope.JsonRpc.Should().Be("2.0");
        envelope.Id.Should().Be(new JsonRpcId(1));
        ReferenceEquals(envelope.Method, "engine_newPayloadV3").Should().BeTrue();
        envelope.HasParams.Should().BeTrue();
        envelope.ParamsKind.Should().Be(JsonValueKind.Array);
        Encoding.UTF8.GetString(body, envelope.ParamsStart, envelope.ParamsLength).Should().Be("[1,{\"a\":2}]");
    }

    [Test]
    public void JsonRpcEnvelopeReader_reads_matching_shape_from_json_element()
    {
        JsonRpcEnvelope envelope = ReadEnvelope(CreateRequest("\"\\u0041\\n\"", "engine_newPayloadV4", "[{\"a\":2}]"), out byte[] body);
        using JsonDocument document = JsonDocument.Parse(body);

        JsonRpcEnvelope elementEnvelope = JsonRpcEnvelopeReader.Read(document.RootElement, out JsonElement paramsElement);

        elementEnvelope.JsonRpc.Should().Be(envelope.JsonRpc);
        elementEnvelope.Id.Should().Be(envelope.Id);
        elementEnvelope.Method.Should().BeSameAs(envelope.Method);
        elementEnvelope.HasParams.Should().Be(envelope.HasParams);
        elementEnvelope.ParamsKind.Should().Be(envelope.ParamsKind);
        paramsElement.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Test]
    public void JsonRpcEnvelopeReader_reads_unknown_method_and_missing_params()
    {
        JsonRpcEnvelope envelope = ReadEnvelope("{\"id\":12345678901234567890,\"method\":\"eth_unknown\"}", out _);

        envelope.Id.Should().Be(new JsonRpcId(decimal.Parse("12345678901234567890")));
        envelope.Method.Should().Be("eth_unknown");
        envelope.HasParams.Should().BeFalse();
        envelope.ParamsKind.Should().Be(JsonValueKind.Undefined);
    }

    [Test]
    public void JsonRpcEnvelopeReader_echoes_validated_raw_string_id_token()
    {
        JsonRpcEnvelope envelope = ReadEnvelope("{\"id\":\"\\u0041\\n\",\"method\":\"eth_blockNumber\"}", out _);

        JsonRpcId expectedId = new("A\n");
        envelope.Id.Should().Be(expectedId);
        envelope.Id.GetHashCode().Should().Be(expectedId.GetHashCode());
        object? firstObjectId = envelope.Id.ToObject();
        envelope.Id.ToObject().Should().BeSameAs(firstObjectId);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            envelope.Id.WriteTo(writer);
        }

        Encoding.UTF8.GetString(buffer.WrittenSpan).Should().Be("\"\\u0041\\n\"");
    }

    [Test]
    public void JsonRpcEnvelopeReader_keeps_numeric_ids_typed_and_preserves_raw_decimal_token()
    {
        JsonRpcEnvelope envelope = ReadEnvelope("{\"id\":1e2,\"method\":\"eth_blockNumber\"}", out _);

        envelope.Id.TryGetDecimal(out decimal id).Should().BeTrue();
        id.Should().Be(100m);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            envelope.Id.WriteTo(writer);
        }

        Encoding.UTF8.GetString(buffer.WrittenSpan).Should().Be("1e2");
    }

    [Test]
    public void JsonRpcEnvelopeReader_returns_false_for_non_object_root()
    {
        JsonRpcEnvelopeReader reader = new(Encoding.UTF8.GetBytes("[{\"id\":1}]"));
        reader.TryRead(out JsonRpcEnvelope envelope).Should().BeFalse();
        envelope.Should().Be(default(JsonRpcEnvelope));
    }

    [Test]
    public void JsonRpcEnvelopeReader_rejects_fractional_numeric_ids()
    {
        Action read = () => ReadEnvelope("{\"id\":1.1,\"method\":\"eth_blockNumber\"}", out _);

        read.Should().Throw<JsonException>();
    }

    private static JsonRpcEnvelope ReadEnvelope(string request, out byte[] body)
    {
        body = Encoding.UTF8.GetBytes(request);
        JsonRpcEnvelopeReader reader = new(body);
        reader.TryRead(out JsonRpcEnvelope envelope).Should().BeTrue();
        return envelope;
    }

    private static IJsonRpcService CreateEchoService() =>
        CreateService(static request => new JsonRpcSuccessResponse { Id = request.Id });

    [TestCase(RpcEndpoint.Http)]
    [TestCase(RpcEndpoint.Ws)]
    [TestCase(RpcEndpoint.IPC)]
    public async Task Request_recorder_captures_payload(RpcEndpoint endpoint)
    {
        List<string> records = [];
        JsonRpcProcessor processor = CreateRecordingProcessor(RpcRecorderState.Request, records);

        string request = endpoint == RpcEndpoint.Http
            ? CreateRequest("1", "eth_blockNumber")
            : CreateRequest("1", "eth_blockNumber") + CreateRequest("2", "net_version");

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, request, new JsonRpcContext(endpoint));

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
            ? CreateBatchRequest(CreateRequest("1", "eth_blockNumber"), CreateRequest("2", "net_version"))
            : CreateRequest("1", "eth_blockNumber");

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, request, CreateHttpContext());

        records.Should().HaveCount(expectedRecordCount);
        records.Should().Contain(record => record.Contains("eth_blockNumber"));
        if (isBatch)
        {
            records.Should().Contain(record => record.Contains("net_version"));
        }
    }

    [TestCase(false, TestName = "Single request")]
    [TestCase(true, TestName = "Batch request")]
    public async Task Params_document_is_disposed_after_sink_write(bool isBatch)
    {
        JsonElement capturedParams = default;
        IJsonRpcService service = CreateService(capturedRequest =>
        {
            capturedParams = capturedRequest.Params;
            return new JsonRpcSuccessResponse { Id = capturedRequest.Id };
        });
        CollectingJsonRpcResponseSink sink = isBatch
            ? new() { OnEndBatch = () => capturedParams.ValueKind.Should().Be(JsonValueKind.Array) }
            : new() { OnSingleWrite = (_, _) => capturedParams.ValueKind.Should().Be(JsonValueKind.Array) };
        JsonRpcProcessor processor = CreateProcessor(service);
        string request = isBatch
            ? CreateBatchRequest(CreateRequest("1", "eth_blockNumber", "[1]"), CreateRequest("2", "net_version", "[2]"))
            : CreateRequest("1", "eth_blockNumber", "[{\"a\":1}]");

        await ProcessAsync(processor, request, CreateHttpContext(), sink);

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
        JsonRpcProcessor processor = CreateProcessor(service);

        await ProcessAsync(processor, CreateRequest("1", "eth_blockNumber"), CreateHttpContext(), sink);

        disposedDuringWrite.Should().BeFalse();
        disposed.Should().BeTrue();
    }

    private static IJsonRpcService CreateService(Func<JsonRpcRequest, JsonRpcResponse> responseFactory, JsonRpcErrorResponse? errorResponse = null)
    {
        IJsonRpcService service = Substitute.For<IJsonRpcService>();
        service.SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>())
            .Returns(callInfo => responseFactory(callInfo.Arg<JsonRpcRequest>()));
        if (errorResponse is not null)
        {
            service.GetErrorResponse(0, null!).ReturnsForAnyArgs(errorResponse);
            service.GetErrorResponse(0, null!, Arg.Any<JsonRpcId>(), null).ReturnsForAnyArgs(errorResponse);
            return service;
        }

        service.GetErrorResponse(Arg.Any<int>(), Arg.Any<string>())
            .Returns(static ci => new JsonRpcErrorResponse { Error = new Error { Code = ci.ArgAt<int>(0), Message = ci.ArgAt<string>(1) } });
        service.GetErrorResponse(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<JsonRpcId>(), Arg.Any<string?>())
            .Returns(static ci => new JsonRpcErrorResponse
            {
                Id = ci.ArgAt<JsonRpcId>(2),
                Error = new Error { Code = ci.ArgAt<int>(0), Message = ci.ArgAt<string>(1) }
            });
        return service;
    }

    private static JsonRpcProcessor CreateShutdownProcessor(out IJsonRpcService service)
    {
        JsonRpcErrorResponse shutdownResponse = new() { Error = new Error { Code = ErrorCodes.ResourceUnavailable, Message = "Shutting down" } };
        service = CreateService(static request => new JsonRpcSuccessResponse { Id = request.Id }, shutdownResponse);

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

    private CollectedJsonRpcResult AssertBatchResponse(CollectedJsonRpcResult result, int expectedCount)
    {
        result.Response.Should().BeNull();
        result.BatchItems.Should().NotBeNull();
        result.BatchItems.Should().HaveCount(expectedCount);
        if (expectedCount != 0)
        {
            result.BatchItems.Should().AllSatisfy(AssertResponseTypeMatchesFixtureMode);
        }

        result.BatchItems.Should().NotContain(_errorResponse);
        return result;
    }

    private CollectedJsonRpcResult AssertBatchResponse(CollectedJsonRpcResponses responses, int expectedCount) =>
        AssertBatchResponse(AssertOnlyResult(responses), expectedCount);

    private void AssertResponseTypeMatchesFixtureMode(JsonRpcResponse response) =>
        response.Should().BeOfType(returnErrors ? typeof(JsonRpcErrorResponse) : typeof(JsonRpcSuccessResponse));

    private CollectedJsonRpcResult AssertSingleResponse(CollectedJsonRpcResult result, bool shouldBeParseError = false)
    {
        result.Response.Should().NotBeNull();
        result.BatchItems.Should().BeNull();
        ReferenceEquals(result.Response, _errorResponse).Should().Be(shouldBeParseError);
        return result;
    }

    private CollectedJsonRpcResult AssertSingleResponse(CollectedJsonRpcResponses responses, bool shouldBeParseError = false) =>
        AssertSingleResponse(AssertOnlyResult(responses), shouldBeParseError);

    private static CollectedJsonRpcResult AssertOnlyResult(CollectedJsonRpcResponses responses)
    {
        responses.Should().HaveCount(1);
        return responses[0];
    }

    [TestCaseSource(nameof(JsonRpcIdCases))]
    public async Task Can_process_ids(string idJson, JsonRpcId expectedId)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountRequest(idJson));
        Assert.That(AssertSingleResponse(result).Response!.Id, Is.EqualTo(expectedId));
    }

    [Test]
    public async Task Can_process_uppercase_params()
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountRequest("67", "Params"));
        JsonRpcResponse response = AssertSingleResponse(result).Response!;
        Assert.That(response.Id, Is.EqualTo(new JsonRpcId(67)));
        AssertResponseTypeMatchesFixtureMode(response);
    }

    [TestCase(TransactionCountObjectParamsJson, TransactionCountObjectParamsJson, false, TestName = "Nested object params")]
    [TestCase(TransactionCountNestedArrayParamsJson, TransactionCountNestedArrayWithValueParamsJson, false, TestName = "Nested array params")]
    [TestCase(TransactionCountAddressParamJson, TransactionCountBlockParamJson, false, TestName = "Value params")]
    [TestCase(TransactionCountInvalidObjectParamsJson, TransactionCountInvalidObjectParamsJson, true, TestName = "Invalid object params")]
    public async Task Can_process_batch_request_with_nonstandard_params(string firstParamsJson, string secondParamsJson, bool expectSingleError)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountBatchRequest(firstParamsJson, secondParamsJson));
        if (!expectSingleError)
        {
            AssertBatchResponse(result, 2);
            return;
        }

        AssertOnlyResult(result).Response.Should().BeOfType<JsonRpcErrorResponse>();
    }

    [TestCase(false, TestName = "All params present")]
    [TestCase(true, TestName = "Last params omitted")]
    public async Task Can_process_batch_request(bool omitLastParams)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(CreateTransactionCountBatchRequest(4, omitLastParams));
        AssertBatchResponse(result, 4);
    }

    [TestCaseSource(nameof(MultipleDocumentRequestCases))]
    public async Task Can_process_multiple_document_requests(string request, bool secondIsBatch, bool secondIsParseError)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(request, new JsonRpcContext(RpcEndpoint.Ws));
        result.Should().HaveCount(2);
        AssertSingleResponse(result[0]);
        if (secondIsBatch)
        {
            AssertBatchResponse(result[1], 2);
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
        JsonRpcProcessor processor = CreateProcessor(service, new JsonRpcConfig { MaxBatchSize = 1 });
        using JsonRpcContext context = isAuthenticated
            ? new JsonRpcContext(RpcEndpoint.Http, url: new JsonRpcUrl(string.Empty, string.Empty, 0, RpcEndpoint.Http, true, []))
            : CreateHttpContext();

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, CreateTransactionCountBatchRequest(2), context);

        CollectedJsonRpcResult response = AssertOnlyResult(result);
        if (!isAuthenticated)
        {
            JsonRpcErrorResponse errorResponse = response.Response.Should().BeOfType<JsonRpcErrorResponse>().Subject;
            errorResponse.Error!.Code.Should().Be(ErrorCodes.LimitExceeded);
            response.BatchItems.Should().BeNull();
            await service.DidNotReceive().SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>());
            return;
        }

        response.Response.Should().BeNull();
        List<JsonRpcResponse> batchItems = response.BatchItems!;
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
            CreateFixtureProcessor(),
            CreateTransactionCountBatchRequest(TransactionCountParamsJson, TransactionCountParamsJson),
            CreateHttpContext(),
            sink);
        CollectedJsonRpcResult response = AssertOnlyResult(result);
        response.IsCollection.Should().BeTrue();
        response.BatchItems.Should().NotBeNull();
        IReadOnlyList<JsonRpcResponse> batchItems = response.BatchItems!;
        batchItems[0].Should().BeOfType(returnErrors ? typeof(JsonRpcErrorResponse) : typeof(JsonRpcSuccessResponse));
        batchItems[1].Should().BeOfType(limit || returnErrors ? typeof(JsonRpcErrorResponse) : typeof(JsonRpcSuccessResponse));
    }

    [TestCase("invalid", true, null, TestName = "Invalid JSON")]
    [TestCase("\"aaa\"", true, null, TestName = "String root")]
    [TestCase("null", true, null, TestName = "Null root")]
    [TestCase("{}", false, null, TestName = "Empty object")]
    [TestCase("[]", false, 0, TestName = "Empty array")]
    [TestCase("[{},{},{}]", false, 3, TestName = "Array of empty requests")]
    public async Task Can_handle_request_shapes(string request, bool shouldBeParseError, int? expectedBatchItems)
    {
        using CollectedJsonRpcResponses result = await ProcessAsync(request);
        if (expectedBatchItems is null)
        {
            AssertSingleResponse(result, shouldBeParseError);
            return;
        }

        AssertBatchResponse(result, expectedBatchItems.Value);
    }

    [Test]
    public async Task Should_stop_processing_when_shutdown_requested()
    {
        JsonRpcProcessor processor = CreateShutdownProcessor(out IJsonRpcService service);
        string request = CreateTransactionCountRequest("67");
        using CollectedJsonRpcResponses results = await ProcessAsync(processor, request, CreateHttpContext());

        JsonRpcResponse response = AssertSingleResponse(results).Response!;
        response.Should().BeOfType<JsonRpcErrorResponse>();
        ((JsonRpcErrorResponse)response).Error!.Code.Should().Be(ErrorCodes.ResourceUnavailable);
        await service.DidNotReceive().SendRequestAsync(Arg.Any<JsonRpcRequest>(), Arg.Any<JsonRpcContext>());
    }

    [Test]
    public async Task Should_complete_pipe_reader_when_shutdown_requested()
    {
        JsonRpcProcessor processor = CreateShutdownProcessor(out _);
        Pipe pipe = new();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(CreateRequest("1", "eth_blockNumber")));

        using CollectedJsonRpcResponses results = await ProcessAsync(processor, pipe.Reader, CreateHttpContext());

        AssertSingleResponse(results).Response.Should().BeOfType<JsonRpcErrorResponse>();

        await FluentActions.Invoking(async () => await pipe.Reader.ReadAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public void Cannot_accept_null_file_system() =>
        Assert.Throws<ArgumentNullException>(static () => new JsonRpcProcessor(Substitute.For<IJsonRpcService>(), Substitute.For<IJsonRpcConfig>(), null!, LimboLogs.Instance));

    [Test]
    public async Task Can_process_multiple_large_requests_arriving_in_chunks()
    {
        Pipe pipe = new();
        JsonRpcProcessor processor = CreateFixtureProcessor();
        JsonRpcContext context = new(RpcEndpoint.Ws);

        string[] requests = new string[5];
        for (int i = 0; i < requests.Length; i++) requests[i] = CreateLargeRequest(i, targetSize: 10_000);
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", requests));

        ValueTask<CollectedJsonRpcResponses> processTask = ProcessAsync(processor, pipe.Reader, context);

        const int chunkSize = 1024;
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            int size = Math.Min(chunkSize, bytes.Length - i);
            await pipe.Writer.WriteAsync(bytes.AsMemory(i, size));
            await Task.Yield();
        }
        await pipe.Writer.CompleteAsync();

        using CollectedJsonRpcResponses results = await processTask;
        results.Should().HaveCount(5);
        for (int i = 0; i < 5; i++)
        {
            results[i].Response.Should().NotBeNull();
        }
    }

    private static string CreateLargeRequest(int id, int targetSize)
    {
        StringBuilder sb = new($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"test_method\",\"params\":[");

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
        IJsonRpcService service = CreateService(request => methodNotFound
            ? new JsonRpcErrorResponse { Id = request.Id, Error = new Error { Code = ErrorCodes.MethodNotFound, Message = "Method not found" } }
            : new JsonRpcSuccessResponse { Id = request.Id });

        JsonRpcProcessor processor = CreateProcessor(service);
        using CollectedJsonRpcResponses result = await ProcessAsync(processor, CreateRequest("1", methodName), CreateHttpContext());

        RpcReport? report = AssertOnlyResult(result).Report;
        report.Should().NotBeNull();
        report!.Value.Method.Should().Be(expectedReportMethod);
        report!.Value.Success.Should().Be(expectedSuccess);
    }

    [TestCase(50, false, TestName = "Input below the 64-depth limit is accepted")]
    [TestCase(65, true, TestName = "Input above the 64-depth limit is rejected as parse error")]
    public async Task Input_depth_is_bounded_by_reader_default_max_depth(int paramNestingDepth, bool expectParseError)
    {
        bool requestCaptured = false;
        string? capturedMethod = null;
        int observedDepth = 0;
        IJsonRpcService service = CreateService(request =>
        {
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
        }, _errorResponse);
        JsonRpcProcessor processor = CreateProcessor(service);

        string nested = BuildNestedArrayParams(paramNestingDepth);
        string request = CreateTransactionCountRequest("1", paramsJson: $"[{nested}]");

        using CollectedJsonRpcResponses result = await ProcessAsync(processor, request, CreateHttpContext());

        CollectedJsonRpcResult response = AssertSingleResponse(result, expectParseError);

        if (expectParseError)
        {
            requestCaptured.Should().BeFalse("a depth-rejected request must never reach the service");
            return;
        }

        response.Response.Should().BeOfType<JsonRpcSuccessResponse>();
        response.Response!.Id.Should().Be(1);
        requestCaptured.Should().BeTrue();
        capturedMethod.Should().Be("eth_getTransactionCount");
        observedDepth.Should().Be(paramNestingDepth);
    }

    private static string BuildNestedArrayParams(int depth) => new string('[', depth) + new string(']', depth);

    private sealed class CollectingJsonRpcResponseSink : IJsonRpcResponseSink
    {
        private CollectedJsonRpcResult? _currentBatch;
        private int _batchItemCount;

        public CollectedJsonRpcResponses Responses { get; } = new();
        public Action<JsonRpcResponse, RpcReport>? OnSingleWrite { get; init; }
        public Action? OnEndBatch { get; init; }
        public int StopAfterBatchItems { get; init; } = int.MaxValue;
        public long BytesWritten { get; private set; }
        public bool StopRequested { get; private set; }

        public ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            OnSingleWrite?.Invoke(response, report);
            Responses.AddSingle(response, report);
            BytesWritten++;
            return ValueTask.CompletedTask;
        }

        public ValueTask BeginBatchAsync(CancellationToken cancellationToken)
        {
            _currentBatch = Responses.AddBatch();
            _batchItemCount = 0;
            BytesWritten++;
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            _currentBatch!.AddBatchItem(response);
            _batchItemCount++;
            BytesWritten++;
            StopRequested = _batchItemCount >= StopAfterBatchItems;
            return ValueTask.CompletedTask;
        }

        public ValueTask EndBatchAsync(CancellationToken cancellationToken)
        {
            OnEndBatch?.Invoke();
            _currentBatch = null;
            BytesWritten++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CollectedJsonRpcResponses : List<CollectedJsonRpcResult>, IDisposable
    {
        public void AddSingle(JsonRpcResponse response, RpcReport report) =>
            Add(CollectedJsonRpcResult.Single(response, report));

        public CollectedJsonRpcResult AddBatch()
        {
            CollectedJsonRpcResult batch = CollectedJsonRpcResult.Batch();
            Add(batch);
            return batch;
        }

        public void Dispose()
        {
            foreach (CollectedJsonRpcResult result in this)
            {
                result.Dispose();
            }
        }
    }

    private sealed class CollectedJsonRpcResult : IDisposable
    {
        private CollectedJsonRpcResult(JsonRpcResponse? response, RpcReport? report, List<JsonRpcResponse>? batchItems)
        {
            Response = response;
            Report = report;
            BatchItems = batchItems;
        }

        public JsonRpcResponse? Response { get; }
        public RpcReport? Report { get; }
        public List<JsonRpcResponse>? BatchItems { get; }
        public bool IsCollection => BatchItems is not null;

        public static CollectedJsonRpcResult Single(JsonRpcResponse response, RpcReport report) =>
            new(response, report, null);

        public static CollectedJsonRpcResult Batch() =>
            new(null, null, []);

        public void AddBatchItem(JsonRpcResponse response) => BatchItems!.Add(response);

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
