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
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Facade.Find;
using Nethermind.JsonRpc;
using Nethermind.Logging;

namespace Nethermind.PortfolioViewer.Plugin;

/// <summary>Hooks the portfolio viewer middleware into the JSON-RPC web host.</summary>
/// <remarks>
/// Services are registered in the web host's MS DI container (not Autofac) because
/// <see cref="IJsonRpcUrlCollection"/> only exists there; the Autofac dependencies are bridged in.
/// </remarks>
public sealed class PortfolioViewerConfigurer(
    IPortfolioViewerConfig config, IInitConfig initConfig, IBackgroundTaskScheduler scheduler,
    ILogFinder logFinder, IBlockFinder blockFinder, ILogManager logManager) : IJsonRpcServiceConfigurer
{
    public void Configure(IServiceCollection services)
    {
        DetectionCache cache = new(initConfig.BaseDbPath, logManager);
        services.AddSingleton(config);
        services.AddSingleton(logManager);
        services.AddSingleton<ISiblingNodeRegistry, SiblingNodeRegistry>();
        services.AddSingleton<IDetectionCache>(cache);
        services.AddSingleton<IPinnedCidStore>(new PinnedCidStore(initConfig.BaseDbPath, logManager));
        services.AddSingleton<IDetectionScanner>(new DetectionScanner(scheduler, logFinder, blockFinder, cache, logManager));
        services.AddTransient<IStartupFilter, PortfolioViewerStartupFilter>();
    }
}

internal sealed class PortfolioViewerStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseMiddleware<PortfolioViewerMiddleware>();
            next(app);
        };
}

/// <summary>
/// Serves the embedded portfolio viewer page at <c>/portfolio</c> (plus its service worker), the
/// sibling-node discovery list at <c>/portfolio-nodes</c>, and proxies JSON-RPC to discovered
/// siblings via <c>/portfolio-rpc/{port}</c> so the multi-chain view works through a single port.
/// </summary>
public sealed class PortfolioViewerMiddleware(RequestDelegate next, IJsonRpcUrlCollection jsonRpcUrlCollection, ISiblingNodeRegistry siblings, IDetectionCache detection, IDetectionScanner scanner, IPinnedCidStore pins)
{
    private static readonly PathString NodesPath = new("/portfolio-nodes");
    private static readonly PathString ProxyPathPrefix = new("/portfolio-rpc");
    private static readonly PathString DetectProxyPathPrefix = new("/portfolio-detect-rpc");
    private static readonly PathString DetectPath = new("/portfolio-detect");
    private static readonly PathString IpfsPathPrefix = new("/portfolio-ipfs");
    private static readonly PathString PinPathPrefix = new("/portfolio-ipfs-pin");

    // Local Kubo gateway (art rendering) and RPC API (pin/add only). Opt-in from the UI after a privacy prompt.
    private const string IpfsGateway = "http://127.0.0.1:8080";
    private const string IpfsApi = "http://127.0.0.1:5001";
    private static readonly HttpClient IpfsClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    // Bounded so an unresolvable CID doesn't hang the request; the UI retries, re-triggering the node's fetch.
    private static readonly TimeSpan IpfsGetTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private static readonly ManifestEmbeddedFileProvider FileProvider =
        new(typeof(PortfolioViewerMiddleware).Assembly, "wwwroot");

    private static readonly Dictionary<PathString, (IFileInfo File, string ContentType)> Routes = new()
    {
        [new PathString("/portfolio")] = (FileProvider.GetFileInfo("portfolio.html"), "text/html; charset=utf-8"),
        [new PathString("/portfolio-sw.js")] = (FileProvider.GetFileInfo("portfolio-sw.js"), "text/javascript; charset=utf-8"),
        [new PathString("/portfolio.webmanifest")] = (FileProvider.GetFileInfo("portfolio.webmanifest"), "application/manifest+json"),
        [new PathString("/portfolio-icon.svg")] = (FileProvider.GetFileInfo("portfolio-icon.svg"), "image/svg+xml"),
    };

