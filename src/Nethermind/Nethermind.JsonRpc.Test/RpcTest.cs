// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Test;

public static class RpcTest
{
    public static JsonRpcResponse TestRequest<T>(T module, string method, params string[] parameters) where T : class, IRpcModule
    {
        IJsonRpcService service = BuildRpcService(module);
        JsonRpcRequest request = GetJsonRequest(method, parameters);
        return service.SendRequestAsync(request, new JsonRpcContext(RpcEndpoint.Http)).Result;
    }

    public static async Task<string> TestSerializedRequest<T>(IReadOnlyCollection<JsonConverter> converters, T module, string method, params string[] parameters) where T : class, IRpcModule
    {
        long Serialize(EthereumJsonSerializer ethereumJsonSerializer, JsonRpcResponse jsonRpcResponse, IJsonRpcService jsonRpcService, Stream stream, bool indented = false)
        {
            try
            {
                return ethereumJsonSerializer.SerializeWaitForEnumeration(stream, jsonRpcResponse, indented);
            }
            catch (Exception e) when (e.InnerException is OperationCanceledException)
            {
                return SerializeTimeoutException(ethereumJsonSerializer, jsonRpcResponse, jsonRpcService, stream);
            }
            catch (OperationCanceledException)
            {
                return SerializeTimeoutException(ethereumJsonSerializer, jsonRpcResponse, jsonRpcService, stream);
            }
        }

        static long SerializeTimeoutException(IJsonSerializer jsonSerializer, JsonRpcResponse response, IJsonRpcService service, Stream resultStream) =>
            jsonSerializer.Serialize(resultStream, service.GetErrorResponse(ErrorCodes.Timeout, "Request was canceled due to enabled timeout.", response.Id, response.MethodName));

        IJsonRpcService service = BuildRpcService(module, converters);
        JsonRpcRequest request = GetJsonRequest(method, parameters);

        JsonRpcContext context = module is IContextAwareRpcModule { Context: not null } contextAwareModule
            ? contextAwareModule.Context
            : new JsonRpcContext(RpcEndpoint.Http);

        JsonRpcResponse response = await service.SendRequestAsync(request, context);

        EthereumJsonSerializer serializer = new();
        foreach (JsonConverter converter in converters)
        {
            serializer.RegisterConverter(converter);
        }

        await using Stream stream = new MemoryStream();
        long size = Serialize(serializer, response, service, stream);

        // for coverage (and to prove that it does not throw
        await using Stream indentedStream = new MemoryStream();
        Serialize(serializer, response, service, indentedStream, true);

        stream.Seek(0, SeekOrigin.Begin);
        string serialized = await new StreamReader(stream).ReadToEndAsync();

        size.Should().Be(serialized.Length);

        return serialized;
    }

    public static Task<string> TestSerializedRequest<T>(T module, string method, params string[] parameters) where T : class, IRpcModule
    {
        return TestSerializedRequest(Array.Empty<JsonConverter>(), module, method, parameters);
    }

    public static IJsonRpcService BuildRpcService<T>(T module, IReadOnlyCollection<JsonConverter>? converters = null) where T : class, IRpcModule
    {
        var moduleProvider = new TestRpcModuleProvider<T>(module);

        moduleProvider.Register(new SingletonModulePool<T>(new TestSingletonFactory<T>(module, converters), true));
        IJsonRpcService service = new JsonRpcService(moduleProvider, LimboLogs.Instance, new JsonRpcConfig());
        return service;
    }

    public static JsonRpcRequest GetJsonRequest(string method, params string[] parameters)
    {
        var request = new JsonRpcRequest()
        {
            JsonRpc = "2.0",
            Method = method,
            Params = parameters?.ToArray() ?? Array.Empty<string>(),
            Id = 67
        };

        return request;
    }

    private class TestSingletonFactory<T> : SingletonFactory<T> where T : IRpcModule
    {
        private readonly IReadOnlyCollection<JsonConverter>? _converters;

        public TestSingletonFactory(T module, IReadOnlyCollection<JsonConverter>? converters) : base(module)
        {
            _converters = converters;
        }

        public override IReadOnlyCollection<JsonConverter> GetConverters() => _converters ?? base.GetConverters();
    }
}
