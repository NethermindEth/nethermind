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
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(new SingletonFactory<IEngineRpcModule>(engineModule), true));

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

        AssertJsonResponse(response, root => Assert.That(root.GetProperty("id").GetString(), Is.EqualTo(injId)));
    }

    [Test]
    [NonParallelizable]
    public async Task ProcessJsonRpcRequest_ProcessesAndCountsBytes([Values] bool setContentLength)
    {
        string request = CreateJsonRpcRequest();

        long receivedBefore = JsonRpcMetrics.JsonRpcBytesReceivedHttp;
        string response = await ProcessJsonRpcRequest(request, setContentLength: setContentLength);
        long receivedBytes = JsonRpcMetrics.JsonRpcBytesReceivedHttp - receivedBefore;

        AssertArrayResultResponse(response);
        Assert.That(receivedBytes, Is.EqualTo(Encoding.UTF8.GetByteCount(request)));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_ReportsHttpCallStats()
    {
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        jsonRpcLocalStats.IsEnabled.Returns(true);
        Startup startup = CreateStartup(jsonRpcLocalStats: jsonRpcLocalStats);

        string response = await ProcessJsonRpcRequest(CreateJsonRpcRequest(), startup: startup);

        AssertArrayResultResponse(response);

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

        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(request);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        AssertErrorCodeResponse(response, ErrorCodes.ParseError);
    }

    [TestCase("", TestName = "Empty input")]
    [TestCase(" \r\n\t", TestName = "Whitespace-only input")]
    public async Task ProcessJsonRpcRequest_EmptyInput_ReturnsParseErrorBadRequest(string request)
    {
        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(request);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        AssertErrorCodeResponse(response, ErrorCodes.ParseError);
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AcceptsTrailingWhitespaceAfterSingleDocument()
    {
        string request = CreateJsonRpcRequest() + "\r\n\t ";

        string response = await ProcessJsonRpcRequest(request);

        AssertArrayResultResponse(response, expectedId: 1);
    }

    [Test]
    public async Task ProcessJsonRpcRequest_AcceptsBatchDocument()
    {
        string request = CreateBlobsBatchRequest(2);

        string response = await ProcessJsonRpcRequest(request);

        AssertBatchArrayResultResponse(response, 2);
    }

    [Test]
    [NonParallelizable]
    public async Task ProcessJsonRpcRequest_OverMaxRequestBodySize_ReturnsPayloadTooLarge()
    {
        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(
            CreateJsonRpcRequest(),
            maxRequestBodySize: 1);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status413PayloadTooLarge));
        AssertErrorCodeResponse(response, ErrorCodes.LimitExceeded);
    }

    [Test]
    public async Task ProcessJsonRpcRequest_BodyReadThrowsIOException_ReturnsBadRequestFramedError()
    {
        // Kestrel reports a chunk-size line that overflows Int32 (e.g. "80000000") as a plain
        // IOException rather than a BadHttpRequestException.
        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(
            new ThrowingReadStream(new IOException("Bad chunk size data.")),
            contentLength: null);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        AssertErrorCodeResponse(response, ErrorCodes.InvalidRequest);
    }

    // The framed 400 renders the exception's Message into the client-visible JSON-RPC error, and this endpoint is
    // reachable unauthenticated, so the transport-layer message must not survive into the response.
    [Test]
    public async Task ProcessJsonRpcRequest_BodyReadThrowsIOException_does_not_echo_the_transport_message()
    {
        const string transportDetail = "Reading the request body timed out due to data arriving too slowly. See MinRequestBodyDataRate. /home/build/src/Kestrel";

        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(
            new ThrowingReadStream(new IOException(transportDetail)),
            contentLength: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(response, Does.Not.Contain("MinRequestBodyDataRate"));
            Assert.That(response, Does.Not.Contain("/home/build"));
            Assert.That(response, Does.Contain("Invalid request body."));
        }
        AssertErrorCodeResponse(response, ErrorCodes.InvalidRequest);
    }

    // A reset connection is a transport failure, not a malformed request: there is no client left to receive a 400,
    // and framing it as one would blame the caller for a dropped connection.
    [Test]
    public void ProcessJsonRpcRequest_BodyReadThrowsConnectionReset_is_not_reframed_as_a_client_error() =>
        Assert.That(async () => await ProcessJsonRpcRequestWithStatus(
                new ThrowingReadStream(new ConnectionResetException("connection reset")),
                contentLength: null),
            Throws.InstanceOf<ConnectionResetException>());

    // A transport failure Kestrel surfaces as a *derived* IOException must keep propagating rather than being
    // reframed as a client error: doing so would blame the caller for a broken connection and, because the 400
    // handler logs at Debug, would leave a real I/O failure with no trace at default log level. The catch tests the
    // exact type for this reason - a bare IOException is how Kestrel reports malformed chunk framing.
    private sealed class DerivedIOException(string message) : IOException(message);

    [Test]
    public void ProcessJsonRpcRequest_BodyReadThrowsDerivedIOException_is_not_reframed_as_a_client_error() =>
        Assert.That(async () => await ProcessJsonRpcRequestWithStatus(
                new ThrowingReadStream(new DerivedIOException("The request stream was aborted.")),
                contentLength: null),
            Throws.InstanceOf<DerivedIOException>());

    // 0x7fffffff is a legitimate chunk size, so it reaches Kestrel's MinRequestBodyDataRate timeout rather than
    // anything this PR touches - the point of the case is that the fix does not over-catch and turn it into a 400.
    // Explicit because it can only reach the 408 after that grace period plus a heartbeat tick elapse, which makes
    // it a multi-second wall-clock test that depends on the runner not being starved. The narrowing itself is
    // pinned instantly by the two IOException propagation tests above.
    [Explicit("~5s: waits out Kestrel's MinRequestBodyDataRate grace period")]
    [TestCase("7fffffff", StatusCodes.Status408RequestTimeout, "Request body read timed out.", TestName = "Chunk size int.MaxValue never completes")]
    public Task Kestrel_ValidButUnfulfilledChunkSize_StillTimesOut(string chunkSizeLine, int expectedStatusCode, string expectedMessage) =>
        Kestrel_MalformedChunkedRequestBody_ReturnsFramedJsonRpcError(chunkSizeLine, expectedStatusCode, expectedMessage);

    [TestCase("80000000", StatusCodes.Status400BadRequest, "Invalid request body.", TestName = "Chunk size int.MaxValue + 1")]
    [TestCase("ffffffff", StatusCodes.Status400BadRequest, "Invalid request body.", TestName = "Chunk size uint.MaxValue")]
    [TestCase("zzz", StatusCodes.Status400BadRequest, "Invalid request body.", TestName = "Non-hex chunk size")]
    public async Task Kestrel_MalformedChunkedRequestBody_ReturnsFramedJsonRpcError(string chunkSizeLine, int expectedStatusCode, string expectedMessage)
    {
        await using KestrelJsonRpcHost host = await KestrelJsonRpcHost.StartAsync(Startup, CreateUrl());
        byte[] request = Encoding.ASCII.GetBytes(
            "POST / HTTP/1.1\r\nHost: h\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
            + chunkSizeLine + "\r\n" + CreateJsonRpcRequest());

        (int statusCode, string body) = await host.SendRawAsync(request);

        Assert.That(statusCode, Is.EqualTo(expectedStatusCode));
        Assert.That(body, Is.Not.Empty, "Expected a framed JSON-RPC error body");
        AssertErrorCodeResponse(body, ErrorCodes.InvalidRequest);
        // Kestrel authors its own text for these - "Bad chunk size data." for "zzz", a MinRequestBodyDataRate
        // message for the 408. This endpoint serves unauthenticated callers, so what reaches them has to be a
        // message this repo authored, which is why the expectation is a literal per status rather than a passthrough.
        AssertJsonResponse(body, root =>
            Assert.That(root.GetProperty("error").GetProperty("message").GetString(), Is.EqualTo(expectedMessage)));
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

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        AssertErrorCodeResponse(response, ErrorCodes.InvalidRequest);
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

        AssertBatchArrayResultResponse(response, 3, assertItemResults: false);
        await engineModule.Received(expectedDispatches).engine_getBlobsV1(Arg.Any<byte[][]>());
    }

    [Test]
    public async Task ProcessJsonRpcRequest_SerializesTypedErrorData()
    {
        string request = CreateJsonRpcRequest(GetBlobsV2Method);

        string response = await ProcessJsonRpcRequest(request);

        AssertJsonResponse(response, static root =>
        {
            JsonElement error = root.GetProperty("error");
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidInput));
            Assert.That(error.GetProperty("data").ValueKind, Is.EqualTo(JsonValueKind.Array));
        });
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

        AssertJsonResponse(response, static root =>
        {
            JsonElement result = root.GetProperty("result");
            Assert.That(result.GetArrayLength(), Is.EqualTo(1));
            Assert.That(result[0].ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
        Assert.That(streamableResult.WriteCount, Is.EqualTo(1));
        Assert.That(streamableResult.DisposeCount, Is.EqualTo(1));
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

    [TestCaseSource(nameof(SimpleResultCases))]
    public async Task HttpJsonRpcResponseSink_SerializesSimpleResultWithRpcShape(object value, string expectedResultJson)
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

        Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32000,\"message\":\"out of gas\"},\"id\":1}"));
    }

    [TestCase(true, "HTTP/1.1", true)]
    [TestCase(true, "HTTP/2", false)]
    [TestCase(false, "HTTP/1.1", true)]
    public async Task HttpJsonRpcResponseSink_SetsContentLengthForUnflushedHttp11Response(
        bool isAuthenticated,
        string protocol,
        bool expectedContentLength)
    {
        HttpJsonRpcResponseSinkFixture fixture = CreateHttpJsonRpcResponseSink(isAuthenticated: isAuthenticated);
        fixture.Context.Request.Protocol = protocol;
        JsonRpcSuccessResponse response = new() { Id = JsonRpcId.FromObject(1), Result = "ok" };

        await fixture.Sink.WriteSingleAsync(response, new RpcReport("test", 0, true), CancellationToken.None);
        long bytesWritten = fixture.Sink.BytesWritten;
        await fixture.Sink.CompleteAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.Context.Response.ContentLength, expectedContentLength ? Is.EqualTo(bytesWritten) : Is.Null);
            Assert.That(fixture.ResponseBody.Length, Is.EqualTo(bytesWritten));
        }
    }

    [Test]
    public async Task HttpJsonRpcResponseSink_DoesNotSetContentLengthAfterStreamFlush()
    {
        bool hasStarted = false;
        IHttpResponseFeature responseFeature = Substitute.For<IHttpResponseFeature>();
        responseFeature.Headers.Returns(new HeaderDictionary());
        responseFeature.HasStarted.Returns(_ => hasStarted);
        HttpJsonRpcResponseSinkFixture fixture = CreateHttpJsonRpcResponseSink(
            isAuthenticated: true,
            responseFeature: responseFeature);
        fixture.Context.Request.Protocol = "HTTP/1.1";
        JsonRpcSuccessResponse response = new()
        {
            Id = JsonRpcId.FromObject(1),
            Result = new FlushingStreamableResult(() => hasStarted = true)
        };

        await fixture.Sink.WriteSingleAsync(response, new RpcReport(GetBlobsV2Method, 0, true), CancellationToken.None);
        await fixture.Sink.CompleteAsync(CancellationToken.None);

        Assert.That(fixture.Context.Response.ContentLength, Is.Null);
    }

    [Test]
    [NonParallelizable]
    public async Task HttpJsonRpcResponseSink_ReportsStreamableFlushCount()
    {
        HttpJsonRpcResponseSinkFixture fixture = CreateHttpJsonRpcResponseSink(isAuthenticated: true, enableLocalStats: true);

        JsonRpcSuccessResponse response = new()
        {
            Id = JsonRpcId.FromObject(1),
            Result = new FlushingStreamableResult()
        };

        await WriteHttpJsonRpcResponseAndAssertReported(fixture, response, GetBlobsV2Method);

        Assert.That(fixture.Context.Response.ContentType, Is.EqualTo("application/json"));
    }

    [Test]
    [NonParallelizable]
    public async Task HttpJsonRpcResponseSink_ReportsBufferedSerializedShape()
    {
        HttpJsonRpcResponseSinkFixture fixture = CreateHttpJsonRpcResponseSink(new JsonRpcConfig { BufferResponses = true }, enableLocalStats: true);

        await WriteHttpJsonRpcResponseAndAssertReported(fixture,
            new JsonRpcSuccessResponse { Id = JsonRpcId.FromObject(1), Result = new string('x', 8 * 1024) },
            "eth_chainId");
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
    [TestCase("POST", "application/json", "127.0.0.1", RpcEndpoint.Http, false, "http://example.com")]
    public void TrustedHttpFastLane_RequiresTrustedJsonHttpPost(
        string method,
        string contentType,
        string remoteIp,
        RpcEndpoint endpoint,
        bool expected,
        string? origin = null)
    {
        JsonRpcUrl jsonRpcUrl = CreateUrl(endpoint: endpoint);
        DefaultHttpContext ctx = CreateFastLaneContext(jsonRpcUrl.Port, method, contentType, IPAddress.Parse(remoteIp));
        if (origin is not null)
        {
            ctx.Request.Headers.Origin = origin;
        }

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
        return await ProcessJsonRpcRequestWithStatus(
            new MemoryStream(requestBytes),
            setContentLength ? requestBytes.Length : null,
            maxRequestBodySize,
            startup,
            isAuthenticated);
    }

    private static async Task<(string Response, int StatusCode)> ProcessJsonRpcRequestWithStatus(
        Stream body,
        long? contentLength,
        long? maxRequestBodySize = null,
        Startup? startup = null,
        bool isAuthenticated = false)
    {
        DefaultHttpContext ctx = new()
        {
            Request =
            {
                Method = "POST",
                ContentType = "application/json",
                Body = body
            }
        };
        if (contentLength is not null) ctx.Request.ContentLength = contentLength;

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

    private static async Task WriteHttpJsonRpcResponseAndAssertReported(HttpJsonRpcResponseSinkFixture fixture, JsonRpcResponse response, string method)
    {
        await fixture.Sink.WriteSingleAsync(response, new RpcReport(method, 0, true), CancellationToken.None);
        await fixture.Sink.CompleteAsync(CancellationToken.None);

        fixture.LocalStats.Received(1).ReportCall(
            Arg.Is<RpcReport>(report => report.Method == method),
            Arg.Any<long>(),
            Arg.Any<long?>());
    }

    private static void AssertArrayResultResponse(string response, long? expectedId = null) =>
        AssertJsonResponse(response, root => AssertArrayResult(root, expectedId));

    private static void AssertBatchArrayResultResponse(string response, int expectedCount, bool assertItemResults = true) =>
        AssertJsonResponse(response, root =>
        {
            Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(root.GetArrayLength(), Is.EqualTo(expectedCount));
            for (int i = 0; assertItemResults && i < expectedCount; i++)
            {
                AssertArrayResult(root[i], i + 1);
            }
        });

    private static void AssertArrayResult(JsonElement root, long? expectedId = null)
    {
        if (expectedId is not null) Assert.That(root.GetProperty("id").GetInt64(), Is.EqualTo(expectedId.Value));

        Assert.That(root.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    private static void AssertErrorCodeResponse(string response, int expectedCode) =>
        AssertJsonResponse(response, root => Assert.That(root.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(expectedCode)));

    private static void AssertJsonResponse(string response, Action<JsonElement> assert)
    {
        using JsonDocument doc = JsonDocument.Parse(response);
        assert(doc.RootElement);
    }

    private static HttpJsonRpcResponseSinkFixture CreateHttpJsonRpcResponseSink(
        JsonRpcConfig? rpcConfig = null,
        bool isAuthenticated = false,
        bool enableLocalStats = false,
        IJsonRpcLocalStats? jsonRpcLocalStats = null,
        IHttpResponseFeature? responseFeature = null)
    {
        DefaultHttpContext ctx = new();
        MemoryStream responseBody = new();
        if (responseFeature is not null)
        {
            ctx.Features.Set(responseFeature);
        }

        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));

        jsonRpcLocalStats ??= Substitute.For<IJsonRpcLocalStats>();
        jsonRpcLocalStats.IsEnabled.Returns(enableLocalStats);
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
        string[] requests = new string[count];
        for (int i = 0; i < count; i++) requests[i] = CreateJsonRpcRequest(idJson: (i + 1).ToString());
        return "[" + string.Join(",", requests) + "]";
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

    private static readonly TestCaseData[] SimpleResultCases =
    [
        new TestCaseData("ok", "\"ok\"").SetName("string"),
        new TestCaseData("x\"\\\n\u0001", JsonSerializer.Serialize("x\"\\\n\u0001", EthereumJsonSerializer.JsonOptions)).SetName("escaped string"),
        new TestCaseData(false, "false").SetName("false"),
        new TestCaseData(true, "true").SetName("true"),
        new TestCaseData(1, "1").SetName("int"),
        new TestCaseData(1L, "\"0x1\"").SetName("long"),
        new TestCaseData(1UL, "\"0x1\"").SetName("ulong")
    ];

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

    private sealed class FlushingStreamableResult(Action? onFlushed = null) : IStreamableResult
    {
        public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            writer.Write("["u8);
            await writer.FlushAsync(cancellationToken);
            onFlushed?.Invoke();
            writer.Write("null"u8);
            await writer.FlushAsync(cancellationToken);
            onFlushed?.Invoke();
            writer.Write("]"u8);
        }
    }

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override int Read(Span<byte> buffer) => throw exception;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(exception);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(exception);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Real Kestrel listener on a loopback ephemeral port routing every request to
    /// <see cref="Startup.ProcessJsonRpcRequestCoreAsync"/>, so tests can exercise Kestrel's own
    /// HTTP/1.1 framing (chunk parsing, body data-rate timeouts) which <c>DefaultHttpContext</c> bypasses.
    /// </summary>
    private sealed class KestrelJsonRpcHost(IHost host, int port) : IAsyncDisposable
    {
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

        public static async Task<KestrelJsonRpcHost> StartAsync(Startup startup, JsonRpcUrl url)
        {
            IHost host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseKestrel(static options =>
                    {
                        options.Listen(IPAddress.Loopback, 0);
                        // Kestrel requires a grace period above its 1 s heartbeat; keep it short so a body that never completes times out quickly.
                        options.Limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(2));
                    })
                    .Configure(app => app.Run(ctx => startup.ProcessJsonRpcRequestCoreAsync(ctx, url))))
                .Build();
            await host.StartAsync();

            int port = 0;
            foreach (string address in host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)
            {
                port = new Uri(address).Port;
            }

            return new KestrelJsonRpcHost(host, port);
        }

        public async Task<(int StatusCode, string Body)> SendRawAsync(byte[] request)
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream stream = client.GetStream();
            await stream.WriteAsync(request);

            using CancellationTokenSource cts = new(ResponseTimeout);
            StringBuilder raw = new();
            byte[] buffer = new byte[16 * 1024];
            string? headers = null;
            string body = string.Empty;
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0) break;

                // Latin-1 keeps byte count == char count, so Content-Length arithmetic works on the string.
                raw.Append(Encoding.Latin1.GetString(buffer, 0, read));
                string received = raw.ToString();
                int headersEnd = received.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headersEnd < 0) continue;

                headers = received[..headersEnd];
                body = received[(headersEnd + 4)..];
                if (IsBodyComplete(headers, body)) break;
            }

            Assert.That(headers, Is.Not.Null, $"Incomplete HTTP response: {raw}");
            int statusCode = int.Parse(headers.Split(' ', 3)[1]);
            if (IsChunked(headers)) body = Dechunk(body);
            return (statusCode, body);
        }

        private static bool IsChunked(string headers) =>
            headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase);

        private static bool IsBodyComplete(string headers, string body)
        {
            if (IsChunked(headers)) return body.EndsWith("0\r\n\r\n", StringComparison.Ordinal);

            const string contentLengthHeader = "Content-Length: ";
            int index = headers.IndexOf(contentLengthHeader, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;

            int valueStart = index + contentLengthHeader.Length;
            int valueEnd = headers.IndexOf("\r\n", valueStart, StringComparison.Ordinal);
            long contentLength = long.Parse(valueEnd < 0 ? headers[valueStart..] : headers[valueStart..valueEnd]);
            return body.Length >= contentLength;
        }

        private static string Dechunk(string chunked)
        {
            StringBuilder result = new();
            int position = 0;
            while (true)
            {
                int lineEnd = chunked.IndexOf("\r\n", position, StringComparison.Ordinal);
                if (lineEnd < 0) break;

                int size = Convert.ToInt32(chunked[position..lineEnd], 16);
                if (size == 0) break;

                result.Append(chunked, lineEnd + 2, size);
                position = lineEnd + 2 + size + 2;
            }

            return result.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed class TestJsonRpcUrlCollection : Dictionary<int, JsonRpcUrl>, IJsonRpcUrlCollection
    {
        public TestJsonRpcUrlCollection(JsonRpcUrl url)
            : base(capacity: 1)
        {
            Add(url.Port, url);
            Urls = [url.ToString()];
        }

        public string[] Urls { get; }
    }
}