    public Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        bool isStaticFile = HttpMethods.IsGet(context.Request.Method) && Routes.ContainsKey(path);
        bool isNodesList = HttpMethods.IsGet(context.Request.Method) && path == NodesPath;
        bool isProxy = HttpMethods.IsPost(context.Request.Method) && path.StartsWithSegments(ProxyPathPrefix) && !path.StartsWithSegments(DetectProxyPathPrefix);
        bool isDetectProxy = HttpMethods.IsPost(context.Request.Method) && path.StartsWithSegments(DetectProxyPathPrefix);
        bool isDetectGet = HttpMethods.IsGet(context.Request.Method) && path == DetectPath;
        bool isDetectPost = HttpMethods.IsPost(context.Request.Method) && path == DetectPath;
        bool isDetectDelete = HttpMethods.IsDelete(context.Request.Method) && path == DetectPath;
        bool isPin = HttpMethods.IsPost(context.Request.Method) && path.StartsWithSegments(PinPathPrefix);
        bool isUnpinAll = HttpMethods.IsDelete(context.Request.Method) && path == PinPathPrefix; // disabling auto-pin
        bool isIpfs = HttpMethods.IsGet(context.Request.Method) && path.StartsWithSegments(IpfsPathPrefix);
        if (!isStaticFile && !isNodesList && !isProxy && !isDetectProxy && !isDetectGet && !isDetectPost && !isDetectDelete && !isIpfs && !isPin && !isUnpinAll)
        {
            return next(context);
        }

        // Not served on authenticated (Engine API) ports.
        if (!jsonRpcUrlCollection.TryGetValue(context.Connection.LocalPort, out JsonRpcUrl? url) || url.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        // CSRF guard: reject state-changing requests carrying a foreign Origin (browsers attach Origin on any
        // cross-origin request, incl. CORS-simple POSTs), so a drive-by page can't enqueue scans or pin CIDs.
        bool isSideEffecting = isProxy || isDetectProxy || isDetectPost || isDetectDelete || isPin || isUnpinAll;
        if (isSideEffecting && IsCrossOrigin(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        if (isPin) return ServePinAsync(context);
        if (isUnpinAll) return ServeUnpinAllAsync(context);
        if (isIpfs) return ServeIpfsAsync(context);
        if (isNodesList) return ServeNodesAsync(context);
        if (isProxy) return ProxyAsync(context);
        if (isDetectProxy) return ProxyDetectAsync(context);
        if (isDetectGet) return ServeDetectGetAsync(context);
        if (isDetectPost) return ServeDetectPostAsync(context);
        if (isDetectDelete)
        {
            // with chainId+address: drop one account (per-account rescan); with no params: drop everything (diagnostic)
            string? delAddress = context.Request.Query["address"];
            if (!string.IsNullOrEmpty(delAddress) && long.TryParse(context.Request.Query["chainId"], out long delChainId))
                detection.Remove(delChainId, delAddress);
            else
                detection.Clear();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }

        (IFileInfo file, string contentType) = Routes[path];
        if (!file.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.ContentType = contentType;
        // force a fresh fetch each load, so a browser never runs a stale UI after a plugin upgrade
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return context.Response.SendFileAsync(file);
    }

    // Absent Origin (same-origin navigations, non-browser clients) counts as same-origin; an unparseable
    // Origin ("null" from a sandboxed context) counts as cross-origin.
    private static bool IsCrossOrigin(HttpContext context)
    {
        string? origin = context.Request.Headers.Origin;
        if (string.IsNullOrEmpty(origin)) return false;
        return !Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed) || parsed.Authority != context.Request.Host.Value;
    }

    // A CID (optionally with a subpath); rejects any '.'/'..' segment so it can't escape the '/ipfs/' URL prefix.
    private static bool IsSafeIpfsRef(string s) =>
        s.Length is > 0 and <= 256
        && s.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '-' or '_')
        && !s.Split('/').Any(segment => segment is "." or "..");

    private async Task ServeNodesAsync(HttpContext context)
    {
        IReadOnlyList<SiblingNode> nodes = await siblings.GetSiblingsAsync(context.RequestAborted);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            nodes.Select(n => new { port = n.Port, chainId = n.ChainId }),
            cancellationToken: context.RequestAborted);
    }

