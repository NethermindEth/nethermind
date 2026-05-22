// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;
using JsonRpcMetrics = Nethermind.JsonRpc.Metrics;

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
public class StartupTests
{
    private const string GetBlobsV1Method = "engine_getBlobsV1";
    private const string GetBlobsV2Method = "engine_getBlobsV2";

    private static readonly Startup Startup;

    static StartupTests() => Startup = CreateStartup();

    private static Startup CreateStartup(
        IRpcAuthentication? rpcAuthentication = null,
        IEngineRpcModule? engineModule = null,
        JsonRpcConfig? rpcConfig = null,
        IJsonRpcLocalStats? jsonRpcLocalStats = null)
    {
        rpcConfig ??= new JsonRpcConfig { EnabledModules = [ModuleType.Engine] };
        engineModule ??= CreateEngineModule();

        RpcModuleProvider moduleProvider = new(new RealFileSystem(), rpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(
            new SingletonFactory<IEngineRpcModule>(engineModule), true));

        EthereumJsonSerializer jsonSerializer = new();
        jsonRpcLocalStats ??= Substitute.For<IJsonRpcLocalStats>();
        JsonRpcService jsonRpcService = new(moduleProvider, LimboLogs.Instance, rpcConfig);
        JsonRpcProcessor jsonRpcProcessor = new(jsonRpcService, rpcConfig, Substitute.For<IFileSystem>(), LimboLogs.Instance);

        return new Startup(jsonRpcProcessor, jsonRpcService, jsonRpcLocalStats, jsonSerializer, rpcConfig, rpcAuthentication);
    }

