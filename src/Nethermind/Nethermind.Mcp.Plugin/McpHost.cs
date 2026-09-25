// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
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

/// <summary>Hosts the MCP Streamable HTTP endpoint on a dedicated Kestrel listener.</summary>
/// <remarks>
/// The listener binds a loopback address by default (plain HTTP, or HTTPS when a certificate is configured). A non-loopback
/// address is remote mode: HTTPS only, bearer token required, and only <see cref="IMcpConfig.AllowedHosts"/> accepted as
/// <c>Host</c>, as enforced by <see cref="McpConfigValidator"/> and <see cref="McpSecurityMiddleware"/>.
/// The listener is independent of the JSON-RPC host and uses its own service container. It is started once by
/// <see cref="StartMcpServer"/> and stopped at node shutdown through <see cref="IStoppableService"/>, with
/// <see cref="DisposeAsync"/> as the final guarantee when the node container is disposed.
/// </remarks>
public sealed class McpHost(
    IMcpConfig config,
    IJsonRpcConfig jsonRpcConfig,
    McpToolCatalog tools,
    McpResources resources,
    McpPrompts prompts,
    McpChainProfile chainProfile,
    ILogManager logManager,
    IMetricsConfig? metricsConfig = null) : IAsyncDisposable, IDisposable, IStoppableService
{
    /// <summary>The route the MCP endpoint is served at.</summary>
    public const string EndpointPath = "/mcp";

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logManager.GetClassLogger<McpHost>();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WebApplication? _app;
    private McpListenerSettings? _settings;
    private bool _started;
    private bool _disposed;

    /// <summary>Gets the bound endpoint, such as <c>http://127.0.0.1:8555/mcp</c> (or <c>https://</c> with TLS, <c>https://0.0.0.0:8555/mcp</c> when bound to all interfaces), or <see langword="null"/> when not running.</summary>
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

            McpListenerSettings settings = McpConfigValidator.Load(config, jsonRpcConfig, metricsConfig);
            IPAddress address = settings.Address;
            McpSecurityMiddleware security = new(
                settings.AllowedOrigins,
                settings.AuthToken,
                settings.IsRemote
                    ? McpHostPolicy.Remote(address, settings.AllowedHosts)
                    : McpHostPolicy.Loopback(settings.Certificate is null ? McpHostPolicy.DefaultHttpPort : McpHostPolicy.DefaultHttpsPort, settings.AllowedHosts),
                settings.IsRemote ? new McpAuthFailureLimiter() : null);

            WebApplication app;
            try
            {
                app = Build(settings, security);
            }
            catch
            {
                settings.Dispose();
                throw;
            }

            try
            {
                await app.StartAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await app.DisposeAsync();
                settings.Dispose();
                throw;
            }
            catch (Exception e)
            {
                await app.DisposeAsync();
                settings.Dispose();
                string message = $"Failed to start the MCP server on {FormatAuthority(address, config.Port)}: {e.Message}";
                if (_logger.IsError) _logger.Error(message);
                throw new InvalidOperationException(message, e);
            }

            _app = app;
            _settings = settings;
            Endpoint = ResolveEndpoint(_app, address);

            foreach (string warning in settings.Warnings)
            {
                if (_logger.IsWarn) _logger.Warn(warning);
            }

            if (_logger.IsInfo) _logger.Info($"MCP server listening on {Endpoint} ({DescribeMode(settings)})");
            if (settings.IsRemote && _logger.IsWarn)
                _logger.Warn($"MCP remote mode is on: read-only node data is served to the network at {Endpoint}. Keep Mcp.AuthTokenFile secret and firewall the port to trusted clients.");
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

        McpListenerSettings? settings = _settings;
        _app = null;
        _settings = null;
        Endpoint = null;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
            settings?.Dispose();
        }

        if (_logger.IsInfo) _logger.Info("MCP server stopped");
    }

    private WebApplication Build(McpListenerSettings settings, McpSecurityMiddleware security)
    {
        // The empty builder reads no appsettings, environment variables or command line, so nothing outside the
        // Nethermind config can add listeners or change behaviour.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = "Nethermind.Mcp",
        });

        builder.WebHost.UseKestrelCore();
        if (settings.Certificate is not null) builder.WebHost.UseKestrelHttpsConfiguration();
        builder.WebHost
            .ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = config.MaxRequestBodySize;
                options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
                options.Limits.MaxRequestHeaderCount = 64;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
                options.Limits.MaxConcurrentConnections = 64;
                options.Listen(settings.Address, config.Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    if (settings.Certificate is not null) listen.UseHttps(CreateHttpsOptions(settings));
                });
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
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "Nethermind", Title = "Nethermind execution client (read-only)", Version = ProductInfo.Version };
                o.ServerInstructions = CreateInstructions(chainProfile);
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(tools.CreateServerTools())
            .WithResources(resources.CreateServerResources())
            .WithPrompts(prompts.CreateServerPrompts());

        WebApplication app = builder.Build();
        app.Use(security.InvokeAsync);
        app.Use(RejectBadRequestsAsync);
        app.MapMcp(EndpointPath);
        return app;
    }

    /// <summary>Builds the instructions sent to clients at initialization, orienting the agent on this node and chain.</summary>
    internal static string CreateInstructions(McpChainProfile profile)
    {
        StringBuilder text = new();
        text.Append($"Read-only access to a Nethermind node on {profile.NetworkName} (chain id {profile.ChainId}, native currency {profile.NativeCurrencySymbol}");
        text.Append(profile.IsTestnet ? ", a testnet whose coins have no value). " : "). ");
        text.Append("Start with node_status to learn whether the node is synced and which blocks still have state, bodies and receipts (pruned nodes serve recent history only), ");
        text.Append("then read the nethermind://guide resource for which tool answers which question, block selectors, units and error codes. ");
        text.Append("Amounts are hex quantities in wei with formatted fields next to them: present the formatted amounts with their symbols. ");
        if (profile.IsGnosisFamily)
            text.Append("This is a Gnosis chain: gas and native balances are in xDAI, never ETH; validators stake GNO, an ERC-20 token. ");
        text.Append("Nothing here can send transactions, sign, or change the node.");
        return text.ToString();
    }

    private static HttpsConnectionAdapterOptions CreateHttpsOptions(McpListenerSettings settings) => new()
    {
        ServerCertificate = settings.Certificate,
        ServerCertificateChain = settings.CertificateChain.Count > 0 ? settings.CertificateChain : null,
        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        ClientCertificateMode = ClientCertificateMode.NoCertificate,
        HandshakeTimeout = TimeSpan.FromSeconds(10),
    };

    private static string DescribeMode(McpListenerSettings settings)
    {
        StringBuilder mode = new(settings.IsRemote ? "remote mode" : "loopback");
        mode.Append(settings.AuthToken is null ? ", no authentication" : ", bearer auth");
        if (settings.AllowedHosts.Length > 0) mode.Append(", allowed hosts: ").AppendJoin(", ", (object[])settings.AllowedHosts);
        return mode.ToString();
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
