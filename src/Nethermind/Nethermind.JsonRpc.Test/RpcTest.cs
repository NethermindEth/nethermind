// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public static class RpcTest
{
    public static async Task<JsonRpcResponse> TestRequest<T>(T module, string method, params object?[]? parameters) where T : class, IRpcModule
    {
        await using IContainer container = CreateContainerForModule<T>(module);

        IJsonRpcService service = container.Resolve<IJsonRpcService>();
        JsonRpcRequest request = BuildJsonRequest(method, parameters);
        return await service.SendRequestAsync(request, new JsonRpcContext(RpcEndpoint.Http));
    }

    public static async Task<string> TestSerializedRequest<T>(T module, string method, params object?[]? parameters) where T : class, IRpcModule
    {
        await using IContainer container = CreateContainerForModule<T>(module);

        IJsonRpcService service = container.Resolve<IJsonRpcService>();
        JsonRpcRequest request = BuildJsonRequest(method, parameters);

        using JsonRpcContext context = module is IContextAwareRpcModule { Context: not null } contextAwareModule
            ? contextAwareModule.Context
            : new JsonRpcContext(RpcEndpoint.Http);
        using JsonRpcResponse response = await service.SendRequestAsync(request, context).ConfigureAwait(false);

        ArrayBufferWriter<byte> writer = new();
        JsonRpcResponseWriter.Write(writer, response, EthereumJsonSerializer.JsonOptions);

        ArrayBufferWriter<byte> indentedWriter = new();
        JsonRpcResponseWriter.Write(indentedWriter, response, EthereumJsonSerializer.JsonOptionsIndented);

        string serialized = Encoding.UTF8.GetString(writer.WrittenSpan);
        await TestContext.Out.WriteLineAsync(serialized);

        writer.WrittenCount.Should().Be(serialized.Length);

        return serialized;
    }

    private static IContainer CreateContainerForModule<T>(T module) where T : class, IRpcModule => new ContainerBuilder()
            .AddModule(new TestNethermindModule(new JsonRpcConfig()
            {
                EnabledModules = [typeof(T).GetCustomAttribute<RpcModuleAttribute>()!.ModuleType]
            }))
            .RegisterBoundedJsonRpcModule<T, AutoRpcModuleFactory<T>>(1, new JsonRpcConfig().Timeout)
            .AddScoped<T>(module)
            .Build();

    public static JsonRpcRequest BuildJsonRequest(string method, params object?[]? parameters)
    {
        // TODO: Eventually we would like to support injecting a custom serializer
        EthereumJsonSerializer serializer = new();
        parameters ??= [];

        JsonElement jsonParameters = serializer.Deserialize<JsonElement>(serializer.Serialize(parameters));

        return new JsonRpcRequest
        {
            JsonRpc = "2.0",
            Method = method,
            Params = jsonParameters,
            Id = 67
        };
    }
}
