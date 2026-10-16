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
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        MetricsConfig config = new()
        {
            Enabled = true,
            ExposeHost = "127.0.0.1",
            ExposePort = port,
            InitializeStaticLabels = false
        };
        await using IContainer container = new ContainerBuilder()
            .AddModule(new MonitoringModule(config))
            .AddSingleton<IMetricsConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMetricsController, MetricsController>()
            .Build();

        await container.Resolve<IMonitoringService>().StartAsync();
        using (HttpClient client = new())
        {
            await Assert.ThatAsync(async () =>
            {
                try
                {
                    using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/metrics");
                    return response.IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            }, Is.True.After(10_000, 50));
        }

        await container.DisposeAsync();

        using TcpListener reboundListener = new(IPAddress.Loopback, port);
        Assert.DoesNotThrow(() => reboundListener.Start());
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
