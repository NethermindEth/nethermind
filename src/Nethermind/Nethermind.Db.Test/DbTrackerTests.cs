// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
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
    // Metric keys mutated across tests; cleared in TearDown so test order does not cause flakiness.
    private static readonly string[] TouchedMetricKeys =
    {
        "TestDb", "GoodDb", "ThrowingDb", "SkippedDb", "TrackedDb", "PrunedState",
    };

    private static readonly IDictionary<string, long>[] PerDbMetricMaps =
    {
        Metrics.DbReads, Metrics.DbWrites, Metrics.DbSize,
        Metrics.DbMemtableSize, Metrics.DbBlockCacheSize, Metrics.DbIndexFilterSize,
    };

    [TearDown]
    public void TearDown()
    {
        foreach (string key in TouchedMetricKeys)
            foreach (IDictionary<string, long> map in PerDbMetricMaps)
                map.Remove(key);
    }

    [Test]
    public void TestTrackOnlyCreatedDb()
    {
        (IContainer container, _) = BuildTrackerContainer(new MemDbFactory());
        using IContainer _disposable = container;

        IDbFactory dbFactory = container.Resolve<IDbFactory>();
        DbMonitoringModule.DbTracker tracker = container.Resolve<DbMonitoringModule.DbTracker>();
        Assert.That(tracker.GetAllDbMeta().Count(), Is.EqualTo(0));

        dbFactory.CreateDb(new DbSettings("TestDb", "TestDb"));

        Assert.That(tracker.GetAllDbMeta().Count(), Is.EqualTo(1));
        KeyValuePair<string, IDbMeta> firstEntry = tracker.GetAllDbMeta().First();
        Assert.That(firstEntry.Key, Is.EqualTo("TestDb"));
    }

    [Parallelizable(ParallelScope.None)]
    [TestCase(true)]
    [TestCase(false)]
    public void TestUpdateDbMetric(bool isProcessing)
    {
        IBlockProcessingQueue queue = Substitute.For<IBlockProcessingQueue>();
        (IContainer container, Action updateAction, FakeDb fakeDb) = ConfigureMetricUpdater((builder) => builder.AddSingleton<IBlockProcessingQueue>(queue));
        using IContainer _ = container;

        Metrics.DbReads["TestDb"] = 0;

        if (isProcessing)
        {
            container.Resolve<IBlockProcessingQueue>(); // Only setup is something requested the block processing queue.
            queue.IsEmpty.Returns(false);
            queue.BlockAdded += Raise.EventWith<BlockEventArgs>(new BlockEventArgs(Build.A.Block.TestObject));
        }

        updateAction!();

        Assert.That(Metrics.DbReads["TestDb"], isProcessing ? Is.EqualTo(0) : Is.EqualTo(10));
    }

    [Test]
    public void TestSkipMetricsTracking()
    {
        (IContainer container, _) = BuildTrackerContainer(new MemDbFactory());
        using IContainer _disposable = container;

        IDbFactory dbFactory = container.Resolve<IDbFactory>();
        DbMonitoringModule.DbTracker tracker = container.Resolve<DbMonitoringModule.DbTracker>();

        dbFactory.CreateDb(new DbSettings("SkippedDb", "SkippedDb") { SkipMetricsTracking = true });
        dbFactory.CreateDb(new DbSettings("TrackedDb", "TrackedDb"));

        KeyValuePair<string, IDbMeta>[] allDbMeta = [.. tracker.GetAllDbMeta()];
        Assert.That(allDbMeta, Has.Length.EqualTo(1));
        Assert.That(allDbMeta[0].Key, Is.EqualTo("TrackedDb"));
    }

    [Parallelizable(ParallelScope.None)]
    [Test]
    public void ExceptionInGatherMetricDoesNotAbortOtherDbs()
    {
        IDbFactory fakeDbFactory = Substitute.For<IDbFactory>();
        fakeDbFactory.CreateDb(Arg.Is<DbSettings>(s => s.DbName == "ThrowingDb")).Returns(new ThrowingDb());
        fakeDbFactory.CreateDb(Arg.Is<DbSettings>(s => s.DbName == "GoodDb")).Returns(new FakeDb(new IDbMeta.DbMetric { TotalReads = 42 }));

        (IContainer container, Action updateAction) = BuildTrackerContainer(fakeDbFactory);
        using IContainer _ = container;

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
        // Inner factory returns a new FakeDb per call with a distinct size, so we can tell which
        // inner DB the FullPruningDb wrapper is currently pointing at.
        IDbFactory innerFactory = Substitute.For<IDbFactory>();
        innerFactory.CreateDb(Arg.Any<DbSettings>()).Returns(
            new FakeDb(new IDbMeta.DbMetric { Size = 100 }),
            new FakeDb(new IDbMeta.DbMetric { Size = 200 }));

        // DbMetricIntervalSeconds = 0 disables the interval guard so we can update twice in a row.
        (IContainer container, Action updateAction) = BuildTrackerContainer(
            innerFactory,
            new MetricsConfig { DbMetricIntervalSeconds = 0 },
            withInterceptor: false);
        using IContainer _ = container;

        DbMonitoringModule.DbTracker tracker = container.Resolve<DbMonitoringModule.DbTracker>();
        FullPruningDb pruningDb = new(new DbSettings("PrunedState", "PrunedState"), innerFactory);

        // Mirror WorldStateModule's behavior: register the outer wrapper, not the inner DBs.
        tracker.AddDb("PrunedState", pruningDb);

        updateAction!();
        Assert.That(Metrics.DbSize["PrunedState"], Is.EqualTo(100));

        // Trigger and commit a full pruning cycle; pruningDb._currentDb now points to the second inner DB.
        Assert.That(pruningDb.TryStartPruning(out IPruningContext context), Is.True);
        context.Commit();
        context.Dispose();

        updateAction!();

        // After pruning, the wrapper delegates GatherMetric() to the new inner DB. No stale entry.
        Assert.That(Metrics.DbSize["PrunedState"], Is.EqualTo(200));
        KeyValuePair<string, IDbMeta>[] allDbMeta = [.. tracker.GetAllDbMeta()];
        Assert.That(allDbMeta, Has.Length.EqualTo(1));
        Assert.That(allDbMeta[0].Key, Is.EqualTo("PrunedState"));
    }

    [Parallelizable(ParallelScope.None)]
    [Test]
    public void DoesNotUpdateIfIntervalHasNotPassed()
    {
        (IContainer container, Action updateAction, FakeDb fakeDb) = ConfigureMetricUpdater();
        using IContainer _ = container;

        container.Resolve<IDbFactory>().CreateDb(new DbSettings("TestDb", "TestDb"));

        Metrics.DbReads["TestDb"] = 0;

        updateAction!();
        Assert.That(Metrics.DbReads["TestDb"], Is.EqualTo(10));

        fakeDb.SetMetric(new IDbMeta.DbMetric { TotalReads = 11 });

        updateAction!();
        Assert.That(Metrics.DbReads["TestDb"], Is.EqualTo(10));
    }

    [Parallelizable(ParallelScope.None)]
    [Test]
    public void DoesNotThrowOrRepeatErrorAfterContainerDisposed()
    {
        TestLogger testLogger = new() { IsDebug = false };
        (IContainer container, Action updateAction, FakeDb _) = ConfigureMetricUpdater(builder =>
            builder.AddSingleton<ILogManager>(new OneLoggerLogManager(new(testLogger))));

        container.Dispose();

        Action invoke = () => updateAction!();
        Assert.That(invoke, Throws.Nothing);
        Assert.That(invoke, Throws.Nothing);

        Assert.That(testLogger.LogList, Is.Empty);
    }

    /// <summary>
    /// Builds a container wired with <see cref="DbMonitoringModule.DbTracker"/> and a captured
    /// metrics-update action. By default the <c>DbFactoryInterceptor</c> decorator is registered
    /// so DB creations through the resolved <see cref="IDbFactory"/> flow through the tracker;
    /// pass <c>withInterceptor: false</c> to skip the decorator (used by tests that register DBs
    /// directly with the tracker).
    /// </summary>
    private static (IContainer container, Action updateAction) BuildTrackerContainer(
        IDbFactory factory,
        IMetricsConfig? metricsConfig = null,
        bool withInterceptor = true)
    {
        IMonitoringService monitoringService = Substitute.For<IMonitoringService>();
        Action updateAction = null!;
        monitoringService
            .When(m => m.AddMetricsUpdateAction(Arg.Any<Action>()))
            .Do(c => updateAction = (Action)c[0]);

        ContainerBuilder builder = new ContainerBuilder()
            .AddSingleton<DbMonitoringModule.DbTracker>()
            .AddSingleton<IDbConfig>(new DbConfig())
            .AddSingleton<HyperClockCacheWrapper>()
            .AddSingleton<IMetricsConfig>(metricsConfig ?? new MetricsConfig())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IMonitoringService>(monitoringService);

        if (withInterceptor)
            builder.AddDecorator<IDbFactory, DbMonitoringModule.DbTracker.DbFactoryInterceptor>();

        builder.AddSingleton<IDbFactory>(factory);

        IContainer container = builder.Build();
        // Force DbTracker construction so the monitoring service receives AddMetricsUpdateAction
        // and our captured updateAction is non-null on return.
        container.Resolve<DbMonitoringModule.DbTracker>();
        return (container, updateAction);
    }

    private (IContainer, Action, FakeDb) ConfigureMetricUpdater(Action<ContainerBuilder>? configurer = null)
    {
        IDbFactory fakeDbFactory = Substitute.For<IDbFactory>();
        IDbMeta.DbMetric metric = new() { TotalReads = 10 };
        FakeDb fakeDb = new(metric);
        fakeDbFactory.CreateDb(Arg.Any<DbSettings>()).Returns(fakeDb);

        IMonitoringService monitoringService = Substitute.For<IMonitoringService>();
        Action updateAction = null!;
        monitoringService
            .When(m => m.AddMetricsUpdateAction(Arg.Any<Action>()))
            .Do(c => updateAction = (Action)c[0]);

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

        IContainer container = builder.Build();
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
