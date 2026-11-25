// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Hosting;

/// <summary>Hosts JSON-RPC middleware on Kestrel using pooled request contexts.</summary>
/// <remarks>Owns the supplied service provider and disposes it when the host is disposed.</remarks>
public sealed partial class WebHost : IHost, IAsyncDisposable
{
    private ApplicationLifetime? _applicationLifetime;
    private readonly IConfiguration _config;
    private readonly IServiceProvider? _applicationServices;
    private readonly ILogger _logger;
    private readonly ILogManager _logManager;
    private readonly IStartup _startup;

    private readonly Lock _stopLock = new();
    private Task? _stopTask;
    private bool _startedServer;

    private IServer? Server { get; set; }

    /// <summary>Creates a host for the supplied service provider and middleware startup.</summary>
    /// <param name="applicationServices">Services owned by this host.</param>
    /// <param name="config">The listener configuration.</param>
    /// <param name="startup">Configures the request middleware.</param>
    /// <param name="logManager">Provides host logging.</param>
    public WebHost(
        IServiceProvider applicationServices,
        IConfiguration config,
        IStartup startup,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(applicationServices);
        ArgumentNullException.ThrowIfNull(config);

        _applicationServices = applicationServices;
        _config = config;
        _startup = startup;
        _logger = logManager.GetLogger("Runner.JsonRpc.WebHost");
        _logManager = logManager;
    }

    /// <inheritdoc/>
    public IServiceProvider Services
    {
        get
        {
            Debug.Assert(_applicationServices != null, "Initialize must be called before accessing services.");
            return _applicationServices;
        }
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Debug.Assert(_applicationServices != null, "Initialize must be called first.");

        RequestDelegate application = BuildApplication();

        _applicationLifetime = _applicationServices.GetRequiredService<ApplicationLifetime>();

        HttpContextFactory httpContextFactory = new(Services);
        HostingApplication hostingApp = new(application, _logManager, httpContextFactory);

        await Server.StartAsync(hostingApp, cancellationToken).ConfigureAwait(false);
        _startedServer = true;

        // Fire IApplicationLifetime.Started
        _applicationLifetime?.NotifyStarted();
    }

    [MemberNotNull(nameof(Server))]
    internal RequestDelegate BuildApplication()
    {
        Debug.Assert(_applicationServices != null, "Initialize must be called first.");

        try
        {
            EnsureServer();

            IApplicationBuilderFactory builderFactory = _applicationServices.GetRequiredService<IApplicationBuilderFactory>();
            IApplicationBuilder builder = builderFactory.CreateBuilder(Server.Features);
            builder.ApplicationServices = _applicationServices;

            Action<IApplicationBuilder> configure = _startup!.Configure;

            IEnumerable<IStartupFilter>? startupFilters =
                _applicationServices.GetService<IEnumerable<IStartupFilter>>();
            if (startupFilters is not null)
            {
                foreach (IStartupFilter filter in startupFilters.Reverse())
                    configure = filter.Configure(configure);
            }

            // Ahead of the startup filters, so that plugin middleware such as SSZ-REST cannot bypass it.
            builder.UseAllowedHosts(
                _applicationServices.GetRequiredService<IConfigProvider>().GetConfig<IJsonRpcConfig>(),
                _applicationServices.GetRequiredService<IJsonRpcUrlCollection>(),
                _logger);
            configure(builder);

            return builder.Build();
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("Application startup exception", ex);
            throw;
        }
    }

    [MemberNotNull(nameof(Server))]
    private void EnsureServer()
    {
        Debug.Assert(_applicationServices != null, "Initialize must be called first.");

        if (Server == null)
        {
            Server = _applicationServices.GetRequiredService<IServer>();

            IServerAddressesFeature serverAddressesFeature = Server.Features?.Get<IServerAddressesFeature>();
            ICollection<string> addresses = serverAddressesFeature?.Addresses;
            if (addresses != null && !addresses.IsReadOnly && addresses.Count == 0)
            {
                string urls = _config[WebHostDefaults.ServerUrlsKey];//?? _config[DeprecatedServerUrlsKey];
                if (!string.IsNullOrEmpty(urls))
                {
                    //serverAddressesFeature!.PreferHostingUrls = WebHostUtilities.ParseBool(_config[WebHostDefaults.PreferHostingUrlsKey]);

                    foreach (string value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        addresses.Add(value);
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource completion;
        lock (_stopLock)
        {
            if (_stopTask is not null) return _stopTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }

        // Publish completion before shutdown callbacks can reenter StopAsync.
        _ = StopCoreAsync(completion, cancellationToken);
        return completion.Task;
    }

    private async Task StopCoreAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(1));
            cancellationToken = cts.Token;

            // Fire IApplicationLifetime.Stopping
            _applicationLifetime?.StopApplication();

            if (Server != null && _startedServer)
                await Server.StopAsync(cancellationToken).ConfigureAwait(false);

            // Fire IApplicationLifetime.Stopped
            _applicationLifetime?.NotifyStopped();
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("WebHost shutdown error", ex);
        }

        await DisposeServiceProviderAsync(_applicationServices).ConfigureAwait(false);
    }

    private static ValueTask DisposeServiceProviderAsync(IServiceProvider? serviceProvider)
    {
        switch (serviceProvider)
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
        return default;
    }
}
