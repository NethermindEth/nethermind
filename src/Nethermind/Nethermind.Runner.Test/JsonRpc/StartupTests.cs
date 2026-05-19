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
    private static readonly Startup Startup;

    static StartupTests() => Startup = CreateStartup();

    private static Startup CreateStartup(
        IRpcAuthentication? rpcAuthentication = null,
        IEngineRpcModule? engineModule = null,
        JsonRpcConfig? rpcConfig = null)
    {
        rpcConfig ??= new JsonRpcConfig { EnabledModules = [ModuleType.Engine] };
        engineModule ??= CreateEngineModule();

        RpcModuleProvider moduleProvider = new(new RealFileSystem(), rpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(
            new SingletonFactory<IEngineRpcModule>(engineModule), true));

        EthereumJsonSerializer jsonSerializer = new();
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
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
        string response = await ProcessJsonRpcRequest(
            $$"""
            {
                "jsonrpc":"2.0",
                "id": {{JsonSerializer.Serialize(injId)}},
                "method":"engine_getBlobsV1",
                "params":[[]]
            }
            """
        );

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(
            doc.RootElement.GetProperty("id").GetString(),
            Is.EqualTo(injId)
        );
    }

    [Test]
    [NonParallelizable]
    public async Task ProcessJsonRpcRequest_WithoutContentLength_ProcessesAndCountsActualBytes()
    {
        const string request =
            """
            {
                "jsonrpc":"2.0",
                "id": 1,
                "method":"engine_getBlobsV1",
                "params":[[]]
            }
            """;

        long receivedBefore = JsonRpcMetrics.JsonRpcBytesReceivedHttp;
        long withoutContentLengthBefore = JsonRpcMetrics.JsonRpcHttpRequestsWithoutContentLength;
        long bodyReadsBefore = JsonRpcMetrics.JsonRpcHttpRequestBodyReads;
        long bodySegmentsBefore = JsonRpcMetrics.JsonRpcHttpRequestBodySegments;
        string response = await ProcessJsonRpcRequest(request, setContentLength: false);
        long receivedBytes = JsonRpcMetrics.JsonRpcBytesReceivedHttp - receivedBefore;
        long withoutContentLength = JsonRpcMetrics.JsonRpcHttpRequestsWithoutContentLength - withoutContentLengthBefore;
        long bodyReads = JsonRpcMetrics.JsonRpcHttpRequestBodyReads - bodyReadsBefore;
        long bodySegments = JsonRpcMetrics.JsonRpcHttpRequestBodySegments - bodySegmentsBefore;

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(receivedBytes, Is.EqualTo(Encoding.UTF8.GetByteCount(request)));
        Assert.That(withoutContentLength, Is.EqualTo(1));
        Assert.That(bodyReads, Is.GreaterThanOrEqualTo(1));
        Assert.That(bodySegments, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    [NonParallelizable]
    public async Task ProcessJsonRpcRequest_WithContentLength_RecordsRequestShape()
    {
        const string request =
            """
            {
                "jsonrpc":"2.0",
                "id": 1,
                "method":"engine_getBlobsV1",
                "params":[[]]
            }
            """;

        long withContentLengthBefore = JsonRpcMetrics.JsonRpcHttpRequestsWithContentLength;
        string response = await ProcessJsonRpcRequest(request);
        long withContentLength = JsonRpcMetrics.JsonRpcHttpRequestsWithContentLength - withContentLengthBefore;

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(withContentLength, Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_RejectsAdjacentTopLevelValues()
    {
        const string request =
            """
            {"jsonrpc":"2.0","id":1,"method":"engine_getBlobsV1","params":[[]]}{"jsonrpc":"2.0","id":2,"method":"engine_getBlobsV1","params":[[]]}
            """;

        string response = await ProcessJsonRpcRequest(request);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ParseError));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_RejectsObjectThenArrayTopLevelValues()
    {
        const string request =
            """
            {"jsonrpc":"2.0","id":1,"method":"engine_getBlobsV1","params":[[]]}[{"jsonrpc":"2.0","id":2,"method":"engine_getBlobsV1","params":[[]]}]
            """;

        string response = await ProcessJsonRpcRequest(request);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.ParseError));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AcceptsTrailingWhitespaceAfterSingleDocument()
    {
        const string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_getBlobsV1\",\"params\":[[]]}\r\n\t ";

        string response = await ProcessJsonRpcRequest(request);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("id").GetInt64(), Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AcceptsBatchDocument()
    {
        const string request =
            """
            [{"jsonrpc":"2.0","id":1,"method":"engine_getBlobsV1","params":[[]]},{"jsonrpc":"2.0","id":2,"method":"engine_getBlobsV1","params":[[]]}]
            """;

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
    public async Task ProcessJsonRpcRequest_OverMaxRequestBodySize_ReturnsPayloadTooLarge()
    {
        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_getBlobsV1\",\"params\":[[]]}",
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
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_getBlobsV1\",\"params\":[[]]}",
            startup: CreateStartup(rpcAuthentication),
            isAuthenticated: true);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidRequest));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_UnauthenticatedBatchResponseSizeLimitStopsDispatch()
    {
        IEngineRpcModule engineModule = CreateEngineModule();
        JsonRpcConfig rpcConfig = new() { EnabledModules = [ModuleType.Engine], MaxBatchResponseBodySize = 1 };

        string response = await ProcessJsonRpcRequest(
            CreateBlobsBatchRequest(3),
            startup: CreateStartup(engineModule: engineModule, rpcConfig: rpcConfig));

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(3));
        await engineModule.Received(1).engine_getBlobsV1(Arg.Any<byte[][]>());
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AuthenticatedBatchResponseSizeLimitDispatchesAll()
    {
        IEngineRpcModule engineModule = CreateEngineModule();
        JsonRpcConfig rpcConfig = new() { EnabledModules = [ModuleType.Engine], MaxBatchResponseBodySize = 1 };
        IRpcAuthentication rpcAuthentication = Substitute.For<IRpcAuthentication>();
        rpcAuthentication.Authenticate(Arg.Any<string>()).Returns(Task.FromResult(true));

        string response = await ProcessJsonRpcRequest(
            CreateBlobsBatchRequest(3),
            startup: CreateStartup(rpcAuthentication, engineModule, rpcConfig),
            isAuthenticated: true);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(3));
        await engineModule.Received(3).engine_getBlobsV1(Arg.Any<byte[][]>());
    }

    [Test]
    public async Task ProcessJsonRpcRequest_SerializesTypedErrorData()
    {
        const string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_getBlobsV2\",\"params\":[[]]}";

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
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_getBlobsV2\",\"params\":[[]]}",
            startup: CreateStartup(engineModule: engineModule));

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").GetArrayLength(), Is.EqualTo(1));
        Assert.That(doc.RootElement.GetProperty("result")[0].ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(streamableResult.WriteCount, Is.EqualTo(1));
    }

    [TestCase("ok")]
    [TestCase("x\"\\\n\u0001")]
    public async Task HttpJsonRpcResponseSink_SerializesStringResultSafely(string value)
    {
        string response = await WriteHttpJsonRpcResponse(new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = value });

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").GetString(), Is.EqualTo(value));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HttpJsonRpcResponseSink_SerializesBooleanResultSafely(bool value)
    {
        string response = await WriteHttpJsonRpcResponse(new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = value });

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").GetBoolean(), Is.EqualTo(value));
    }

    [Test]
    [NonParallelizable]
    public async Task HttpJsonRpcResponseSink_ReportsStreamableFlushCount()
    {
        DefaultHttpContext ctx = new();
        MemoryStream responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        long streamableBefore = JsonRpcMetrics.JsonRpcHttpStreamableResponses;
        long unbufferedBefore = JsonRpcMetrics.JsonRpcHttpUnbufferedResponses;

        HttpJsonRpcResponseSink sink = new(
            ctx,
            new JsonRpcUrl("http", "127.0.0.1", 0, RpcEndpoint.Http, true, [ModuleType.Engine]),
            new JsonRpcConfig(),
            jsonRpcLocalStats,
            EthereumJsonSerializer.JsonOptions,
            LimboLogs.Instance.GetClassLogger<StartupTests>(),
            Stopwatch.GetTimestamp());

        JsonRpcSuccessResponse response = new()
        {
            Id = JsonRpcId.FromObject(1),
            Result = new FlushingStreamableResult()
        };

        await sink.WriteSingleAsync(response, new RpcReport("engine_getBlobsV2", 0, true), CancellationToken.None);
        await sink.CompleteAsync(CancellationToken.None);

        Assert.That(JsonRpcMetrics.JsonRpcHttpStreamableResponses - streamableBefore, Is.EqualTo(1));
        Assert.That(JsonRpcMetrics.JsonRpcHttpUnbufferedResponses - unbufferedBefore, Is.EqualTo(1));
        jsonRpcLocalStats.Received(1).ReportCall(
            Arg.Is<RpcReport>(static report =>
                report.Method == "engine_getBlobsV2" &&
                report.BoundaryTimings.ResponseFlushCount == 2 &&
                report.BoundaryTimings.ResponseFlushMicroseconds >= 0),
            Arg.Any<long>(),
            Arg.Any<long?>());
    }

    [Test]
    [NonParallelizable]
    public async Task HttpJsonRpcResponseSink_ReportsBufferedSerializedShape()
    {
        DefaultHttpContext ctx = new();
        MemoryStream responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        long serializedBefore = JsonRpcMetrics.JsonRpcHttpSerializedResponses;
        long bufferedBefore = JsonRpcMetrics.JsonRpcHttpBufferedResponses;

        HttpJsonRpcResponseSink sink = new(
            ctx,
            new JsonRpcUrl("http", "127.0.0.1", 0, RpcEndpoint.Http, false, [ModuleType.Engine]),
            new JsonRpcConfig { BufferResponses = true },
            Substitute.For<IJsonRpcLocalStats>(),
            EthereumJsonSerializer.JsonOptions,
            LimboLogs.Instance.GetClassLogger<StartupTests>(),
            Stopwatch.GetTimestamp());

        await sink.WriteSingleAsync(
            new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = true },
            new RpcReport("eth_chainId", 0, true),
            CancellationToken.None);
        await sink.CompleteAsync(CancellationToken.None);

        Assert.That(JsonRpcMetrics.JsonRpcHttpSerializedResponses - serializedBefore, Is.EqualTo(1));
        Assert.That(JsonRpcMetrics.JsonRpcHttpBufferedResponses - bufferedBefore, Is.EqualTo(1));
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

    private static async Task<string> WriteHttpJsonRpcResponse(JsonRpcResponse response)
    {
        DefaultHttpContext ctx = new();
        MemoryStream responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));

        JsonRpcConfig rpcConfig = new();
        HttpJsonRpcResponseSink sink = new(
            ctx,
            new JsonRpcUrl("http", "127.0.0.1", 0, RpcEndpoint.Http, false, [ModuleType.Engine]),
            rpcConfig,
            Substitute.For<IJsonRpcLocalStats>(),
            EthereumJsonSerializer.JsonOptions,
            LimboLogs.Instance.GetClassLogger<StartupTests>(),
            Stopwatch.GetTimestamp());

        await sink.WriteSingleAsync(response, new RpcReport("test", 0, true), CancellationToken.None);
        await sink.CompleteAsync(CancellationToken.None);

        return Encoding.UTF8.GetString(responseBody.ToArray());
    }

    private static string CreateBlobsBatchRequest(int count)
    {
        StringBuilder request = new("[");
        for (int i = 1; i <= count; i++)
        {
            if (i != 1)
            {
                request.Append(',');
            }

            request.Append("{\"jsonrpc\":\"2.0\",\"id\":");
            request.Append(i);
            request.Append(",\"method\":\"engine_getBlobsV1\",\"params\":[[]]}");
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
        const string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_newPayloadV4\",\"params\":[null,[],null,null]}";

        DefaultHttpContext ctx = new()
        {
            Request =
            {
                Method = method,
                ContentType = contentType,
                Body = new MemoryStream(Encoding.UTF8.GetBytes(request))
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

    private sealed class ProbeBlobStreamableResult : IStreamableResult, IReadOnlyList<BlobAndProofV2?>
    {
        public int WriteCount { get; private set; }

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
