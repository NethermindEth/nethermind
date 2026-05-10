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
    public void Configure(IServiceCollection services)
    {
        // IJsonRpcUrlCollection is registered by JsonRpcRunner.Start; we bridge only what isn't.
        services.AddTransient<IStartupFilter, SszMiddlewareStartupFilter>();

        services.Bridge<ILogManager>(ctx);
        services.Bridge<IRpcAuthentication>(ctx);
        services.Bridge<IEngineRpcModule>(ctx);
        services.Bridge<IProcessExitSource>(ctx);

        services.AddSszRpcEndpointHandlers();
    }
}

file static class ServiceCollectionExtensions
{
    public static void Bridge<T>(this IServiceCollection services, IComponentContext ctx) where T : class
        => services.AddSingleton<T>(_ => ctx.Resolve<T>());

    public static void AddSszRpcEndpointHandlers(this IServiceCollection services)
    {
        foreach ((System.Reflection.MethodInfo method, SszRestMetadata metadata) in SszRpcEndpointHandler.GetEndpoints())
            services.AddSingleton<ISszEndpointHandler>(sp =>
                new SszRpcEndpointHandler(sp.GetRequiredService<IEngineRpcModule>(), method, metadata));
    }
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
