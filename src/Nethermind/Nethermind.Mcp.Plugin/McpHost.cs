// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Nethermind.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Logging.Microsoft;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Monitoring.Config;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;
using ILogger = Nethermind.Logging.ILogger;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Nethermind.Mcp.Plugin;

/// <summary>Hosts the MCP Streamable HTTP endpoint on a dedicated loopback Kestrel listener.</summary>
/// <remarks>
/// The listener is independent of the JSON-RPC host and uses its own service container. It is started once by
/// <see cref="StartMcpServer"/> and stopped at node shutdown through <see cref="IStoppableService"/>, with
/// <see cref="DisposeAsync"/> as the final guarantee when the node container is disposed.
/// </remarks>
public sealed class McpHost(
    IMcpConfig config,
    IJsonRpcConfig jsonRpcConfig,
    McpEthTools tools,
    ILogManager logManager,
    IMetricsConfig? metricsConfig = null) : IAsyncDisposable, IDisposable, IStoppableService
{
    /// <summary>The route the MCP endpoint is served at.</summary>
    public const string EndpointPath = "/mcp";

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logManager.GetClassLogger<McpHost>();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WebApplication? _app;
    private bool _started;
    private bool _disposed;

    /// <summary>Gets the bound endpoint, such as <c>http://127.0.0.1:8555/mcp</c>, or <see langword="null"/> when not running.</summary>
    public Uri? Endpoint { get; private set; }

    /// <inheritdoc/>
    string IStoppableService.Description => "MCP server";

    /// <summary>Validates the configuration, then binds and starts the listener.</summary>
    /// <param name="cancellationToken">Cancels startup; the listener is torn down if it fires while starting.</param>
    /// <exception cref="Core.Exceptions.InvalidConfigurationException">The configuration is invalid.</exception>
    /// <exception cref="InvalidOperationException">The host was already started, or the listener failed to bind.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("The MCP server can only be started once.");
            _started = true;

            cancellationToken.ThrowIfCancellationRequested();

            McpConfigValidator.Validate(config, jsonRpcConfig, metricsConfig);
            IPAddress address = McpConfigValidator.ParseLoopbackAddress(config.Host);
            McpSecurityMiddleware security = new(
                McpConfigValidator.NormalizeOrigins(config.AllowedOrigins),
                McpConfigValidator.LoadAuthToken(config.AuthTokenFile));

            WebApplication app = Build(address, security);
            try
            {
                await app.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await app.DisposeAsync();
                throw;
            }
            catch (Exception e)
            {
                await app.DisposeAsync();
                string message = $"Failed to start the MCP server on {FormatAuthority(address, config.Port)}: {e.Message}";
                if (_logger.IsError) _logger.Error(message);
                throw new InvalidOperationException(message, e);
            }

            _app = app;
            Endpoint = ResolveEndpoint(app, address);

            if (_logger.IsInfo)
                _logger.Info($"MCP server is listening on {Endpoint} ({(security.RequiresAuth ? "bearer token required" : "no authentication")})");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Stops the listener, waiting for in-flight requests until <paramref name="cancellationToken"/> fires. No-op when not running.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            await StopCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc/>
    async Task IStoppableService.StopAsync()
    {
        using CancellationTokenSource timeout = new(ShutdownTimeout);
        await StopAsync(timeout.Token);
    }

    /// <summary>Stops the listener if running and releases it. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed) return;
            _disposed = true;

            using CancellationTokenSource timeout = new(ShutdownTimeout);
            await StopCoreAsync(timeout.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Synchronous counterpart of <see cref="DisposeAsync"/> for containers disposed synchronously.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        WebApplication? app = _app;
        if (app is null) return;

        _app = null;
        Endpoint = null;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
        }

        if (_logger.IsInfo) _logger.Info("MCP server stopped");
    }

    private WebApplication Build(IPAddress address, McpSecurityMiddleware security)
    {
        // The empty builder reads no appsettings, environment variables or command line, so nothing outside the
        // Nethermind config can add listeners or change behaviour.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = "Nethermind.Mcp",
        });

        builder.WebHost
            .UseKestrelCore()
            .ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = config.MaxRequestBodySize;
                options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
                options.Limits.MaxRequestHeaderCount = 64;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
                options.Limits.MaxConcurrentConnections = 64;
                options.Listen(address, config.Port, listen => listen.Protocols = HttpProtocols.Http1);
            });

        IServiceCollection services = builder.Services;
        // The default console lifetime hooks SIGINT/SIGTERM, which belong to the node's own shutdown handling.
        services.AddSingleton<IHostLifetime, NoopHostLifetime>();
        services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            // Warning and above only: lower levels of the SDK and Kestrel can include request content.
            logging.SetMinimumLevel(MsLogLevel.Warning);
            // The SDK warns on every malformed client request, and hosting duplicates the start failure reported below.
            logging.AddFilter("ModelContextProtocol", MsLogLevel.Error);
            logging.AddFilter("Microsoft.Extensions.Hosting", MsLogLevel.Critical);
            logging.AddProvider(new NethermindLoggerProvider(logManager));
        });
        services.AddRouting();
        services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "Nethermind", Version = ProductInfo.Version })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(tools.CreateServerTools());

        WebApplication app = builder.Build();
        app.Use(security.InvokeAsync);
        app.Use(RejectBadRequestsAsync);
        app.MapMcp(EndpointPath);
        return app;
    }

    /// <summary>Answers oversized or malformed bodies with their status code instead of letting Kestrel log each one as an unhandled error.</summary>
    private static async Task RejectBadRequestsAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException e) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = e.StatusCode;
        }
    }

    private static Uri ResolveEndpoint(WebApplication app, IPAddress address)
    {
        IServerAddressesFeature? addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        if (addresses is not null)
        {
            foreach (string bound in addresses.Addresses)
            {
                if (Uri.TryCreate(bound, UriKind.Absolute, out Uri? uri))
                    return new Uri(uri, EndpointPath);
            }
        }

        throw new InvalidOperationException($"The MCP server started but its bound address on {address} could not be determined.");
    }

    private static string FormatAuthority(IPAddress address, int port) => new IPEndPoint(address, port).ToString();

    private sealed class NoopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NethermindLoggerProvider(ILogManager logManager) : ILoggerProvider
    {
        private readonly NethermindLoggerFactory _factory = new(logManager);

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => _factory.CreateLogger($"Mcp.{categoryName}");

        public void Dispose() { }
    }
}
