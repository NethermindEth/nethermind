// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync.SnapSyncFeed;

public class SnapSyncFeedTests
{
    [Test]
    public void Code_response_must_match_a_requested_hash_to_count_as_progress([Values] bool matchesRequest)
    {
        byte[] code = new byte[256];
        ValueHash256 requested = matchesRequest ? ValueKeccak.Compute(code) : Keccak.Zero.ValueHash256;
        ISnapProvider provider = Substitute.For<ISnapProvider>();
        (Synchronization.SnapSync.SnapSyncFeed feed, PeerInfo peer, NodeStatsLight stats) = CreateMeasuredFeed(provider);
        using SnapSyncBatch batch = new()
        {
            CodesRequest = new ArrayPoolList<ValueHash256>([requested]),
            CodesResponse = new ByteArrayListAdapter(new ArrayPoolList<byte[]>([code])),
            ResponseMilliseconds = 2
        };

        Assert.That(feed.HandleResponse(batch, peer),
            Is.EqualTo(matchesRequest ? SyncResponseHandlingResult.OK : SyncResponseHandlingResult.LesserQuality));
        provider.Received(1).AddCodes(batch.CodesRequest, batch.CodesResponse);
        Assert.That(stats.GetAverageTransferSpeed(TransferSpeedType.SnapRanges), Is.EqualTo(matchesRequest ? 32 : 0));
    }

    [Test]
    public void Invalid_proof_never_improves_useful_transfer_speed([Values(AddRangeResult.OK, AddRangeResult.InvalidProof)] AddRangeResult result)
    {
        ISnapProvider provider = Substitute.For<ISnapProvider>();
        provider.AddAccountRange(Arg.Any<AccountRange>(), Arg.Any<AccountsAndProofs>()).Returns(result);
        (Synchronization.SnapSync.SnapSyncFeed feed, PeerInfo peer, NodeStatsLight stats) = CreateMeasuredFeed(provider);
        using SnapSyncBatch batch = new()
        {
            AccountRangeRequest = new AccountRange(Keccak.Zero, Keccak.Zero),
            AccountRangeResponse = new AccountsAndProofs
            {
                PathAndAccounts = new ArrayPoolList<PathWithAccount>([new(Keccak.Zero, Nethermind.Core.Account.TotallyEmpty.ToStruct())])
            },
            ResponseMilliseconds = 1
        };

        feed.HandleResponse(batch, peer);

        Assert.That(stats.GetAverageTransferSpeed(TransferSpeedType.SnapRanges) > 0, Is.EqualTo(result == AddRangeResult.OK));
    }

