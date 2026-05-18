// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System.Collections.Generic;
using System.Globalization;
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
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Runner.JsonRpc;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
public class StartupTests
{
    private static readonly Startup Startup;
    private static readonly JsonRpcProcessor JsonRpcProcessor;
    private static readonly JsonRpcService JsonRpcService;

    static StartupTests()
    {
        JsonRpcConfig rpcConfig = new() { EnabledModules = [ModuleType.Engine, ModuleType.Eth] };

        IEngineRpcModule engineModule = Substitute.For<IEngineRpcModule>();
        {
            engineModule
                .engine_exchangeCapabilities(Arg.Any<IEnumerable<string>>())
                .Returns(ResultWrapper<IReadOnlyList<string>>.Success(["engine_exchangeCapabilities"]));
            engineModule
                .engine_getBlobsV1(Arg.Any<byte[][]>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV1?>>.Success(new BlobsV1DirectResponse(new(0)))));
            engineModule
                .engine_getBlobsV2(Arg.Any<byte[][]>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(new BlobsV2DirectResponse([], [], 0))));
            engineModule
                .engine_getBlobsV3(Arg.Any<byte[][]>())
                .Returns(Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(new BlobsV2DirectResponse([], [], 0))));
        }

        RpcModuleProvider moduleProvider = new(new RealFileSystem(), rpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);
        moduleProvider.Register(new SingletonModulePool<IEngineRpcModule>(
            new SingletonFactory<IEngineRpcModule>(engineModule), true));
        IEthRpcModule ethModule = Substitute.For<IEthRpcModule>();
        ethModule.eth_chainId().Returns(ResultWrapper<ulong>.Success(1));
        moduleProvider.Register(new SingletonModulePool<IEthRpcModule>(
            new SingletonFactory<IEthRpcModule>(ethModule), true));

        EthereumJsonSerializer jsonSerializer = new();
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        JsonRpcService jsonRpcService = new(moduleProvider, LimboLogs.Instance, rpcConfig);
        JsonRpcProcessor jsonRpcProcessor = new(jsonRpcService, rpcConfig, Substitute.For<IFileSystem>(), LimboLogs.Instance);

        JsonRpcProcessor = jsonRpcProcessor;
        JsonRpcService = jsonRpcService;
        Startup = new Startup(jsonRpcProcessor, jsonRpcService, jsonRpcLocalStats, jsonSerializer, rpcConfig);
    }

    [Test, Ignore("Escaping is disabled for now to maximize performance")]
    public async Task ProcessJsonRpcRequest_EscapesId()
    {
        const string injId = "x\" , \"injected\":\"value";
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

    private static async Task<string> ProcessJsonRpcRequest(string request)
    {
        byte[] requestBytes = Encoding.UTF8.GetBytes(request);

        DefaultHttpContext ctx = new()
        {
            Request =
            {
                Method = "POST",
                ContentType = "application/json",
                ContentLength = requestBytes.Length,
                Body = new MemoryStream(requestBytes)
            }
        };

        MemoryStream responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));

        JsonRpcUrl url = new("http", "127.0.0.1", 0, RpcEndpoint.Http, false, [ModuleType.Engine, ModuleType.Eth]);
        await Startup.ProcessJsonRpcRequestCoreAsync(ctx, url);

        return Encoding.UTF8.GetString(responseBody.ToArray());
    }

    [Test]
    public async Task ProcessJsonRpcRequest_WritesUlongResult()
    {
        string response = await ProcessJsonRpcRequest(
            """
            {
                "jsonrpc":"2.0",
                "id": 1,
                "method":"eth_chainId",
                "params":[]
            }
            """
        );

        Assert.That(response, Is.EqualTo("""{"jsonrpc":"2.0","result":"0x1","id":1}"""));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_RejectsExtraParamsForUlongResultMethod()
    {
        string response = await ProcessJsonRpcRequest(
            """
            {
                "jsonrpc":"2.0",
                "id": 1,
                "method":"eth_chainId",
                "params":[1]
            }
            """
        );

        using JsonDocument doc = JsonDocument.Parse(response);

        Assert.That(doc.RootElement.TryGetProperty("error", out JsonElement error), Is.True);
        Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    public async Task JsonRpcHttpFastPath_ProcessesLocalJsonRpcRequest()
    {
        (bool processed, int statusCode, string response) = await ProcessFastPathRequest(
            """{"jsonrpc":"2.0","method":"eth_chainId","params":[],"id":1}""");

        Assert.That(processed, Is.True);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(response, Is.EqualTo("""{"jsonrpc":"2.0","result":"0x1","id":1}"""));
    }

    [Test]
    public async Task JsonRpcHttpFastPath_ProcessesGenericLocalJsonRpcRequest()
    {
        (bool processed, int statusCode, string response) = await ProcessFastPathRequest(
            """{"jsonrpc":"2.0","id":2,"method":"eth_chainId"}""");

        Assert.That(processed, Is.True);
        Assert.That(statusCode, Is.EqualTo(StatusCodes.Status200OK));
        Assert.That(response, Is.EqualTo("""{"jsonrpc":"2.0","result":"0x1","id":2}"""));
    }

    private static async Task<(bool Processed, int StatusCode, string Response)> ProcessFastPathRequest(string request)
    {
        byte[] requestBytes = Encoding.UTF8.GetBytes(request);
        MemoryStream responseBody = new();
        FeatureCollection features = new();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = "POST",
            Headers = new HeaderDictionary
            {
                ["Content-Type"] = "application/json",
                ["Content-Length"] = requestBytes.Length.ToString(CultureInfo.InvariantCulture)
            }
        });
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            LocalPort = 8545,
            RemoteIpAddress = IPAddress.Loopback
        });
        features.Set<IRequestBodyPipeFeature>(new TestRequestBodyPipeFeature(PipeReader.Create(new MemoryStream(requestBytes))));
        features.Set<IHttpResponseFeature>(new HttpResponseFeature { Headers = new HeaderDictionary() });
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));

        JsonRpcConfig rpcConfig = new()
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 8545,
            EnabledModules = [ModuleType.Eth]
        };
        JsonRpcUrlCollection urls = new(LimboLogs.Instance, rpcConfig, includeWebSockets: false);
        JsonRpcHttpFastPath fastPath = new(
            urls,
            JsonRpcProcessor,
            JsonRpcService,
            rpcConfig,
            Substitute.For<IJsonRpcLocalStats>(),
            rpcAuthentication: null,
            LimboLogs.Instance);

        bool processed = await fastPath.TryProcessAsync(features);
        return (processed, features.Get<IHttpResponseFeature>()!.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_DoesNotStartResponseForNonStreamableSingleResponse()
    {
        TestResponseBodyFeature responseBody = await ProcessJsonRpcRequestWithResponseBody(
            """
            {
                "jsonrpc":"2.0",
                "id": 1,
                "method":"engine_exchangeCapabilities",
                "params":[[]]
            }
            """
        );

        Assert.That(responseBody.StartCalls, Is.EqualTo(0));
        Assert.That(responseBody.CompleteCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task ProcessJsonRpcRequest_StartsResponseForStreamableSingleResponse()
    {
        TestResponseBodyFeature responseBody = await ProcessJsonRpcRequestWithResponseBody(
            """
            {
                "jsonrpc":"2.0",
                "id": 1,
                "method":"engine_getBlobsV1",
                "params":[[]]
            }
            """
        );

        Assert.That(responseBody.StartCalls, Is.EqualTo(1));
        Assert.That(responseBody.CompleteCalls, Is.EqualTo(1));
    }

    private static async Task<TestResponseBodyFeature> ProcessJsonRpcRequestWithResponseBody(string request)
    {
        byte[] requestBytes = Encoding.UTF8.GetBytes(request);

        DefaultHttpContext ctx = new()
        {
            Request =
            {
                Method = "POST",
                ContentType = "application/json",
                ContentLength = requestBytes.Length,
                Body = new MemoryStream(requestBytes)
            }
        };

        TestResponseBodyFeature responseBody = new();
        ctx.Features.Set<IHttpResponseBodyFeature>(responseBody);

        JsonRpcUrl url = new("http", "127.0.0.1", 0, RpcEndpoint.Http, false, [ModuleType.Engine]);
        await Startup.ProcessJsonRpcRequestCoreAsync(ctx, url);

        return responseBody;
    }

    private sealed class TestResponseBodyFeature : IHttpResponseBodyFeature
    {
        private readonly StreamResponseBodyFeature _inner = new(new MemoryStream());

        public int StartCalls { get; private set; }

        public int CompleteCalls { get; private set; }

        public Stream Stream => _inner.Stream;

        public PipeWriter Writer => _inner.Writer;

        public void DisableBuffering() => _inner.DisableBuffering();

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            _inner.SendFileAsync(path, offset, count, cancellationToken);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return _inner.StartAsync(cancellationToken);
        }

        public Task CompleteAsync()
        {
            CompleteCalls++;
            return _inner.CompleteAsync();
        }
    }

    private sealed class TestRequestBodyPipeFeature(PipeReader reader) : IRequestBodyPipeFeature
    {
        public PipeReader Reader { get; } = reader;
    }
}
