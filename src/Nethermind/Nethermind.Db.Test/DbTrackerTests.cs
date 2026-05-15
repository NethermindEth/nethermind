// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.FullPruning;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Modules;
using Nethermind.Logging;
using Nethermind.Monitoring;
using Nethermind.Monitoring.Config;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test;

public class DbTrackerTests
{
    // Names mutated across tests are reset here so test order does not cause flakiness.
    private static readonly string[] TouchedMetricKeys =
    {
        "TestDb", "GoodDb", "ThrowingDb", "SkippedDb", "TrackedDb", "PrunedState",
    };

    [TearDown]
    public void TearDown()
    {
        foreach (string key in TouchedMetricKeys)
        {
            ((IDictionary<string, long>)Metrics.DbReads).Remove(key);
            ((IDictionary<string, long>)Metrics.DbWrites).Remove(key);
            ((IDictionary<string, long>)Metrics.DbSize).Remove(key);
            ((IDictionary<string, long>)Metrics.DbMemtableSize).Remove(key);
            ((IDictionary<string, long>)Metrics.DbBlockCacheSize).Remove(key);
            ((IDictionary<string, long>)Metrics.DbIndexFilterSize).Remove(key);
        }
    }

    [Test]
    public void TestTrackOnlyCreatedDb()
    {
        using IContainer container = new ContainerBuilder()
            .AddSingleton<DbMonitoringModule.DbTracker>()
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<HyperClockCacheWrapper>()
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(NoopMonitoringService.Instance)
            .AddDecorator<IDbFactory, DbMonitoringModule.DbTracker.DbFactoryInterceptor>()
            .AddSingleton<IDbFactory, MemDbFactory>()
            .Build();

        IDbFactory dbFactory = container.Resolve<IDbFactory>();

        DbMonitoringModule.DbTracker tracker = container.Resolve<DbMonitoringModule.DbTracker>();
        tracker.GetAllDbMeta().Count().Should().Be(0);

        dbFactory.CreateDb(new DbSettings("TestDb", "TestDb"));

        tracker.GetAllDbMeta().Count().Should().Be(1);
        KeyValuePair<string, IDbMeta> firstEntry = tracker.GetAllDbMeta().First();
        firstEntry.Key.Should().Be("TestDb");
    }

    [Parallelizable(ParallelScope.None)]
    [TestCase(true)]
    [TestCase(false)]
    public void TestUpdateDbMetric(bool isProcessing)
    {
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        (IContainer container, Action updateAction, FakeDb fakeDb) = ConfigureMetricUpdater((builder) => builder.AddSingleton<IBlockProcessingQueue>(queue));
        using IContainer _ = container;

        // Reset
        Metrics.DbReads["TestDb"] = 0;

        if (isProcessing)
        {
            container.Resolve<IBlockProcessingQueue>(); // Only setup is something requested the block processing queue.
            queue.IsEmpty.Returns(false);
            queue.BlockAdded += Raise.EventWith<BlockEventArgs>(new BlockEventArgs(Build.A.Block.TestObject));
        }

        updateAction!();

        // Assert
        Assert.That(Metrics.DbReads["TestDb"], isProcessing ? Is.EqualTo(0) : Is.EqualTo(10));
    }

    [Test]
    public void TestSkipMetricsTracking()
    {
        using IContainer container = new ContainerBuilder()
            .AddSingleton<DbMonitoringModule.DbTracker>()
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<HyperClockCacheWrapper>()
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(NoopMonitoringService.Instance)
            .AddDecorator<IDbFactory, DbMonitoringModule.DbTracker.DbFactoryInterceptor>()
            .AddSingleton<IDbFactory, MemDbFactory>()
            .Build();

        IDbFactory dbFactory = container.Resolve<IDbFactory>();
        DbMonitoringModule.DbTracker tracker = container.Resolve<DbMonitoringModule.DbTracker>();

        DbSettings skipped = new("SkippedDb", "SkippedDb") { SkipMetricsTracking = true };
        DbSettings tracked = new("TrackedDb", "TrackedDb");

        dbFactory.CreateDb(skipped);
        dbFactory.CreateDb(tracked);

        List<KeyValuePair<string, IDbMeta>> entries = tracker.GetAllDbMeta().ToList();
        entries.Should().ContainSingle().Which.Key.Should().Be("TrackedDb");
    }

