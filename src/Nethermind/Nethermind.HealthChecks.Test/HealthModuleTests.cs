// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HealthChecks.UI.Core;
using HealthChecks.UI.Core.HostedService;
using HealthChecks.UI.Core.Notifications;
using HealthChecks.UI.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Network;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.HealthChecks.Test
{
    public class HealthModuleTests
    {
        [Test]
        public async Task Ui_collects_health_without_http([Values] bool healthy)
        {
            INodeHealthService nodeHealthService = Substitute.For<INodeHealthService>();
            nodeHealthService.CheckHealth().Returns(CreateResult(healthy, "Current status"));
            using ServiceProvider provider = CreateUiServices(nodeHealthService);
            using IServiceScope scope = provider.CreateScope();
            HealthChecksDb db = await InitializeUi(scope);

            await scope.ServiceProvider.GetRequiredService<IHealthCheckReportCollector>().Collect(CancellationToken.None);

            HealthCheckExecution execution = await db.Executions.Include(item => item.Entries).SingleAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(execution.Name, Is.EqualTo("health"));
                Assert.That(execution.Status, Is.EqualTo(healthy ? UIHealthStatus.Healthy : UIHealthStatus.Unhealthy));
                Assert.That(execution.Entries.Single().Description, Is.EqualTo("Current status."));
                Assert.That(execution.LastExecuted, Is.GreaterThan(DateTime.MinValue));
            }
            nodeHealthService.Received(1).CheckHealth();
        }

        [Test]
        public async Task Ui_updates_history_and_notifies_on_failure_and_recovery()
        {
            INodeHealthService nodeHealthService = Substitute.For<INodeHealthService>();
            IHealthCheckFailureNotifier notifier = Substitute.For<IHealthCheckFailureNotifier>();
            using ServiceProvider provider = CreateUiServices(nodeHealthService, notifier);

            using (IServiceScope scope = provider.CreateScope())
            {
                await InitializeUi(scope);
            }

            foreach (bool healthy in new[] { true, false, false, true })
            {
                nodeHealthService.CheckHealth().Returns(CreateResult(healthy, healthy ? "Recovered" : "Failed"));
                using IServiceScope scope = provider.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IHealthCheckReportCollector>().Collect(CancellationToken.None);
            }

            using IServiceScope finalScope = provider.CreateScope();
            HealthChecksDb db = finalScope.ServiceProvider.GetRequiredService<HealthChecksDb>();
            HealthCheckExecution execution = await db.Executions.Include(item => item.Entries).Include(item => item.History).SingleAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(execution.Status, Is.EqualTo(UIHealthStatus.Healthy));
                Assert.That(execution.Entries.Single().Description, Is.EqualTo("Recovered."));
                Assert.That(execution.History.Select(item => item.Status), Is.EqualTo(new[] { UIHealthStatus.Unhealthy, UIHealthStatus.Healthy }));
            }
            await notifier.Received(2).NotifyDown("health", Arg.Is<UIHealthReport>(report => report.Status == UIHealthStatus.Unhealthy));
            await notifier.Received(1).NotifyWakeUp("health");
        }

        [Test]
        public async Task Ui_passes_report_data_to_notifier_without_serializing()
        {
            IEnumerable<string> errors = new[] { "error" }.Select<string, string>(_ => throw new InvalidOperationException("Unexpected JSON serialization"));
            CheckHealthResult result = CreateResult(false, "Failed");
            result.Errors = errors;
            INodeHealthService nodeHealthService = Substitute.For<INodeHealthService>();
            nodeHealthService.CheckHealth().Returns(result);
            IHealthCheckFailureNotifier notifier = Substitute.For<IHealthCheckFailureNotifier>();
            using ServiceProvider provider = CreateUiServices(nodeHealthService, notifier);
            using IServiceScope scope = provider.CreateScope();
            await InitializeUi(scope);

            await scope.ServiceProvider.GetRequiredService<IHealthCheckReportCollector>().Collect(CancellationToken.None);

            await notifier.Received(1).NotifyDown("health", Arg.Is<UIHealthReport>(report =>
                ReferenceEquals(report.Entries["node-health"].Data[nameof(CheckHealthResult.Errors)], errors)));
        }

        [Test]
        public async Task Ui_stores_health_check_exceptions()
        {
            INodeHealthService nodeHealthService = Substitute.For<INodeHealthService>();
            nodeHealthService.CheckHealth().Returns(_ => throw new InvalidOperationException("Health failure"));
            using ServiceProvider provider = CreateUiServices(nodeHealthService);
            using IServiceScope scope = provider.CreateScope();
            HealthChecksDb db = await InitializeUi(scope);

            await scope.ServiceProvider.GetRequiredService<IHealthCheckReportCollector>().Collect(CancellationToken.None);

            HealthCheckExecution execution = await db.Executions.Include(item => item.Entries).SingleAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(execution.Status, Is.EqualTo(UIHealthStatus.Unhealthy));
                Assert.That(execution.Entries.Single().Description, Is.EqualTo("Health failure"));
            }
        }

        [Test]
        public async Task Ui_uses_http_collector_for_additional_endpoints()
        {
            INodeHealthService nodeHealthService = Substitute.For<INodeHealthService>();
            IHealthCheckReportCollector httpCollector = Substitute.For<IHealthCheckReportCollector>();
            using ServiceProvider provider = CreateUiServices(nodeHealthService, httpCollector: httpCollector);
            using IServiceScope scope = provider.CreateScope();
            HealthChecksDb db = await InitializeUi(scope);
            db.Configurations.Add(new HealthCheckConfiguration { Name = "remote", Uri = "http://localhost:2/health" });
            await db.SaveChangesAsync();

            await scope.ServiceProvider.GetRequiredService<IHealthCheckReportCollector>().Collect(CancellationToken.None);

            nodeHealthService.DidNotReceive().CheckHealth();
            await httpCollector.Received(1).Collect(CancellationToken.None);
        }

        private static CheckHealthResult CreateResult(bool healthy, string description) => new()
        {
            Healthy = healthy,
            Errors = [],
            Messages = [(description, description)]
        };

        private static ServiceProvider CreateUiServices(INodeHealthService nodeHealthService, IHealthCheckFailureNotifier notifier = null,
            IHealthCheckReportCollector httpCollector = null)
        {
            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton(Substitute.For<IServer>());
            services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
            new HealthCheckJsonRpcConfigurer(
                nodeHealthService,
                new HealthChecksConfig { Enabled = true, UIEnabled = true },
                Substitute.For<IIPResolver>(),
                new MetricsConfig(),
                new JsonRpcConfig { Host = "localhost", Port = 1 },
                LimboLogs.Instance).Configure(services);
            services.AddSingleton(notifier ?? Substitute.For<IHealthCheckFailureNotifier>());
            services.AddSingleton(new DbContextOptionsBuilder<HealthChecksDb>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            if (httpCollector is not null)
            {
                object key = services.Single(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(IHealthCheckReportCollector)).ServiceKey;
                services.AddKeyedSingleton(key, httpCollector);
            }
            services.AddHttpClient("health-checks").ConfigurePrimaryHttpMessageHandler(() => new RejectHttpHandler());
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        private static async Task<HealthChecksDb> InitializeUi(IServiceScope scope)
        {
            HealthChecksDb db = scope.ServiceProvider.GetRequiredService<HealthChecksDb>();
            await db.Database.EnsureCreatedAsync();
            db.Configurations.Add(new HealthCheckConfiguration { Name = "health", Uri = "http://localhost:1/health" });
            await db.SaveChangesAsync();
            return db;
        }

        private sealed class RejectHttpHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Unexpected HTTP health poll");
        }

        [Test]
        public void NodeStatus_returns_expected_results()
        {
            INodeHealthService nodeHealthService = Substitute.For<INodeHealthService>();
            nodeHealthService.CheckHealth().Returns(new CheckHealthResult()
            {
                Healthy = true,
                Errors = new List<string>(),
                IsSyncing = true,
                Messages = new List<(string, string)>()
                {
                    {("Still syncing", "Syncing in progress")}
                }
            });
            IHealthRpcModule healthRpcModule = new HealthRpcModule(nodeHealthService);
            ResultWrapper<NodeStatusResult> nodeStatus = healthRpcModule.health_nodeStatus();
            Assert.That(nodeStatus.Data.Healthy, Is.EqualTo(true));
            Assert.That(nodeStatus.Data.Messages.First(), Is.EqualTo("Still syncing"));
            Assert.That(nodeStatus.Data.IsSyncing, Is.EqualTo(true));
            Assert.That(nodeStatus.Data.Errors, Is.Empty);
        }
    }
}
