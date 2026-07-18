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

namespace Nethermind.BalanceViewer.Plugin;

/// <summary>Hooks the balance viewer middleware into the JSON-RPC web host.</summary>
/// <remarks>
/// Registered in Autofac by <see cref="BalanceViewerModule"/>; called by
/// <c>JsonRpcRunner</c> during web-host startup via <see cref="IJsonRpcServiceConfigurer"/>.
/// The registry is built in the web host's MS DI container because
/// <see cref="IJsonRpcUrlCollection"/> only exists there (it is created by the StartRpc step,
/// not registered in Autofac); the plugin's Autofac dependencies are bridged in.
/// </remarks>
public sealed class BalanceViewerConfigurer(
    IPortfolioConfig config, IInitConfig initConfig, IBackgroundTaskScheduler scheduler,
    ILogFinder logFinder, IBlockFinder blockFinder, ILogManager logManager) : IJsonRpcServiceConfigurer
{
    public void Configure(IServiceCollection services)
    {
        DetectionCache cache = new(initConfig.BaseDbPath, logManager);
        services.AddSingleton(config);
        services.AddSingleton(logManager);
        services.AddSingleton<ISiblingNodeRegistry, SiblingNodeRegistry>();
        services.AddSingleton<IDetectionCache>(cache);
        services.AddSingleton<IDetectionScanner>(new DetectionScanner(scheduler, logFinder, blockFinder, cache, logManager));
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
/// Serves the embedded balance viewer page at <c>/portfolio</c> (plus its service worker), the
/// sibling-node discovery list at <c>/portfolio-nodes</c>, and proxies JSON-RPC to discovered
/// siblings via <c>/portfolio-rpc/{port}</c> so the multi-chain view works through a single port.
/// </summary>
public sealed class BalanceViewerMiddleware(RequestDelegate next, IJsonRpcUrlCollection jsonRpcUrlCollection, ISiblingNodeRegistry siblings, IDetectionCache detection, IDetectionScanner scanner)
{
    private static readonly PathString NodesPath = new("/portfolio-nodes");
    private static readonly PathString ProxyPathPrefix = new("/portfolio-rpc");
    private static readonly PathString DetectProxyPathPrefix = new("/portfolio-detect-rpc");
    private static readonly PathString DetectPath = new("/portfolio-detect");
    private static readonly PathString IpfsPathPrefix = new("/portfolio-ipfs");
    private static readonly PathString PinPathPrefix = new("/portfolio-ipfs-pin");

    // Opt-in only: the UI calls this exclusively after the user enables IPFS (with a privacy prompt). Forwards
    // to a local Kubo gateway so off-chain NFT art can be rendered without the browser talking to a third party.
    private const string IpfsGateway = "http://127.0.0.1:8080";
    // Local Kubo RPC API, used only for the single hardcoded pin/add operation (auto-pin viewed art). Not a
    // general proxy — only the CID is forwarded, so the browser can't drive arbitrary node-API commands.
    private const string IpfsApi = "http://127.0.0.1:5001";
    private static readonly HttpClient IpfsClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    // Per-request cap for a gateway GET. Long enough to give the local node a fair chance to find a provider and
    // fetch, but bounded so an unresolvable CID doesn't hang the request indefinitely (the UI shows a retry, which
    // re-triggers the node's provider search + fetch).
    private static readonly TimeSpan IpfsGetTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private static readonly ManifestEmbeddedFileProvider FileProvider =
        new(typeof(BalanceViewerMiddleware).Assembly, "wwwroot");

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
            // ?chainId=<id>&address=<0x…> drops one account (the per-account rescan button, so it re-walks from
            // head); with no params it drops everything (developer/diagnostic, e.g. bvClearDetectionCache).
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
        // The UI is a single self-contained file that changes with each plugin build; without this a
        // browser heuristically caches it and keeps running stale code after an upgrade (e.g. detection
        // appearing broken on a device that visited an older version). Force a fresh fetch each load.
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
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

    // GET /portfolio-ipfs/{cid}/{path} — forwards to the local IPFS gateway's /ipfs/ path so the browser can
    // render off-chain NFT art same-origin (never contacting a third-party gateway). Opt-in from the UI only.
    private async Task ServeIpfsAsync(HttpContext context)
    {
        context.Request.Path.StartsWithSegments(IpfsPathPrefix, out PathString remaining);
        string rel = remaining.Value?.TrimStart('/') ?? string.Empty;
        if (rel.Length == 0)
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
            // IPFS is content-addressed (the CID is the content hash), so a resolved response never changes —
            // let the browser cache it permanently. Reopening art after a reload is then served from cache.
            if (resp.IsSuccessStatusCode)
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            await resp.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // the local node couldn't resolve the CID in time — the UI shows a retry (which re-triggers a fetch)
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    // POST /portfolio-ipfs-pin/{cid} — pins the CID on the user's own Kubo node (auto-pin viewed art). Opt-in
    // from the UI, and only when Local IPFS is enabled. This is NOT a general node-API proxy: only pin/add is
    // ever issued and only the URL-encoded CID is forwarded, so the browser can't reach other Kubo commands.
    private Task ServePinAsync(HttpContext context)
    {
        context.Request.Path.StartsWithSegments(PinPathPrefix, out PathString remaining);
        string cid = remaining.Value?.TrimStart('/') ?? string.Empty;
        // accept only a CID (optionally with a subpath): base32/58 chars, '/', '.', '-', '_'; reject anything else
        if (cid.Length is 0 or > 256 || !cid.All(c => char.IsLetterOrDigit(c) || c is '/' or '.' or '-' or '_'))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        // Fire-and-forget on the node: the pin must run to completion even if the browser navigates away (tying it
        // to the request would cancel in-flight pins on reload/close), and Kubo may take a while to fetch the
        // content, which shouldn't block the response. Auto-pin is best-effort, so acknowledge immediately.
        _ = PinAsync(cid);
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        return Task.CompletedTask;
    }

    private static async Task PinAsync(string cid)
    {
        try { using HttpResponseMessage _ = await IpfsClient.PostAsync($"{IpfsApi}/api/v0/pin/add?arg={Uri.EscapeDataString(cid)}", content: null); }
        catch { /* best-effort: no local Kubo RPC (5001), or the content couldn't be retrieved */ }
    }

    // DELETE /portfolio-ipfs-pin — unpins every recursive pin and reclaims the space. Issued when the user turns
    // auto-pin off, so disabling pinning frees the art it accumulated. Detached (may unpin thousands + GC), 202.
    private Task ServeUnpinAllAsync(HttpContext context)
    {
        _ = UnpinAllAsync();
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        return Task.CompletedTask;
    }

    private static async Task UnpinAllAsync()
    {
        try
        {
            using HttpResponseMessage lsResp = await IpfsClient.PostAsync($"{IpfsApi}/api/v0/pin/ls?type=recursive", content: null);
            if (!lsResp.IsSuccessStatusCode) return;
            using JsonDocument doc = JsonDocument.Parse(await lsResp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("Keys", out JsonElement keys))
            {
                foreach (JsonProperty pin in keys.EnumerateObject())
                {
                    try { using HttpResponseMessage _ = await IpfsClient.PostAsync($"{IpfsApi}/api/v0/pin/rm?arg={Uri.EscapeDataString(pin.Name)}", content: null); }
                    catch { /* best-effort per pin */ }
                }
            }
            // reclaim the now-unpinned blocks from disk
            try { using HttpResponseMessage _ = await IpfsClient.PostAsync($"{IpfsApi}/api/v0/repo/gc", content: null); } catch { }
        }
        catch { /* best-effort: no local Kubo RPC (5001) */ }
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

    // POST /portfolio-detect-rpc/{port} — forwards a detection request to a sibling node's /portfolio-detect,
    // so the multi-chain view drives historical detection on sibling chains too (not only the connected one).
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

    // GET /portfolio-detect?chainId=<id>&address=<0x…> — the cached detection entry, or null.
    private async Task ServeDetectGetAsync(HttpContext context)
    {
        long chainId = long.TryParse(context.Request.Query["chainId"], out long parsed) ? parsed : 0;
        string address = context.Request.Query["address"].ToString();
        DetectionEntry? entry = string.IsNullOrEmpty(address) ? null : detection.Get(chainId, address);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, entry, JsonOpts, context.RequestAborted);
    }

    // POST /portfolio-detect — trigger (or resume) an in-process detection scan for one account,
    // then return the current cached progress so the client can start polling.
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
