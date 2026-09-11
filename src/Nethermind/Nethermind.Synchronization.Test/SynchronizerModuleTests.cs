// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.DbTuner;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class SynchronizerModuleTests
{
    [Test]
    public void SyncPeerPool_should_use_INetworkConfig_MaxActivePeers()
    {
        NetworkConfig networkConfig = new() { MaxActivePeers = 75 };

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(networkConfig))
            .AddModule(new SynchronizerModule(new TestSyncConfig()))
            .AddSingleton(Substitute.For<IWorldStateManager>())
            .Build();

        SyncPeerPool pool = container.Resolve<SyncPeerPool>();

        Assert.That(pool.PeerMaxCount, Is.EqualTo(75));
    }

    [Test]
    public void Block_access_lists_feed_should_be_active_when_fast_bodies_are_disabled()
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            DownloadHeadersInFastSync = true,
            DownloadBodiesInFastSync = false,
            DownloadBlockAccessListsInFastSync = true
        };

        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(syncConfig)))
            .AddModule(new SynchronizerModule(syncConfig))
            .AddSingleton(Substitute.For<IStateSyncRunner>())
            .AddSingleton(Substitute.For<IWorldStateManager>())
            .Build();

        ISyncFeed<BlockAccessListsSyncBatch> feed = container.Resolve<ISyncFeed<BlockAccessListsSyncBatch>>();

        Assert.That(feed, Is.TypeOf<BlockAccessListsSyncFeed>());
    }

    [Test]
    public async Task Synchronizer_dispose_is_idempotent()
    {
        ISyncFeed<BlocksRequest> fullSyncFeed = Substitute.For<ISyncFeed<BlocksRequest>>();
        await using IContainer container = BuildSyncContainer(fullSyncFeed: fullSyncFeed);

        ISynchronizer synchronizer = container.Resolve<ISynchronizer>();

        await synchronizer.DisposeAsync();
        await synchronizer.DisposeAsync();

        // Container teardown can dispose this more than once, and a repeat run would wait on
        // the feed tasks again - with a stuck feed it would pay the full termination timeout twice.
        _ = fullSyncFeed.Received(1).FeedTask;
    }

    [Test, CancelAfter(30_000)]
    public async Task Synchronizer_dispose_waits_for_state_sync_runner(CancellationToken cancellationToken)
    {
        // Start launches the state sync runner fire-and-forget; container teardown disposes the
        // databases right after DisposeAsync returns, so DisposeAsync must join that task the same way
        // it joins the feed tasks. The runner is gated to model in-flight snap/state sync work.
        TaskCompletionSource runnerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken runnerToken = default;
        IStateSyncRunner stateSyncRunner = Substitute.For<IStateSyncRunner>();
        stateSyncRunner.Run(Arg.Any<CancellationToken>()).Returns(ci =>
        {
            runnerToken = ci.Arg<CancellationToken>();
            return runnerGate.Task;
        });

        await using IContainer container = BuildSyncContainer(stateSyncRunner);

        // Resolved rather than constructed: the join only runs on a real node because Autofac owns the concrete
        // Synchronizer and disposes it in reverse activation order, ahead of the databases.
        ISynchronizer synchronizer = container.Resolve<ISynchronizer>();

        synchronizer.Start();
        _ = stateSyncRunner.Received(1).Run(Arg.Any<CancellationToken>());

        Task disposeTask = synchronizer.DisposeAsync().AsTask();

        // Production invariant: container teardown disposes the databases the moment DisposeAsync returns, so the
        // runner must have finished by then. Sampling in a synchronous continuation states that as an ordering fact
        // rather than as a deadline the test runner has to beat.
        Task<bool> runnerDoneWhenDisposeReturned = disposeTask.ContinueWith(
            _ => runnerGate.Task.IsCompleted,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            // DisposeAsync cancels the sync token before it joins anything, so once the runner has seen the
            // cancellation, everything left is the join itself.
            await WaitForCancellation(runnerToken, cancellationToken);
            Assert.That(disposeTask.IsCompleted, Is.False, "DisposeAsync must wait for the state sync runner");
        }
        finally
        {
            // Unconditional: a failed assertion above must not leave DisposeAsync blocked on a gate nobody will open.
            runnerGate.TrySetResult();
        }

        await disposeTask.WaitAsync(cancellationToken);
        Assert.That(await runnerDoneWhenDisposeReturned, Is.True);
    }

    [Test, CancelAfter(30_000)]
    public async Task Synchronizer_dispose_gives_up_on_a_state_sync_runner_that_outlives_its_budget(CancellationToken cancellationToken)
    {
        // The join is bounded on purpose: the runner sets ProcessTerminationTimeout to infinite, so a wait with no
        // ceiling here is a node that hangs on SIGTERM instead of one that crashes. What the operator gets instead is
        // a line naming the consequence, because the databases are disposed the moment DisposeAsync returns.
        TaskCompletionSource runnerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IStateSyncRunner stateSyncRunner = Substitute.For<IStateSyncRunner>();
        stateSyncRunner.Run(Arg.Any<CancellationToken>()).Returns(runnerGate.Task);

        TestLogger logger = new() { IsInfo = false, IsDebug = false, IsTrace = false };
        const int budgetMs = 50;

        await using IContainer container = BuildSyncContainer(stateSyncRunner, logManager: new OneLoggerLogManager(new(logger)));

        // The budget is an internal test knob with no DI surface, so this one Synchronizer is constructed rather than
        // resolved - out of the container's own components, so the graph under test is still the production one.
        await using Synchronizer synchronizer = ResolveSynchronizer(container, budgetMs);

        synchronizer.Start();

        try
        {
            // The runner never completes, so this returning at all is the assertion: DisposeAsync gave up.
            await synchronizer.DisposeAsync().AsTask().WaitAsync(cancellationToken);

            Assert.That(
                logger.LogList.Where(static l => l.Contains($"State sync did not stop within {budgetMs}ms")),
                Is.Not.Empty,
                $"WARN/ERROR lines: {string.Join(" | ", logger.LogList)}");
        }
        finally
        {
            // Unconditional: nothing else will ever release a runner substituted as a gate.
            runnerGate.TrySetResult();
        }
    }

    /// <summary>
    /// A production container: SynchronizerModule wires the feeds, their dispatchers and the lifetime scopes that own
    /// them, and the join added for #13154 relies on Autofac owning the concrete <see cref="Synchronizer"/>. Only the
    /// collaborators a test drives are substituted, and a feed override is applied inside the keyed scope that owns it
    /// rather than on the outer container, which cannot reach in.
    /// </summary>
    private static IContainer BuildSyncContainer(
        IStateSyncRunner? stateSyncRunner = null,
        ISyncFeed<BlocksRequest>? fullSyncFeed = null,
        ILogManager? logManager = null)
    {
        SyncConfig syncConfig = new() { FastSync = true };

        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new ConfigProvider(syncConfig)))
            .AddModule(new SynchronizerModule(syncConfig))
            .AddSingleton(stateSyncRunner ?? Substitute.For<IStateSyncRunner>())
            .AddSingleton(Substitute.For<IWorldStateManager>());

        if (fullSyncFeed is not null)
        {
            builder.RegisterNamedComponentInItsOwnLifetime<SyncFeedComponent<BlocksRequest>>(
                nameof(FullSyncFeed), cfg => cfg.AddSingleton(fullSyncFeed));
        }

        if (logManager is not null) builder.AddSingleton(logManager);

        return builder.Build();
    }

    /// <summary>
    /// <see cref="Synchronizer"/> out of the container's own components, for the one test that has to set
    /// <see cref="Synchronizer.StateSyncTerminationTimeout"/> - an <c>init</c> member with no registration to override.
    /// </summary>
    private static Synchronizer ResolveSynchronizer(IContainer container, int stateSyncTerminationTimeout) =>
        new(container.Resolve<ISyncModeSelector>(),
            container.Resolve<ISyncReport>(),
            container.Resolve<ISyncConfig>(),
            container.Resolve<IBlockTree>(),
            container.Resolve<ISyncPivotResolver>(),
            container.Resolve<ILogManager>(),
            container.Resolve<INodeStatsManager>(),
            container.ResolveNamed<SyncFeedComponent<BlocksRequest>>(nameof(FullSyncFeed)),
            container.ResolveNamed<SyncFeedComponent<BlocksRequest>>(nameof(FastSyncFeed)),
            container.Resolve<IStateSyncRunner>(),
            container.ResolveNamed<SyncFeedComponent<HeadersSyncBatch>>(nameof(HeadersSyncFeed)),
            container.Resolve<SyncFeedComponent<BodiesSyncBatch>>(),
            container.Resolve<SyncFeedComponent<ReceiptsSyncBatch>>(),
            container.Resolve<SyncFeedComponent<BlockAccessListsSyncBatch>>(),
            container.Resolve<SyncDbTuner>(),
            container.Resolve<MallocTrimmer>(),
            container.Resolve<IProcessExitSource>())
        {
            StateSyncTerminationTimeout = stateSyncTerminationTimeout,
        };

    private static async Task WaitForCancellation(CancellationToken watched, CancellationToken cancellationToken)
    {
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (watched.Register(() => cancelled.TrySetResult()))
        {
            await cancelled.Task.WaitAsync(cancellationToken);
        }
    }
}