    // Forwards to the local IPFS gateway so the browser renders off-chain NFT art same-origin.
    private async Task ServeIpfsAsync(HttpContext context)
    {
        context.Request.Path.StartsWithSegments(IpfsPathPrefix, out PathString remaining);
        string rel = remaining.Value?.TrimStart('/') ?? string.Empty;
        if (!IsSafeIpfsRef(rel))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(IpfsGetTimeout);
        try
        {
            using HttpResponseMessage resp = await IpfsClient.GetAsync(
                $"{IpfsGateway}/ipfs/{rel}", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            context.Response.StatusCode = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentType is not null)
                context.Response.ContentType = resp.Content.Headers.ContentType.ToString();
            // IPFS is content-addressed, so a resolved response never changes — cache it permanently
            if (resp.IsSuccessStatusCode)
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            await resp.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    // Pins the CID on the user's own Kubo node (auto-pin viewed art). Only pin/add is ever issued — not a
    // general node-API proxy.
    private Task ServePinAsync(HttpContext context)
    {
        context.Request.Path.StartsWithSegments(PinPathPrefix, out PathString remaining);
        string cid = remaining.Value?.TrimStart('/') ?? string.Empty;
        if (!IsSafeIpfsRef(cid))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        // Fire-and-forget: the pin must survive the browser navigating away, and it's best-effort, so ack now.
        _ = PinAsync(cid);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        return Task.CompletedTask;
    }

    private async Task PinAsync(string cid)
    {
        try
        {
            using HttpResponseMessage resp = await IpfsClient.PostAsync($"{IpfsApi}/api/v0/pin/add?arg={Uri.EscapeDataString(cid)}", content: null);
            // track only pins we added, so unpin-all reclaims exactly these
            if (resp.IsSuccessStatusCode) pins.Add(cid);
        }
        catch { /* best-effort: no local Kubo RPC (5001), or the content couldn't be retrieved */ }
    }

    // Unpins only the CIDs this plugin pinned (never the user's other pins) and reclaims the space. Issued when
    // the user turns auto-pin off.
    private Task ServeUnpinAllAsync(HttpContext context)
    {
        _ = UnpinAllAsync();
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        return Task.CompletedTask;
    }

    private async Task UnpinAllAsync()
    {
        IReadOnlyCollection<string> ours = pins.Snapshot();
        if (ours.Count == 0) return;
        foreach (string cid in ours)
        {
            try { using HttpResponseMessage _ = await IpfsClient.PostAsync($"{IpfsApi}/api/v0/pin/rm?arg={Uri.EscapeDataString(cid)}", content: null); }
            catch { /* best-effort per pin: no local Kubo RPC (5001), or already unpinned */ }
        }
        pins.Clear();
        try { using HttpResponseMessage _ = await IpfsClient.PostAsync($"{IpfsApi}/api/v0/repo/gc", content: null); } catch { }
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

    // Forwards a detection request to a sibling node, so the multi-chain view drives detection on sibling chains.
    private async Task ProxyDetectAsync(HttpContext context)
    {
        context.Request.Path.StartsWithSegments(DetectProxyPathPrefix, out PathString remaining);
        if (!int.TryParse(remaining.Value?.TrimStart('/'), out int port) || !siblings.IsKnownSibling(port))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "application/json";
        await siblings.ProxyDetectAsync(port, context.Request.Body, context.Response.Body, context.RequestAborted);
    }

    private async Task ServeDetectGetAsync(HttpContext context)
    {
        long chainId = long.TryParse(context.Request.Query["chainId"], out long parsed) ? parsed : 0;
        string address = context.Request.Query["address"].ToString();
        DetectionEntry? entry = string.IsNullOrEmpty(address) ? null : detection.Get(chainId, address);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, entry, JsonOpts, context.RequestAborted);
    }

    // Trigger (or resume) a detection scan for one account, returning the current cached progress to poll on.
    private async Task ServeDetectPostAsync(HttpContext context)
    {
        DetectPost? post = await JsonSerializer.DeserializeAsync<DetectPost>(context.Request.Body, JsonOpts, context.RequestAborted);
        if (post is null || !Address.TryParse(post.Address, out Address? account) || account is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        scanner.RequestScan(post.ChainId, account);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, detection.Get(post.ChainId, post.Address), JsonOpts, context.RequestAborted);
    }

    private sealed record DetectPost(long ChainId, string Address);
}
