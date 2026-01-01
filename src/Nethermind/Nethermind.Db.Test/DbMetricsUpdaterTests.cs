// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using NUnit.Framework;

namespace Nethermind.Db.Test;

public class DbMetricsUpdaterTests
{
    [Test]
    public void TestTrackOnlyCreatedDb()
    {
        using IContainer container = new ContainerBuilder()
            .AddSingleton<DbMetricsUpdater>()
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(NoopMonitoringService.Instance)
            .AddDecorator<IDbFactory, DbMetricsUpdater.DbFactoryInterceptor>()
            .AddSingleton<IDbFactory, MemDbFactory>()
            .Build();

        IDbFactory dbFactory = container.Resolve<IDbFactory>();

        DbMetricsUpdater metricsUpdater = container.Resolve<DbMetricsUpdater>();
        metricsUpdater.GetAllDbMeta().Count().Should().Be(0);

        dbFactory.CreateDb(new DbSettings("TestDb", "TestDb"));

        metricsUpdater.GetAllDbMeta().Count().Should().Be(1);
        var firstEntry = metricsUpdater.GetAllDbMeta().First();
        firstEntry.Key.Should().Be("TestDb");
    }
}
