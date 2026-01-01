// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using NSubstitute;
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

    [Parallelizable(ParallelScope.None)]
    [TestCase(true)]
    [TestCase(false)]
    public void TestUpdateDbMetric(bool isProcessing)
    {
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        (IContainer container, Action updateAction, FakeDb fakeDb) = ConfigureMetricUpdater((builder) => builder.AddSingleton<IBlockProcessingQueue>(queue));
        using var _ = container;

        // Reset
        Metrics.DbReads["TestDb"] = 0;

        if (isProcessing)
        {
            container.Resolve<IBlockProcessingQueue>(); // Only setup is something requested the block processing queue.
            queue.IsEmpty.Returns(false);
            queue.BlockAdded += Raise.EventWith<BlockAddedEventArgs>(new BlockAddedEventArgs(Keccak.Zero));
        }

        updateAction!();

        // Assert
        Assert.That(Metrics.DbReads["TestDb"], isProcessing ? Is.EqualTo(0) : Is.EqualTo(10));
    }

    [Parallelizable(ParallelScope.None)]
    [Test]
    public void DoesNotUpdateIfIntervalHasNotPassed()
    {
        (IContainer container, Action updateAction, FakeDb fakeDb) = ConfigureMetricUpdater();
        using var _ = container;

        container.Resolve<IDbFactory>().CreateDb(new DbSettings("TestDb", "TestDb"));

        // Reset
        Metrics.DbReads["TestDb"] = 0;

        updateAction!();
        Assert.That(Metrics.DbReads["TestDb"], Is.EqualTo(10));

        fakeDb.SetMetric(new IDbMeta.DbMetric()
        {
            TotalReads = 11
        });

        updateAction!();
        Assert.That(Metrics.DbReads["TestDb"], Is.EqualTo(10));
    }

    private (IContainer, Action, FakeDb) ConfigureMetricUpdater(Action<ContainerBuilder>? configurer = null)
    {
        IDbFactory fakeDbFactory = Substitute.For<IDbFactory>();

        IMonitoringService monitoringService = Substitute.For<IMonitoringService>();
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new DbModule(new InitConfig(), new ReceiptConfig(), new SyncConfig()))
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(monitoringService)
            .AddDecorator<IDbFactory, DbTracker.DbFactoryInterceptor>()
            .AddSingleton<IDbFactory>(fakeDbFactory);

        configurer?.Invoke(builder);

        IContainer container = builder
            .Build();

        IDbMeta.DbMetric metric = new IDbMeta.DbMetric()
        {
            TotalReads = 10
        };
        FakeDb fakeDb = new FakeDb(metric);
        fakeDbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(fakeDb);

        Action updateAction = null;
        monitoringService
            .When((m) => m.AddMetricsUpdateAction(Arg.Any<Action>()))
            .Do((c) =>
            {
                updateAction = (Action)c[0];
            });

        container.Resolve<IDbFactory>().CreateDb(new DbSettings("TestDb", "TestDb"));

        return (container, updateAction, fakeDb);
    }

    private class FakeDb(IDbMeta.DbMetric metric) : TestMemDb, IDbMeta
    {
        private IDbMeta.DbMetric _metric = metric;

        public override IDbMeta.DbMetric GatherMetric(bool includeSharedCache = false)
        {
            return _metric;
        }

        internal void SetMetric(IDbMeta.DbMetric metric)
        {
            _metric = metric;
        }
    }
}
