// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using FluentAssertions.Extensions;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Core.Utils;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

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
        using AutoCancelTokenSource cts = AutoCancelTokenSource.ThatCancelAfter(Debugger.IsAttached ? TimeSpan.FromMilliseconds(-1) : 10.Seconds());
        await using IContainer container = CreateContainerForModule<T>(module);

        IJsonRpcService service = container.Resolve<IJsonRpcService>();
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

    private static IContainer CreateContainerForModule<T>(T module) where T : class, IRpcModule
    {
        return new ContainerBuilder()
            .AddModule(new TestNethermindModule(new JsonRpcConfig()
            {
                EnabledModules = [typeof(T).GetCustomAttribute<RpcModuleAttribute>()!.ModuleType]
            }))
            .RegisterBoundedJsonRpcModule<T, AutoRpcModuleFactory<T>>(1, new JsonRpcConfig().Timeout)
            .AddScoped<T>(module)
            .Build();
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
}
