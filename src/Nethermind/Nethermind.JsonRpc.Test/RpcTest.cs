// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test;

public static class RpcTest
{
    public static void AssertSuccess(JsonRpcResponse response)
    {
        Assert.That(response, Is.InstanceOf<IResultWrapper>(), GetFailureMessage(response));
        IResultWrapper resultWrapper = (IResultWrapper)response;
        Assert.That(resultWrapper.Result.ResultType, Is.EqualTo(ResultType.Success), resultWrapper.Result.ToString());
    }

    public static T AssertSuccess<T>(JsonRpcResponse response)
    {
        Assert.That(response, Is.InstanceOf<ResultWrapper<T>>(), GetFailureMessage(response));
        ResultWrapper<T> resultWrapper = (ResultWrapper<T>)response;
        Assert.That(resultWrapper.Result.ResultType, Is.EqualTo(ResultType.Success), resultWrapper.Result.ToString());
        return resultWrapper.Data;
    }

    public static Error AssertError(JsonRpcResponse response)
    {
        if (response is JsonRpcErrorResponse { Error: { } error })
        {
            return error;
        }

        Assert.That(response, Is.InstanceOf<IResultWrapper>(), GetFailureMessage(response));
        IResultWrapper resultWrapper = (IResultWrapper)response;
        Assert.That(resultWrapper.Result.ResultType, Is.Not.EqualTo(ResultType.Success), resultWrapper.Result.ToString());
        return new Error
        {
            Code = resultWrapper.ErrorCode,
            Message = resultWrapper.Result.Error,
            Data = resultWrapper.HasErrorData ? resultWrapper.Data : null,
            SuppressWarning = resultWrapper.IsTemporary
        };
    }

    public static string SerializeResponse(JsonRpcResponse? response)
    {
        Assert.That(response, Is.Not.Null);
        ArrayBufferWriter<byte> writer = new();
        JsonRpcResponseWriter.Write(writer, response!, EthereumJsonSerializer.JsonOptions);
        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

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

        if (response.TryGetStreamableResult(out _))
        {
            using MemoryStream stream = new();
            PipeWriter pipeWriter = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
            await JsonRpcResponseWriter.WriteAsync(pipeWriter, response, EthereumJsonSerializer.JsonOptions, CancellationToken.None);
            await pipeWriter.FlushAsync();
            await pipeWriter.CompleteAsync();

            string streamableSerialized = Encoding.UTF8.GetString(stream.ToArray());
            await TestContext.Out.WriteLineAsync(streamableSerialized);
            return streamableSerialized;
        }

        ArrayBufferWriter<byte> writer = new();
        JsonRpcResponseWriter.Write(writer, response, EthereumJsonSerializer.JsonOptions);

        string serialized = Encoding.UTF8.GetString(writer.WrittenSpan);
        await TestContext.Out.WriteLineAsync(serialized);

        Assert.That(writer.WrittenCount, Is.EqualTo(serialized.Length));

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

    private static string GetFailureMessage(JsonRpcResponse response) =>
        response is JsonRpcErrorResponse error
            ? $"RPC error: {error.Error?.Code} {error.Error?.Message}"
            : $"Unexpected response type: {response.GetType().FullName}";
}
