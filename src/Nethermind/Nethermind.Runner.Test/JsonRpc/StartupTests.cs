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

namespace Nethermind.Runner.Test.JsonRpc;

[TestFixture]
public class StartupTests
{
    private static readonly Startup Startup;

    static StartupTests()
    {
        JsonRpcConfig rpcConfig = new() { EnabledModules = [ModuleType.Engine] };

        IEngineRpcModule engineModule = Substitute.For<IEngineRpcModule>();
        {
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

        EthereumJsonSerializer jsonSerializer = new();
        IJsonRpcLocalStats jsonRpcLocalStats = Substitute.For<IJsonRpcLocalStats>();
        JsonRpcService jsonRpcService = new(moduleProvider, LimboLogs.Instance, rpcConfig);
        JsonRpcProcessor jsonRpcProcessor = new(jsonRpcService, rpcConfig, Substitute.For<IFileSystem>(), LimboLogs.Instance);

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

        JsonRpcUrl url = new("http", "127.0.0.1", 0, RpcEndpoint.Http, false, [ModuleType.Engine]);
        await Startup.ProcessJsonRpcRequestCoreAsync(ctx, url);

        return Encoding.UTF8.GetString(responseBody.ToArray());
    }
}
