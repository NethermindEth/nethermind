// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Logging;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Resources;
using Nethermind.Mcp.Tools;

namespace Nethermind.Mcp.Hosting;

/// <summary>
/// Owns a Kestrel web host that exposes the Model Context Protocol over HTTP/SSE.
/// </summary>
/// <remarks>
/// Startup is fail-soft: a port collision (or any other startup exception) is logged at
/// <c>Error</c> level and surfaces as a <c>false</c> return from <see cref="StartAsync"/>,
/// rather than propagating up to the caller. This isolates the MCP listener from the rest
/// of the node — if MCP cannot start, the node continues without it.
///
/// The pipeline is, in order: <see cref="ApiKeyAuthMiddleware"/> (bearer-token gate, no-op
/// when <see cref="IMcpConfig.ApiKey"/> is unset), then a request-level concurrency
/// semaphore sized by <see cref="IMcpConfig.MaxConcurrent"/>, then the SDK's MCP handler at
/// the configured route prefix. The semaphore counts every HTTP request to the MCP
/// endpoint rather than only tool invocations — acceptable for v1 because MCP is the only
/// route mounted on this host.
///
/// Tools and resources are provided by re-using the singleton instances already
/// constructed in the plugin's Autofac container, mirrored into the Kestrel
/// <see cref="IServiceCollection"/> so the SDK can resolve them. Each instance is
/// singleton in both containers and is shared, so this does not duplicate state.
/// </remarks>
public sealed class McpWebHost : IAsyncDisposable
{
    private readonly IMcpConfig _config;
    private readonly Logging.ILogger _logger;
    private readonly IServiceProvider _mcpServices;

    private WebApplication? _app;
    private bool _started;

    public McpWebHost(IMcpConfig config, ILogManager logManager, IServiceProvider mcpServices)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logManager);
        ArgumentNullException.ThrowIfNull(mcpServices);

        _config = config;
        _logger = logManager.GetClassLogger<McpWebHost>();
        _mcpServices = mcpServices;
    }

    /// <summary>
    /// Gets the URI Kestrel actually bound to. Null until <see cref="StartAsync"/> succeeds.
    /// </summary>
    public Uri? BoundUri { get; private set; }

    /// <summary>
    /// Builds and starts the Kestrel host. Returns <c>true</c> on success, <c>false</c> if
    /// startup throws (e.g. port already in use). Failures are logged at <c>Error</c> and
    /// never propagate.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return BoundUri is not null;
        }

        WebApplication? app = null;
        try
        {
            app = BuildApp();
            await app.StartAsync(ct).ConfigureAwait(false);

            BoundUri = ReadBoundUri(app);
            _app = app;
            _started = true;

            if (_logger.IsInfo) _logger.Info($"MCP server listening at {BoundUri}/mcp");
            return true;
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"MCP server failed to start on {_config.HttpHost}:{_config.HttpPort}", ex);

            // Best-effort cleanup so we don't leave a half-started host behind. The local
            // `app` may have allocated Kestrel sockets even if StartAsync threw.
            if (app is not null)
            {
                try
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeEx)
                {
                    if (_logger.IsError) _logger.Error("MCP server failed to clean up after startup error", disposeEx);
                }
            }

            _app = null;
            BoundUri = null;
            return false;
        }
    }

    /// <summary>
    /// Stops the Kestrel host if running. Safe to call multiple times.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (!_started || _app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("MCP server failed to stop cleanly", ex);
        }
        finally
        {
            _started = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("MCP server stop during dispose threw", ex);
        }

        try
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("MCP server dispose threw", ex);
        }
        finally
        {
            _app = null;
        }
    }

    private WebApplication BuildApp()
    {
        // Use the slim builder: minimal configuration sources, no static assets, no built-in
        // logging providers. This keeps the MCP host self-contained and avoids picking up any
        // appsettings.json from the calling process's content root.
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "Nethermind.Mcp",
        });

        IPAddress address = IPAddress.Parse(_config.HttpHost);
        int port = _config.HttpPort;
        builder.WebHost.UseKestrel(options => options.Listen(address, port));

        // Bridge tool/resource singletons from the plugin Autofac container into Kestrel DI.
        // The MCP SDK resolves tool types via IServiceProvider; sharing the same instances
        // avoids constructing them twice and keeps the adapter graph (INethermindNodeAdapter,
        // ConfigRedactor, …) consistent with the rest of the plugin.
        builder.Services.AddSingleton(_config);
        ResolveAndRegister<INethermindNodeAdapter>(builder.Services);
        ResolveAndRegister<ConfigRedactor>(builder.Services);
        ResolveAndRegister<NodeStatusTools>(builder.Services);
        ResolveAndRegister<NodeHealthTools>(builder.Services);
        ResolveAndRegister<ChainQueryTools>(builder.Services);
        ResolveAndRegister<NodeStatusResource>(builder.Services);
        ResolveAndRegister<NodeConfigResource>(builder.Services);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(NodeStatusTools).Assembly)
            .WithResourcesFromAssembly(typeof(NodeStatusResource).Assembly);

        SemaphoreSlim concurrencyGate = new(Math.Max(1, _config.MaxConcurrent), Math.Max(1, _config.MaxConcurrent));
        builder.Services.AddSingleton(concurrencyGate);

        WebApplication app = builder.Build();

        // Order matters: auth (cheap reject) → concurrency gate → MCP handler.
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.Use(async (context, next) =>
        {
            await concurrencyGate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        app.MapMcp("/mcp");
        return app;
    }

    private void ResolveAndRegister<T>(IServiceCollection services) where T : class
    {
        T? instance = (T?)_mcpServices.GetService(typeof(T));
        if (instance is not null)
        {
            services.AddSingleton(instance);
        }
    }

    private static Uri? ReadBoundUri(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        IServerAddressesFeature? addresses = server.Features.Get<IServerAddressesFeature>();
        if (addresses is null)
        {
            return null;
        }

        foreach (string address in addresses.Addresses)
        {
            if (!string.IsNullOrEmpty(address) && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
            {
                return uri;
            }
        }

        return null;
    }
}
