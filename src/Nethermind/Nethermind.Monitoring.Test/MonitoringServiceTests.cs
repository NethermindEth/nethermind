// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Monitoring.Metrics;
using NUnit.Framework;

namespace Nethermind.Monitoring.Test;

public class MonitoringServiceTests
{
    [Test]
    public async Task Disposing_container_stops_metrics_server()
    {
        // The probed port is released before the server binds it and bind failures are not surfaced,
        // so retry on a fresh port if another process takes it in between.
        int port = 0;
        IContainer container = null;
        for (int attempt = 0; attempt < 3 && container is null; attempt++)
        {
            port = GetFreePort();
            IContainer candidate = BuildMonitoringContainer(port);
            await candidate.Resolve<IMonitoringService>().StartAsync();
            if (await ServesMetrics(port))
                container = candidate;
            else
                await candidate.DisposeAsync();
        }

        Assert.That(container, Is.Not.Null, "metrics server did not start");
        await container.DisposeAsync();

        using TcpListener reboundListener = new(IPAddress.Loopback, port);
        Assert.DoesNotThrow(() => reboundListener.Start());
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
        return new ContainerBuilder()
            .AddModule(new MonitoringModule(config))
            .AddSingleton<IMetricsConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMetricsController, MetricsController>()
            .Build();
    }

    private static async Task<bool> ServesMetrics(int port)
    {
        using HttpClient client = new();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (HttpRequestException)
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
