// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal static class LightClientHost
{
    internal static (int Beacon, int Quic, int Execution) ReadPorts(IConfiguration configuration)
    {
        int beacon = ReadPort("beacon-port", 9050);
        int quic = ReadPort("beacon-quic-port", 9051, allowDisabled: true);
        int execution = ReadPort("execution-port", 30307);
        if (beacon == execution || quic != 0 && (quic == beacon || quic == execution))
            throw new ArgumentException("Beacon, beacon QUIC and execution ports must not overlap.");
        return (beacon, quic, execution);

        int ReadPort(string option, int defaultPort, bool allowDisabled = false)
        {
            int port = configuration.GetValue(option, defaultPort);
            if (port < (allowDisabled ? 0 : 1) || port > 65535)
                throw new ArgumentOutOfRangeException(option, port, "Port must be between 1 and 65535; beacon QUIC also accepts 0 to disable it.");
            return port;
        }
    }

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
                logger.LogInformation("Light-client RPC and status listening at {Url}", url);
        Task server = app.WaitForShutdownAsync();
        Task? sync = null;
        try
        {
            sync = SynchronizeAsync();
            await Task.WhenAny(server, sync);
        }
        finally
        {
            lifetime.StopApplication();
            await Task.WhenAll(server, sync ?? Task.CompletedTask);
        }

        async Task SynchronizeAsync()
        {
            try { await synchronize(lifetime.ApplicationStopping); }
            catch (OperationCanceledException exception) when (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                throw new InvalidOperationException("Light-client startup or synchronization was canceled before application shutdown.", exception);
            }
        }
    }
}
