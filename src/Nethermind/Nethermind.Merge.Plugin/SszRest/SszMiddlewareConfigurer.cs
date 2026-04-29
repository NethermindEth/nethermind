// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.SszRest.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Bridges the Autofac container (where Engine API domain handlers live) to ASP.NET
/// Core's MS DI container so that <see cref="SszMiddleware"/> and its
/// <see cref="ISszEndpointHandler"/> implementations can be resolved by Kestrel.
/// <para>
/// Registered in Autofac by <c>BaseMergePluginModule</c>; called by
/// <c>JsonRpcRunner</c> during web-host startup via <see cref="IJsonRpcServiceConfigurer"/>.
/// </summary>
public sealed class SszMiddlewareConfigurer(IComponentContext ctx) : IJsonRpcServiceConfigurer
{
    private static readonly Type[] SingletonHandlers =
    [
        typeof(NewPayloadSszHandler),
        typeof(ForkchoiceUpdatedSszHandler),
        typeof(ClientVersionSszHandler),
        typeof(CapabilitiesSszHandler),
        typeof(TransitionConfigurationSszHandler),
    ];

    public void Configure(IServiceCollection services)
    {
        services.Bridge<ILogManager>(ctx);
        services.Bridge<IJsonRpcUrlCollection>(ctx);
        services.Bridge<IRpcAuthentication>(ctx);
        services.Bridge<IAsyncHandler<ExecutionPayload, PayloadStatusV1>>(ctx);

        services.Bridge<IAsyncHandler<byte[], ExecutionPayload?>>(ctx);

        services.Bridge<IForkchoiceUpdatedHandler>(ctx);

        services.Bridge<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>>(ctx);

        services.Bridge<IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>>(ctx);

        services.Bridge<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>>(ctx);
        services.Bridge<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>>>(ctx);
        services.Bridge<IGetPayloadBodiesByRangeV1Handler>(ctx);
        services.Bridge<IGetPayloadBodiesByRangeV2Handler>(ctx);

        services.Bridge<IHandler<IEnumerable<string>, IEnumerable<string>>>(ctx);
        services.Bridge<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(ctx);

        services.AddSingleton<ISszProcessExitSource>(new SszProcessExitSource { ProcessExit = ctx.Resolve<IProcessExitSource>().Token });

        void RegisterGetPayloadVersion<TResult>(int version, Func<TResult?, (byte[] buffer, int length)> encoder) where TResult : class
        {
            services.Bridge<IAsyncHandler<byte[], TResult?>>(ctx);
            services.AddSingleton<ISszEndpointHandler>(
                _ => new GetPayloadSszHandler<TResult>(version,
                    ctx.Resolve<IAsyncHandler<byte[], TResult?>>(),
                    encoder));
        }

        void AddGetPayloadBodiesByHash<TResult>(
            int version,
            Func<IReadOnlyList<TResult?>, (byte[] buffer, int length)> encoder)
            where TResult : class =>
            services.AddSingleton<ISszEndpointHandler>(
                _ => new GetPayloadBodiesByHashSszHandler<TResult>(version,
                    ctx.Resolve<IHandler<IReadOnlyList<Hash256>, IEnumerable<TResult?>>>(),
                    (IEnumerable<TResult?> e) => encoder(e as IReadOnlyList<TResult?> ?? ServiceCollectionExtensions.AsReadOnlyList(e))));

        void AddGetPayloadBodiesByRange<TResult>(
            int version,
            Func<long, long, Task<ResultWrapper<IEnumerable<TResult?>>>> rangeHandle,
            Func<IReadOnlyList<TResult?>, (byte[] buffer, int length)> encoder)
            where TResult : class =>
            services.AddSingleton<ISszEndpointHandler>(
                _ => new GetPayloadBodiesByRangeSszHandler<TResult>(version, rangeHandle,
                    (IEnumerable<TResult?> e) => encoder(e as IReadOnlyList<TResult?> ?? ServiceCollectionExtensions.AsReadOnlyList(e))));

        void AddGetBlobsV2(
            int version,
            bool allowPartialReturn,
            Func<IReadOnlyList<BlobAndProofV2?>, (byte[] buffer, int length)> encoder) =>
            services.AddSingleton<ISszEndpointHandler>(
                _ => new GetBlobsV2SszHandler(version,
                    allowPartialReturn,
                    ctx.Resolve<IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>>(),
                    encoder));

        services.AddSingleton<ISszEndpointHandler>(
            _ => new GetPayloadSszHandler<ExecutionPayload>(1,
                ctx.Resolve<IAsyncHandler<byte[], ExecutionPayload?>>(),
                SszCodec.EncodeGetPayloadV1Response));
        RegisterGetPayloadVersion<GetPayloadV2Result>(2, SszCodec.EncodeGetPayloadV2Response);
        RegisterGetPayloadVersion<GetPayloadV3Result>(3, SszCodec.EncodeGetPayloadV3Response);
        RegisterGetPayloadVersion<GetPayloadV4Result>(4, SszCodec.EncodeGetPayloadV4Response);
        RegisterGetPayloadVersion<GetPayloadV5Result>(5, SszCodec.EncodeGetPayloadV5Response);
        RegisterGetPayloadVersion<GetPayloadV6Result>(6, SszCodec.EncodeGetPayloadV6Response);

        services.AddSingleton<ISszEndpointHandler>(
            _ => new GetBlobsV1SszHandler(
                ctx.Resolve<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>>()));

        AddGetBlobsV2(2, allowPartialReturn: false, SszCodec.EncodeGetBlobsV2Response);
        AddGetBlobsV2(3, allowPartialReturn: true, SszCodec.EncodeGetBlobsV3Response);

        AddGetPayloadBodiesByHash<ExecutionPayloadBodyV1Result>(1, SszCodec.EncodePayloadBodiesV1Response);
        AddGetPayloadBodiesByHash<ExecutionPayloadBodyV2Result>(2, SszCodec.EncodePayloadBodiesV2Response);

        AddGetPayloadBodiesByRange<ExecutionPayloadBodyV1Result>(1, ctx.Resolve<IGetPayloadBodiesByRangeV1Handler>().Handle, SszCodec.EncodePayloadBodiesV1Response);
        AddGetPayloadBodiesByRange<ExecutionPayloadBodyV2Result>(2, ctx.Resolve<IGetPayloadBodiesByRangeV2Handler>().Handle, SszCodec.EncodePayloadBodiesV2Response);

        foreach (Type handler in SingletonHandlers)
            services.AddSingleton(typeof(ISszEndpointHandler), handler);
    }
}

file static class ServiceCollectionExtensions
{
    public static void Bridge<T>(this IServiceCollection services, IComponentContext ctx) where T : class
        => services.AddSingleton(ctx.Resolve<T>());

    public static IReadOnlyList<T?> AsReadOnlyList<T>(IEnumerable<T?> source)
    {
        if (source is IReadOnlyList<T?> list) return list;
        List<T?> result = [.. source];
        return result;
    }
}