    [Parallelizable(ParallelScope.None)]
    [Test]
    public void ExceptionInGatherMetricDoesNotAbortOtherDbs()
    {
        IMonitoringService monitoringService = Substitute.For<IMonitoringService>();
        Action updateAction = null!;
        monitoringService
            .When(m => m.AddMetricsUpdateAction(Arg.Any<Action>()))
            .Do(c => updateAction = (Action)c[0]);

        IDbFactory fakeDbFactory = Substitute.For<IDbFactory>();

        using IContainer container = new ContainerBuilder()
            .AddSingleton<DbMonitoringModule.DbTracker>()
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<HyperClockCacheWrapper>()
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(monitoringService)
            .AddDecorator<IDbFactory, DbMonitoringModule.DbTracker.DbFactoryInterceptor>()
            .AddSingleton<IDbFactory>(fakeDbFactory)
            .Build();

        ThrowingDb throwingDb = new();
        FakeDb goodDb = new(new IDbMeta.DbMetric { TotalReads = 42 });
        fakeDbFactory.CreateDb(Arg.Is<DbSettings>(s => s.DbName == "ThrowingDb")).Returns(throwingDb);
        fakeDbFactory.CreateDb(Arg.Is<DbSettings>(s => s.DbName == "GoodDb")).Returns(goodDb);

        IDbFactory intercepted = container.Resolve<IDbFactory>();
        intercepted.CreateDb(new DbSettings("ThrowingDb", "ThrowingDb"));
        intercepted.CreateDb(new DbSettings("GoodDb", "GoodDb"));

        Metrics.DbReads["GoodDb"] = 0;

        updateAction!();

        Assert.That(Metrics.DbReads.ContainsKey("GoodDb"), Is.True);
        Assert.That(Metrics.DbReads["GoodDb"], Is.EqualTo(42));
    }

    [Parallelizable(ParallelScope.None)]
    [Test]
    public void FullPruningDbTrackedWrapper_SurvivesPruningCycle()
    {
        IMonitoringService monitoringService = Substitute.For<IMonitoringService>();
        Action updateAction = null!;
        monitoringService
            .When(m => m.AddMetricsUpdateAction(Arg.Any<Action>()))
            .Do(c => updateAction = (Action)c[0]);

        // Inner factory returns a new FakeDb per call with a distinct size, so we can tell which
        // inner DB the FullPruningDb wrapper is currently pointing at.
        IDbFactory innerFactory = Substitute.For<IDbFactory>();
        FakeDb innerDbV0 = new(new IDbMeta.DbMetric { Size = 100 });
        FakeDb innerDbV1 = new(new IDbMeta.DbMetric { Size = 200 });
        innerFactory.CreateDb(Arg.Any<DbSettings>()).Returns(innerDbV0, innerDbV1);

        // DbMetricIntervalSeconds = 0 disables the interval guard so we can update twice in a row.
        MetricsConfig metricsConfig = new() { DbMetricIntervalSeconds = 0 };

        using IContainer container = new ContainerBuilder()
            .AddSingleton<DbMonitoringModule.DbTracker>()
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<HyperClockCacheWrapper>()
            .AddSingleton<IMetricsConfig>(metricsConfig)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(monitoringService)
            .Build();

        DbMonitoringModule.DbTracker tracker = container.Resolve<DbMonitoringModule.DbTracker>();
        FullPruningDb pruningDb = new(new DbSettings("PrunedState", "PrunedState"), innerFactory);

        // Mirror WorldStateModule's behavior: register the outer wrapper, not the inner DBs.
        tracker.AddDb("PrunedState", pruningDb);

        updateAction!();
        Assert.That(Metrics.DbSize["PrunedState"], Is.EqualTo(100));

        // Trigger and commit a full pruning cycle; pruningDb._currentDb now points to innerDbV1.
        pruningDb.TryStartPruning(out IPruningContext context).Should().BeTrue();
        context.Commit();
        context.Dispose();

        updateAction!();

        // After pruning, the wrapper delegates GatherMetric() to the new inner DB. No stale entry.
        Assert.That(Metrics.DbSize["PrunedState"], Is.EqualTo(200));
        tracker.GetAllDbMeta().Should().ContainSingle().Which.Key.Should().Be("PrunedState");
    }

    [Parallelizable(ParallelScope.None)]
    [Test]
    public void DoesNotUpdateIfIntervalHasNotPassed()
    {
        (IContainer container, Action updateAction, FakeDb fakeDb) = ConfigureMetricUpdater();
        using IContainer _ = container;

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
            .AddModule(new DbMonitoringModule())
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<IMetricsConfig>(new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(monitoringService)
            .AddDecorator<IDbFactory, DbMonitoringModule.DbTracker.DbFactoryInterceptor>()
            .AddSingleton<IDbFactory>(fakeDbFactory);

        configurer?.Invoke(builder);

        IContainer container = builder
            .Build();

        IDbMeta.DbMetric metric = new()
        {
            TotalReads = 10
        };
        FakeDb fakeDb = new(metric);
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

        public override IDbMeta.DbMetric GatherMetric() => _metric;

        internal void SetMetric(IDbMeta.DbMetric metric) => _metric = metric;
    }

    private class ThrowingDb : TestMemDb, IDbMeta
    {
        public override IDbMeta.DbMetric GatherMetric() => throw new InvalidOperationException("Simulated GatherMetric failure");
    }
}
