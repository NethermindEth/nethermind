// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api.Monitoring;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Monitoring.Metrics;
using Nethermind.Network;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test;

public class MonitoringServiceTests
{
    [Test]
    public async Task Disposing_container_stops_metrics_server()
    {
        // The probed port is released before the server binds it, so retry on a fresh port if another process takes it in between.
        int port = 0;
        IContainer container = null;
        for (int attempt = 0; attempt < 3 && container is null; attempt++)
        {
            port = GetFreePort();
            IContainer candidate = BuildMonitoringContainer(port);
            try
            {
                await candidate.Resolve<IMonitoringService>().StartAsync();
                container = candidate;
            }
            catch (IOException)
            {
                await candidate.DisposeAsync();
            }
        }

        Assert.That(container, Is.Not.Null, "metrics server did not start");
        Assert.That(await ServesMetrics(port), Is.True);
        await container.DisposeAsync();

        using TcpListener reboundListener = new(IPAddress.Loopback, port);
        Assert.DoesNotThrow(() => reboundListener.Start());
    }

    [Test]
    public async Task Taken_metrics_port_fails_startup_and_disposal_does_not_throw()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        IContainer container = BuildMonitoringContainer(((IPEndPoint)listener.LocalEndpoint).Port);
        StartMonitoring step = new(container.Resolve<IMonitoringService>(), LimboLogs.Instance, container.Resolve<IMetricsConfig>());

        await Assert.ThatAsync(() => step.Execute(CancellationToken.None), Throws.InstanceOf<PortInUseException>());
        await Assert.ThatAsync(async () => await container.DisposeAsync(), Throws.Nothing);
    }

    [Test]
    public async Task Counters_only_monitoring_runs_update_actions_without_exposing_or_pushing_metrics()
    {
        using TcpListener pushGateway = new(IPAddress.Loopback, 0);
        pushGateway.Start();
        using TcpListener exposeListener = new(IPAddress.Loopback, 0);
        exposeListener.Start();
        MetricsConfig config = new()
        {
            Enabled = false,
            CountersEnabled = true,
            IntervalSeconds = 1,
            ExposeHost = "127.0.0.1",
            ExposePort = ((IPEndPoint)exposeListener.LocalEndpoint).Port,
            PushGatewayUrl = $"http://127.0.0.1:{((IPEndPoint)pushGateway.LocalEndpoint).Port}/metrics",
            InitializeStaticLabels = false
        };
        await using IContainer container = BuildMonitoringContainer(config);
        IMonitoringService monitoringService = container.Resolve<IMonitoringService>();
        TaskCompletionSource updated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        monitoringService.AddMetricsUpdateAction(() => updated.TrySetResult());
        StartMonitoring step = new(monitoringService, LimboLogs.Instance, config);

        await Assert.ThatAsync(() => step.Execute(CancellationToken.None), Throws.Nothing,
            "metrics server tried to bind the expose port");

        Assert.That(await Task.WhenAny(updated.Task, Task.Delay(TimeSpan.FromSeconds(5))), Is.SameAs(updated.Task),
            "metrics update actions never ran");
        bool pushed = pushGateway.Pending();
        pushGateway.Stop();
        Assert.That(pushed, Is.False, "metrics were pushed to the Pushgateway");
    }

    private static int GetFreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static IContainer BuildMonitoringContainer(int port)
    {
        MetricsConfig config = new()
        {
            Enabled = true,
            ExposeHost = "127.0.0.1",
            ExposePort = port,
            InitializeStaticLabels = false
        };
        return BuildMonitoringContainer(config);
    }

    private static IContainer BuildMonitoringContainer(MetricsConfig config) =>
        new ContainerBuilder()
            .AddModule(new MonitoringModule())
            .AddSingleton<IMetricsConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMetricsController, MetricsController>()
            .Build();

    private static async Task<bool> ServesMetrics(int port)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(1) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(50);
        }

        return false;
    }

    [TestCase("user", "pass")]
    [TestCase("user", "p@ss:word")]
    public void Basic_auth_header_encodes_credentials(string username, string password)
    {
        AuthenticationHeaderValue header = MonitoringService.CreateBasicAuthHeader(username, password);

        Assert.That(header.Scheme, Is.EqualTo("Basic"));
        Assert.That(header.Parameter, Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))));
    }
}
