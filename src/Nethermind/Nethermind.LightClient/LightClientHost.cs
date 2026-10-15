// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal static class LightClientHost
{
    internal static void ConfigureRequestPolicy(WebApplication app, string rpcUrls)
    {
        bool loopbackOnly = rpcUrls.Split(';').All(static value => Uri.TryCreate(value, UriKind.Absolute, out Uri? url) && url.IsLoopback);
        app.Use(async (context, next) =>
        {
            if (loopbackOnly && (!Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out Uri? host) || !host.IsLoopback))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            string requestOrigin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(requestOrigin) &&
                !string.Equals(requestOrigin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase) &&
                !IsBrowserExtensionOrigin(requestOrigin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.UseCors(static policy => policy.SetIsOriginAllowed(IsBrowserExtensionOrigin).WithMethods("GET", "POST").AllowAnyHeader());
    }

    private static bool IsBrowserExtensionOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? url) ||
            !string.Equals(origin, $"{url.Scheme}://{url.Host}", StringComparison.OrdinalIgnoreCase)) return false;
        return url.Scheme switch
        {
            "chrome-extension" => url.Host.Length == 32 && url.Host.AsSpan().IndexOfAnyExceptInRange('a', 'p') < 0,
            "moz-extension" => Guid.TryParseExact(url.Host, "D", out _),
            _ => false,
        };
    }

    internal static async Task RunAsync(WebApplication app, Func<CancellationToken, Task> synchronize, ILogger? logger = null)
    {
        IHostApplicationLifetime lifetime = app.Lifetime;
        await app.StartAsync(lifetime.ApplicationStopping);
        if (logger is not null)
            foreach (string url in app.Urls)
                logger.LogInformation("Verified RPC listening at {Url}", url);
        Task server = app.WaitForShutdownAsync();
        Task sync = synchronize(lifetime.ApplicationStopping);
        await Task.WhenAny(server, sync);
        lifetime.StopApplication();
        await Task.WhenAll(server, sync);
    }
}
