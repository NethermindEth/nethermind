// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus.Producers;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

public sealed class SszRpcEndpointHandler(
    IEngineRpcModule engineModule,
    MethodInfo method,
    SszRestMetadata metadata) : SszEndpointHandlerBase
{
    private const int PayloadIdHexLength = 16;
    private const int PayloadIdByteLength = 8;
    private readonly RpcInvoker _invoke = CreateInvoker(method);

    public override string HttpMethod => metadata.HttpMethod;
    public override string Resource => metadata.Resource;
    public override int? Version => metadata.Version;
    public override bool AcceptsPathExtra => metadata.AcceptsPathExtra;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        if (metadata.NoStore)
            ctx.Response.Headers.CacheControl = "no-store";

        object? invocationResult;
        try
        {
            invocationResult = _invoke(engineModule, DecodeArguments(metadata.Request, extra, body));
        }
        catch (SszRequestValidationException ex)
        {
            await WriteErrorAsync(ctx, ex.StatusCode, ex.Message);
            return;
        }

        object? result = await UnwrapInvocationResultAsync(invocationResult);
        await WriteResponseAsync(ctx, metadata.Response, result);
    }

    public static ISszEndpointHandler[] CreateHandlers(IEngineRpcModule engineModule)
    {
        (MethodInfo Method, SszRestMetadata Metadata)[] endpoints = GetEndpoints();
        ISszEndpointHandler[] handlers = new ISszEndpointHandler[endpoints.Length];

        for (int i = 0; i < endpoints.Length; i++)
            handlers[i] = new SszRpcEndpointHandler(engineModule, endpoints[i].Method, endpoints[i].Metadata);

        return handlers;
    }

    public static (MethodInfo Method, SszRestMetadata Metadata)[] GetEndpoints()
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

    private static object?[] DecodeArguments(SszRestRequest request, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body) => request switch
    {
        SszRestRequest.Capabilities => [SszCodec.DecodeCapabilitiesRequest(body)],
        SszRestRequest.ClientVersion => [SszCodec.DecodeClientVersionRequest(body)],
        SszRestRequest.ForkchoiceUpdatedV1 => DecodeForkchoiceUpdatedV1(body),
        SszRestRequest.ForkchoiceUpdatedV2 => DecodeForkchoiceUpdatedV2(body),
        SszRestRequest.ForkchoiceUpdatedV3 => DecodeForkchoiceUpdatedV3(body),
        SszRestRequest.ForkchoiceUpdatedV4 => DecodeForkchoiceUpdatedV4(body),
        SszRestRequest.GetBlobs => [SszCodec.DecodeGetBlobsRequest(body)],
        SszRestRequest.PayloadBodiesByHash => [SszCodec.DecodeGetPayloadBodiesByHashRequest(body)],
        SszRestRequest.PayloadBodiesByRange => DecodePayloadBodiesByRange(body),
        SszRestRequest.PayloadId => [ParsePayloadId(extra.Span)],
        SszRestRequest.NewPayloadV1 => DecodeNewPayloadV1(body),
        SszRestRequest.NewPayloadV2 => DecodeNewPayloadV2(body),
        SszRestRequest.NewPayloadV3 => DecodeNewPayloadV3(body),
        SszRestRequest.NewPayloadV4 => DecodeNewPayloadV4(body),
        SszRestRequest.NewPayloadV5 => DecodeNewPayloadV5(body),
        _ => throw new InvalidOperationException($"Unsupported SSZ-REST request kind {request}")
    };

    private static object?[] DecodeForkchoiceUpdatedV1(ReadOnlySequence<byte> body)
    {
        ForkchoiceUpdatedV1RequestWire.Decode(body, out ForkchoiceUpdatedV1RequestWire wire);
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return [state, attrs];
    }

    private static object?[] DecodeForkchoiceUpdatedV2(ReadOnlySequence<byte> body)
    {
        ForkchoiceUpdatedV2RequestWire.Decode(body, out ForkchoiceUpdatedV2RequestWire wire);
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return [state, attrs];
    }

    private static object?[] DecodeForkchoiceUpdatedV3(ReadOnlySequence<byte> body)
    {
        ForkchoiceUpdatedV3RequestWire.Decode(body, out ForkchoiceUpdatedV3RequestWire wire);
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return [state, attrs];
    }

    private static object?[] DecodeForkchoiceUpdatedV4(ReadOnlySequence<byte> body)
    {
        ForkchoiceUpdatedRequestWire.Decode(body, out ForkchoiceUpdatedRequestWire wire);
        ForkchoiceStateV1 state = SszCodec.ForkchoiceStateV1FromWire(wire.ForkchoiceState);
        PayloadAttributes? attrs = wire.PayloadAttributes is { Length: > 0 } a ? SszCodec.PayloadAttributesFromWire(a[0]) : null;
        return [state, attrs];
    }

    private static object?[] DecodePayloadBodiesByRange(ReadOnlySequence<byte> body)
    {
        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(body);
        return [start, count];
    }

    private static object?[] DecodeNewPayloadV1(ReadOnlySequence<byte> body)
    {
        NewPayloadV1RequestWire.Decode(body, out NewPayloadV1RequestWire wire);
        return [wire.ExecutionPayload.Unwrap()];
    }

    private static object?[] DecodeNewPayloadV2(ReadOnlySequence<byte> body)
    {
        NewPayloadV2RequestWire.Decode(body, out NewPayloadV2RequestWire wire);
        return [wire.ExecutionPayload.Unwrap()];
    }

    private static object?[] DecodeNewPayloadV3(ReadOnlySequence<byte> body)
    {
        NewPayloadV3RequestWire.Decode(body, out NewPayloadV3RequestWire wire);
        return [wire.ExecutionPayload.Unwrap(), wire.ExpectedBlobVersionedHashes.ToBytesArrays(), wire.ParentBeaconBlockRoot];
    }

    private static object?[] DecodeNewPayloadV4(ReadOnlySequence<byte> body)
    {
        NewPayloadV4RequestWire.Decode(body, out NewPayloadV4RequestWire wire);
        return [
            wire.ExecutionPayload.Unwrap(),
            wire.ExpectedBlobVersionedHashes.ToBytesArrays(),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests()
        ];
    }

    private static object?[] DecodeNewPayloadV5(ReadOnlySequence<byte> body)
    {
        NewPayloadV5RequestWire.Decode(body, out NewPayloadV5RequestWire wire);
        return [
            wire.ExecutionPayload.Unwrap(),
            wire.ExpectedBlobVersionedHashes.ToBytesArrays(),
            wire.ParentBeaconBlockRoot,
            wire.ExecutionRequests.ToExecutionRequests()
        ];
    }

    private static byte[] ParsePayloadId(ReadOnlySpan<char> extra)
    {
        if (extra.Length == 0)
            throw new SszRequestValidationException(StatusCodes.Status400BadRequest, "Missing payload ID");

        ReadOnlySpan<char> hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        if (hex.Length != PayloadIdHexLength)
            throw new SszRequestValidationException(
                StatusCodes.Status400BadRequest,
                $"Invalid payload ID: '{extra}' (expected {PayloadIdHexLength} hex chars)");

        Span<byte> stack = stackalloc byte[PayloadIdByteLength];
        if (Convert.FromHexString(hex, stack, out _, out _) != OperationStatus.Done)
            throw new SszRequestValidationException(StatusCodes.Status400BadRequest, $"Invalid payload ID: '{extra}'");

        return stack.ToArray();
    }

    private static async Task<object?> UnwrapInvocationResultAsync(object? invocationResult)
    {
        if (invocationResult is not Task task)
            return invocationResult;

        await task;

        Type taskType = task.GetType();
        if (!taskType.IsGenericType)
            return null;

        return taskType.GetProperty(nameof(Task<object>.Result))?.GetValue(task);
    }

    private static Task WriteResponseAsync(HttpContext ctx, SszRestResponse response, object? result) => response switch
    {
        SszRestResponse.Capabilities => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<IReadOnlyList<string>>>(result), SszCodec.EncodeCapabilitiesResponse),
        SszRestResponse.ClientVersion => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<ClientVersionV1[]>>(result), SszCodec.EncodeClientVersionResponse),
        SszRestResponse.ForkchoiceUpdated => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<ForkchoiceUpdatedV1Result>>(result), SszCodec.EncodeForkchoiceUpdatedResponse),
        SszRestResponse.GetBlobsV1 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<IReadOnlyList<BlobAndProofV1?>>>(result), SszCodec.EncodeGetBlobsV1Response),
        SszRestResponse.GetBlobsV2 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>>(result), static (d, w) => SszCodec.EncodeGetBlobsV2Response(d!, w)),
        SszRestResponse.GetBlobsV3 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>>(result), static (d, w) => SszCodec.EncodeGetBlobsV3Response(d!, w)),
        SszRestResponse.GetPayloadV1 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<ExecutionPayload?>>(result), static (d, w) => SszCodec.EncodeGetPayloadV1Response(d!, w)),
        SszRestResponse.GetPayloadV2 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<GetPayloadV2Result?>>(result), static (d, w) => SszCodec.EncodeGetPayloadV2Response(d, w)),
        SszRestResponse.GetPayloadV3 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<GetPayloadV3Result?>>(result), static (d, w) => SszCodec.EncodeGetPayloadV3Response(d, w)),
        SszRestResponse.GetPayloadV4 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<GetPayloadV4Result?>>(result), static (d, w) => SszCodec.EncodeGetPayloadV4Response(d, w)),
        SszRestResponse.GetPayloadV5 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<GetPayloadV5Result?>>(result), static (d, w) => SszCodec.EncodeGetPayloadV5Response(d, w)),
        SszRestResponse.GetPayloadV6 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<GetPayloadV6Result?>>(result), static (d, w) => SszCodec.EncodeGetPayloadV6Response(d, w)),
        SszRestResponse.PayloadBodiesV1 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>>(result), SszCodec.EncodePayloadBodiesV1Response),
        SszRestResponse.PayloadBodiesV2 => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>>(result), SszCodec.EncodePayloadBodiesV2Response),
        SszRestResponse.PayloadStatus => WriteSszResultAsync(ctx,
            Cast<ResultWrapper<PayloadStatusV1>>(result), SszCodec.EncodePayloadStatus),
        _ => throw new InvalidOperationException($"Unsupported SSZ-REST response kind {response}")
    };

    private static T Cast<T>(object? result) where T : class =>
        result as T ?? throw new InvalidOperationException($"Expected {typeof(T).Name}, got {result?.GetType().Name ?? "null"}");

    private sealed class SszRequestValidationException(int statusCode, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }
}
