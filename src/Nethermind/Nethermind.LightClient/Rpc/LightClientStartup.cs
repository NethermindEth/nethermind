// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

internal sealed class LightClientStartup(LightClientStatus status, IJsonRpcProcessor processor,
    LightClientMetrics metrics, string rpcUrls, ILogger logger) : IStartup
{
    private const string MetricsPath = "/metrics";

    IServiceProvider IStartup.ConfigureServices(IServiceCollection services) => throw new NotSupportedException();

    public void Configure(IApplicationBuilder app)
    {
        IHostApplicationLifetime lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
        LightClientHost.ConfigureRequestPolicy(app, rpcUrls);
        app.UseWhen(static context => context.Request.Path == MetricsPath,
            static branch => branch.UseResponseCompression());
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapPost("/", (HttpContext context) => RpcEndpoint.HandleAsync(context, processor, logger,
                stoppingToken: lifetime.ApplicationStopping, metrics: metrics));
            endpoints.MapGet("/status", status.Read);
            endpoints.MapGet(MetricsPath, metrics.WriteAsync);
            endpoints.MapGet("/ws", status.HandleWebSocketAsync);
        });
    }
}
