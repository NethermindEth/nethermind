// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// A gossip block past the head whose parent no peer returns by root showed the chain moving on while every peer's last status
/// said it had not (Hoodi, 16:05-16:10): the head then waited minutes for gossip. The pool must hear of it, and range sync must bring the parent.
/// </summary>
public partial class BeaconSyncOrchestratorTests
{
    [TestCase(WallSlot, WallSlot, TestName = "a block at the wall slot signals that slot")]
    [TestCase(WallSlot + 1, WallSlot, TestName = "an early block signals no further than the wall slot")]
    [TestCase(NearHeadAnchorSlot, null, TestName = "a block at the head slot signals nothing")]
    public async Task An_unknown_parent_gossip_block_past_the_head_asks_for_peer_status_before_its_ancestor_is_fetched(ulong blockSlot, ulong? signalled)
    {
        (Harness harness, IBeaconSyncPeer peer) = CreateBackfillHarness();
        int signalsAtFetch = -1;
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            signalsAtFetch = harness.Pool.StatusRefreshSlots.Length;
            return Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([]);
        });

        await harness.Orchestrator.ProcessGossipBlockAsync(UnknownParentBlock(blockSlot, seed: 0), CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Pool.StatusRefreshSlots, Is.EqualTo(signalled is { } slot ? new[] { slot } : Array.Empty<ulong>()));
        Assert.That(signalsAtFetch, Is.EqualTo(signalled is null ? 0 : 1), "the status refresh runs while the ancestor is fetched, not after it failed");
    }

    [TestCase(AnchorSlot, true, TestName = "a head an epoch behind signals the wall slot")]
    [TestCase(WallSlot - 2, false, TestName = "a head within the slack of the wall clock signals nothing")]
    public async Task A_head_behind_the_wall_clock_with_no_peer_past_it_asks_for_peer_status(ulong headSlot, bool signals)
    {
        Harness harness = CreateHarness(anchorSlot: headSlot);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, headSlot, finalizedEpoch: Spec.GetEpoch(headSlot));

        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        Assert.That(harness.Pool.StatusRefreshSlots, Is.EqualTo(signals ? new[] { WallSlot } : Array.Empty<ulong>()));
    }

    [Test]
    public async Task A_head_left_behind_signals_each_later_wall_slot_while_only_peers_on_a_stale_status_are_offered()
    {
        Harness harness = CreateHarness();
        harness.Pool.OfferedAfterRefresh = [StalePeerServing([], AnchorSlot)];

        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        Assert.That(harness.Pool.StatusRefreshSlots, Is.EqualTo(new[] { WallSlot, WallSlot + 1 }));
    }

    // The log's case end to end: stale statuses, a by-root fetch that times out on every peer, and range sync from the head.
    [Test]
    [CancelAfter(60_000)]
    public async Task A_gossip_block_whose_ancestor_no_peer_returns_by_root_imports_once_range_sync_brings_the_ancestor(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 4, WallSlot - 3, WallSlot - 2, WallSlot - 1, WallSlot);
        ForkedSignedBeaconBlock gossip = new ForkedSignedBeaconBlock.OfFulu(chain[^1]);
        // The peers have the ancestors but not yet the gossip block itself, so only the held copy can import it.
        IBeaconSyncPeer stale = StalePeerServing(chain[..^1]);
        Harness harness = CreateHarness(anchorSlot: NearHeadAnchorSlot);
        harness.Importer.Known.Add(anchorRoot);
        harness.Pool.OfferedAfterRefresh = [stale];

        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(gossip, token);
        int heldAfterFailedFetch = harness.Orchestrator.PendingGossipBlockCount;
        const int maxRounds = 3;
        int rounds = 0;
        while (rounds < maxRounds && !harness.Importer.Known.Contains(gossip.ComputeMessageRoot()))
        {
            rounds++;
            await RunRangeRoundAsync(harness, token, TimeSpan.FromSeconds(3));
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(heldAfterFailedFetch, Is.EqualTo(1), "the gossip block is kept while its ancestry is missing");
        Assert.That(harness.Importer.Known, Does.Contain(gossip.ComputeMessageRoot()), $"the head did not reach the gossip block's slot within {maxRounds} range sync rounds");
        Assert.That(harness.Importer.Imports.Select(static i => i.Slot).Where(static s => s == WallSlot).Count(), Is.EqualTo(1));
    }

    /// <summary>Range sync that already reached the wall clock waits a slot between rounds; a failed ancestor fetch must not wait that out.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_failed_ancestor_fetch_starts_a_range_sync_round_without_waiting_out_the_slot(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, WallSlot - 1, WallSlot);
        // Its status covers the wall slot, but it has none of the blocks, so the first round ends at once.
        RangeSyncTests.StubPeer empty = new("empty", WallSlot, static (_, _) => []);
        IBeaconSyncPeer stale = StalePeerServing(chain[..^1]);
        Harness harness = CreateHarness(anchorSlot: NearHeadAnchorSlot, peers: [empty]);
        harness.Importer.Known.Add(anchorRoot);
        using CancellationTokenSource stopFeed = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task feed = harness.Orchestrator.RunRangeSyncFeedAsync(stopFeed.Token);
        while (empty.Requests == 0)
        {
            await Task.Delay(10, token);
        }

        // The round has ended and the feed waits for the next slot.
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        harness.Pool.OfferedAfterRefresh = [stale];
        await harness.Orchestrator.ProcessGossipBlockAndFetchAncestorsAsync(new ForkedSignedBeaconBlock.OfFulu(chain[^1]), token);
        TimeSpan bound = TimeSpan.FromSeconds(Spec.SecondsPerSlot / 2.0);
        Stopwatch sinceFailedFetch = Stopwatch.StartNew();
        while (RangeRequests(stale) == 0 && sinceFailedFetch.Elapsed < bound)
        {
            await Task.Delay(10, token);
        }

        TimeSpan untilNextRound = sinceFailedFetch.Elapsed;
        await stopFeed.CancelAsync();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(await EndsAsync(feed, token), Is.True);
        Assert.That(RangeRequests(stale), Is.Positive, "a range sync round started after the failed fetch");
        Assert.That(untilNextRound, Is.LessThan(bound));
    }

    /// <summary>
    /// Mainnet shape: the head tens of slots behind, no gossip block to link, every status stale and most peers timing out by range.
    /// The slot tick alone must tell the pool, and range sync must reach the wall clock through the one peer that serves.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_head_tens_of_slots_behind_with_most_peers_timing_out_reaches_the_wall_clock(CancellationToken token)
    {
        const ulong behindBy = 40;
        ulong anchorSlot = WallSlot - behindBy;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(anchorSlot, [.. Enumerable.Range(1, (int)behindBy).Select(i => anchorSlot + (ulong)i)]);
        Harness harness = CreateHarness(anchorSlot: anchorSlot);
        harness.Importer.Known.Add(anchorRoot);
        Hash256 wallSlotRoot = new ForkedSignedBeaconBlock.OfFulu(chain[^1]).ComputeMessageRoot();
        harness.Pool.OfferedAfterRefresh = [TimingOutPeer("timing out 1", anchorSlot), TimingOutPeer("timing out 2", anchorSlot), StalePeerServing(chain, anchorSlot)];

        await harness.Orchestrator.ProcessSlotAsync(WallSlot, token);
        const int maxRounds = 3;
        int rounds = 0;
        while (rounds < maxRounds && !harness.Importer.Known.Contains(wallSlotRoot))
        {
            rounds++;
            await RunRangeRoundAsync(harness, token, TimeSpan.FromSeconds(15));
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Pool.StatusRefreshSlots, Does.Contain(WallSlot), "the slot tick told the pool the chain is past its peers");
        Assert.That(harness.Importer.Known, Does.Contain(wallSlotRoot), $"the head did not reach the wall slot within {maxRounds} range sync rounds");
    }

    private static IBeaconSyncPeer TimingOutPeer(string id, ulong headSlot)
    {
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.Id.Returns(id);
        peer.HeadSlot.Returns(headSlot);
        peer.RequestBlocksByRangeAsync(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ForkedSignedBeaconBlock>>>(static _ => throw new OperationCanceledException("request timed out"));
        return peer;
    }

    /// <summary>A peer whose last status is our head: it times out every by-root request and serves <paramref name="chain"/> by range.</summary>
    private static IBeaconSyncPeer StalePeerServing(SignedBeaconBlock[] chain, ulong headSlot = NearHeadAnchorSlot)
    {
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.Id.Returns("stale");
        peer.HeadSlot.Returns(headSlot);
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ForkedSignedBeaconBlock>>>(static _ => throw new OperationCanceledException("request timed out"));
        peer.RequestBlocksByRangeAsync(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            ulong start = call.ArgAt<ulong>(0);
            ulong end = start + call.ArgAt<ulong>(1);
            return Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([.. chain.Where(b => b.Message!.Slot >= start && b.Message.Slot < end).Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))]);
        });
        return peer;
    }

    private static int RangeRequests(IBeaconSyncPeer peer) =>
        peer.ReceivedCalls().Count(static c => c.GetMethodInfo().Name == nameof(IBeaconSyncPeer.RequestBlocksByRangeAsync));

    private static async Task RunRangeRoundAsync(Harness harness, CancellationToken token, TimeSpan timeout)
    {
        using CancellationTokenSource round = CancellationTokenSource.CreateLinkedTokenSource(token);
        round.CancelAfter(timeout);
        try
        {
            await harness.Orchestrator.FeedRangeSyncRoundAsync(round.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
        }

        await harness.Orchestrator.ProcessQueuedAsync(token);
    }
}
