// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.RangeSyncTests;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// fulu/p2p-interface.md DataColumnSidecarsByRange: a peer serves a range only from its Status v2 <c>earliest_available_slot</c>,
/// and it can serve the range once its head reaches the range's first slot, not its last.
/// </summary>
public class RangeSyncPeerSelectionTests
{
    private const ulong AnchorSlot = 10;
    private const ulong TargetSlot = 18;

    /// <summary>The first slot of the batch is 11, so a peer that has nothing before 12 cannot serve it, and one starting at 11 can.</summary>
    [TestCase(0UL, true)]
    [TestCase(11UL, true)]
    [TestCase(12UL, false)]
    [CancelAfter(30_000)]
    public async Task Blocks_by_range_skip_a_peer_whose_earliest_available_slot_is_above_the_batch_start(ulong earliestAvailableSlot, bool served, CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12, 13, 14, 16, 17, 18);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        // First in the pool, so the round-robin asks it first whenever it is eligible.
        StubPeer pruned = new("pruned", TargetSlot, (_, _) => chainBlocks, earliestAvailableSlot: earliestAvailableSlot);
        StubPeer full = new("full", TargetSlot, (_, _) => chainBlocks);
        RangeSync sync = new(new StubPool(pruned, full), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> yielded = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(chain.Length));
        Assert.That(pruned.Requests, Is.EqualTo(served ? 1 : 0), "the peer is asked only when its earliest available slot is at or before the batch start");
        Assert.That(full.Requests, Is.EqualTo(served ? 0 : 1));
    }

    /// <summary>
    /// The batch holds a blob block at slot 1 and an empty one at slot 2, so its column request runs from slot 1 to slot 2.
    /// </summary>
    [TestCase(1UL, 0UL, true, TestName = "A head at the request start serves it although it is below the request end")]
    [TestCase(0UL, 0UL, false, TestName = "A head below the request start does not")]
    [TestCase(2UL, 1UL, true, TestName = "An earliest available slot equal to the request start is allowed")]
    [TestCase(2UL, 2UL, false, TestName = "An earliest available slot above the request start is not, although it is inside the range")]
    [CancelAfter(60_000)]
    public async Task Columns_by_range_go_to_custodians_whose_head_reaches_the_start_slot_and_whose_earliest_available_slot_covers_it(
        ulong headSlot, ulong earliestAvailableSlot, bool asked, CancellationToken token)
    {
        await using ColumnBatch batch = ColumnBatch.Create();
        StubPeer blocks = new("blocks", 2, (_, _) => batch.Blocks, custody: PeerColumnCustody.None);
        StubPeer custodian = new(
            "custodian",
            headSlot,
            (_, _) => batch.Blocks,
            (_, _, columns) => [.. columns.Select(c => batch.Chain.Columns[(int)c])],
            custody: new PeerColumnCustody(batch.Sampled, isAdvertised: true),
            earliestAvailableSlot: earliestAvailableSlot);

        await batch.RunAsync(token, blocks, custodian);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(custodian.ColumnRequests, Is.EqualTo(asked ? 1 : 0));
        Assert.That(batch.Sampled.All(c => batch.SidecarPool.TryGet(batch.Chain.BlockRoot, c, out _)), Is.EqualTo(asked), "the sampled columns arrive exactly when the custodian is asked");
    }

    /// <summary>BeaconBlocksByRange: skipped slots are accepted only when the retained block links to the anchor.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Blocks_by_range_resume_at_the_earliest_retained_block_after_empty_slots([Values(12UL, 27UL, 43UL)] ulong firstSlot, CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, firstSlot);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        List<ulong> starts = [];
        // Asked first and serving from inside the target too, but it lacks the block that links to the anchor.
        StubPeer later = new("later", firstSlot + 10, static (_, _) => [], earliestAvailableSlot: firstSlot + 1);
        StubPeer peer = new("earliest", firstSlot + 1, (start, count) => { starts.Add(start); return [.. chainBlocks.Where(b => b.Slot >= start && b.Slot < start + count)]; }, earliestAvailableSlot: firstSlot);
        RangeSync sync = new(new StubPool(later, peer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> yielded = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => firstSlot + 1, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Is.EqualTo(chainBlocks));
        Assert.That(starts.FirstOrDefault(), Is.EqualTo(firstSlot));
        Assert.That(peer.Failures, Is.Zero);
    }

    /// <summary>BeaconBlocksByRange: limited replies do not make another supplier responsible for missing blocks.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_short_or_empty_reply_does_not_penalize_the_next_supplier([Values] bool empty, CancellationToken token)
    {
        ulong[] slots = [.. Enumerable.Range(11, 33).Select(static slot => (ulong)slot)];
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, slots);
        ForkedSignedBeaconBlock[] blocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        bool first = true;
        StubPeer limited = new("limited", 43, (start, count) =>
        {
            if (first)
            {
                first = false;
                return empty ? [] : [blocks[0]];
            }
            return [.. blocks.Where(b => b.Slot >= start && b.Slot < start + count)];
        });
        List<ulong> starts = [];
        StubPeer honest = new("honest", 43, (start, count) =>
        {
            starts.Add(start);
            return [.. blocks.Where(b => b.Slot >= start && b.Slot < start + count)];
        });
        RangeSync sync = new(new StubPool(limited, honest), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));
        List<ForkedSignedBeaconBlock> yielded = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => 43, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Is.EqualTo(blocks));
        Assert.That(honest.Reports, Is.Empty);
        Assert.That(starts[0], Is.EqualTo(empty ? 27UL : 12UL));
    }

    /// <summary>BeaconBlocksByRange: a reply that does not link to the previous one is held against its supplier only when that peer served the previous one too.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_link_break_across_replies_is_blamed_only_on_the_peer_that_served_both(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12, 13, 14);
        ForkedSignedBeaconBlock[] blocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        SignedBeaconBlock offChain = TestChain.CreateBlock(11, anchorRoot);
        offChain.Message!.StateRoot = Keccak.Compute("off-chain");
        ForkedSignedBeaconBlock[] offChainReply = [new ForkedSignedBeaconBlock.OfFulu(offChain)];
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        StubPeer both = new("both", 14, (start, count) =>
        {
            if (start == 11)
            {
                return offChainReply;
            }

            stop.Cancel();
            return [.. blocks.Where(b => b.Slot >= start && b.Slot < start + count)];
        });
        StubPeer other = new("other", 14, (start, count) => [.. blocks.Where(b => b.Slot >= start && b.Slot < start + count)]);
        RangeSync sync = new(new StubPool(both, other), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        Assert.That(async () =>
        {
            await RangeSyncTests.DrainAsync(sync.Run(anchorRoot, AnchorSlot, () => 14, stop.Token));
        }, Throws.InstanceOf<OperationCanceledException>());

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(other.Requests, Is.EqualTo(1), "fixture: the other peer was asked after the off-chain reply");
        Assert.That(other.Reports, Is.Empty);
        Assert.That(both.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
    }

    /// <summary>The sync gate fails a job on any log line containing "Exception", so a failed batch is logged by its cause, not its exception type.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_failed_batch_is_logged_by_its_cause_rather_than_its_exception_type(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 12, 13);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        int calls = 0;
        StubPeer peer = new("slow", 13, (start, count) => ++calls <= 1 ? throw new TimeoutException("slow") : chainBlocks, earliestAvailableSlot: 0);
        TestLogRecorder log = new();
        RangeSync sync = new(new StubPool(peer), new OneLoggerLogManager(new ILogger(log)), new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        await RangeSyncTests.DrainAsync(sync.Run(anchorRoot, AnchorSlot, () => 13, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(log.Messages, Has.Some.Contains("request timed out"), "the failed batch is logged");
        Assert.That(log.Messages, Has.None.Contains("Exception"));
    }

    /// <summary>Sync components log from several threads at once; a lost or null line fails the log assertions with a false cause.</summary>
    [Test]
    public void Log_capture_keeps_every_line_written_from_concurrent_threads()
    {
        const int Writers = 8;
        const int LinesPerWriter = 20_000;
        TestLogRecorder log = new();

        Parallel.For(0, Writers + 1, new ParallelOptions { MaxDegreeOfParallelism = Writers + 1 }, writer =>
        {
            for (int i = 0; i < LinesPerWriter; i++)
            {
                if (writer < Writers)
                    log.Debug($"{writer}:{i}");
                else if (i % 200 == 0)
                    _ = log.Messages.Length;
            }
        });

        string[] lines = log.Messages;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(lines, Has.Length.EqualTo(Writers * LinesPerWriter));
        Assert.That(lines, Has.None.Null);
    }


    /// <summary>Failures shrink the batch; the fallback window stays the default batch, so the peer serving from slot 20 is still asked after the shrink.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Blocks_by_range_keep_using_a_fallback_peer_after_failures_shrink_the_batch(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 20, 21, 22);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        Func<ulong, ulong, ForkedSignedBeaconBlock[]> serve = RefusingBelow(20, chainBlocks);
        int calls = 0;
        StubPeer peer = new("from20", 22, (start, count) => ++calls <= 3 ? throw new TimeoutException("slow") : serve(start, count), earliestAvailableSlot: 20);
        RangeSync sync = new(new StubPool(peer), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> yielded = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => 22, token));

        Assert.That(yielded, Has.Count.EqualTo(chain.Length));
    }

    private static Func<ulong, ulong, ForkedSignedBeaconBlock[]> RefusingBelow(ulong earliestSlot, ForkedSignedBeaconBlock[] blocks) =>
        (start, _) => start < earliestSlot
            ? throw new Eth2ReqRespException("Requested range predates the earliest available slot", ReqRespFraming.ResponseCode.ResourceUnavailable)
            : [.. blocks.Where(b => b.Slot >= start)];

    /// <summary>A peer whose earliest available slot is past the whole batch has nothing to answer with, so the sync waits on its retry delay instead of asking it or spinning.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Blocks_by_range_wait_without_spinning_when_every_peer_serves_only_past_the_batch(CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 11, 12);
        StubPeer peer = new("late", TargetSlot, (_, _) => [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))], earliestAvailableSlot: TargetSlot + 1);
        CountingPool pool = new(peer);
        RangeSync sync = new(pool, LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(TimeSpan.FromMilliseconds(500));
        Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await RangeSyncTests.DrainAsync(sync.Run(anchorRoot, AnchorSlot, () => TargetSlot, stop.Token));
        });

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(peer.Requests, Is.Zero);
        Assert.That(pool.Calls, Is.InRange(1, 2), "one peer lookup per retry delay, not a busy loop");
    }

    /// <summary>
    /// The batch is the genesis-slot block (empty) and the blob block at slot 1, so the request starts at 0. A custodian serving from 1 covers no start,
    /// yet holds the only blob block; it is asked from its own earliest slot, never below it, unless another custodian covers the start.
    /// </summary>
    [TestCase(false, TestName = "Columns by range fall back to a custodian serving from inside the range and ask it from its earliest slot")]
    [TestCase(true, TestName = "Columns by range prefer a custodian covering the start over one serving from inside the range")]
    [CancelAfter(60_000)]
    public async Task Columns_by_range_use_a_custodian_serving_from_inside_the_range_only_when_none_covers_the_start(bool coveringCustodian, CancellationToken token)
    {
        await using ColumnBatch batch = ColumnBatch.Create();
        List<(ulong Start, ulong Count)> lateRequests = [];
        StubPeer blocks = new("blocks", 1, (_, _) => [new ForkedSignedBeaconBlock.OfFulu(batch.Chain.AnchorBlock), batch.Blocks[0]], custody: PeerColumnCustody.None);
        StubPeer late = new(
            "late",
            1,
            (_, _) => [],
            (start, count, columns) =>
            {
                lateRequests.Add((start, count));
                return [.. columns.Select(c => batch.Chain.Columns[(int)c])];
            },
            custody: new PeerColumnCustody(batch.Sampled, isAdvertised: true),
            earliestAvailableSlot: 1);
        StubPeer covering = new(
            "covering",
            1,
            (_, _) => [],
            (_, _, columns) => [.. columns.Select(c => batch.Chain.Columns[(int)c])],
            custody: new PeerColumnCustody(batch.Sampled, isAdvertised: true));

        await batch.RunFromBelowTheBlobBlockAsync(token, coveringCustodian ? [blocks, late, covering] : [blocks, late]);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(late.ColumnRequests, Is.EqualTo(coveringCustodian ? 0 : 1));
        Assert.That(covering.ColumnRequests, Is.EqualTo(coveringCustodian ? 1 : 0));
        Assert.That(lateRequests, coveringCustodian ? (NUnit.Framework.Constraints.IResolveConstraint)Is.Empty : Is.EqualTo(new[] { (1UL, 1UL) }), "requested from its earliest slot, not from the batch start");
        Assert.That(batch.Sampled.All(c => batch.SidecarPool.TryGet(batch.Chain.BlockRoot, c, out _)), Is.True);
    }

    /// <summary>
    /// The pool offers a peer whose last status is below the range once the chain is known to be past it (phase0/p2p-interface.md Status: that status may be stale).
    /// Its empty answer may only mean it is behind: when the next batch does not link, the peer that served it is not at fault, the range is asked again of
    /// another peer, and the peer that answered empty is not asked again in the round.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_below_the_range_whose_empty_answer_the_next_batch_contradicts_is_not_believed_again_and_blames_no_one(CancellationToken token)
    {
        // Two batches, so the round-robin would come back to the peer if its empty answer did not leave it out.
        ulong[] slots = [.. Enumerable.Range((int)AnchorSlot + 1, 2 * (int)RangeSync.DefaultBatchSize).Select(static s => (ulong)s)];
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, slots);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        // First in the pool, so the round-robin asks it first.
        StubPeer behind = new("behind", AnchorSlot, static (_, _) => []);
        StubPeer ahead = new("ahead", slots[^1], (start, count) => [.. chainBlocks.Where(b => b.Slot >= start && b.Slot < start + count)]);
        RangeSync sync = new(new OfferingPool(behind, ahead), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> yielded = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => slots[^1], token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(chain.Length));
        Assert.That(behind.Requests, Is.EqualTo(1), "asked once in the round");
        Assert.That(ahead.Failures, Is.Zero);
        Assert.That(behind.Failures, Is.Zero, "an empty answer from a peer below the range is not a fault");
    }

    /// <summary>BeaconBlocksByRange (phase0/p2p-interface.md) leaves skipped slots out, so a peer below the range may answer a whole batch of them empty truthfully.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_below_the_range_that_answers_a_batch_of_skipped_slots_empty_serves_the_blocks_after_them(CancellationToken token)
    {
        ulong firstBlockSlot = AnchorSlot + 1 + RangeSync.DefaultBatchSize;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, firstBlockSlot, firstBlockSlot + 1, firstBlockSlot + 2);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        StubPeer behind = new("behind", AnchorSlot, (start, count) => [.. chainBlocks.Where(b => b.Slot >= start && b.Slot < start + count)]);
        RangeSync sync = new(new OfferingPool(behind), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> yielded = await RangeSyncTests.CollectAsync(sync.Run(anchorRoot, AnchorSlot, () => chain[^1].Message!.Slot, token));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(yielded, Has.Count.EqualTo(chain.Length));
        Assert.That(behind.Failures, Is.Zero);
    }

    /// <summary>A sole peer whose empty answer the next batch contradicts is left out for the round, so the round ends rather than waiting for another peer; the next round asks it again.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_round_whose_only_peer_is_left_out_for_an_empty_answer_ends_and_the_next_round_asks_it_again(CancellationToken token)
    {
        ulong[] slots = [.. Enumerable.Range((int)AnchorSlot + 1, 2 * (int)RangeSync.DefaultBatchSize).Select(static s => (ulong)s)];
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, slots);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        int answers = 0;
        // Behind at its first answer, caught up from its second.
        StubPeer catchingUp = new("catching up", AnchorSlot, (start, count) => ++answers == 1 ? [] : [.. chainBlocks.Where(b => b.Slot >= start && b.Slot < start + count)]);
        RangeSync sync = new(new OfferingPool(catchingUp), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        (bool firstEnded, int firstYielded) = await RunBoundedAsync(sync, anchorRoot, slots[^1], token);
        (bool secondEnded, int secondYielded) = await RunBoundedAsync(sync, anchorRoot, slots[^1], token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(firstEnded, Is.True, "the round waited for a peer instead of ending");
        Assert.That(firstYielded, Is.Zero);
        Assert.That((secondEnded, secondYielded), Is.EqualTo((true, chain.Length)));
        Assert.That(catchingUp.Failures, Is.Zero);
    }

    /// <summary>A first block that does not link after an empty answer is put down to an unverified anchor before the peer that answered empty.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task An_empty_answer_before_a_block_off_an_unverified_anchor_rejects_the_anchor_rather_than_the_peer(CancellationToken token)
    {
        const ulong heldSlot = AnchorSlot + 10;
        ulong blockAfterGap = heldSlot + 1 + RangeSync.DefaultBatchSize;
        ulong[] slots = [.. Enumerable.Range((int)AnchorSlot + 1, (int)(heldSlot - AnchorSlot)).Select(static s => (ulong)s), blockAfterGap];
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, slots);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        StubPeer behind = new("behind", AnchorSlot, (start, count) => [.. chainBlocks.Where(b => b.Slot >= start && b.Slot < start + count)]);
        RangeSync sync = new(new OfferingPool(behind), LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));
        bool rejected = false;
        RangeSync.AnchorFallback fallback = new(anchorRoot, AnchorSlot, () => rejected = true);

        List<ForkedSignedBeaconBlock> yielded = await RangeSyncTests.CollectAsync(sync.Run(Keccak.Compute("a held block off the chain"), heldSlot, () => blockAfterGap, token, fallback));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(rejected, Is.True);
        Assert.That(yielded, Has.Count.EqualTo(chain.Length));
        Assert.That(behind.Failures, Is.Zero);
    }

    /// <summary>
    /// Every peer's last status names this node's head while the chain has moved on (phase0/p2p-interface.md Status), so no peer reaches the next slot.
    /// The periodic refresh comes about once a minute, so range sync asks for the statuses itself once the slot it waits on is behind the wall slot;
    /// while that slot is the wall slot its block may not be out yet, and asking every slot would only load the peers.
    /// </summary>
    [TestCase(2UL, true, TestName = "Range sync behind the wall slot whose peers all report its own head refreshes their status instead of waiting")]
    [TestCase(1UL, false, TestName = "Range sync waiting on the wall slot's block does not refresh peer status")]
    [CancelAfter(30_000)]
    public async Task Range_sync_refreshes_stale_peer_status_only_behind_the_wall_slot(ulong wallSlotsPastHead, bool refreshed, CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, AnchorSlot + 1, AnchorSlot + 2);
        ForkedSignedBeaconBlock[] chainBlocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        StubPeer peer = new("ahead", AnchorSlot + 2, (start, count) => [.. chainBlocks.Where(b => b.Slot >= start && b.Slot < start + count)]);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Each lookup after the first follows a whole wait, so by the third range sync has waited twice; the run is ended there, not by the clock.
        StaleStatusPool pool = new(peer, AnchorSlot, lookups => { if (lookups == 3) stop.Cancel(); });
        RangeSync sync = new(pool, LimboLogs.Instance, new DataColumnSidecarPool(), BeaconChainSpec.Mainnet, ClockAtGenesis(BeaconChainSpec.Mainnet));

        List<ForkedSignedBeaconBlock> yielded = [];
        try
        {
            await foreach (ForkedSignedBeaconBlock block in sync.Run(anchorRoot, AnchorSlot, () => AnchorSlot + wallSlotsPastHead, stop.Token))
            {
                yielded.Add(block);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested)
        {
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(pool.Refreshes, refreshed ? (NUnit.Framework.Constraints.IResolveConstraint)Is.EqualTo(new[] { AnchorSlot + 1 }) : Is.Empty, "asked once, for the slot range sync waits on");
        Assert.That(yielded, refreshed ? (NUnit.Framework.Constraints.IResolveConstraint)Is.EqualTo(chainBlocks) : Is.Empty);
        Assert.That(pool.Lookups, Is.EqualTo(refreshed ? 2 : 3), "refreshed, the run ends after one wait; otherwise it is still waiting when stopped");
        Assert.That(pool.ChainClaims, Is.Empty, "a slot behind the wall slot may be empty, so it is no evidence the chain reached it");
    }

    private static async Task<(bool Ended, int Yielded)> RunBoundedAsync(RangeSync sync, Hash256 anchorRoot, ulong target, CancellationToken token)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(token);
        bound.CancelAfter(TimeSpan.FromSeconds(5));
        int yielded = 0;
        try
        {
            await foreach (ForkedSignedBeaconBlock _ in sync.Run(anchorRoot, AnchorSlot, () => target, bound.Token))
            {
                yielded++;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, yielded);
        }

        return (true, yielded);
    }

    /// <summary>Offers every peer whatever its head, as <see cref="PeerManager"/> offers peers whose status predates the chain.</summary>
    private sealed class OfferingPool(params IBeaconSyncPeer[] peers) : IBeaconSyncPeerPool
    {
        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot) => peers;
    }

    /// <summary>Selects <paramref name="peer"/> by a last status of <paramref name="staleHeadSlot"/> until a refresh is asked for, as <see cref="PeerManager"/> does.</summary>
    /// <param name="onLookup">Called with the number of lookups so far, the current one included.</param>
    private sealed class StaleStatusPool(IBeaconSyncPeer peer, ulong staleHeadSlot, Action<int>? onLookup = null) : IBeaconSyncPeerPool
    {
        private ulong _statusHeadSlot = staleHeadSlot;

        public int Lookups { get; private set; }

        public List<ulong> Refreshes { get; } = [];

        /// <summary>The slots claimed reached, which would let a pool offer peers past their last head.</summary>
        public List<ulong> ChainClaims { get; } = [];

        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot)
        {
            onLookup?.Invoke(++Lookups);
            return _statusHeadSlot >= minHeadSlot ? [peer] : [];
        }

        public void RefreshStatusesBelow(ulong slot, string reason)
        {
            Refreshes.Add(slot);
            _statusHeadSlot = peer.HeadSlot;
        }

        public void RefreshStatusesBehind(ulong slot, string reason)
        {
            ChainClaims.Add(slot);
            RefreshStatusesBelow(slot, reason);
        }
    }

    private sealed class CountingPool(IBeaconSyncPeer peer) : IBeaconSyncPeerPool
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot)
        {
            Interlocked.Increment(ref _calls);
            return peer.HeadSlot >= minHeadSlot ? [peer] : [];
        }
    }

    private sealed class ColumnBatch : IAsyncDisposable
    {
        private BeaconDiscovery _discovery = null!;

        public ImportableBlobBlock Chain { get; } = ImportableBlobBlock.Create();
        public DataColumnSidecarPool SidecarPool { get; } = new();
        public ulong[] Sampled { get; private set; } = [];

        /// <summary>The blob block at slot 1 and an empty child at slot 2.</summary>
        public ForkedSignedBeaconBlock[] Blocks { get; private set; } = [];

        public static ColumnBatch Create()
        {
            ColumnBatch batch = new();
            batch._discovery = new BeaconDiscovery(new BeaconChainConfig { Discv5Port = 0 }, batch.Chain.Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
            batch._discovery.CreateDiscv5Services(IPAddress.Loopback);
            batch.Sampled = [.. new DiscoveryNodeCustodySource(batch._discovery).Current!.SampledColumns];
            batch.Blocks =
            [
                new ForkedSignedBeaconBlock.OfFulu(batch.Chain.Block),
                new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(2, batch.Chain.BlockRoot)),
            ];
            return batch;
        }

        public async Task RunAsync(CancellationToken token, params IBeaconSyncPeer[] peers)
        {
            RangeSync sync = new(new StubPool(peers), LimboLogs.Instance, SidecarPool, Chain.Spec, Chain.ClockAtEpoch(1), _discovery);
            await RangeSyncTests.DrainAsync(sync.Run(Chain.AnchorRoot, Chain.AnchorBlock.Message!.Slot, () => 2, token));
        }

        /// <summary>Syncs from the anchor's parent so the batch starts at the anchor block at slot 0 (a ulong anchor slot of MaxValue makes the next slot 0), ahead of the blob block at slot 1.</summary>
        public async Task RunFromBelowTheBlobBlockAsync(CancellationToken token, params IBeaconSyncPeer[] peers)
        {
            RangeSync sync = new(new StubPool(peers), LimboLogs.Instance, SidecarPool, Chain.Spec, Chain.ClockAtEpoch(1), _discovery);
            await RangeSyncTests.DrainAsync(sync.Run(Chain.AnchorBlock.Message!.ParentRoot!, ulong.MaxValue, () => 1, token));
        }

        public ValueTask DisposeAsync() => _discovery.DisposeAsync();
    }
}
