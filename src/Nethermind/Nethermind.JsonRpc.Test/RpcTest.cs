// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using FluentAssertions;
using FluentAssertions.Extensions;
using Nethermind.Core.Utils;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test;

public static class RpcTest
{
    public static async Task<JsonRpcResponse> TestRequest<T>(T module, string method, params object?[]? parameters) where T : class, IRpcModule
    {
        IJsonRpcService service = BuildRpcService(module);
        JsonRpcRequest request = BuildJsonRequest(method, parameters);
        return await service.SendRequestAsync(request, new JsonRpcContext(RpcEndpoint.Http));
    }

    public static async Task<string> TestSerializedRequest<T>(T module, string method, params object?[]? parameters) where T : class, IRpcModule
    {
        using AutoCancelTokenSource cts = AutoCancelTokenSource.ThatCancelAfter(10.Seconds());
        IJsonRpcService service = BuildRpcService(module);
        JsonRpcRequest request = BuildJsonRequest(method, parameters);

        using JsonRpcContext context = module is IContextAwareRpcModule { Context: not null } contextAwareModule
            ? contextAwareModule.Context
            : new JsonRpcContext(RpcEndpoint.Http);
        using JsonRpcResponse response = await service.SendRequestAsync(request, context).ConfigureAwait(false);

        EthereumJsonSerializer serializer = new();

        Stream stream = new MemoryStream();
        long size = await serializer.SerializeAsync(stream, response, cts.Token).ConfigureAwait(false);

        // for coverage (and to prove that it does not throw
        Stream indentedStream = new MemoryStream();
        await serializer.SerializeAsync(indentedStream, response, cts.Token, true).ConfigureAwait(false);

        stream.Seek(0, SeekOrigin.Begin);
        string serialized = await new StreamReader(stream).ReadToEndAsync().ConfigureAwait(false);

        size.Should().Be(serialized.Length);

        return serialized;
    }

    private static IJsonRpcService BuildRpcService<T>(T module) where T : class, IRpcModule
    {
        var moduleProvider = new TestRpcModuleProvider<T>(module);

        moduleProvider.Register(new SingletonModulePool<T>(new TestSingletonFactory<T>(module), true, true));
        IJsonRpcService service = new JsonRpcService(moduleProvider, LimboLogs.Instance, new JsonRpcConfig());
        return service;
    }

    public static JsonRpcRequest BuildJsonRequest(string method, params object?[]? parameters)
    {
        // TODO: Eventually we would like to support injecting a custom serializer
        var serializer = new EthereumJsonSerializer();
        parameters ??= [];

        var jsonParameters = serializer.Deserialize<JsonElement>(serializer.Serialize(parameters));

        return new JsonRpcRequest
        {
            JsonRpc = "2.0",
            Method = method,
            Params = jsonParameters,
            Id = 67
        };
    }

    private class TestSingletonFactory<T>(T module) : SingletonFactory<T>(module)
        where T : IRpcModule;
}
