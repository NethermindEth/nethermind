// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using FluentAssertions;

using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public static class RpcTest
{
    public static async Task<JsonRpcResponse> TestRequest<T>(T module, string method, params string[] parameters) where T : class, IRpcModule
    {
        IJsonRpcService service = BuildRpcService(module);
        JsonRpcRequest request = GetJsonRequest(method, parameters);
        return await service.SendRequestAsync(request, new JsonRpcContext(RpcEndpoint.Http));
    }

    public static async Task<string> TestSerializedRequest<T>(T module, string method, params string[] parameters) where T : class, IRpcModule
    {
        IJsonRpcService service = BuildRpcService(module);
        JsonRpcRequest request = GetJsonRequest(method, parameters);

        JsonRpcContext context = (module is IContextAwareRpcModule contextAwareModule && contextAwareModule.Context is not null) ?
            contextAwareModule.Context :
            new JsonRpcContext(RpcEndpoint.Http);
        JsonRpcResponse response = await service.SendRequestAsync(request, context);

        EthereumJsonSerializer serializer = new();

        Stream stream = new MemoryStream();
        long size = serializer.Serialize(stream, response);

        // for coverage (and to prove that it does not throw
        Stream indentedStream = new MemoryStream();
        serializer.Serialize(indentedStream, response, true);

        stream.Seek(0, SeekOrigin.Begin);
        string serialized = new StreamReader(stream).ReadToEnd();

        size.Should().Be(serialized.Length);

        return serialized;
    }

    public static IJsonRpcService BuildRpcService<T>(T module) where T : class, IRpcModule
    {
        var moduleProvider = new TestRpcModuleProvider<T>(module);

        moduleProvider.Register(new SingletonModulePool<T>(new TestSingletonFactory<T>(module), true));
        IJsonRpcService service = new JsonRpcService(moduleProvider, LimboLogs.Instance, new JsonRpcConfig());
        return service;
    }

    public static JsonRpcRequest GetJsonRequest(string method, params string[]? parameters)
    {
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(parameters?.ToArray()));
        var request = new JsonRpcRequest()
        {
            JsonRpc = "2.0",
            Method = method,
            Params = doc.RootElement,
            Id = 67
        };

        return request;
    }

    private class TestSingletonFactory<T> : SingletonFactory<T> where T : IRpcModule
    {
        public TestSingletonFactory(T module) : base(module)
        {
        }
    }
}