    private static IEngineRpcModule CreateEngineModule()
    {
        IEngineRpcModule engineModule = Substitute.For<IEngineRpcModule>();
        engineModule
            .engine_getBlobsV1(Arg.Any<byte[][]>())
            .Returns(Task.FromResult(CreateBlobsV1Response()));
        engineModule
            .engine_getBlobsV2(Arg.Any<byte[][]>())
            .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Fail("typed error", ErrorCodes.InvalidInput, new BlobsV2DirectResponse([], [], 0))));
        engineModule
            .engine_getBlobsV3(Arg.Any<byte[][]>())
            .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(new BlobsV2DirectResponse([], [], 0))));
        return engineModule;
    }

    private static ResultWrapper<IReadOnlyList<BlobAndProofV1?>> CreateBlobsV1Response() =>
        ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success(new BlobsV1DirectResponse(new(0)));

    [Test]
    public async Task ProcessJsonRpcRequest_EscapesId()
    {
        const string injId = "x\"\\\n\u0001";
        string response = await ProcessJsonRpcRequest(CreateJsonRpcRequest(idJson: JsonSerializer.Serialize(injId)));

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(
            doc.RootElement.GetProperty("id").GetString(),
            Is.EqualTo(injId)
        );
    }

    [TestCase(false)]
    [TestCase(true)]
    [NonParallelizable]
    public async Task ProcessJsonRpcRequest_ProcessesAndCountsBytes(bool setContentLength)
    {
        string request = CreateJsonRpcRequest();

        long receivedBefore = JsonRpcMetrics.JsonRpcBytesReceivedHttp;
        string response = await ProcessJsonRpcRequest(request, setContentLength: setContentLength);
        long receivedBytes = JsonRpcMetrics.JsonRpcBytesReceivedHttp - receivedBefore;

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(receivedBytes, Is.EqualTo(Encoding.UTF8.GetByteCount(request)));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_ReportsHttpCallStats()
    {
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        jsonRpcLocalStats.IsEnabled.Returns(true);
        Startup startup = CreateStartup(jsonRpcLocalStats: jsonRpcLocalStats);

        string response = await ProcessJsonRpcRequest(
            CreateJsonRpcRequest(),
            startup: startup);

        using JsonDocument doc = JsonDocument.Parse(response);
        Assert.That(doc.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));

        jsonRpcLocalStats.Received(1).ReportCall(
            Arg.Is<RpcReport>(static report => report.Method == GetBlobsV1Method),
            Arg.Any<long>(),
            Arg.Any<long?>());
    }

    [TestCase(false, TestName = "Rejects object followed by object")]
    [TestCase(true, TestName = "Rejects object followed by array")]
    public async Task ProcessJsonRpcRequest_RejectsAdjacentTopLevelValues(bool secondValueIsArray)
    {
        string secondValue = secondValueIsArray ? "[" + CreateJsonRpcRequest(idJson: "2") + "]" : CreateJsonRpcRequest(idJson: "2");
        string request = CreateJsonRpcRequest() + secondValue;

        string response = await ProcessJsonRpcRequest(request);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ParseError));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AcceptsTrailingWhitespaceAfterSingleDocument()
    {
        string request = CreateJsonRpcRequest() + "\r\n\t ";

        string response = await ProcessJsonRpcRequest(request);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AcceptsBatchDocument()
    {
        string request = CreateBlobsBatchRequest(2);

        string response = await ProcessJsonRpcRequest(request);

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement root = doc.RootElement;

        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(root.GetArrayLength(), Is.EqualTo(2));
        Assert.That(root[0].GetProperty("id").GetInt64(), Is.EqualTo(1));
        Assert.That(root[0].GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(root[1].GetProperty("id").GetInt64(), Is.EqualTo(2));
        Assert.That(root[1].GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    [Test]
    [NonParallelizable]
    public async Task ProcessJsonRpcRequest_OverMaxRequestBodySize_ReturnsPayloadTooLarge()
    {
        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(
            CreateJsonRpcRequest(),
            maxRequestBodySize: 1);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.LimitExceeded));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AuthFailure_ReturnsUnauthorizedError()
    {
        IRpcAuthentication rpcAuthentication = Substitute.For<IRpcAuthentication>();
        rpcAuthentication.Authenticate(Arg.Any<string>()).Returns(Task.FromResult(false));

        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(
            CreateJsonRpcRequest(),
            startup: CreateStartup(rpcAuthentication),
            isAuthenticated: true);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidRequest));
    }

    [TestCase(false, 1)]
    [TestCase(true, 3)]
    public async Task ProcessJsonRpcRequest_BatchResponseSizeLimitDispatchesExpected(bool isAuthenticated, int expectedDispatches)
    {
        IEngineRpcModule engineModule = CreateEngineModule();
        JsonRpcConfig rpcConfig = new() { EnabledModules = [ModuleType.Engine], MaxBatchResponseBodySize = 1 };
        IRpcAuthentication? rpcAuthentication = isAuthenticated ? Substitute.For<IRpcAuthentication>() : null;
        if (rpcAuthentication is not null)
        {
            rpcAuthentication.Authenticate(Arg.Any<string>()).Returns(Task.FromResult(true));
        }

        string response = await ProcessJsonRpcRequest(
            CreateBlobsBatchRequest(3),
            startup: CreateStartup(rpcAuthentication, engineModule, rpcConfig),
            isAuthenticated: isAuthenticated);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(3));
        await engineModule.Received(expectedDispatches).engine_getBlobsV1(Arg.Any<byte[][]>());
    }

    [Test]
    public async Task ProcessJsonRpcRequest_SerializesTypedErrorData()
    {
        string request = CreateJsonRpcRequest(GetBlobsV2Method);

        string response = await ProcessJsonRpcRequest(request);

        using JsonDocument doc = JsonDocument.Parse(response);
        JsonElement error = doc.RootElement.GetProperty("error");

        Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidInput));
        Assert.That(error.GetProperty("data").ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_StreamsBlobResultsWithoutGenericSerialization()
    {
        ProbeBlobStreamableResult streamableResult = new();
        IEngineRpcModule engineModule = Substitute.For<IEngineRpcModule>();
        engineModule
            .engine_getBlobsV2(Arg.Any<byte[][]>())
            .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(streamableResult)));

        string response = await ProcessJsonRpcRequest(
            CreateJsonRpcRequest(GetBlobsV2Method),
            startup: CreateStartup(engineModule: engineModule));

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("result")[0].ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(streamableResult.WriteCount, Is.EqualTo(1));
        Assert.That(streamableResult.DisposeCount, Is.EqualTo(1));
    }

    [TestCase("ok")]
    [TestCase("x\"\\\n\u0001")]
    public async Task HttpJsonRpcResponseSink_SerializesStringResultSafely(string value)
    {
        string response = await WriteHttpJsonRpcResponse(new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = value });

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").GetString(), Is.EqualTo(value));
    }

    [Test]
    public async Task HttpJsonRpcResponseSink_SerializesHexBytesResult()
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(32 * 1024);
        Array.Fill(bytes, (byte)0xaa);
        string expectedValue = "0x" + new string('a', bytes.Length * 2);

        string response = await WriteHttpJsonRpcResponse(
            new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = new HexBytes(bytes) },
            "eth_call");

        Assert.That(response, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedValue}\",\"id\":1}}"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HttpJsonRpcResponseSink_SerializesBooleanResultSafely(bool value)
    {
        string response = await WriteHttpJsonRpcResponse(new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = value });

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").GetBoolean(), Is.EqualTo(value));
    }

    [TestCaseSource(nameof(SimpleNumericResultCases))]
    public async Task HttpJsonRpcResponseSink_SerializesPrimitiveNumericResultWithRpcShape(object value, string expectedResultJson)
    {
        string response = await WriteHttpJsonRpcResponse(new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = value });

        Assert.That(response, Is.EqualTo($"{{\"jsonrpc\":\"2.0\",\"result\":{expectedResultJson},\"id\":1}}"));
    }

    [Test]
    public async Task HttpJsonRpcResponseSink_OmitsNullErrorData()
    {
        string response = await WriteHttpJsonRpcResponse(new JsonRpcErrorResponse
        {
            Id = JsonRpcId.FromObject(1),
            Error = new Error { Code = ErrorCodes.ExecutionError, Message = "out of gas" }
        });

        Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32003,\"message\":\"out of gas\"},\"id\":1}"));
    }

    [Test]
    [NonParallelizable]
    public async Task HttpJsonRpcResponseSink_ReportsStreamableFlushCount()
    {
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        jsonRpcLocalStats.IsEnabled.Returns(true);
        HttpJsonRpcResponseSinkFixture fixture = CreateHttpJsonRpcResponseSink(isAuthenticated: true, jsonRpcLocalStats: jsonRpcLocalStats);

        JsonRpcSuccessResponse response = new()
        {
            Id = JsonRpcId.FromObject(1),
            Result = new FlushingStreamableResult()
        };

        await fixture.Sink.WriteSingleAsync(response, new RpcReport(GetBlobsV2Method, 0, true), CancellationToken.None);
        await fixture.Sink.CompleteAsync(CancellationToken.None);

        Assert.That(fixture.Context.Response.ContentType, Is.EqualTo("application/json"));
        jsonRpcLocalStats.Received(1).ReportCall(
            Arg.Is<RpcReport>(static report => report.Method == GetBlobsV2Method),
            Arg.Any<long>(),
            Arg.Any<long?>());
    }

    [Test]
    [NonParallelizable]
    public async Task HttpJsonRpcResponseSink_ReportsBufferedSerializedShape()
    {
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        jsonRpcLocalStats.IsEnabled.Returns(true);
        HttpJsonRpcResponseSinkFixture fixture = CreateHttpJsonRpcResponseSink(new JsonRpcConfig { BufferResponses = true }, jsonRpcLocalStats: jsonRpcLocalStats);

        await fixture.Sink.WriteSingleAsync(
            new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = new string('x', 8 * 1024) },
            new RpcReport("eth_chainId", 0, true),
            CancellationToken.None);
        await fixture.Sink.CompleteAsync(CancellationToken.None);

        jsonRpcLocalStats.Received(1).ReportCall(
            Arg.Is<RpcReport>(static report => report.Method == "eth_chainId"),
            Arg.Any<long>(),
            Arg.Any<long?>());
    }

    [TestCase("127.0.0.1", true)]
    [TestCase("::1", true)]
    [TestCase("10.1.2.3", true)]
    [TestCase("172.16.0.1", true)]
    [TestCase("172.31.255.255", true)]
    [TestCase("172.32.0.1", false)]
    [TestCase("192.168.1.1", true)]
    [TestCase("8.8.8.8", false)]
    public void IsTrustedSource_RecognizesBuiltInNetworks(string remoteIp, bool expected)
    {
        bool isTrusted = Startup.IsTrustedSource(IPAddress.Parse(remoteIp), []);

        Assert.That(isTrusted, Is.EqualTo(expected));
    }

    [Test]
    public void IsTrustedSource_AcceptsAdditionalTrustedNetworks()
    {
        Startup.TrustedCidr[] networks = Startup.ParseTrustedNetworks(["100.64.0.0/10"], LimboLogs.Instance.GetClassLogger<StartupTests>());

        Assert.That(Startup.IsTrustedSource(IPAddress.Parse("100.64.1.2"), networks), Is.True);
        Assert.That(Startup.IsTrustedSource(IPAddress.Parse("100.128.1.2"), networks), Is.False);
    }

    [Test]
    public void IsTrustedSource_CachesResultInFeatures()
    {
        DefaultHttpContext ctx = CreateFastLaneContext(8551, remoteIp: IPAddress.Parse("8.8.8.8"));

        Assert.That(Startup.IsTrustedSource(ctx, []), Is.False);

        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.That(Startup.IsTrustedSource(ctx, []), Is.False);
    }

    [Test]
    public void TrustedEngineNewPayloadPost_UsesHttpFastLaneAndKeepsAuthenticatedUrl()
    {
        JsonRpcUrl engineUrl = CreateUrl(isAuthenticated: true);
        DefaultHttpContext ctx = CreateFastLaneContext(engineUrl.Port);

        bool usesFastLane = Startup.TryGetTrustedHttpJsonRpcUrl(ctx, new TestJsonRpcUrlCollection(engineUrl), [], out JsonRpcUrl? resolvedUrl);

        Assert.That(usesFastLane, Is.True);
        Assert.That(resolvedUrl, Is.SameAs(engineUrl));
        Assert.That(resolvedUrl!.IsAuthenticated, Is.True);
    }

    [TestCase("GET", "application/json", "127.0.0.1", RpcEndpoint.Http, false)]
    [TestCase("POST", "text/plain", "127.0.0.1", RpcEndpoint.Http, false)]
    [TestCase("POST", "application/json", "8.8.8.8", RpcEndpoint.Http, false)]
    [TestCase("POST", "application/json", "127.0.0.1", RpcEndpoint.Ws, false)]
    [TestCase("POST", "application/json", "127.0.0.1", RpcEndpoint.Http, true)]
    [TestCase("POST", "application/json; charset=utf-8", "127.0.0.1", RpcEndpoint.Http, true)]
    public void TrustedHttpFastLane_RequiresTrustedJsonHttpPost(
        string method,
        string contentType,
        string remoteIp,
        RpcEndpoint endpoint,
        bool expected)
    {
        JsonRpcUrl jsonRpcUrl = CreateUrl(endpoint: endpoint);
        DefaultHttpContext ctx = CreateFastLaneContext(jsonRpcUrl.Port, method, contentType, IPAddress.Parse(remoteIp));

        bool usesFastLane = Startup.TryGetTrustedHttpJsonRpcUrl(ctx, new TestJsonRpcUrlCollection(jsonRpcUrl), [], out _);

        Assert.That(usesFastLane, Is.EqualTo(expected));
    }

    [Test]
    public async Task JsonRpcHttpMiddleware_PassesThroughSelectedEndpoint()
    {
        JsonRpcUrl jsonRpcUrl = CreateUrl();
        DefaultHttpContext ctx = CreateFastLaneContext(jsonRpcUrl.Port);
        ctx.SetEndpoint(new Endpoint(static _ => Task.CompletedTask, new EndpointMetadataCollection(), "health"));
        bool nextCalled = false;

        await new Startup().HandleJsonRpcHttpRequestAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, new TestJsonRpcUrlCollection(jsonRpcUrl));

        Assert.That(nextCalled, Is.True);
        Assert.That(ctx.Response.HasStarted, Is.False);
    }

    [TestCase(null, false)]
    [TestCase("application/json", true)]
    [TestCase("Application/Json", true)]
    [TestCase("application/json; charset=utf-8", true)]
    [TestCase("application/jsonx", false)]
    [TestCase("text/plain", false)]
    [TestCase("text/plain application/json", false)]
    public void IsJsonContentType_MatchesJsonMediaTypeOnly(string? contentType, bool expected)
    {
        bool isJson = Startup.IsJsonContentType(contentType);

        Assert.That(isJson, Is.EqualTo(expected));
    }

    private static async Task<string> ProcessJsonRpcRequest(
        string request,
        bool setContentLength = true,
        Startup? startup = null,
        bool isAuthenticated = false) =>
        (await ProcessJsonRpcRequestWithStatus(request, setContentLength, startup: startup, isAuthenticated: isAuthenticated)).Response;

    private static async Task<(string Response, int StatusCode)> ProcessJsonRpcRequestWithStatus(
        string request,
        bool setContentLength = true,
        long? maxRequestBodySize = null,
        Startup? startup = null,
        bool isAuthenticated = false)
    {
        byte[] requestBytes = Encoding.UTF8.GetBytes(request);

        DefaultHttpContext ctx = new()
        {
            Request =
            {
                Method = "POST",
                ContentType = "application/json",
                Body = new MemoryStream(requestBytes)
            }
        };
        if (setContentLength)
        {
            ctx.Request.ContentLength = requestBytes.Length;
        }

        ctx.Request.Headers.Authorization = "Bearer test";
        MemoryStream responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));

        JsonRpcUrl url = new("http", "127.0.0.1", 0, RpcEndpoint.Http, isAuthenticated, [ModuleType.Engine], maxRequestBodySize);
        await (startup ?? Startup).ProcessJsonRpcRequestCoreAsync(ctx, url);

        return (Encoding.UTF8.GetString(responseBody.ToArray()), ctx.Response.StatusCode);
    }

    private static string CreateJsonRpcRequest(string method = GetBlobsV1Method, string idJson = "1", string paramsJson = "[[]]") =>
        $$"""{"jsonrpc":"2.0","id":{{idJson}},"method":"{{method}}","params":{{paramsJson}}}""";

    private static async Task<string> WriteHttpJsonRpcResponse(JsonRpcResponse response, string method = "test")
    {
        HttpJsonRpcResponseSinkFixture fixture = CreateHttpJsonRpcResponseSink();

        await fixture.Sink.WriteSingleAsync(response, new RpcReport(method, 0, true), CancellationToken.None);
        await fixture.Sink.CompleteAsync(CancellationToken.None);

        return Encoding.UTF8.GetString(fixture.ResponseBody.ToArray());
    }

    private static HttpJsonRpcResponseSinkFixture CreateHttpJsonRpcResponseSink(
        JsonRpcConfig? rpcConfig = null,
        bool isAuthenticated = false,
        IJsonRpcLocalStats? jsonRpcLocalStats = null)
    {
        DefaultHttpContext ctx = new();
        MemoryStream responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));

        jsonRpcLocalStats ??= Substitute.For<IJsonRpcLocalStats>();
        HttpJsonRpcResponseSink sink = new(
            ctx,
            new JsonRpcUrl("http", "127.0.0.1", 0, RpcEndpoint.Http, isAuthenticated, [ModuleType.Engine]),
            rpcConfig ?? new JsonRpcConfig(),
            jsonRpcLocalStats,
            LimboLogs.Instance.GetClassLogger<StartupTests>(),
            Stopwatch.GetTimestamp());

        return new(sink, ctx, responseBody, jsonRpcLocalStats);
    }

    private readonly record struct HttpJsonRpcResponseSinkFixture(HttpJsonRpcResponseSink Sink, DefaultHttpContext Context, MemoryStream ResponseBody, IJsonRpcLocalStats LocalStats);

    private static string CreateBlobsBatchRequest(int count)
    {
        StringBuilder request = new("[");
        for (int i = 1; i <= count; i++)
        {
            if (i != 1)
            {
                request.Append(',');
            }

            request.Append(CreateJsonRpcRequest(idJson: i.ToString()));
        }

        request.Append(']');
        return request.ToString();
    }

    private static DefaultHttpContext CreateFastLaneContext(
        int localPort,
        string method = "POST",
        string contentType = "application/json",
        IPAddress? remoteIp = null)
    {
        DefaultHttpContext ctx = new()
        {
            Request =
            {
                Method = method,
                ContentType = contentType,
                Body = new MemoryStream(Encoding.UTF8.GetBytes(CreateJsonRpcRequest("engine_newPayloadV4", paramsJson: "[null,[],null,null]")))
            }
        };

        ctx.Connection.LocalPort = localPort;
        ctx.Connection.RemoteIpAddress = remoteIp ?? IPAddress.Loopback;
        return ctx;
    }

    private static JsonRpcUrl CreateUrl(
        RpcEndpoint endpoint = RpcEndpoint.Http,
        bool isAuthenticated = false) =>
        new("http", "127.0.0.1", 8551, endpoint, isAuthenticated, [ModuleType.Engine]);

    private static IEnumerable<TestCaseData> SimpleNumericResultCases()
    {
        yield return new TestCaseData(1, "1").SetName("int");
        yield return new TestCaseData(1L, "\"0x1\"").SetName("long");
        yield return new TestCaseData(1UL, "\"0x1\"").SetName("ulong");
    }

    private sealed class ProbeBlobStreamableResult : IStreamableResult, IReadOnlyList<BlobAndProofV2?>, IDisposable
    {
        public int WriteCount { get; private set; }
        public int DisposeCount { get; private set; }

        public int Count => 1;

        public BlobAndProofV2? this[int index] => throw new InvalidOperationException("Generic blob serialization path was used.");

        public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            WriteCount++;
            writer.Write("[null]"u8);
            return ValueTask.CompletedTask;
        }

        public IEnumerator<BlobAndProofV2?> GetEnumerator() =>
            throw new InvalidOperationException("Generic blob serialization path was used.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() => DisposeCount++;
    }

    private sealed class FlushingStreamableResult : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("["u8);
            await writer.FlushAsync(cancellationToken);
            writer.Write("null"u8);
            await writer.FlushAsync(cancellationToken);
            writer.Write("]"u8);
        }
    }

    private sealed class TestJsonRpcUrlCollection : Dictionary<int, JsonRpcUrl>, IJsonRpcUrlCollection
    {
        private readonly string[] _urls;

        public TestJsonRpcUrlCollection(JsonRpcUrl url)
            : base(capacity: 1)
        {
            Add(url.Port, url);
            _urls = [url.ToString()];
        }

        public string[] Urls => _urls;
    }
}
