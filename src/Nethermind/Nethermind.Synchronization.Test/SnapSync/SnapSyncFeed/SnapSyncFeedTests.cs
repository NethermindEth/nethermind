// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync.SnapSyncFeed;

public class SnapSyncFeedTests
{
    [Test]
    public void WhenAccountRequestEmpty_ReturnNoProgress()
    {
        ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
        Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

        snapProvider.AddAccountRange(Arg.Any<AccountRange>(), Arg.Any<AccountsAndProofs>())
            .Returns(AddRangeResult.ExpiredRootHash);

        using SnapSyncBatch response = new();
        response.AccountRangeRequest = new AccountRange(Keccak.Zero, Keccak.Zero);
        response.AccountRangeResponse = new AccountsAndProofs();

        PeerInfo peer = new(Substitute.For<ISyncPeer>());

        Assert.That(feed.HandleResponse(response, peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
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
