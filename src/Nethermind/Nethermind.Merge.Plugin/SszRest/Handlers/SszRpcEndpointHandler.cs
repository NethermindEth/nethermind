// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public abstract class SszRpcEndpointHandler : SszEndpointHandlerBase
{
    private const int MaxPayloadBodiesByRangeCount = 32;

    private readonly IEngineRpcModule _engineModule;
    private readonly ISpecProvider _specProvider;
    private readonly SszRestMetadata _metadata;

    private protected SszRpcEndpointHandler(IEngineRpcModule engineModule, ISpecProvider specProvider, MethodInfo method, SszRestMetadata metadata)
    {
        _engineModule = engineModule;
        _specProvider = specProvider;
        _metadata = metadata;
    }

    public override string HttpMethod => _metadata.HttpMethod;
    public override string Resource => _metadata.Resource;
    public override int? Version => _metadata.Version;
    public override bool AcceptsPathExtra => _metadata.AcceptsPathExtra;

    private SszRestMetadata Metadata => _metadata;

    public static ISszEndpointHandler[] CreateHandlers(IEngineRpcModule engineModule, ISpecProvider specProvider)
    {
        SszRestEndpoint[] endpoints = GetEndpoints();
        ISszEndpointHandler[] handlers = new ISszEndpointHandler[endpoints.Length];

        for (int i = 0; i < endpoints.Length; i++)
            handlers[i] = CreateHandler(engineModule, specProvider, endpoints[i]);

        return handlers;
    }

    internal static ISszEndpointHandler CreateHandler(IEngineRpcModule engineModule, ISpecProvider specProvider, SszRestEndpoint endpoint)
    {
        Type resultType = GetResultType(endpoint.Method);
        Type argumentsType = GetRequestArgumentsType(endpoint.RequestType);
        Type handlerType = typeof(Handler<,,,>).MakeGenericType(endpoint.RequestType, argumentsType, endpoint.ResponseType, resultType);
        return (ISszEndpointHandler)Activator.CreateInstance(handlerType, engineModule, specProvider, endpoint.Method, endpoint.Metadata)!;
    }

    internal static SszRestEndpoint[] GetEndpoints()
    {
        List<SszRestEndpoint> endpoints = [];

        foreach (MethodInfo methodInfo in typeof(IEngineRpcModule).GetMethods())
        {
            SszRestAttribute? attribute = methodInfo.GetCustomAttribute<SszRestAttribute>();
            if (attribute is not null)
                endpoints.Add(attribute.ToEndpoint(methodInfo));
        }

        endpoints.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Metadata.Capability, b.Metadata.Capability));
        return endpoints.ToArray();
    }

    protected IEngineRpcModule EngineModule => _engineModule;

    private protected bool TryValidateForkchoiceFork<TArguments>(HttpContext ctx, ref TArguments arguments, out string error)
    {
        error = string.Empty;
        if (!TryGetForkchoiceTimestamp(ref arguments, out ulong timestamp))
            return true;

        if (!ctx.Items.TryGetValue("SszRouteFork", out object? forkObj) || forkObj is not string urlFork)
            return true;

        if (TimestampMatchesForkOrdinal(timestamp, SszRestPaths.ForkOrdinal(urlFork)))
            return true;

        error = $"URL fork '{urlFork}' does not match the fork for timestamp {timestamp}";
        return false;
    }

    private static bool TryGetForkchoiceTimestamp<TArguments>(ref TArguments arguments, out ulong timestamp)
    {
        timestamp = 0;
        if (typeof(TArguments) != typeof(ForkchoiceUpdatedArguments))
            return false;

        ForkchoiceUpdatedArguments forkchoice = Unsafe.As<TArguments, ForkchoiceUpdatedArguments>(ref arguments);
        if (forkchoice.Timestamp is not { } forkchoiceTimestamp)
            return false;

        timestamp = forkchoiceTimestamp;
        return true;
    }

    private protected static bool TryValidatePayloadBodiesByRange<TArguments>(ref TArguments arguments, out string error)
    {
        error = string.Empty;
        if (typeof(TArguments) != typeof(PayloadBodiesByRangeArguments))
            return true;

        PayloadBodiesByRangeArguments range = Unsafe.As<TArguments, PayloadBodiesByRangeArguments>(ref arguments);
        if (range.Count <= MaxPayloadBodiesByRangeCount)
            return true;

        error = $"The number of requested bodies must not exceed {MaxPayloadBodiesByRangeCount}";
        return false;
    }

    private bool TimestampMatchesForkOrdinal(ulong timestamp, int urlForkOrdinal)
    {
        if (urlForkOrdinal < 0)
            return false;

        IReleaseSpec payloadSpec = _specProvider.GetSpec(ForkActivation.TimestampOnly(timestamp));
        int ordinal = 0;
        IReleaseSpec? lastSeen = null;
        foreach (ForkActivation forkActivation in _specProvider.TransitionActivations)
        {
            if (forkActivation.Timestamp is null)
                continue;

            IReleaseSpec spec = _specProvider.GetSpec(forkActivation);
            if (ReferenceEquals(spec, lastSeen))
                continue;

            if (ReferenceEquals(spec, payloadSpec))
                return ordinal == urlForkOrdinal;

            lastSeen = spec;
            ordinal++;
        }

        return false;
    }

    private delegate ValueTask<ResultWrapper<TResult>> RpcInvoker<in TArguments, TResult>(
        IEngineRpcModule engineModule,
        TArguments arguments);

    private static RpcInvoker<TArguments, TResult> CreateInvoker<TArguments, TResult>(MethodInfo method)
    {
        ParameterExpression module = Expression.Parameter(typeof(IEngineRpcModule), "module");
        ParameterExpression arguments = Expression.Parameter(typeof(TArguments), "arguments");
        ParameterInfo[] parameters = method.GetParameters();
        Expression[] callArgs = new Expression[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            callArgs[i] = BuildCallArgument(arguments, parameters, i);
        }

        MethodCallExpression call = Expression.Call(module, method, callArgs);
        NewExpression body = call.Type == typeof(Task<ResultWrapper<TResult>>)
            ? Expression.New(typeof(ValueTask<ResultWrapper<TResult>>).GetConstructor([typeof(Task<ResultWrapper<TResult>>)])!, call)
            : Expression.New(typeof(ValueTask<ResultWrapper<TResult>>).GetConstructor([typeof(ResultWrapper<TResult>)])!, call);

        return Expression.Lambda<RpcInvoker<TArguments, TResult>>(body, module, arguments).Compile();
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

    private static Type GetRequestArgumentsType(Type requestType)
    {
        Type requestInterface = typeof(ISszRpcRequest<,>);
        Type[] interfaces = requestType.GetInterfaces();
        for (int i = 0; i < interfaces.Length; i++)
        {
            Type iface = interfaces[i];
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == requestInterface)
                return iface.GetGenericArguments()[1];
        }

        throw new InvalidOperationException($"{requestType.Name} must implement ISszRpcRequest<TSelf, TArguments>.");
    }

    private static Expression BuildCallArgument(ParameterExpression arguments, ParameterInfo[] parameters, int index)
    {
        ParameterInfo parameter = parameters[index];
        if (parameters.Length == 1 && parameter.ParameterType.IsAssignableFrom(arguments.Type))
            return Expression.Convert(arguments, parameter.ParameterType);

        string propertyName = ToPascalCase(parameter.Name ?? string.Empty);
        PropertyInfo property = arguments.Type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)
            ?? throw new InvalidOperationException($"{arguments.Type.Name} has no property matching parameter '{parameter.Name}'.");

        return Expression.Convert(Expression.Property(arguments, property), parameter.ParameterType);
    }

    private static string ToPascalCase(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private sealed class Handler<TRequest, TArguments, TResponse, TResult>(
        IEngineRpcModule engineModule,
        ISpecProvider specProvider,
        MethodInfo method,
        SszRestMetadata metadata)
        : SszRpcEndpointHandler(engineModule, specProvider, method, metadata)
        where TRequest : ISszRpcRequest<TRequest, TArguments>
        where TResponse : ISszCodec<TResponse>, INew<TResult, TResponse>
    {
        private readonly RpcInvoker<TArguments, TResult> _invoke = CreateInvoker<TArguments, TResult>(method);

        public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
        {
            if (Metadata.NoStore)
                ctx.Response.Headers.CacheControl = "no-store";

            Result<TArguments> arguments = TRequest.DecodeArguments(ctx, extra, body);
            if (arguments.IsError)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, arguments.Error);
                return;
            }

            TArguments data = arguments.Data!;
            if (!TryValidateForkchoiceFork(ctx, ref data, out string forkError))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, forkError, MergeErrorCodes.UnsupportedFork);
                return;
            }

            if (!TryValidatePayloadBodiesByRange(ref data, out string requestError))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, requestError, MergeErrorCodes.TooLargeRequest);
                return;
            }

            ResultWrapper<TResult> result = await _invoke(EngineModule, data);
            await WriteSszResultAsync(ctx, result, EncodeResponse);
        }

        private static int EncodeResponse(TResult result, IBufferWriter<byte> writer)
        {
            TResponse response = TResponse.New(result);
            int length = TResponse.GetLength(response);
            Span<byte> dst = writer.GetSpan(length)[..length];
            TResponse.Encode(dst, response);
            writer.Advance(length);
            return length;
        }
    }
}
