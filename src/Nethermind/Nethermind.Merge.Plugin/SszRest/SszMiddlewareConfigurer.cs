// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
        services.AddTransient<IStartupFilter, SszMiddlewareStartupFilter>();

        services.Bridge<ILogManager>(ctx);
        services.Bridge<IJsonRpcUrlCollection>(ctx);
        services.Bridge<IRpcAuthentication>(ctx);
        services.Bridge<IEngineRpcModule>(ctx);
        services.Bridge<IProcessExitSource>(ctx);

        services.Bridge<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>>(ctx);
        services.AddSingleton<Func<byte[][], Task<ResultWrapper<IEnumerable<BlobAndProofV1?>>>>>(
        sp => ((IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>?>)
            sp.GetRequiredService<IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>>()).AsFunc()!);

        services.Bridge<IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>>(ctx);
        services.AddSingleton<Func<GetBlobsHandlerV2Request, Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>>>>(
            sp => sp.GetRequiredService<IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>>().AsFunc());

        services.Bridge<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>>(ctx);
        services.Bridge<IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>>>(ctx);
        services.Bridge<IGetPayloadBodiesByRangeV1Handler>(ctx);
        services.Bridge<IGetPayloadBodiesByRangeV2Handler>(ctx);

        services.Bridge<IHandler<IEnumerable<string>, IEnumerable<string>>>(ctx);
        services.Bridge<IHandler<TransitionConfigurationV1, TransitionConfigurationV1>>(ctx);

        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV1, ExecutionPayload>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV2, GetPayloadV2Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV3, GetPayloadV3Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV4, GetPayloadV4Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV5, GetPayloadV5Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV6, GetPayloadV6Result>>();

        services.AddSingleton<ISszEndpointHandler, GetBlobsV1SszHandler>();

        services.AddSingleton<ISszEndpointHandler, GetBlobsV2SszHandler<GetBlobsDescriptorV2>>();
        services.AddSingleton<ISszEndpointHandler, GetBlobsV2SszHandler<GetBlobsDescriptorV3>>();

        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV1, ExecutionPayloadBodyV1Result>>();
        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV2, ExecutionPayloadBodyV2Result>>();

        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV1, ExecutionPayloadBodyV1Result, IGetPayloadBodiesByRangeV1Handler>>();
        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV2, ExecutionPayloadBodyV2Result, IGetPayloadBodiesByRangeV2Handler>>();

        foreach (Type handler in SingletonHandlers)
            services.AddSingleton(typeof(ISszEndpointHandler), handler);
    }
}

file static class ServiceCollectionExtensions
{
    public static void Bridge<T>(this IServiceCollection services, IComponentContext ctx) where T : class
        => services.AddSingleton<T>(_ => ctx.Resolve<T>());
}

internal sealed class SszMiddlewareStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseMiddleware<SszMiddleware>();
            next(app);
        };
}
