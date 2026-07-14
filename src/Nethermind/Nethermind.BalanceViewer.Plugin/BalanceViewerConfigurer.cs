// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.BalanceViewer.Plugin;

/// <summary>Hooks the balance viewer middleware into the JSON-RPC web host.</summary>
/// <remarks>
/// Registered in Autofac by <see cref="BalanceViewerModule"/>; called by
/// <c>JsonRpcRunner</c> during web-host startup via <see cref="IJsonRpcServiceConfigurer"/>.
/// The registry is built in the web host's MS DI container because
/// <see cref="IJsonRpcUrlCollection"/> only exists there (it is created by the StartRpc step,
/// not registered in Autofac); the plugin's Autofac dependencies are bridged in.
/// </remarks>
public sealed class BalanceViewerConfigurer(IBalanceViewerConfig config, IInitConfig initConfig, ILogManager logManager) : IJsonRpcServiceConfigurer
{
    public void Configure(IServiceCollection services)
    {
        services.AddSingleton(config);
        services.AddSingleton(logManager);
        services.AddSingleton<ISiblingNodeRegistry, SiblingNodeRegistry>();
        services.AddSingleton<IDetectionCache>(new DetectionCache(initConfig.BaseDbPath, logManager));
        services.AddTransient<IStartupFilter, BalanceViewerStartupFilter>();
    }
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

/// <summary>
/// Serves the embedded balance viewer page at <c>/balances</c> (plus its service worker), the
/// sibling-node discovery list at <c>/balances-nodes</c>, and proxies JSON-RPC to discovered
/// siblings via <c>/balances-rpc/{port}</c> so the multi-chain view works through a single port.
/// </summary>
public sealed class BalanceViewerMiddleware(RequestDelegate next, IJsonRpcUrlCollection jsonRpcUrlCollection, ISiblingNodeRegistry siblings, IDetectionCache detection)
{
    private static readonly PathString NodesPath = new("/balances-nodes");
    private static readonly PathString ProxyPathPrefix = new("/balances-rpc");
    private static readonly PathString DetectPath = new("/balances-detect");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private static readonly ManifestEmbeddedFileProvider FileProvider =
        new(typeof(BalanceViewerMiddleware).Assembly, "wwwroot");

    private static readonly Dictionary<PathString, (IFileInfo File, string ContentType)> Routes = new()
    {
        [new PathString("/balances")] = (FileProvider.GetFileInfo("balances.html"), "text/html; charset=utf-8"),
        [new PathString("/balances-sw.js")] = (FileProvider.GetFileInfo("balances-sw.js"), "text/javascript; charset=utf-8"),
        [new PathString("/balances.webmanifest")] = (FileProvider.GetFileInfo("balances.webmanifest"), "application/manifest+json"),
        [new PathString("/balances-icon.svg")] = (FileProvider.GetFileInfo("balances-icon.svg"), "image/svg+xml"),
    };

    public Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        bool isStaticFile = HttpMethods.IsGet(context.Request.Method) && Routes.ContainsKey(path);
        bool isNodesList = HttpMethods.IsGet(context.Request.Method) && path == NodesPath;
        bool isProxy = HttpMethods.IsPost(context.Request.Method) && path.StartsWithSegments(ProxyPathPrefix);
        bool isDetectGet = HttpMethods.IsGet(context.Request.Method) && path == DetectPath;
        bool isDetectPost = HttpMethods.IsPost(context.Request.Method) && path == DetectPath;
        if (!isStaticFile && !isNodesList && !isProxy && !isDetectGet && !isDetectPost)
        {
            return next(context);
        }

        // Not served on authenticated (Engine API) ports.
        if (!jsonRpcUrlCollection.TryGetValue(context.Connection.LocalPort, out JsonRpcUrl? url) || url.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        if (isNodesList) return ServeNodesAsync(context);
        if (isProxy) return ProxyAsync(context);
        if (isDetectGet) return ServeDetectGetAsync(context);
        if (isDetectPost) return ServeDetectPostAsync(context);

        (IFileInfo file, string contentType) = Routes[path];
        if (!file.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.ContentType = contentType;
        return context.Response.SendFileAsync(file);
    }

    private async Task ServeNodesAsync(HttpContext context)
    {
        IReadOnlyList<SiblingNode> nodes = await siblings.GetSiblingsAsync(context.RequestAborted);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            nodes.Select(n => new { port = n.Port, chainId = n.ChainId }),
            cancellationToken: context.RequestAborted);
    }

    private async Task ProxyAsync(HttpContext context)
    {
        context.Request.Path.StartsWithSegments(ProxyPathPrefix, out PathString remaining);
        if (!int.TryParse(remaining.Value?.TrimStart('/'), out int port) || !siblings.IsKnownSibling(port))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "application/json";
        await siblings.ProxyAsync(port, context.Request.Body, context.Response.Body, context.RequestAborted);
    }

    // GET /balances-detect?chainId=<id>&address=<0x…> — the cached detection entry, or null.
    private async Task ServeDetectGetAsync(HttpContext context)
    {
        long chainId = long.TryParse(context.Request.Query["chainId"], out long parsed) ? parsed : 0;
        string address = context.Request.Query["address"].ToString();
        DetectionEntry? entry = string.IsNullOrEmpty(address) ? null : detection.Get(chainId, address);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, entry, JsonOpts, context.RequestAborted);
    }

    // POST /balances-detect — the client persists a scan's results and progress for one account.
    private async Task ServeDetectPostAsync(HttpContext context)
    {
        DetectPost? post = await JsonSerializer.DeserializeAsync<DetectPost>(context.Request.Body, JsonOpts, context.RequestAborted);
        if (post is null || string.IsNullOrEmpty(post.Address))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        detection.Put(post.ChainId, post.Address, new DetectionEntry(
            post.Tokens ?? [], post.ScannedFrom, post.Head, post.Complete, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private sealed record DetectPost(
        long ChainId, string Address, DetectedToken[]? Tokens, long ScannedFrom, long Head, bool Complete);
}
