// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace Nethermind.Monitoring;

/// <summary>
/// Copy of KestrelMetricServer but does not wait for Ctrl-C so that it does not intercept exit code
/// </summary>
public sealed class NethermindKestrelMetricServer : MetricHandler
{
    public NethermindKestrelMetricServer(int port, string url = "/metrics", CollectorRegistry? registry = null, X509Certificate2? certificate = null) : this("+", port, url, registry, certificate)
    {
    }

    public NethermindKestrelMetricServer(string hostname, int port, string url = "/metrics", CollectorRegistry? registry = null, X509Certificate2? certificate = null) : this(LegacyOptions(hostname, port, url, registry, certificate))
    {
    }

    private static KestrelMetricServerOptions LegacyOptions(string hostname, int port, string url, CollectorRegistry? registry, X509Certificate2? certificate) =>
        new KestrelMetricServerOptions
        {
            Hostname = hostname,
            Port = (ushort)port,
            Url = url,
            Registry = registry,
            TlsCertificate = certificate,
        };

    public NethermindKestrelMetricServer(KestrelMetricServerOptions options)
    {
        _hostname = options.Hostname;
        _port = options.Port;
        _url = options.Url;
        _certificate = options.TlsCertificate;

        // We use one callback to apply the legacy settings, and from within this we call the real callback.
        _configureExporter = settings =>
        {
            // Legacy setting, may be overridden by ConfigureExporter.
            settings.Registry = options.Registry;

            if (options.ConfigureExporter != null)
                options.ConfigureExporter(settings);
        };
    }

    private readonly string _hostname;
    private readonly int _port;
    private readonly string _url;

    private readonly X509Certificate2? _certificate;

    private readonly Action<MetricServerMiddleware.Settings> _configureExporter;

    protected override Task StartServer(CancellationToken cancel)
    {
        var s = _certificate != null ? "s" : "";
        var hostAddress = $"http{s}://{_hostname}:{_port}";

        // If the caller needs to customize any of this, they can just set up their own web host and inject the middleware.
        var builder = new WebHostBuilder()
            .UseKestrel()
            .UseIISIntegration()
            .Configure(app =>
            {
                app.UseMetricServer(_configureExporter, _url);

                // If there is any URL prefix, we just redirect people going to root URL to our prefix.
                if (!string.IsNullOrWhiteSpace(_url.Trim('/')))
                {
                    app.MapWhen(context => context.Request.Path.Value?.Trim('/') == "",
                        configuration =>
                        {
                            configuration.Use((HttpContext context, RequestDelegate next) =>
                            {
                                context.Response.Redirect(_url);
                                return Task.CompletedTask;
                            });
                        });
                }
            });

        if (_certificate != null)
        {
            builder = builder.ConfigureServices(services =>
            {
                Action<ListenOptions> configureEndpoint = options =>
                {
                    options.UseHttps(_certificate);
                };

                services.Configure<KestrelServerOptions>(options =>
                {
                    options.Listen(IPAddress.Any, _port, configureEndpoint);
                });
            });
        }
        else
        {
            builder = builder.UseUrls(hostAddress);
        }

        var webHost = builder.Build();

        // This is what changed
        // webHost.Start();
        // return webHost.WaitForShutdownAsync(cancel);

        return webHost.RunAsync(cancel);
    }
}
