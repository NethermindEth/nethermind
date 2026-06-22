// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.Logging;
using Nethermind.Core.Specs;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Bridges the Autofac container (where Engine API domain handlers live) to ASP.NET
/// Core's MS DI container so that <see cref="SszMiddleware"/> and its
/// <see cref="ISszEndpointHandler"/> implementations can be resolved by Kestrel.
/// </summary>
/// <remarks>
/// Registered in Autofac by <c>BaseMergePluginModule</c>; called by
/// <c>JsonRpcRunner</c> during web-host startup via <see cref="IJsonRpcServiceConfigurer"/>.
/// </remarks>
public sealed class SszMiddlewareConfigurer(IComponentContext ctx) : IJsonRpcServiceConfigurer
{
    private static readonly Type[] SingletonHandlers =
    [
        typeof(ClientVersionSszHandler),
        typeof(CapabilitiesSszHandler),
    ];

    public void Configure(IServiceCollection services)
    {
        // IJsonRpcUrlCollection is registered by JsonRpcRunner.Start; we bridge only what isn't.
        services.AddTransient<IStartupFilter, SszMiddlewareStartupFilter>();

        services.Bridge<ILogManager>(ctx);
        services.Bridge<IRpcAuthentication>(ctx);
        services.Bridge<IEngineRpcModule>(ctx);
        services.Bridge<ISpecProvider>(ctx);
        services.Bridge<IProcessExitSource>(ctx);
        services.Bridge<Nethermind.Blockchain.Find.IBlockFinder>(ctx);

        services.AddSingleton<ISszEndpointHandler, NewPayloadSszHandler<NewPayloadDescriptorV1, NewPayloadV1RequestWire>>();
        services.AddSingleton<ISszEndpointHandler, NewPayloadSszHandler<NewPayloadDescriptorV2, NewPayloadV2RequestWire>>();
        services.AddSingleton<ISszEndpointHandler, NewPayloadSszHandler<NewPayloadDescriptorV3, NewPayloadV3RequestWire>>();
        services.AddSingleton<ISszEndpointHandler, NewPayloadSszHandler<NewPayloadDescriptorV4, NewPayloadV4RequestWire>>();
        services.AddSingleton<ISszEndpointHandler, NewPayloadSszHandler<NewPayloadDescriptorV5, NewPayloadV5RequestWire>>();

        services.AddSingleton<ISszEndpointHandler, ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV1, ForkchoiceUpdatedV1RequestWire>>();
        services.AddSingleton<ISszEndpointHandler, ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV2, ForkchoiceUpdatedV2RequestWire>>();
        services.AddSingleton<ISszEndpointHandler, ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV3, ForkchoiceUpdatedV3RequestWire>>();
        services.AddSingleton<ISszEndpointHandler, ForkchoiceUpdatedSszHandler<ForkchoiceUpdatedDescriptorV4, ForkchoiceUpdatedRequestWire>>();

        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV1, ExecutionPayload>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV2, GetPayloadV2Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV3, GetPayloadV3Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV4, GetPayloadV4Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV5, GetPayloadV5Result>>();
        services.AddSingleton<ISszEndpointHandler, GetPayloadSszHandler<GetPayloadDescriptorV6, GetPayloadV6Result>>();

        services.AddSingleton<ISszEndpointHandler, GetBlobsV1SszHandler>();

        services.AddSingleton<ISszEndpointHandler, GetBlobsV2SszHandler<GetBlobsDescriptorV2>>();
        services.AddSingleton<ISszEndpointHandler, GetBlobsV2SszHandler<GetBlobsDescriptorV3>>();
        services.AddSingleton<ISszEndpointHandler, GetBlobsV4SszHandler>();

        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV1, ExecutionPayloadBodyV1Result>>();
        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByHashSszHandler<PayloadBodiesByHashDescriptorV2, ExecutionPayloadBodyV2Result>>();

        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV1, ExecutionPayloadBodyV1Result>>();
        services.AddSingleton<ISszEndpointHandler,
            GetPayloadBodiesByRangeSszHandler<PayloadBodiesByRangeDescriptorV2, ExecutionPayloadBodyV2Result>>();

        foreach (Type handler in SingletonHandlers)
            services.AddSingleton(typeof(ISszEndpointHandler), handler);

        // EIP-7928 witness endpoint: registered ONLY as the concrete type, which SszMiddleware takes
        // directly for its dedicated fast-path. Deliberately NOT registered as ISszEndpointHandler so
        // it never enters the routing table (it has no place there — see SszMiddleware.BuildRoutes).
        services.AddSingleton<NewPayloadWithWitnessSszHandler>();
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
