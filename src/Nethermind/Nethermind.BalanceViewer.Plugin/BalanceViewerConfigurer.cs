// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc;

namespace Nethermind.BalanceViewer.Plugin;

/// <summary>Hooks the balance viewer middleware into the JSON-RPC web host.</summary>
/// <remarks>
/// Registered in Autofac by <see cref="BalanceViewerModule"/>; called by
/// <c>JsonRpcRunner</c> during web-host startup via <see cref="IJsonRpcServiceConfigurer"/>.
/// </remarks>
public sealed class BalanceViewerConfigurer : IJsonRpcServiceConfigurer
{
    public void Configure(IServiceCollection services)
        => services.AddTransient<IStartupFilter, BalanceViewerStartupFilter>();
}

internal sealed class BalanceViewerStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseMiddleware<BalanceViewerMiddleware>();
            next(app);
        };
}

/// <summary>Serves the embedded balance viewer page at the <c>/balances</c> path.</summary>
public sealed class BalanceViewerMiddleware(RequestDelegate next, IJsonRpcUrlCollection jsonRpcUrlCollection)
{
    private static readonly PathString BalancesPath = new("/balances");

    private readonly IFileInfo _page =
        new ManifestEmbeddedFileProvider(typeof(BalanceViewerMiddleware).Assembly, "wwwroot").GetFileInfo("balances.html");

    public Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || context.Request.Path != BalancesPath)
        {
            return next(context);
        }

        // Not served on authenticated (Engine API) ports.
        if (!_page.Exists ||
            !jsonRpcUrlCollection.TryGetValue(context.Connection.LocalPort, out JsonRpcUrl? url) ||
            url.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.SendFileAsync(_page);
    }
}
