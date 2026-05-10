// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public abstract class SszRpcEndpointHandler : SszEndpointHandlerBase
{
    private readonly IEngineRpcModule _engineModule;
    private readonly SszRestMetadata _metadata;
    private readonly RpcInvoker _invoke;

    private protected SszRpcEndpointHandler(IEngineRpcModule engineModule, MethodInfo method, SszRestMetadata metadata)
    {
        _engineModule = engineModule;
        _metadata = metadata;
        _invoke = CreateInvoker(method);
    }

    public override string HttpMethod => _metadata.HttpMethod;
    public override string Resource => _metadata.Resource;
    public override int? Version => _metadata.Version;
    public override bool AcceptsPathExtra => _metadata.AcceptsPathExtra;

    private SszRestMetadata Metadata => _metadata;

    public static ISszEndpointHandler[] CreateHandlers(IEngineRpcModule engineModule)
    {
        (MethodInfo Method, SszRestMetadata Metadata)[] endpoints = GetEndpoints();
        ISszEndpointHandler[] handlers = new ISszEndpointHandler[endpoints.Length];

        for (int i = 0; i < endpoints.Length; i++)
            handlers[i] = CreateHandler(engineModule, endpoints[i].Method, endpoints[i].Metadata);

        return handlers;
    }

    internal static ISszEndpointHandler CreateHandler(IEngineRpcModule engineModule, MethodInfo method, SszRestMetadata metadata)
    {
        Type resultType = GetResultType(method);
        Type handlerType = typeof(Handler<,,>).MakeGenericType(metadata.RequestType, metadata.ResponseType, resultType);
        return (ISszEndpointHandler)Activator.CreateInstance(handlerType, engineModule, method, metadata)!;
    }

    internal static (MethodInfo Method, SszRestMetadata Metadata)[] GetEndpoints()
    {
        List<(MethodInfo Method, SszRestMetadata Metadata)> endpoints = [];

        foreach (MethodInfo methodInfo in typeof(IEngineRpcModule).GetMethods())
        {
            SszRestAttribute? attribute = methodInfo.GetCustomAttribute<SszRestAttribute>();
            if (attribute is not null)
                endpoints.Add((methodInfo, attribute.ToMetadata(methodInfo)));
        }

        endpoints.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Metadata.Capability, b.Metadata.Capability));
        return endpoints.ToArray();
    }

    protected object? Invoke(object?[] args) => _invoke(_engineModule, args);

    protected static async Task<object?> UnwrapInvocationResultAsync(object? invocationResult)
    {
        if (invocationResult is not Task task)
            return invocationResult;

        await task;

        Type taskType = task.GetType();
        if (!taskType.IsGenericType)
            return null;

        return taskType.GetProperty(nameof(Task<object>.Result))?.GetValue(task);
    }

    private delegate object? RpcInvoker(IEngineRpcModule engineModule, object?[] args);

    private static RpcInvoker CreateInvoker(MethodInfo method)
    {
        ParameterExpression module = Expression.Parameter(typeof(IEngineRpcModule), "module");
        ParameterExpression args = Expression.Parameter(typeof(object?[]), "args");
        ParameterInfo[] parameters = method.GetParameters();
        Expression[] callArgs = new Expression[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            BinaryExpression arg = Expression.ArrayIndex(args, Expression.Constant(i));
            callArgs[i] = Expression.Convert(arg, parameters[i].ParameterType);
        }

        MethodCallExpression call = Expression.Call(module, method, callArgs);
        Expression body = Expression.Convert(call, typeof(object));
        return Expression.Lambda<RpcInvoker>(body, module, args).Compile();
    }

    private static Type GetResultType(MethodInfo method)
    {
        Type returnType = method.ReturnType;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            returnType = returnType.GetGenericArguments()[0];

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ResultWrapper<>))
            return returnType.GetGenericArguments()[0];

        throw new InvalidOperationException($"{method.Name} must return ResultWrapper<T> or Task<ResultWrapper<T>> to be exposed as SSZ REST.");
    }

    private sealed class Handler<TRequest, TResponse, TResult>(
        IEngineRpcModule engineModule,
        MethodInfo method,
        SszRestMetadata metadata)
        : SszRpcEndpointHandler(engineModule, method, metadata)
        where TRequest : ISszRpcRequest<TRequest>
        where TResponse : ISszCodec<TResponse>, ISszRpcResponse<TResponse, TResult>
    {
        public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
        {
            if (Metadata.NoStore)
                ctx.Response.Headers.CacheControl = "no-store";

            object? invocationResult;
            try
            {
                invocationResult = Invoke(TRequest.DecodeArguments(extra, body));
            }
            catch (SszRequestValidationException ex)
            {
                await WriteErrorAsync(ctx, ex.StatusCode, ex.Message);
                return;
            }

            object? result = await UnwrapInvocationResultAsync(invocationResult);
            await WriteSszResultAsync(ctx, Cast<ResultWrapper<TResult>>(result), EncodeResponse);
        }

        private static int EncodeResponse(TResult result, IBufferWriter<byte> writer)
        {
            TResponse response = TResponse.FromDomain(result);
            int length = TResponse.GetLength(response);
            Span<byte> dst = writer.GetSpan(length)[..length];
            TResponse.Encode(dst, response);
            writer.Advance(length);
            return length;
        }
    }

    private static T Cast<T>(object? result) where T : class =>
        result as T ?? throw new InvalidOperationException($"Expected {typeof(T).Name}, got {result?.GetType().Name ?? "null"}");
}
