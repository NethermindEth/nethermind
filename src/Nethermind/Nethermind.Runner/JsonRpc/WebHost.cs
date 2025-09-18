// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Nethermind.Logging;

namespace Nethermind.Runner.JsonRpc;

internal sealed partial class WebHost : IHost, IAsyncDisposable
{
    private ApplicationLifetime? _applicationLifetime;
    private readonly IConfiguration _config;
    private readonly IServiceProvider? _applicationServices;
    private readonly ILogger _logger;
    private readonly ILogManager _logManager;
    private readonly IStartup _startup;

    private bool _stopped;
    private bool _startedServer;

    private IServer? Server { get; set; }

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
        _logger = logManager.GetClassLogger();
        _logManager = logManager;
    }

    public IServiceProvider Services
    {
        get
        {
            Debug.Assert(_applicationServices != null, "Initialize must be called before accessing services.");
            return _applicationServices;
        }
    }

    public IFeatureCollection ServerFeatures
    {
        get
        {
            EnsureServer();
            return Server.Features;
        }
    }

    public void Start()
    {
        StartAsync().GetAwaiter().GetResult();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Debug.Assert(_applicationServices != null, "Initialize must be called first.");

        var application = BuildApplication();

        _applicationLifetime = _applicationServices.GetRequiredService<ApplicationLifetime>();

        _applicationServices.GetRequiredService<IHttpContextFactory>();
        var httpContextFactory = new HttpContextFactory(Services);
        var hostingApp = new HostingApplication(application, _logManager, httpContextFactory);

        await Server.StartAsync(hostingApp, cancellationToken).ConfigureAwait(false);
        _startedServer = true;

        // Fire IApplicationLifetime.Started
        _applicationLifetime?.NotifyStarted();
    }

    [MemberNotNull(nameof(Server))]
    private RequestDelegate BuildApplication()
    {
        Debug.Assert(_applicationServices != null, "Initialize must be called first.");

        try
        {
            EnsureServer();

            var builderFactory = _applicationServices.GetRequiredService<IApplicationBuilderFactory>();
            var builder = builderFactory.CreateBuilder(Server.Features);
            builder.ApplicationServices = _applicationServices;

            Action<IApplicationBuilder> configure = _startup!.Configure;

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

            var serverAddressesFeature = Server.Features?.Get<IServerAddressesFeature>();
            var addresses = serverAddressesFeature?.Addresses;
            if (addresses != null && !addresses.IsReadOnly && addresses.Count == 0)
            {
                var urls = _config[WebHostDefaults.ServerUrlsKey];//?? _config[DeprecatedServerUrlsKey];
                if (!string.IsNullOrEmpty(urls))
                {
                    //serverAddressesFeature!.PreferHostingUrls = WebHostUtilities.ParseBool(_config[WebHostDefaults.PreferHostingUrlsKey]);

                    foreach (var value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        addresses.Add(value);
                    }
                }
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped)
            return;
        _stopped = true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        cancellationToken = cts.Token;

        // Fire IApplicationLifetime.Stopping
        _applicationLifetime?.StopApplication();

        if (Server != null && _startedServer)
            await Server.StopAsync(cancellationToken).ConfigureAwait(false);

        // Fire IApplicationLifetime.Stopped
        _applicationLifetime?.NotifyStopped();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stopped)
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("WebHost shutdown error", ex);
            }
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
