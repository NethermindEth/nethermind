// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Synchronization.Blocks;
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
        // The class under test is constructed directly: the assert needs a substituted feed, and the
        // module registers the feed components in their own keyed lifetime scopes, which an outer
        // override cannot reach.
        ISyncFeed<BlocksRequest> fullSyncFeed = Substitute.For<ISyncFeed<BlocksRequest>>();
        static SyncFeedComponent<T> FeedOnly<T>(ISyncFeed<T> feed) => new(feed, null!, null!, null!, null!);
        Synchronizer synchronizer = BuildSynchronizer(
            Substitute.For<ISyncConfig>(),
            Substitute.For<IBlockTree>(),
            Substitute.For<IStateSyncRunner>(),
            FeedOnly(fullSyncFeed),
            FeedOnly(Substitute.For<ISyncFeed<BlocksRequest>>()),
            FeedOnly(Substitute.For<ISyncFeed<HeadersSyncBatch>>()),
            FeedOnly(Substitute.For<ISyncFeed<BodiesSyncBatch>>()),
            FeedOnly(Substitute.For<ISyncFeed<ReceiptsSyncBatch>>()),
            FeedOnly(Substitute.For<ISyncFeed<BlockAccessListsSyncBatch>>()));

        await synchronizer.DisposeAsync();
        await synchronizer.DisposeAsync();

        // Container teardown disposes twice (dispose tracking); the second run must not wait on
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
        IStateSyncRunner stateSyncRunner = Substitute.For<IStateSyncRunner>();
        stateSyncRunner.Run(Arg.Any<CancellationToken>()).Returns(runnerGate.Task);

        TestSyncConfig syncConfig = new() { FastSync = true };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.CanAcceptNewBlocks.Returns(true);
        ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
        BlockDownloader blockDownloader = new(
            blockTree,
            Substitute.For<IBlockValidator>(),
            Substitute.For<ISyncReport>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBetterPeerStrategy>(),
            Substitute.For<IFullStateFinder>(),
            Substitute.For<IForwardHeaderProvider>(),
            peerPool,
            Substitute.For<IReceiptsRecovery>(),
            Substitute.For<IBlockProcessingQueue>(),
            syncConfig,
            LimboLogs.Instance);

        SyncFeedComponent<T> Component<T>()
        {
            ISyncFeed<T> feed = Substitute.For<ISyncFeed<T>>();
            ISyncDownloader<T> downloader = Substitute.For<ISyncDownloader<T>>();
            SyncDispatcher<T> dispatcher = new(syncConfig, feed, downloader, peerPool, Substitute.For<IPeerAllocationStrategyFactory<T>>(), LimboLogs.Instance);
            return new SyncFeedComponent<T>(feed, dispatcher, downloader, new Lazy<BlockDownloader>(() => blockDownloader), Substitute.For<ILifetimeScope>());
        }

        Synchronizer synchronizer = BuildSynchronizer(
            syncConfig,
            blockTree,
            stateSyncRunner,
            Component<BlocksRequest>(),
            Component<BlocksRequest>(),
            Component<HeadersSyncBatch>(),
            Component<BodiesSyncBatch>(),
            Component<ReceiptsSyncBatch>(),
            Component<BlockAccessListsSyncBatch>());

        synchronizer.Start();
        _ = stateSyncRunner.Received(1).Run(Arg.Any<CancellationToken>());

        Task disposeTask = synchronizer.DisposeAsync().AsTask();

        // Production invariant: DisposeAsync must remain blocked while the state sync runner is running.
        // WaitAsync throws TimeoutException iff disposeTask is still running after the window.
        Func<Task> waitForDisposeToEscape = () => disposeTask.WaitAsync(TimeSpan.FromMilliseconds(200));
        Assert.That(async () => await waitForDisposeToEscape(), Throws.TypeOf<TimeoutException>(), "DisposeAsync must wait for the state sync runner");

        runnerGate.SetResult();
        await disposeTask.WaitAsync(cancellationToken);
    }

    // Both dispose tests construct the class under test directly: the asserts need substituted feeds, and
    // SynchronizerModule registers the feed components in their own keyed lifetime scopes, which an outer override
    // cannot reach. Only the collaborators the tests actually drive are parameters here, so the constructor's other
    // arguments live in one place instead of being repeated per test.
    private static Synchronizer BuildSynchronizer(
        ISyncConfig syncConfig,
        IBlockTree blockTree,
        IStateSyncRunner stateSyncRunner,
        SyncFeedComponent<BlocksRequest> fullSync,
        SyncFeedComponent<BlocksRequest> fastSync,
        SyncFeedComponent<HeadersSyncBatch> fastHeaders,
        SyncFeedComponent<BodiesSyncBatch> oldBodies,
        SyncFeedComponent<ReceiptsSyncBatch> oldReceipts,
        SyncFeedComponent<BlockAccessListsSyncBatch> oldBlockAccessLists) =>
        new(Substitute.For<ISyncModeSelector>(),
            Substitute.For<ISyncReport>(),
            syncConfig,
            blockTree,
            Substitute.For<ISyncPivotResolver>(),
            LimboLogs.Instance,
            Substitute.For<INodeStatsManager>(),
            fullSync,
            fastSync,
            stateSyncRunner,
            fastHeaders,
            oldBodies,
            oldReceipts,
            oldBlockAccessLists,
            null!,
            null!,
            Substitute.For<IProcessExitSource>());
}
