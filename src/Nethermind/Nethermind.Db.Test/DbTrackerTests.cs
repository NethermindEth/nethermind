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

public class DbTrackerTests
{
    [Test]
    public void TestTrackOnlyCreatedDb()
    {
        using IContainer container = new ContainerBuilder()
            .AddSingleton<DbTracker>()
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(NoopMonitoringService.Instance)
            .AddDecorator<IDbFactory, DbTracker.DbFactoryInterceptor>()
            .AddSingleton<IDbFactory, MemDbFactory>()
            .Build();

        IDbFactory dbFactory = container.Resolve<IDbFactory>();

        DbTracker tracker = container.Resolve<DbTracker>();
        tracker.GetAllDbMeta().Count().Should().Be(0);

        dbFactory.CreateDb(new DbSettings("TestDb", "TestDb"));

        tracker.GetAllDbMeta().Count().Should().Be(1);
        var firstEntry = tracker.GetAllDbMeta().First();
        firstEntry.Key.Should().Be("TestDb");
    }
}