    private static (Synchronization.SnapSync.SnapSyncFeed Feed, PeerInfo Peer, NodeStatsLight Stats) CreateMeasuredFeed(ISnapProvider provider)
    {
        Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303);
        NodeStatsLight stats = new(node);
        stats.AddTransferSpeedCaptureEvent(TransferSpeedType.SnapRanges, 0);
        INodeStatsManager manager = Substitute.For<INodeStatsManager>();
        manager.When(s => s.ReportTransferSpeedEvent(node, TransferSpeedType.SnapRanges, Arg.Any<long>()))
            .Do(call => stats.AddTransferSpeedCaptureEvent(TransferSpeedType.SnapRanges, call.Arg<long>()));
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(node);
        return (new(manager, provider, LimboLogs.Instance), new(peer), stats);
    }

    [Test]
    public void Registered_provider_decorator_forwards_pivot_recovery()
    {
        ISnapProvider inner = Substitute.For<ISnapProvider>();
        inner.AddAccountRange(Arg.Any<AccountRange>(), Arg.Any<AccountsAndProofs>()).Returns(AddRangeResult.ExpiredRootHash);
        ContainerBuilder builder = new();
        builder.RegisterInstance(inner).As<ISnapProvider>();
        builder.RegisterDecorator<ISnapProvider>((_, _, provider) =>
        {
            ISnapProvider wrapper = Substitute.For<ISnapProvider>();
            wrapper.AddAccountRange(Arg.Any<AccountRange>(), Arg.Any<AccountsAndProofs>()).Returns(call =>
                provider.AddAccountRange(call.Arg<AccountRange>(), call.Arg<AccountsAndProofs>()));
            wrapper.When(p => p.UpdatePivot()).Do(_ => provider.UpdatePivot());
            return wrapper;
        });
        using IContainer container = builder.Build();
        Synchronization.FastSync.IStateSyncPivot pivot = Substitute.For<Synchronization.FastSync.IStateSyncPivot>();
        pivot.GetPivotHeader().Returns(Build.A.BlockHeader.WithStateRoot(TestItem.KeccakA).TestObject);
        Synchronization.SnapSync.SnapSyncFeed feed = new(Substitute.For<INodeStatsManager>(),
            container.Resolve<ISnapProvider>(), LimboLogs.Instance, new SnapRecoveryCoordinator(TimeProvider.System, pivot));
        for (int i = 0; i <= Synchronization.SnapSync.SnapSyncFeed.AllowedInvalidResponses; i++)
        {
            SnapSyncBatch batch = new()
            {
                AccountRangeRequest = new AccountRange(TestItem.KeccakA, Keccak.Zero),
                AccountRangeResponse = new AccountsAndProofs()
            };
            feed.HandleResponse(batch);
        }
        inner.Received(1).UpdatePivot();
    }

    [Test]
    public void Expired_storage_root_is_neutral_only_when_proven_stale([Values(0, 1, 2)] int scenario)
    {
        using MemDb db = new();
        StateTree state = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        using StorageRange source = ProgressTrackerTests.CreateRequest(1);
        state.Set(source.Accounts[0].Path, source.Accounts[0].Account.ToAccount());
        state.UpdateRootHash();
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 }))
            .WithSuggestedHeaderOfStateRoot(state.RootHash).Build();
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        tracker.IsFinished(out SnapSyncBatch? discovered);
        Assert.That(provider.AddAccountRange(1, state.RootHash, ValueKeccak.Zero, source.Accounts), Is.EqualTo(AddRangeResult.OK));
        provider.ReleaseRequest(discovered!, true);
        discovered!.Dispose();
        void AdvancePivot()
        {
            IBlockTree blockTree = container.Resolve<IBlockTree>();
            BlockHeader header = Build.A.BlockHeader.WithNumber(129).WithStateRoot(TestItem.KeccakF).TestObject;
            blockTree.BestSuggestedHeader.Returns(header);
            blockTree.FindHeader(Arg.Any<ulong>()).Returns(header);
            provider.UpdatePivot();
        }
        if (scenario == 0) AdvancePivot();
        tracker.IsFinished(out SnapSyncBatch? batch);
        if (scenario == 2) AdvancePivot();
        batch!.StorageRangeResponse = new();
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);
        Assert.That(feed.HandleResponse(batch, new PeerInfo(syncPeer)),
            Is.EqualTo(scenario == 1 ? SyncResponseHandlingResult.NoProgress : SyncResponseHandlingResult.Emptish));
        if (scenario == 1) stats.Received(1).ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, 0);
        else stats.DidNotReceive().ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
    }

    [Test]
    public void WhenStorageResponseAppliesOneRangeAndFailsAnother_ReportsTheAppliedProgress()
    {
        ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
        Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);
        using SnapSyncBatch batch = new()
        {
            StorageRangeRequest = new StorageRange { Accounts = ArrayPoolList<PathWithAccount>.Empty() },
            StorageRangeResponse = new SlotsAndProofs(),
        };
        snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse, out Arg.Any<bool>())
            .Returns(call =>
            {
                call[2] = true;
                return AddRangeResult.DifferentRootHash;
            });

        feed.HandleResponse(batch, null);

        snapProvider.Received(1).ReleaseRequest(batch, responseHandled: true, madeRangeProgress: true);
    }

    /// <summary>RefreshAccounts maps every verification failure to InvalidProof itself, so it can only
    /// throw outside that guard, hence a substitute rather than a real provider.</summary>
    [Test]
    public void WhenRefreshAccountsThrows_ReleasesTheRequestForRetry()
    {
        ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
        Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

        snapProvider.RefreshAccounts(Arg.Any<AccountsToRefreshRequest>(), Arg.Any<AccountsAndProofs>())
            .Returns(_ => throw new IOException("state backend unavailable"));

        using SnapSyncBatch batch = new()
        {
            AccountsToRefreshRequest = new AccountsToRefreshRequest { RootHash = Keccak.Zero, Paths = ArrayPoolList<AccountWithStorageStartingHash>.Empty() },
            AccountsToRefreshResponse = new AccountsAndProofs()
        };

        Assert.That(() => feed.HandleResponse(batch, null), Throws.InstanceOf<IOException>());

        snapProvider.Received(1).ReleaseRequest(batch, responseHandled: false);
    }

    // A checkpoint taken between the handler and the release would see the response half handled.
    [Test]
    public void HoldsTheResponseScopeUntilTheRequestIsReleased()
    {
        ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
        IDisposable responseScope = Substitute.For<IDisposable>();
        snapProvider.BeginResponse().Returns(responseScope);
        Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);
        AccountRange request = new(Keccak.Zero, Keccak.Zero);
        AccountsAndProofs response = new();
        using SnapSyncBatch batch = new() { AccountRangeRequest = request, AccountRangeResponse = response };
        snapProvider.AddAccountRange(request, response).Returns(AddRangeResult.OK);

        feed.HandleResponse(batch, new PeerInfo(Substitute.For<ISyncPeer>()));

        Received.InOrder(() =>
        {
            snapProvider.BeginResponse();
            snapProvider.AddAccountRange(request, response);
            snapProvider.ReleaseRequest(batch, responseHandled: true, madeRangeProgress: true);
            responseScope.Dispose();
        });
    }

    /// <summary>Short enough to keep these tests quick; production uses five minutes.</summary>
    private static readonly TimeSpan ShortThreshold = TimeSpan.FromMilliseconds(100);

    // A request in flight that is never answered and never timed out moves no response-driven counter, so only
    // elapsed time can see it. This is the case the check exists for.
    [Test]
    public async Task A_wedged_request_that_never_answers_is_still_reported()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) = CreateFeed(ShortThreshold);

        await Drive(feed, 500);

        Assert.That(StallWarnings(logger), Is.Not.Empty);
        Assert.That(StallWarnings(logger)[0], Does.Contain("none recorded"));
    }

    [Test]
    public async Task No_stall_warning_while_the_threshold_has_not_elapsed()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) = CreateFeed(TimeSpan.FromHours(1));
        PeerInfo peer = new(Substitute.For<ISyncPeer>());

        TimeOutOneRequest(feed, peer);
        await Drive(feed, 300);

        Assert.That(StallWarnings(logger), Is.Empty, "a brief gap is normal - a punished peer or a pivot update");
    }

    [Test]
    public async Task A_stall_warning_names_the_last_unproductive_reason()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) = CreateFeed(ShortThreshold);

        TimeOutOneRequest(feed, new PeerInfo(Substitute.For<ISyncPeer>()));
        await Drive(feed, 500);

        Assert.That(StallWarnings(logger), Is.Not.Empty);
        Assert.That(StallWarnings(logger)[0], Does.Contain("no response"));
    }

    [Test]
    public async Task An_unusable_range_counts_towards_the_stall_and_names_itself()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) = CreateFeed(ShortThreshold);

        // A fresh peer each time so the per-peer budget never trips and only the stall check can fire.
        feed.AnalyzeResponsePerPeer(AddRangeResult.InvalidProof, new PeerInfo(Substitute.For<ISyncPeer>()));
        await Drive(feed, 500);

        Assert.That(StallWarnings(logger), Is.Not.Empty);
        Assert.That(StallWarnings(logger)[0], Does.Contain(nameof(AddRangeResult.InvalidProof)));
    }

    // The gauge, not the log line, is what an alert watches.
    [Test]
    [NonParallelizable]
    public async Task A_useful_range_clears_the_unproductive_streak()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) = CreateFeed(ShortThreshold);
        PeerInfo peer = new(Substitute.For<ISyncPeer>());

        TimeOutOneRequest(feed, peer);
        Assert.That(Metrics.SnapConsecutiveUnproductiveResponses, Is.GreaterThan(0));

        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer);
        Assert.That(Metrics.SnapConsecutiveUnproductiveResponses, Is.Zero);

        await Drive(feed, 500);

        Assert.That(StallWarnings(logger), Is.Not.Empty, "the clock restarts, it does not stop");
        Assert.That(StallWarnings(logger)[0], Does.Contain("0 unproductive responses"));
        Assert.That(StallWarnings(logger)[0], Does.Contain("none recorded"));
    }

    // The dispatcher can retire hundreds of failed requests a minute; one line per failure would bury the log
    // it is meant to explain.
    [Test]
    public async Task A_continuing_stall_does_not_repeat_within_the_threshold()
    {
        // Generous margins: the first warning is due at 1000 ms and a repeat could not come before 2000 ms.
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) = CreateFeed(TimeSpan.FromSeconds(1));

        await Drive(feed, 1500);

        Assert.That(StallWarnings(logger), Has.Count.EqualTo(1));
    }

    // SimpleDispatcher hands the batch back with no peer when the pool could not allocate one. That is the shape
    // of a stall with no usable peers, and nothing else in the feed records it.
    [Test]
    [NonParallelizable]
    public async Task A_request_that_never_got_a_peer_counts_towards_the_stall()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) = CreateFeed(ShortThreshold);

        using (SnapSyncBatch batch = new() { AccountRangeRequest = new AccountRange(Keccak.Zero, Keccak.Zero) })
        {
            Assert.That(feed.HandleResponse(batch, null), Is.EqualTo(SyncResponseHandlingResult.NotAssigned));
        }

        Assert.That(Metrics.SnapConsecutiveUnproductiveResponses, Is.GreaterThan(0));

        await Drive(feed, 500);

        Assert.That(StallWarnings(logger), Is.Not.Empty);
        Assert.That(StallWarnings(logger)[0], Does.Contain("no peer available"));
    }

    /// <summary>
    /// Long enough that only a missing reset, rather than a slow runner, can produce a warning: each run is
    /// driven for a tenth of it, so a false warning would need most of a second of scheduling delay.
    /// </summary>
    private const int RestartThresholdMs = 1000;

    // The feed is a singleton and outlives one run: a reorg during BAL healing restarts snap on the same instance.
    // Healing can run for longer than the threshold, and that gap belongs to no run.
    [Test]
    public async Task A_restarted_run_is_not_blamed_for_the_gap_before_it()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) =
            CreateFeed(TimeSpan.FromMilliseconds(RestartThresholdMs));

        // Each Drive ends where the dispatcher ends a run, so this is two runs with a gap between them.
        await Drive(feed, RestartThresholdMs / 10);
        await Task.Delay(RestartThresholdMs + RestartThresholdMs / 5);
        await Drive(feed, RestartThresholdMs / 10);

        Assert.That(StallWarnings(logger), Is.Empty,
            "neither run was driven for as long as the threshold; the gap between them belongs to neither");
    }

    // ReleaseRequest, which lets IsFinished report the run over, runs before the response is analyzed, so the
    // dispatcher can end the run while the response that ended it is still on its way to the stall clock.
    [Test]
    public async Task A_response_landing_after_the_run_ended_does_not_restart_the_clock()
    {
        (Synchronization.SnapSync.SnapSyncFeed feed, TestLogger logger) =
            CreateFeed(TimeSpan.FromMilliseconds(RestartThresholdMs));

        await Drive(feed, RestartThresholdMs / 10);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, new PeerInfo(Substitute.For<ISyncPeer>()));

        await Task.Delay(RestartThresholdMs + RestartThresholdMs / 5);
        await Drive(feed, RestartThresholdMs / 10);

        Assert.That(StallWarnings(logger), Is.Empty,
            "the range was stored by the run that just ended, so it cannot start the next run's clock");
    }

    private static (Synchronization.SnapSync.SnapSyncFeed, TestLogger) CreateFeed(TimeSpan? stallWarningThreshold = null)
    {
        TestLogger logger = new();
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetClassLogger<Synchronization.SnapSync.SnapSyncFeed>().Returns(new ILogger(logger));
        return (new Synchronization.SnapSync.SnapSyncFeed(Substitute.For<ISnapProvider>(), logManager, stallWarningThreshold), logger);
    }

    /// <summary>
    /// Runs the request loop for <paramref name="milliseconds"/>. A substituted provider offers no batch and
    /// never reports finished, so the loop idles - which is exactly when a stalled node is sitting there.
    /// </summary>
    private static async Task Drive(Synchronization.SnapSync.SnapSyncFeed feed, int milliseconds)
    {
        using CancellationTokenSource cts = new(milliseconds);
        try
        {
            // PrepareRequest awaits its idle delay outside its own try, so cancelling the loop surfaces here.
            // That is how SimpleDispatcher.Run ends too.
            await feed.PrepareRequest(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A batch with a request but no response of any kind is what the dispatcher hands back on timeout.</summary>
    private static void TimeOutOneRequest(Synchronization.SnapSync.SnapSyncFeed feed, PeerInfo peer)
    {
        using SnapSyncBatch batch = new() { AccountRangeRequest = new AccountRange(Keccak.Zero, Keccak.Zero) };
        feed.HandleResponse(batch, peer);
    }

    private static List<string> StallWarnings(TestLogger logger) =>
        logger.LogList.FindAll(static line => line.Contains("Snap sync has not stored a usable range"));
}
