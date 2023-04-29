// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    public static class RpcTest
    {
        public static JsonRpcResponse TestRequest<T>(T module, string method, params string[] parameters) where T : class, IRpcModule
        {
            IJsonRpcService service = BuildRpcService(module);
            JsonRpcRequest request = GetJsonRequest(method, parameters);
            return service.SendRequestAsync(request, new JsonRpcContext(RpcEndpoint.Http)).Result;
        }

        public static string TestSerializedRequest<T>(T module, string method, params string[] parameters) where T : class, IRpcModule
        {
            IJsonRpcService service = BuildRpcService(module);
            JsonRpcRequest request = GetJsonRequest(method, parameters);

            JsonRpcContext context = new JsonRpcContext(RpcEndpoint.Http);
            if (module is IContextAwareRpcModule contextAwareModule
                && contextAwareModule.Context is not null)
            {
                context = contextAwareModule.Context;
            }
            JsonRpcResponse response = service.SendRequestAsync(request, context).Result;

            EthereumJsonSerializer serializer = new();

            Stream stream = new MemoryStream();
            long size = serializer.Serialize(stream, response);

            // for coverage (and to prove that it does not throw
            Stream indentedStream = new MemoryStream();
            serializer.Serialize(indentedStream, response, true);

            stream.Seek(0, SeekOrigin.Begin);
            string serialized = new StreamReader(stream).ReadToEnd();
            TestContext.Out?.WriteLine("Serialized:");
            TestContext.Out?.WriteLine(serialized);

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

        public static JsonRpcRequest GetJsonRequest(string method, params string[] parameters)
        {
            var doc = JsonDocument.Parse(JsonSerializer.Serialize(parameters?.ToArray() ?? Array.Empty<string>()));
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


}
