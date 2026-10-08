// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.LightClient;

internal static class LightClientHost
{
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
