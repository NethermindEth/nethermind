// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
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

    private static Startup CreateStartup(IRpcAuthentication? rpcAuthentication = null)
    {
        JsonRpcConfig rpcConfig = new() { EnabledModules = [ModuleType.Engine] };

        IEngineRpcModule engineModule = Substitute.For<IEngineRpcModule>();
        {
            engineModule
                .engine_getBlobsV1(Arg.Any<byte[][]>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success(new BlobsV1DirectResponse(new(0)))));
            engineModule
                .engine_getBlobsV2(Arg.Any<byte[][]>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Fail("typed error", ErrorCodes.InvalidInput, new BlobsV2DirectResponse([], [], 0))));
            engineModule
                .engine_getBlobsV3(Arg.Any<byte[][]>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(new BlobsV2DirectResponse([], [], 0))));
        }

        RpcModuleProvider moduleProvider = new(new RealFileSystem(), rpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(
            new SingletonFactory<IEngineRpcModule>(engineModule), true));

        EthereumJsonSerializer jsonSerializer = new();
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        JsonRpcService jsonRpcService = new(moduleProvider, LimboLogs.Instance, rpcConfig);
        JsonRpcProcessor jsonRpcProcessor = new(jsonRpcService, rpcConfig, Substitute.For<IFileSystem>(), LimboLogs.Instance);

        return new Startup(jsonRpcProcessor, jsonRpcService, jsonRpcLocalStats, jsonSerializer, rpcConfig, rpcAuthentication);
    }

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
        string response = await ProcessJsonRpcRequest(request, setContentLength: false);
        long receivedBytes = JsonRpcMetrics.JsonRpcBytesReceivedHttp - receivedBefore;

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.GetProperty("result").ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(receivedBytes, Is.EqualTo(Encoding.UTF8.GetByteCount(request)));
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
        rpcAuthentication.Authenticate(Arg.Any<string>()).Returns(false);

        (string response, int statusCode) = await ProcessJsonRpcRequestWithStatus(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"engine_getBlobsV1\",\"params\":[[]]}",
            startup: CreateStartup(rpcAuthentication),
            isAuthenticated: true);

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidRequest));
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

    private static async Task<string> ProcessJsonRpcRequest(string request, bool setContentLength = true) =>
        (await ProcessJsonRpcRequestWithStatus(request, setContentLength)).Response;

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
}
