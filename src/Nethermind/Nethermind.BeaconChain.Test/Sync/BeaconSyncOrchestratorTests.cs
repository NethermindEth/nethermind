// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.P2P.Gossip;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NSubstitute;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    private const ulong AnchorSlot = 100;
    private const ulong WallSlot = 200;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    [CancelAfter(10_000)]
    public async Task Metrics_head_delay_timer_advances_without_worker_progress([Values] bool blockedEngine, CancellationToken token)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        PeerBandTests.Node node = PeerBandTests.CreateNode();
        await using BeaconP2P p2p = node.P2P;
        Harness harness = CreateHarness(discovery: discovery, p2p: p2p, peerManager: new PeerManager(p2p, node.Config, node.StatusHolder, LimboLogs.Instance));
        Assert.That(Metrics.BeaconChainHeadSlotDelay, Is.EqualTo((long)(WallSlot - AnchorSlot)));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource<PayloadStatusV1> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.PendingFcu = blockedEngine ? reply.Task : null;
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, _) = TestChain.BuildLinkedChain(AnchorSlot);
        Task run = blockedEngine
            ? harness.Orchestrator.RunAsync(new ForkedBeaconState.OfFulu(new BeaconStateFulu()), new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot, stop.Token)
            : harness.Orchestrator.RunHeadSlotDelayTimerAsync(stop.Token);
        if (blockedEngine) Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1));
        try
        {
            harness.Timestamper.Add(TimeSpan.FromSeconds(3 * Spec.SecondsPerSlot));
            while (Metrics.BeaconChainHeadSlotDelay != (long)(WallSlot + 3 - AnchorSlot))
            {
                await Task.Delay(10, token);
            }

            if (!blockedEngine)
            {
                harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot + 1, Spec.GetEpoch(AnchorSlot));
                await harness.Orchestrator.RunHeadStepAsync(token);
                Assert.That(Metrics.BeaconChainHeadSlotDelay, Is.EqualTo((long)(WallSlot + 3 - AnchorSlot - 1)));
            }
        }
        finally
        {
            await stop.CancelAsync();
            reply.TrySetResult(PayloadStatusV1.Syncing);
            Assert.CatchAsync<OperationCanceledException>(() => run);
        }

        Metrics.BeaconChainHeadSlotDelay = -1;
        harness.Timestamper.Add(TimeSpan.FromSeconds(Spec.SecondsPerSlot));
        await Task.Delay(1100, token);
        Assert.That(Metrics.BeaconChainHeadSlotDelay, Is.EqualTo(-1));
    }

    [Test]
    public async Task Worker_imports_in_order_drains_queued_gossip_children_and_runs_one_fcu_per_batch()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 101, 102, 103);
        harness.Importer.Known.Add(anchorRoot);

        // The gossip block arrives first with an unknown parent; far behind the wall clock it is
        // queued instead of backfilled, and drains once range sync delivers its parent.
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[2])));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[0])));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[1])));
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, 102, 103]), "import order");
            Assert.That(harness.Importer.Imports.Select(static i => i.VerifySignatures), Is.All.True, "network blocks verify signatures");
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "one head FCU per drained batch");
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(103UL), "sync tip follows imports");
            Assert.That(harness.StatusHolder.CurrentStatus.HeadRoot, Is.EqualTo(harness.Importer.Head.HeadRoot), "status holder refreshed by the head step");
        }
    }

    /// <summary>
    /// The engine API asks for forkchoiceUpdated after each head change. Range sync behind the wall clock keeps the work queue
    /// full, so slot ticks are always stale when the worker reaches them; with a head step only per drained batch or 64 imports,
    /// the execution layer's head froze for many minutes and then jumped 64 blocks. Imports of 0.6 s each must move it every second.
    /// </summary>
    [Test]
    public async Task Range_sync_imports_behind_the_wall_clock_move_the_execution_head_every_second()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, [.. Enumerable.Range(101, 10).Select(static slot => (ulong)slot)]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.OnImported = (block, root) =>
        {
            harness.Timestamper.Add(TimeSpan.FromMilliseconds(600));
            harness.Importer.Head = CreateHead(root, block.Slot, Spec.GetEpoch(AnchorSlot), execHash: ExecutionHashOf(block.Slot));
        };
        int fcusBefore = harness.Engine.FcuCalls.Count;
        foreach (SignedBeaconBlock block in chain)
        {
            harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(block)));
        }

        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports, Has.Count.EqualTo(chain.Length), "fixture: every block imports in one worker pass");
            Assert.That(harness.Engine.FcuCalls.Skip(fcusBefore).Select(static call => call.Head),
                Is.EqualTo(new ulong[] { 102, 104, 106, 108, 110 }.Select(ExecutionHashOf)), "the head of each second of imports, in order");
        }

        static Hash256 ExecutionHashOf(ulong slot) => Keccak.Compute(BitConverter.GetBytes(slot));
    }

    public enum RangeRejection
    {
        AtImport,
        OnRetry,
        FutureOnRetry,
        HeldChildOnRetry,
    }

    /// <summary>fork-choice.md on_block: an invalid block ends its round, also when it fails on a retry of its held chain, and no other peer is blamed.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task RangeSync_restarts_from_the_imported_tip_and_blames_only_the_invalid_blocks_supplier([Values] bool gloas, [Values] bool activeRound, [Values] RangeRejection rejection, CancellationToken token)
    {
        ulong target = activeRound ? WallSlot : 103;
        ulong[] slots = [.. Enumerable.Range(101, (int)(target - AnchorSlot)).Select(static slot => (ulong)slot)];
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, slots);
        ForkedSignedBeaconBlock[] blocks = [.. chain.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        bool childRejected = rejection == RangeRejection.HeldChildOnRetry;
        ulong rejectedSlot = childRejected ? 103UL : 102UL;
        Hash256 rejectedParent = (childRejected ? blocks[1] : blocks[0]).ComputeMessageRoot();
        SignedBeaconBlock signedForged = TestChain.CreateBlock(rejectedSlot, rejectedParent);
        signedForged.Message!.StateRoot = TestItem.KeccakB;
        ForkedSignedBeaconBlock forged = gloas
            ? new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(rejectedSlot, rejectedParent))
            : new ForkedSignedBeaconBlock.OfFulu(signedForged);
        ForkedSignedBeaconBlock first = childRejected ? blocks[1] : forged;
        ForkedSignedBeaconBlock second = childRejected
            ? forged
            : gloas
                ? new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(103, forged.ComputeMessageRoot()))
                : new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(103, forged.ComputeMessageRoot()));
        bool rejectedServed = false;
        EnvelopeServingPeer supplier = new("supplier", target, blocksByRange: (start, count) =>
        {
            if (!rejectedServed)
            {
                rejectedServed = true;
                return [first, second];
            }
            return [.. blocks.Where(b => b.Slot >= start && b.Slot < start + count)];
        });
        bool shortReply = true;
        List<ulong> starts = [];
        EnvelopeServingPeer honest = new("honest", target, blocksByRange: (start, count) =>
        {
            starts.Add(start);
            if (shortReply)
            {
                shortReply = false;
                return [blocks[0]];
            }
            return [.. blocks.Where(b => b.Slot >= start && b.Slot < start + count)];
        });
        Harness harness = CreateHarness(wallSlot: target, peers: [honest, supplier]);
        harness.Importer.Known.Add(anchorRoot);
        Hash256 firstRoot = first.ComputeMessageRoot();
        Hash256 forgedRoot = forged.ComputeMessageRoot();
        if (rejection == RangeRejection.AtImport)
        {
            harness.Importer.Forged.Add(forgedRoot);
        }
        else if (rejection == RangeRejection.FutureOnRetry)
        {
            harness.Importer.Ticks.Clear();
            harness.Importer.Ticks.Add(rejectedSlot - 1);
            harness.Importer.Early.Add(firstRoot);
        }
        else
        {
            harness.Importer.Unavailable.Add(firstRoot);
        }

        Task firstRound = harness.Orchestrator.FeedRangeSyncRoundAsync(token);
        await harness.Orchestrator.ProcessQueuedAsync(token);
        if (rejection != RangeRejection.AtImport)
        {
            harness.Importer.Unavailable.Remove(firstRoot);
            harness.Importer.Forged.Add(forgedRoot);
            await harness.Orchestrator.ProcessSlotAsync(target, token);
        }

        Assert.That(await EndsAsync(firstRound, token), Is.True);
        ulong acceptedSlot = harness.Orchestrator.SyncTip.Slot;
        await harness.Orchestrator.FeedRangeSyncRoundAsync(token);
        await harness.Orchestrator.ProcessQueuedAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(acceptedSlot, Is.EqualTo(rejectedSlot - 1));
            Assert.That(starts, Does.Contain(rejectedSlot), "the restarted request begins after the last imported block");
            Assert.That(honest.Reports, Is.Empty);
            Assert.That(harness.Importer.Known, Does.Not.Contain(forgedRoot));
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(target));
            Assert.That(supplier.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
            if (rejection == RangeRejection.AtImport)
            {
                Assert.That(harness.Importer.Imports.Any(i => i.Root == second.ComputeMessageRoot()), Is.False, "the rejected block's queued child is dropped unseen");
            }
        }
    }

    /// <summary>
    /// Slot ticks pile up while an import holds the worker; answering each would send the execution layer a burst of
    /// identical forkchoiceUpdated calls, so the backlog collapses into the newest tick.
    /// </summary>
    [Test]
    public async Task Slot_ticks_queued_behind_a_newer_tick_collapse_into_one_head_step()
    {
        Harness harness = CreateHarness();
        int ticksBefore = harness.Importer.Ticks.Count;
        int fcusBefore = harness.Engine.FcuCalls.Count;
        for (ulong slot = WallSlot + 1; slot <= WallSlot + 6; slot++)
        {
            await harness.Orchestrator.EnqueueSlotTickAsync(slot, CancellationToken.None);
        }

        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Ticks.Skip(ticksBefore), Is.EqualTo((ulong[])[WallSlot + 6]), "only the newest tick reaches fork choice");
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(fcusBefore + 1), "one forkchoiceUpdated for the whole backlog");
        }
    }

    /// <summary>An operator must see sync move without a line per block: at most one a second, only when the slot moved, saying how far.</summary>
    [Test]
    public void Sync_progress_is_logged_at_most_once_a_second_and_only_when_the_slot_moves()
    {
        Nethermind.Core.Test.TestLogger logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));

        harness.Orchestrator.LogSyncProgress(110); // within the first second
        harness.Timestamper.Add(TimeSpan.FromSeconds(1));
        harness.Orchestrator.LogSyncProgress(120);
        harness.Orchestrator.LogSyncProgress(121); // same second
        harness.Timestamper.Add(TimeSpan.FromSeconds(1));
        harness.Orchestrator.LogSyncProgress(120); // the slot did not move past the last line

        string[] lines = [.. logger.LogList.Where(static l => l.StartsWith("Beacon sync:"))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines, Has.Length.EqualTo(1));
            Assert.That(lines[0], Does.Match(@"slot 120 \(\+20 slots, 2\.0 blocks/s, 0 ms/block of which newPayload \d+ ms\), 80 behind wall slot 200"));
        }
    }

    [TestCase(false, true, TestName = "Sync progress is logged near head while the execution layer syncs")]
    [TestCase(true, false, TestName = "Sync progress is not logged once following head in sync")]
    public async Task Sync_progress_stops_once_the_node_follows_head_in_sync(bool elValid, bool expectLine)
    {
        Nethermind.Core.Test.TestLogger logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));
        harness.Orchestrator.GossipStarted = true;
        harness.Importer.Head = CreateHead(TestItem.KeccakA, WallSlot - 1, finalizedEpoch: 1);
        if (elValid) harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakG });
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        harness.Timestamper.Add(TimeSpan.FromSeconds(1));
        harness.Orchestrator.LogSyncProgress(WallSlot - 1);

        Assert.That(logger.LogList.Count(static l => l.StartsWith("Beacon sync:")), Is.EqualTo(expectLine ? 1 : 0));
    }

    [Test]
    public async Task Head_step_invalidates_payload_recomputes_head_and_retries_fcu_once_on_invalid([Values(PayloadStatus.Valid, PayloadStatus.Invalid)] string retryStatus)
    {
        Harness harness = CreateHarness();
        HeadView badHead = CreateHead(TestItem.KeccakA, 103, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        HeadView goodHead = CreateHead(TestItem.KeccakC, 102, finalizedEpoch: 3, execHash: TestItem.KeccakD);
        harness.Importer.Head = badHead;
        harness.Importer.HeadAfterInvalidation = goodHead;
        harness.Engine.FcuResponses.Enqueue(PayloadStatusV1.Invalid(TestItem.KeccakF));
        harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = retryStatus, LatestValidHash = TestItem.KeccakD });

        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.ForkchoiceVerdicts, Is.EqualTo((List<(Hash256, Hash256, string, Hash256?)>)
            [
                (badHead.HeadRoot, badHead.HeadExecutionHash!, PayloadStatus.Invalid, TestItem.KeccakF),
                (goodHead.HeadRoot, goodHead.HeadExecutionHash!, retryStatus, TestItem.KeccakD),
            ]), "each verdict reaches fork choice keyed by the head hash it answered, the latest valid hash included");
            Assert.That(harness.Importer.ComputeHeadCalls, Is.EqualTo(3), "head recomputed after each applied verdict");
            Assert.That(harness.Engine.FcuCalls, Is.EqualTo((List<(Hash256, Hash256, Hash256)>)
            [
                (badHead.HeadExecutionHash!, badHead.JustifiedExecutionHash!, badHead.FinalizedExecutionHash!),
                (goodHead.HeadExecutionHash!, goodHead.JustifiedExecutionHash!, goodHead.FinalizedExecutionHash!),
            ]), "FCU retried exactly once with the recomputed head");
            Assert.That(harness.StatusHolder.CurrentStatus.HeadRoot, Is.EqualTo(goodHead.HeadRoot), "status advertises the recovered head");
            Assert.That(harness.StatusHolder.JustifiedRoot, Is.EqualTo(goodHead.Justified.Root), "status carries the recovered head's justified root");
            Assert.That(harness.StatusHolder.ExecutionInSync, Is.EqualTo(retryStatus == PayloadStatus.Valid));
        }
    }

    /// <summary>
    /// An unchanged fork choice state tells the EL nothing, so it is not re-sent every head step; it is re-sent after
    /// <see cref="BeaconSyncOrchestrator.ForkchoiceResendInterval"/>, since the EL counts a silent CL as gone.
    /// </summary>
    [TestCase(false, 1, 1, TestName = "Unchanged head within the resend interval is not re-sent")]
    [TestCase(false, 61, 2, TestName = "Unchanged head is re-sent once the resend interval has passed")]
    [TestCase(true, 1, 2, TestName = "A changed head is sent at once")]
    public async Task Forkchoice_updated_is_sent_when_the_state_changes_or_the_resend_interval_passes(bool changeHead, int secondsLater, int expectedFcus)
    {
        Harness harness = CreateHarness();
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        harness.Timestamper.Add(TimeSpan.FromSeconds(secondsLater));
        if (changeHead) harness.Importer.Head = CreateHead(TestItem.KeccakC, 101, finalizedEpoch: 3, execHash: TestItem.KeccakD);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(expectedFcus));
    }

    /// <summary>The head root does not change when get_head flips its payload status, only its execution hash does; the resend window must not hide that.</summary>
    [Test]
    public async Task Forkchoice_updated_is_sent_when_the_same_head_root_flips_its_payload_status()
    {
        Harness harness = CreateHarness();
        HeadView empty = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        HeadView full = empty with { HeadExecutionHash = TestItem.KeccakD };

        foreach (HeadView head in new[] { empty, full, empty })
        {
            harness.Importer.Head = head;
            harness.Timestamper.Add(TimeSpan.FromSeconds(1));
            await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        }

        Assert.That(harness.Engine.FcuCalls.Select(static call => call.Head), Is.EqualTo(new[] { TestItem.KeccakB, TestItem.KeccakD, TestItem.KeccakB }));
    }

    /// <summary>A run stopped before the anchor kick sends the execution layer nothing and skips the work that follows the kick, the public-key cache build.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_run_cancelled_before_the_engine_kick_sends_no_forkchoice_update_and_builds_no_cache(CancellationToken testToken)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        PeerBandTests.Node node = PeerBandTests.CreateNode();
        await using BeaconP2P p2p = node.P2P;
        Harness harness = CreateHarness(discovery: discovery, p2p: p2p, peerManager: new PeerManager(p2p, node.Config, node.StatusHolder, LimboLogs.Instance));
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        int cacheBuilds = 0;
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => harness.Orchestrator.RunAsync(new ForkedBeaconState.OfFulu(new BeaconStateFulu()), new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot, cts.Token, () => cacheBuilds++));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Engine.FcuCalls, Is.Empty);
            Assert.That(cacheBuilds, Is.Zero);
        }
    }

    /// <summary>A run stopped while the anchor kick is answered must not start the work that follows it, whether or not that work checks the token itself.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_run_cancelled_during_the_engine_kick_skips_the_work_that_follows_it(CancellationToken testToken)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        PeerBandTests.Node node = PeerBandTests.CreateNode();
        await using BeaconP2P p2p = node.P2P;
        Harness harness = CreateHarness(discovery: discovery, p2p: p2p, peerManager: new PeerManager(p2p, node.Config, node.StatusHolder, LimboLogs.Instance));
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        int afterKick = 0;
        harness.Engine.OnCall = cts.Cancel;

        Assert.CatchAsync<OperationCanceledException>(() => harness.Orchestrator.RunAsync(new ForkedBeaconState.OfFulu(new BeaconStateFulu()), new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot, cts.Token, () => afterKick++));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "the kick was sent");
            Assert.That(afterKick, Is.Zero);
        }
    }

    /// <summary>
    /// The anchor kick goes through the same send state as the head steps, so a head step that finds the anchor still the head
    /// does not send it again within the resend interval.
    /// </summary>
    [Test]
    public async Task The_anchor_kick_is_not_repeated_by_a_head_step_that_finds_the_anchor_still_the_head()
    {
        Harness harness = CreateHarness();

        await harness.Orchestrator.KickExecutionAsync(TestItem.KeccakA);
        Hash256 anchorExecutionHash = harness.Engine.FcuCalls.Single().Head;
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot, finalizedEpoch: 0, execHash: anchorExecutionHash) with { JustifiedExecutionHash = null, FinalizedExecutionHash = null };
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Engine.FcuCalls, Is.EqualTo((List<(Hash256, Hash256, Hash256)>)[(anchorExecutionHash, anchorExecutionHash, anchorExecutionHash)]), "head, safe and finalized are all the anchor payload, sent once");
        }
    }

    /// <summary>A failed call is no answer: caching it would hold the resend window shut, so the EL would not see the head for a full interval.</summary>
    [Test]
    public async Task A_failed_forkchoice_update_is_retried_by_the_next_head_step_and_the_retry_is_then_remembered()
    {
        Harness harness = CreateHarness();
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        harness.Engine.FailingFcuCalls = 1;

        foreach (int _ in new int[3])
        {
            harness.Timestamper.Add(TimeSpan.FromSeconds(1));
            await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        }

        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(2), "failed, sent again at once, then the answered send is inside the resend interval");
    }

    [Test]
    public async Task A_failed_forkchoice_update_clears_execution_sync_and_still_publishes_the_head([Values] bool thrownFault)
    {
        Harness harness = CreateHarness();
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakB });
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        harness.Importer.Head = CreateHead(TestItem.KeccakC, 101, finalizedEpoch: 3, execHash: TestItem.KeccakD);
        harness.Engine.FailingFcuCalls = 1;
        if (thrownFault) harness.Engine.FcuFailure = new InvalidOperationException("storage failure");

        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.StatusHolder.ExecutionInSync, Is.False);
            Assert.That(Metrics.BeaconChainElInSync, Is.Zero);
            Assert.That(harness.StatusHolder.CurrentStatus.HeadRoot, Is.EqualTo(TestItem.KeccakC), "the status still advertises the new head");
        }

        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(3), "the head answered VALID before the failure is sent again");
            Assert.That(harness.StatusHolder.ExecutionInSync, Is.False, "the fresh SYNCING answer is used, not the cached VALID");
        }
    }

    [Test]
    public void Cancellation_between_head_selection_and_an_engine_call_prevents_the_call([Values] bool retry)
    {
        Harness harness = CreateHarness();
        using CancellationTokenSource cancellation = new();
        if (retry)
        {
            harness.Engine.FcuResponses.Enqueue(PayloadStatusV1.Invalid(TestItem.KeccakF));
            harness.Engine.OnCall = cancellation.Cancel;
        }
        else
        {
            harness.Importer.OnComputeHead = cancellation.Cancel;
        }

        Assert.ThrowsAsync<OperationCanceledException>(() => harness.Orchestrator.RunHeadStepAsync(cancellation.Token));
        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(retry ? 1 : 0));
    }

    [Test]
    public async Task Epoch_log_reports_unavailable_after_a_valid_engine_call_fails()
    {
        Nethermind.Core.Test.TestLogger logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));
        harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = PayloadStatus.Valid });
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        harness.Importer.Head = harness.Importer.Head with { HeadExecutionHash = TestItem.KeccakB };
        harness.Engine.FailingFcuCalls = 1;

        await harness.Orchestrator.ProcessSlotAsync(WallSlot + Spec.SlotsPerEpoch, CancellationToken.None);

        Assert.That(logger.LogList.Where(static line => line.StartsWith("Beacon chain:")),
            Has.Some.EndsWith("EL unavailable"));
        harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = PayloadStatus.Valid });
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.StatusHolder.ExecutionInSync, Is.True);
            Assert.That(Metrics.BeaconChainElInSync, Is.EqualTo(1));
        }
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 2 * Spec.SlotsPerEpoch, CancellationToken.None);
        Assert.That(logger.LogList.Last(static line => line.StartsWith("Beacon chain:")), Does.EndWith("EL in sync"));
    }

    [Test]
    public void Cancellation_releases_a_head_step_waiting_for_the_engine()
    {
        Harness harness = CreateHarness();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<PayloadStatusV1> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.FcuAnswer = answer.Task;
        Task step = harness.Orchestrator.RunHeadStepAsync(cancellation.Token);
        try
        {
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(() => step.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            answer.TrySetResult(PayloadStatusV1.Syncing);
        }
    }

    [Test]
    public async Task A_failed_anchor_kick_is_sent_again_by_the_next_head_step([Values] bool thrownFault)
    {
        Harness harness = CreateHarness();
        harness.Engine.FailingFcuCalls = 1;
        if (thrownFault) harness.Engine.FcuFailure = new InvalidOperationException("storage failure");

        PayloadStatusV1? kick = await harness.Orchestrator.KickExecutionAsync(TestItem.KeccakA);
        Hash256 anchorExecutionHash = harness.Engine.FcuCalls.Single().Head;
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot, finalizedEpoch: 0, execHash: anchorExecutionHash) with { JustifiedExecutionHash = null, FinalizedExecutionHash = null };
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(kick, Is.Null);
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task An_invalid_verdict_is_never_reused_for_an_unchanged_head()
    {
        Harness harness = CreateHarness();
        HeadView head = CreateHead(TestItem.KeccakA, 103, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        harness.Importer.Head = head;
        harness.Importer.HeadAfterInvalidation = head;
        harness.Engine.FcuResponses.Enqueue(PayloadStatusV1.Invalid(TestItem.KeccakF));
        harness.Engine.FcuResponses.Enqueue(PayloadStatusV1.Invalid(TestItem.KeccakF));

        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        // The first step sends and retries once after INVALID; the second asks again rather than reusing the verdict.
        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Finalized_checkpoint_advance_triggers_on_finalized_exactly_once()
    {
        Harness harness = CreateHarness();
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None); // baseline head

        CheckpointRef advanced = new(5, TestItem.KeccakB);
        harness.Importer.Head = harness.Importer.Head with { Finalized = advanced };
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None); // same epoch again

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Finalizations, Is.EqualTo((List<CheckpointRef>)[advanced]), "OnFinalized fired once per advance");
            Assert.That(harness.StatusHolder.CurrentStatus.FinalizedEpoch, Is.EqualTo(5UL), "status advertises the new finality");
        }
    }

    [Test]
    public async Task Slot_tick_advances_fork_choice_runs_fcu_and_rotates_gossip_digest_at_bpo_boundary()
    {
        // Wall clock in the epoch right before the mainnet BPO2 boundary.
        const ulong Bpo2Epoch = 419_072;
        ulong preRotationSlot = (Bpo2Epoch - 1) * Spec.SlotsPerEpoch + 2;
        Harness harness = CreateHarness(anchorSlot: preRotationSlot - 10, wallSlot: preRotationSlot);
        byte[] bpo1Digest = ForkDigest.Compute(Spec, Bpo2Epoch - 1);
        byte[] bpo2Digest = ForkDigest.Compute(Spec, Bpo2Epoch);

        Dictionary<string, FakeTopic> topics = [];
        harness.Orchestrator.StartGossip(id => topics[id] = new FakeTopic());

        await harness.Orchestrator.ProcessSlotAsync(preRotationSlot, CancellationToken.None);
        byte[] digestBeforeBoundary = harness.Orchestrator.CurrentGossipDigest;
        await harness.Orchestrator.ProcessSlotAsync(Bpo2Epoch * Spec.SlotsPerEpoch, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(digestBeforeBoundary, Is.EqualTo(bpo1Digest), "no rotation before the boundary");
            Assert.That(harness.Orchestrator.CurrentGossipDigest, Is.EqualTo(bpo2Digest), "digest rotated at the BPO epoch");
            Assert.That(topics.Keys, Does.Contain(GossipTopics.Topic(bpo2Digest, GossipTopics.BeaconBlock)), "router re-subscribed on the new digest");
            Assert.That(harness.Importer.Ticks, Does.Contain(preRotationSlot).And.Contain(Bpo2Epoch * Spec.SlotsPerEpoch), "fork-choice ticked per slot");
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "the head FCU is sent once; the unchanged head is not re-sent on the next tick");
        }
    }

    [Test]
    public async Task Gossip_blocks_failing_validation_are_dropped_before_import()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150, 151);
        harness.Importer.Known.Add(anchorRoot);
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        // Establish finality at epoch 4 (start slot 128) for the finalized-slot check.
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        SignedBeaconBlock child = chain[1]; // slot 151, parent chain[0] unknown -> queued
        SignedBeaconBlock equivocation = TestChain.CreateBlock(151, TestItem.KeccakB); // same (slot, proposer), different content
        SignedBeaconBlock belowFinality = TestChain.CreateBlock(120, anchorRoot);
        SignedBeaconBlock wrongProposer = TestChain.CreateBlock(160, anchorRoot);

        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(child), CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(equivocation), CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(belowFinality), CancellationToken.None);
        harness.Importer.ExpectedProposer = false;
        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(wrongProposer), CancellationToken.None);
        harness.Importer.ExpectedProposer = true;

        Assert.That(harness.Importer.Imports, Is.Empty, "all gossip blocks were held or dropped before import");

        // Importing the parent through range sync drains only the valid queued child.
        await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[150, 151]), "parent imported, then the queued child - nothing else");
    }

    /// <summary>
    /// A gossip block whose columns trail it must not be dropped for good: it is retried directly
    /// through <see cref="BeaconSyncOrchestrator.ImportBlockAsync"/> on a later slot tick, not through
    /// <see cref="BeaconSyncOrchestrator.ProcessGossipBlockAsync"/> (whose seen-proposal gate would
    /// otherwise drop the retry as a repeat).
    /// </summary>
    [Test]
    public async Task Gossip_block_with_unavailable_data_is_retried_and_imported_once_columns_arrive()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        SignedBeaconBlock block = chain[0];
        Hash256 blockRoot = SszRoots.HashTreeRoot(block.Message!);
        harness.Importer.Unavailable.Add(blockRoot);

        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(block), CancellationToken.None);

        Assert.That(harness.Importer.Known, Does.Not.Contain(blockRoot), "nothing may be recorded while the block's data is unavailable");

        // The missing columns arrive; the next slot tick must retry and import the block without
        // gossip seeing it again.
        harness.Importer.Unavailable.Remove(blockRoot);
        await orchestrator.ProcessSlotAsync(151, CancellationToken.None);

        Assert.That(harness.Importer.Known, Does.Contain(blockRoot), "the retry must import the block once its data becomes available");
    }

    public enum BlockSource
    {
        Gossip,
        RangeSync,
        ByRootBackfill,
        GossipThenFetched,
    }

    /// <summary>
    /// A block this node fetched stays a requested import on every retry, and so does a range-sync child held under it: a
    /// deferred fetched block whose parent's state must be regenerated would otherwise wait on the budget gossip shares, and
    /// be dropped once gossip spent it.
    /// </summary>
    [Test]
    public async Task Deferred_block_is_retried_as_requested_only_when_this_node_fetched_it([Values] BlockSource source)
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        Hash256 blockRoot = block.ComputeMessageRoot();
        Hash256 childRoot = child.ComputeMessageRoot();
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([block]));
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(blockRoot);

        switch (source)
        {
            case BlockSource.Gossip:
                await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
                break;
            case BlockSource.RangeSync:
                // The child is held under the deferred block and imports once that block does.
                harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(block));
                harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(child));
                await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
                break;
            case BlockSource.ByRootBackfill:
                await harness.Orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);
                break;
            case BlockSource.GossipThenFetched:
                await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
                await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None, fetchedByRoot: true);
                break;
        }

        bool waited = harness.Orchestrator.PendingRetryBlockCount == 1 && !harness.Importer.Known.Contains(blockRoot);
        harness.Importer.Unavailable.Remove(blockRoot);
        await harness.Orchestrator.ProcessSlotAsync(NearWallSlot + 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(waited, Is.True, "fixture: the block waits in the retry set");
            Assert.That(harness.Importer.Known, Does.Contain(blockRoot), "fixture: the retry imports the block");
            Assert.That(harness.Importer.RequestedImports.Count(root => root == blockRoot), Is.EqualTo(source == BlockSource.Gossip ? 0 : 2));
            if (source == BlockSource.RangeSync)
            {
                Assert.That(harness.Importer.Known, Does.Contain(childRoot), "fixture: the held child imports");
                Assert.That(harness.Importer.RequestedImports.Count(root => root == childRoot), Is.EqualTo(2), "the held range child imports as requested too");
            }
        }
    }

    /// <summary>
    /// A block fetched by root has the root this node asked for, so when it is invalid the peer that served it served an
    /// invalid block and is blamed, as range sync blames the supplier of an invalid range block; also when the block first
    /// waited for a retry.
    /// </summary>
    [Test]
    public async Task Peer_serving_an_invalid_block_by_root_is_blamed([Values] bool onRetry)
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([parent]));
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Forged.Add(parent.ComputeMessageRoot());
        if (onRetry)
        {
            harness.Importer.Unavailable.Add(parent.ComputeMessageRoot());
        }

        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[1]), CancellationToken.None);
        if (onRetry)
        {
            peer.DidNotReceive().ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string>());
            harness.Importer.Unavailable.Remove(parent.ComputeMessageRoot());
            await harness.Orchestrator.ProcessSlotAsync(NearWallSlot + 3, CancellationToken.None);
        }

        peer.Received(1).ReportFailure(PeerFailureReason.ProtocolViolation, Arg.Any<string>());
    }

    /// <summary>
    /// A forged copy of a fetched block, held beside the genuine one behind the same ancestor and drained first, is refused,
    /// but the children waiting on that root still import with the genuine block drained after it.
    /// </summary>
    [Test]
    public async Task Forged_copy_drained_before_the_genuine_block_keeps_its_children()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2, NearWallSlot + 3);
        ForkedSignedBeaconBlock ancestor = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock genuine = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        ForkedSignedBeaconBlock grandchild = new ForkedSignedBeaconBlock.OfFulu(chain[2]);
        Hash256 ancestorRoot = ancestor.ComputeMessageRoot();
        Hash256 genuineRoot = genuine.ComputeMessageRoot();
        BlsSignature forgedSignature = new(Enumerable.Repeat((byte)0x11, 96).ToArray());
        ForkedSignedBeaconBlock forged = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain[1].Message, Signature = forgedSignature });
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(r => r[0] == ancestorRoot), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([ancestor]));
        peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(r => r[0] == genuineRoot), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([genuine]));
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.RegenerationRefused.Add(ancestorRoot);
        harness.Importer.ForgedSignatures.Add(forgedSignature);

        // The forgery arrives first and is held behind the deferred ancestor; the genuine block, fetched for its child, after it.
        await harness.Orchestrator.ProcessGossipBlockAsync(forged, CancellationToken.None);
        await harness.Orchestrator.ProcessGossipBlockAsync(grandchild, CancellationToken.None);
        int held = harness.Orchestrator.PendingGossipBlockCount;
        harness.Importer.RegenerationRefused.Remove(ancestorRoot);
        await harness.Orchestrator.ProcessSlotAsync(NearWallSlot + 4, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held, Is.EqualTo(3), "fixture: the forgery, the genuine block and the grandchild are held");
            Assert.That(harness.Importer.Known, Does.Contain(genuineRoot), "fixture: the genuine block imports");
            Assert.That(harness.Importer.Known, Does.Contain(grandchild.ComputeMessageRoot()));
        }
    }

    /// <summary>
    /// fork-choice.md <c>on_block</c> checks against this node's own store, such as the finalized slot or descent from the
    /// finalized checkpoint, say nothing of a block's data: a peer that served a block by root those refused is not blamed,
    /// while one that served a block whose data is invalid is.
    /// </summary>
    [Test]
    public async Task Peer_serving_a_block_by_root_is_blamed_only_when_its_data_is_invalid([Values] bool localAdmission)
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        (localAdmission ? harness.Importer.AdmissionRefused : harness.Importer.Forged).Add(block.ComputeMessageRoot());

        BlockImportResult result = await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None, fetchedByRoot: true, servedBy: peer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "fixture");
            peer.Received(localAdmission ? 0 : 1).ReportFailure(PeerFailureReason.ProtocolViolation, Arg.Any<string>());
        }
    }

    /// <summary>
    /// A fetched chain whose oldest block cannot import on its known parent waits for the next slot only when the regeneration
    /// budget refused it; then its descendants are held under the evicting cap of refused backfills. A refusal no later slot
    /// changes, such as a proposer with no cached key, leaves nothing queued.
    /// </summary>
    [Test]
    public async Task Fetched_chain_on_an_unregenerated_parent_is_held_only_for_a_budget_refusal_and_only_up_to_the_cap([Values] bool budgetRefusal)
    {
        const ulong AnchorNearWall = WallSlot - 31;
        ulong[] slots = [.. Enumerable.Range(1, 31).Select(static i => AnchorNearWall + (ulong)i)];
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorNearWall, slots);
        Dictionary<Hash256, ForkedSignedBeaconBlock> byRoot = chain.Select(static b => (ForkedSignedBeaconBlock)new ForkedSignedBeaconBlock.OfFulu(b)).ToDictionary(static b => b.ComputeMessageRoot());
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([byRoot[((Hash256[])call[0])[0]]]));
        Harness harness = CreateHarness(anchorSlot: AnchorNearWall, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        Hash256 oldestRoot = SszRoots.HashTreeRoot(chain[0].Message!);
        (budgetRefusal ? harness.Importer.RegenerationRefused : harness.Importer.RegenerationImpossible).Add(oldestRoot);
        int pendingBefore = harness.Orchestrator.PendingGossipBlockCount;

        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[^1]), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Root), Does.Contain(oldestRoot), "fixture: the whole chain was fetched");
            Assert.That(harness.Orchestrator.PendingRetryBlockCount, Is.EqualTo(budgetRefusal ? 1 : 0));
            Assert.That(harness.Orchestrator.PendingGossipBlockCount - pendingBefore, Is.EqualTo(budgetRefusal ? BeaconSyncOrchestrator.MaxHeldRefusedBackfills : 0));
        }
    }

    /// <summary>
    /// A copy of a block fetched by root, with the same message but another signature, is not the block the peer served: when
    /// the copy arrives by gossip and is invalid, the peer that served the real block is not blamed, and the real block keeps
    /// its retry.
    /// </summary>
    [Test]
    public async Task Forged_gossip_copy_of_a_block_fetched_by_root_does_not_blame_its_supplier()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        Hash256 parentRoot = parent.ComputeMessageRoot();
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([parent]));
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(parentRoot);
        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[1]), CancellationToken.None);
        harness.Importer.Unavailable.Remove(parentRoot);
        harness.Importer.Forged.Add(parentRoot);
        ForkedSignedBeaconBlock copy = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain[0].Message, Signature = new BlsSignature(Enumerable.Repeat((byte)0x11, 96).ToArray()) });

        BlockImportResult result = await harness.Orchestrator.ImportBlockAsync(copy, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Orchestrator.PendingRetryBlockCount, Is.EqualTo(1), "the fetched block still waits for its retry");
            Assert.That(result, Is.EqualTo(BlockImportResult.Invalid), "fixture");
            Assert.That(harness.Importer.ByRootImports.Count(root => root == parentRoot), Is.EqualTo(1), "the copy imports as gossip, not as the fetched block");
            peer.DidNotReceive().ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string>());
        }
    }

    /// <summary>
    /// A block fetched by root that waits behind a fetched ancestor the regeneration budget deferred still imports as fetched
    /// once that ancestor does, so the peer that served it is blamed when it is invalid.
    /// </summary>
    [Test]
    public async Task Fetched_block_held_behind_a_deferred_fetched_ancestor_blames_its_supplier_when_invalid()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2, NearWallSlot + 3);
        ForkedSignedBeaconBlock ancestor = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        ForkedSignedBeaconBlock middle = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        Hash256 ancestorRoot = ancestor.ComputeMessageRoot();
        Hash256 middleRoot = middle.ComputeMessageRoot();
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(r => r[0] == ancestorRoot), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([ancestor]));
        peer.RequestBlocksByRootAsync(Arg.Is<Hash256[]>(r => r[0] == middleRoot), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([middle]));
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.RegenerationRefused.Add(ancestorRoot);
        harness.Importer.Forged.Add(middleRoot);

        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[2]), CancellationToken.None);
        bool ancestorWaited = harness.Orchestrator.PendingRetryBlockCount == 1;
        harness.Importer.RegenerationRefused.Remove(ancestorRoot);
        await harness.Orchestrator.ProcessSlotAsync(NearWallSlot + 4, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ancestorWaited, Is.True, "fixture: the budget-deferred ancestor waits for a retry");
            Assert.That(harness.Importer.Known, Does.Contain(ancestorRoot), "fixture: the ancestor imports on the next slot");
            Assert.That(harness.Importer.ByRootImports, Does.Contain(middleRoot), "the held fetched block imports as fetched");
            peer.Received(1).ReportFailure(PeerFailureReason.ProtocolViolation, Arg.Any<string>());
        }
    }

    /// <summary>
    /// A fetched block that waits for the regeneration budget keeps the gossip child held behind it when a copy of it with a
    /// forged signature arrives and is invalid: that copy says nothing of the queued block, which imports with its child.
    /// </summary>
    [Test]
    public async Task Invalid_copy_of_a_waiting_fetched_block_keeps_its_held_child()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1, NearWallSlot + 2);
        ForkedSignedBeaconBlock ancestor = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        Hash256 ancestorRoot = ancestor.ComputeMessageRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(chain[1]);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.RequestBlocksByRootAsync(Arg.Any<Hash256[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ForkedSignedBeaconBlock>>([ancestor]));
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.RegenerationRefused.Add(ancestorRoot);
        BlsSignature forgedSignature = new(Enumerable.Repeat((byte)0x11, 96).ToArray());
        harness.Importer.ForgedSignatures.Add(forgedSignature);
        await harness.Orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);
        harness.Importer.RegenerationRefused.Remove(ancestorRoot);

        BlockImportResult copy = await harness.Orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain[0].Message, Signature = forgedSignature }), CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(NearWallSlot + 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy, Is.EqualTo(BlockImportResult.Invalid), "fixture");
            Assert.That(harness.Importer.Known, Does.Contain(ancestorRoot));
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()), "the held child imports with the queued block");
        }
    }

    /// <summary>
    /// A gossip block waiting for a retry takes the provenance of a block fetched under its root only when both are the same
    /// signed block: otherwise the honest supplier of the fetched copy would be blamed when the queued forgery fails.
    /// </summary>
    [Test]
    public async Task Fetched_copy_does_not_lend_its_supplier_to_a_queued_forgery()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearWallSlot, NearWallSlot + 1);
        ForkedSignedBeaconBlock genuine = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        Hash256 root = genuine.ComputeMessageRoot();
        BlsSignature forgedSignature = new(Enumerable.Repeat((byte)0x11, 96).ToArray());
        ForkedSignedBeaconBlock forgery = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock { Message = chain[0].Message, Signature = forgedSignature });
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(root);
        harness.Importer.ForgedSignatures.Add(forgedSignature);

        await harness.Orchestrator.ImportBlockAsync(forgery, CancellationToken.None);
        await harness.Orchestrator.ImportBlockAsync(genuine, CancellationToken.None, fetchedByRoot: true, servedBy: peer);
        harness.Importer.Unavailable.Remove(root);
        await harness.Orchestrator.ProcessSlotAsync(NearWallSlot + 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Orchestrator.PendingRetryBlockCount, Is.Zero, "fixture: the queued copy was retried");
            Assert.That(harness.Importer.Known, Does.Not.Contain(root), "fixture: the queued copy is the forgery, which fails");
            peer.DidNotReceive().ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string>());
        }
    }

    /// <summary>The retry list is bounded by finality, not just by size: a block finality has passed stops being retried even after its data becomes available.</summary>
    [Test]
    public async Task Gossip_block_with_unavailable_data_stops_being_retried_once_finality_passes_its_slot()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4); // finalized start slot 128
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        SignedBeaconBlock block = chain[0]; // slot 150, still ahead of finality
        Hash256 blockRoot = SszRoots.HashTreeRoot(block.Message!);
        harness.Importer.Unavailable.Add(blockRoot);
        await orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(block), CancellationToken.None);

        // Finality advances past slot 150 (epoch 6 starts at slot 192) before the block is retried.
        harness.Importer.Head = harness.Importer.Head with { Finalized = new CheckpointRef(6, TestItem.KeccakB) };
        harness.Importer.Unavailable.Remove(blockRoot);
        await orchestrator.ProcessSlotAsync(200, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Importer.Known, Does.Not.Contain(blockRoot), "a block pruned behind finality must not be retried even once its data arrives");
            Assert.That(harness.Importer.Imports.Count(i => i.Root == blockRoot), Is.EqualTo(1), "the pruned entry must not be retried again on a later tick");
        });
    }

    /// <summary>
    /// The spec IGNOREs a block only once a block with a valid signature was seen for its (slot, proposer): a forged
    /// block that fails its signature must not suppress the real one, while an equivocation after it is ignored.
    /// </summary>
    [Test]
    public async Task Forged_gossip_block_does_not_suppress_the_real_block_for_its_slot_and_proposer()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        SignedBeaconBlock real = chain[0];
        SignedBeaconBlock forged = TestChain.CreateBlock(150, anchorRoot);
        forged.Message!.StateRoot = TestItem.KeccakF;
        SignedBeaconBlock equivocation = TestChain.CreateBlock(150, anchorRoot);
        equivocation.Message!.StateRoot = TestItem.KeccakG;
        harness.Importer.Forged.Add(SszRoots.HashTreeRoot(forged.Message));

        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(forged), CancellationToken.None);
        bool seenAfterForged = harness.Router.IsProposalSeen(150, real.Message!.ProposerIndex);
        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(real), CancellationToken.None);
        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(equivocation), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seenAfterForged, Is.False, "a block failing its signature does not mark (slot, proposer)");
            Assert.That(harness.Importer.Known, Does.Contain(SszRoots.HashTreeRoot(real.Message)), "the real block is imported after the forged one");
            Assert.That(harness.Router.IsProposalSeen(150, real.Message.ProposerIndex), "the imported block marks (slot, proposer)");
            Assert.That(harness.Importer.Imports.Select(static i => i.Root), Does.Not.Contain(SszRoots.HashTreeRoot(equivocation.Message)), "a later block for the pair is ignored");
        }
    }

    [Test]
    public async Task Gossip_block_deferred_for_the_engine_marks_its_slot_and_proposer()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EngineDown.Add(SszRoots.HashTreeRoot(chain[0].Message!));

        await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None);

        Assert.That(harness.Router.IsProposalSeen(150, chain[0].Message!.ProposerIndex), "the engine is called only after the proposer signature verified");
    }

    /// <summary>
    /// fork-choice.md on_block: an early block from gossip or range sync waits for its slot tick and then imports.
    /// Its verified proposer signature reserves the proposal while the retry waits.
    /// </summary>
    [Test]
    public async Task Block_before_its_slot_imports_at_its_slot_tick_and_marks_its_proposer([Values] bool fromRange)
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, WallSlot + 1);
        Hash256 root = SszRoots.HashTreeRoot(chain[0].Message!);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Early.Add(root);

        if (fromRange)
        {
            harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[0])));
            await harness.Orchestrator.ProcessQueuedAsync(CancellationToken.None);
        }
        else
        {
            await harness.Orchestrator.ProcessGossipBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None);
        }

        bool seenEarly = harness.Router.IsProposalSeen(WallSlot + 1, chain[0].Message!.ProposerIndex);
        ulong? heldSlot = harness.Orchestrator.RangeHeldSlot;
        harness.Timestamper.Set(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + (WallSlot + 1) * Spec.SecondsPerSlot));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(seenEarly, Is.True);
            Assert.That(heldSlot, Is.EqualTo(fromRange ? WallSlot + 1 : (ulong?)null));
            Assert.That(harness.Orchestrator.RangeHeldSlot, Is.Null);
            Assert.That(harness.Importer.Known, Does.Contain(root), "the retry set re-imports the block after its slot's tick");
            Assert.That(harness.Importer.Imports.Select(static i => i.Root), Is.EqualTo(new[] { root, root }));
        }
    }

    [Test]
    public async Task Gloas_gossip_aggregate_and_attester_slashing_reach_the_importer()
    {
        Harness harness = CreateHarness();
        harness.Orchestrator.RouteGossipEvents();

        MessageValidity[] verdicts =
        [
            harness.Router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.GloasAggregate(slot: WallSlot))),
            harness.Router.Handle(GossipTopics.AttesterSlashing, gloasTopic: true,
                GossipMessageValidatorTests.Encode(GossipMessageValidatorTests.GloasSlashing([1, 2], [2, 3], secondSource: 2, secondTarget: 3))),
        ];
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdicts, Is.All.EqualTo(MessageValidity.Ignored), "consumed by the router, not forwarded");
            Assert.That(harness.Importer.GossipOperations.Select(static o => o.GetType()), Is.EqualTo(new[] { typeof(SignedAggregateAndProofGloas), typeof(AttesterSlashingGloas) }));
        }
    }

    /// <summary>
    /// A vote that holds the worker for minutes left no line at all, so the stall could not be told from a dead node.
    /// One that crosses the threshold names its slot and target checkpoint; a quick one stays silent.
    /// </summary>
    [Test]
    public async Task Gossip_aggregate_that_holds_the_worker_past_the_threshold_is_logged_with_its_target([Values] bool pastThreshold)
    {
        Nethermind.Core.Test.TestLogger logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));
        harness.Orchestrator.SlowWorkItemThreshold = pastThreshold ? TimeSpan.Zero : TimeSpan.FromHours(1);
        harness.Orchestrator.RouteGossipEvents();
        SignedAggregateAndProofGloas aggregate = GossipMessageValidatorTests.GloasAggregate(slot: WallSlot);
        Checkpoint target = aggregate.Message!.Aggregate!.Data!.Target!;

        harness.Router.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: true, GossipMessageValidatorTests.Encode(aggregate));
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        string[] lines = [.. logger.LogList.Where(static l => l.StartsWith("Import worker spent") && l.Contains(" on a gossip aggregate"))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(1), "fixture: the aggregate reaches fork choice");
            Assert.That(lines, Has.Length.EqualTo(pastThreshold ? 1 : 0));
            if (pastThreshold)
                Assert.That(lines[0], Does.EndWith($" ms on a gossip aggregate for slot {WallSlot} with target epoch {target.Epoch} root {target.Root}"));
        }
    }

    /// <summary>The head step after a worker pass reads the justified balances and can hold the worker as long as an import, so it is timed the same way.</summary>
    [Test]
    public async Task Head_step_that_holds_the_worker_past_the_threshold_is_logged([Values] bool pastThreshold)
    {
        Nethermind.Core.Test.TestLogger logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));
        harness.Orchestrator.SlowWorkItemThreshold = pastThreshold ? TimeSpan.Zero : TimeSpan.FromHours(1);
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 101);
        harness.Importer.Known.Add(anchorRoot);

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[0])));
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.ComputeHeadCalls, Is.EqualTo(1), "fixture: one head step ends the pass");
            Assert.That(logger.LogList.Where(static l => l.StartsWith("Import worker spent") && l.EndsWith(" ms on the head step")).ToArray(), Has.Length.EqualTo(pastThreshold ? 1 : 0));
        }
    }

    [Test]
    public async Task Slot_tick_releases_a_gossip_block_held_for_its_slot()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, WallSlot + 1);
        harness.Importer.Known.Add(anchorRoot);
        harness.Orchestrator.RouteGossipEvents();

        harness.Router.Handle(GossipTopics.BeaconBlock, gloasTopic: false, Snappy.CompressToArray(SignedBeaconBlock.Encode(chain[0])));
        harness.Timestamper.Set(DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + (WallSlot + 1) * Spec.SecondsPerSlot));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(new[] { WallSlot + 1 }), "no later gossip message is needed to release the held block");
    }

    [Test]
    public async Task Metrics_replay_imports_canonical_store_blocks_without_network_and_stops_at_a_linkage_break()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 101, 102, 103, 104, 105);
        TestChain.Persist(store, anchor, anchorRoot, chain);
        // A stale canonical entry that does not link to slot 105 must stop the replay.
        SignedBeaconBlock stale = TestChain.CreateBlock(106, TestItem.KeccakA);
        store.PutBlock(SszRoots.HashTreeRoot(stale.Message!), stale);
        store.SetCanonicalRoot(106, SszRoots.HashTreeRoot(stale.Message!));

        Harness harness = CreateHarness(store: store);
        harness.Importer.Known.Add(anchorRoot);

        ulong before = Metrics.BeaconChainBlocksImported;
        Metrics.BeaconChainLastBlockImportMs = -1;
        await harness.Orchestrator.ReplayStoredBlocksAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, 102, 103, 104, 105]), "all linked canonical blocks replayed");
            Assert.That(harness.Importer.Imports.Select(static i => i.VerifySignatures), Is.All.False, "store replays skip signature verification");
            Assert.That(Metrics.BeaconChainBlocksImported, Is.EqualTo(before + 5));
            Assert.That(Metrics.BeaconChainLastBlockImportMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(harness.Pool.GetBestPeersCalls, Is.Zero, "the network was not touched");
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(105UL), "sync tip resumes at the replayed head");
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "a head step follows the replay");
        }
    }

    /// <summary>Gossip topics exist only once the libp2p host has started; a replayed head near the wall clock that finds none must wait for it to start gossip.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_replayed_head_near_the_wall_clock_starts_gossip_once_the_libp2p_host_has_started(CancellationToken token)
    {
        const ulong NearHeadAnchorSlot = WallSlot - 10;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(NearHeadAnchorSlot, NearHeadAnchorSlot + 1, NearHeadAnchorSlot + 2);
        TestChain.Persist(store, anchor, anchorRoot, chain);
        PeerBandTests.Node node = PeerBandTests.CreateNode();
        await using BeaconP2P p2p = node.P2P;
        Harness harness = CreateHarness(anchorSlot: NearHeadAnchorSlot, store: store, p2p: p2p);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, NearHeadAnchorSlot + 2, finalizedEpoch: Spec.GetEpoch(NearHeadAnchorSlot));

        await harness.Orchestrator.ReplayStoredBlocksAsync(token);
        bool startedBeforeHost = harness.Orchestrator.GossipStarted;
        await p2p.StartAsync(token);
        await harness.Orchestrator.RunHeadStepAsync(token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(NearHeadAnchorSlot + 2), "the stored blocks replayed");
            Assert.That(startedBeforeHost, Is.False, "gossip topics cannot be subscribed before the host starts");
            Assert.That(harness.Orchestrator.GossipStarted, Is.True, "the first head step after the host starts starts gossip");
        }
    }

    /// <summary>
    /// A restart replays the stored blocks one by one, which took ten minutes at mainnet size. The libp2p host and discovery start
    /// before it, so peers connect and the discovery table fills while it runs instead of after it.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Networking_starts_before_the_stored_block_replay(CancellationToken testToken)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, AnchorSlot + 1);
        TestChain.Persist(store, anchorBlock, anchorRoot, chain);
        // Not built ahead as CreateDiscovery does, so it has a custody only once started.
        await using BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        PeerBandTests.Node node = PeerBandTests.CreateNode();
        await using BeaconP2P p2p = node.P2P;
        Harness harness = CreateHarness(store: store, discovery: discovery, p2p: p2p, peerManager: new PeerManager(p2p, node.Config, node.StatusHolder, LimboLogs.Instance));
        harness.Importer.Known.Add(anchorRoot);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        (bool Listening, bool Discovering)? duringReplay = null;
        harness.Importer.OnImported = (_, _) =>
        {
            duringReplay = (p2p.LocalPeerId is not null, (LocalCustody?)discovery.LocalCustody is not null);
            cts.Cancel();
        };

        Assert.CatchAsync<OperationCanceledException>(() => harness.Orchestrator.RunAsync(new ForkedBeaconState.OfFulu(new BeaconStateFulu()), new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot, cts.Token));

        Assert.That(duringReplay, Is.EqualTo((true, true)), "the stored block was replayed with the host listening and discovery running");
    }

    /// <summary>
    /// Following gossip, a head more than two epochs behind the wall clock that has not advanced for an epoch restarts range sync
    /// from the head, moving the sync tip off a block the head is not on. A head within that distance, which still counts as
    /// following gossip, a head still advancing, as in a catch-up, and a node not yet following gossip leave the tip alone.
    /// </summary>
    [TestCase(65UL, 32UL, false, true, ExpectedResult = true)]
    [TestCase(65UL, 31UL, false, true, ExpectedResult = false)]
    [TestCase(65UL, 32UL, true, true, ExpectedResult = false)]
    [TestCase(65UL, 32UL, false, false, ExpectedResult = false)]
    [TestCase(64UL, 32UL, false, true, ExpectedResult = false)]
    [TestCase(10UL, 32UL, false, true, ExpectedResult = false)]
    public async Task<bool> A_head_left_behind_the_wall_clock_moves_the_sync_tip_back_to_it(ulong slotsBehind, ulong slotsSinceEarlierTick, bool headAdvanced, bool followingGossip)
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        harness.Importer.Known.Add(anchorRoot);
        harness.Orchestrator.GossipStarted = followingGossip;
        await harness.Orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None);
        (Hash256 Root, ulong Slot) tipOffTheHead = harness.Orchestrator.SyncTip;
        ulong headSlot = WallSlot - slotsBehind;
        harness.Importer.Head = CreateHead(TestItem.KeccakA, headAdvanced ? headSlot - 1 : headSlot, finalizedEpoch: Spec.GetEpoch(AnchorSlot));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot - slotsSinceEarlierTick, CancellationToken.None);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, headSlot, finalizedEpoch: Spec.GetEpoch(AnchorSlot));

        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        Assert.That(tipOffTheHead.Slot, Is.EqualTo(150UL));
        bool restarted = harness.Orchestrator.SyncTip == (TestItem.KeccakA, headSlot);
        Assert.That(restarted || harness.Orchestrator.SyncTip == tipOffTheHead, Is.True, "the tip is either left alone or moved to the head");
        return restarted;
    }

    /// <summary>Each restart ends the round in flight, so a head that stays behind restarts range sync once an epoch, not on every slot tick.</summary>
    [Test]
    public async Task A_head_left_behind_restarts_range_sync_at_most_once_an_epoch()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150, 151, 152, 153);
        harness.Importer.Known.Add(anchorRoot);
        harness.Orchestrator.GossipStarted = true;
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot, finalizedEpoch: Spec.GetEpoch(AnchorSlot));
        List<ulong> tipsAfterTicks = [];
        // The first tick only observes the head; it has been stuck for an epoch from the second on.
        foreach ((SignedBeaconBlock block, ulong tick) in new[] { (chain[0], WallSlot), (chain[1], WallSlot + Spec.SlotsPerEpoch), (chain[2], WallSlot + 2 * Spec.SlotsPerEpoch - 1), (chain[3], WallSlot + 2 * Spec.SlotsPerEpoch) })
        {
            // A block imported off the head moves the tip, which only a restart moves back.
            await harness.Orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(block), CancellationToken.None);
            await harness.Orchestrator.ProcessSlotAsync(tick, CancellationToken.None);
            tipsAfterTicks.Add(harness.Orchestrator.SyncTip.Slot);
        }

        Assert.That(tipsAfterTicks, Is.EqualTo(new[] { 150UL, AnchorSlot, 152UL, AnchorSlot }));
    }

    /// <summary>
    /// A retry that expires restarts range sync only when the head waits on it, as its child; any other, such as a side-fork block,
    /// leaves the round in flight running.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task An_expiring_retry_restarts_range_sync_only_when_it_is_a_child_of_the_head([Values] bool childOfHead, CancellationToken token)
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(block.ComputeMessageRoot());
        harness.Importer.Head = CreateHead(childOfHead ? anchorRoot : TestItem.KeccakA, AnchorSlot, finalizedEpoch: Spec.GetEpoch(AnchorSlot));
        Assert.That(await harness.Orchestrator.ImportBlockAsync(block, token), Is.EqualTo(BlockImportResult.DataUnavailable));

        // No peer is ahead of the tip, so the round waits for one until it is ended.
        using CancellationTokenSource stopRound = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task round = harness.Orchestrator.FeedRangeSyncRoundAsync(stopRound.Token);
        ulong expirySlot = WallSlot + 2 * Spec.SlotsPerEpoch + 1;
        harness.Timestamper.Set(SlotStart(expirySlot));
        await harness.Orchestrator.ProcessSlotAsync(expirySlot, token);
        bool roundEnded = await Task.WhenAny(round, Task.Delay(TimeSpan.FromSeconds(childOfHead ? 5 : 1), token)) == round;
        await stopRound.CancelAsync();

        Assert.That(roundEnded, Is.EqualTo(childOfHead));
        Assert.That(await EndsAsync(round, token), Is.True);
    }

    /// <summary>
    /// A block deferred for its data is routine at the head and is not worth a warning each attempt; one whose data never
    /// arrived within the retry window is, since the node gives up on it.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task Only_a_block_whose_data_never_arrived_is_warned_about(CancellationToken token)
    {
        Nethermind.Core.Test.TestLogger logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(block.ComputeMessageRoot());
        await harness.Orchestrator.ImportBlockAsync(block, token);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, token);
        int warningsWhileWaiting = logger.LogList.Count(static l => l.StartsWith("Dropping block"));

        ulong expirySlot = WallSlot + 2 * Spec.SlotsPerEpoch + 1;
        harness.Timestamper.Set(SlotStart(expirySlot));
        await harness.Orchestrator.ProcessSlotAsync(expirySlot, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(warningsWhileWaiting, Is.Zero);
            Assert.That(logger.LogList.Count(l => l.StartsWith($"Dropping block {block.ComputeMessageRoot()}")), Is.EqualTo(1));
        }
    }

    /// <summary>A restart also ends the wait between rounds, so the round from the head starts at once, not a slot later.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_sync_restart_starts_the_next_round_without_waiting_out_the_slot(CancellationToken token)
    {
        RangeSyncTests.StubPeer server = new("server", WallSlot, static (_, _) => []);
        // The tip is at the wall clock, so the first round ends at once and the feed waits a slot before the next.
        Harness harness = CreateHarness(wallSlot: AnchorSlot, peers: [server]);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot, finalizedEpoch: Spec.GetEpoch(AnchorSlot));
        // Observes the head, which then has not advanced for the epochs until the restart's tick.
        await harness.Orchestrator.ProcessSlotAsync(AnchorSlot, token);
        using CancellationTokenSource stopFeed = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task feed = harness.Orchestrator.RunRangeSyncFeedAsync(stopFeed.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300), token);
        int requestsWhileWaiting = server.Requests;

        harness.Timestamper.Set(SlotStart(WallSlot));
        harness.Orchestrator.GossipStarted = true;
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, token);
        TimeSpan bound = TimeSpan.FromSeconds(Spec.SecondsPerSlot / 2.0);
        Stopwatch sinceRestart = Stopwatch.StartNew();
        while (server.Requests == 0 && sinceRestart.Elapsed < bound)
        {
            await Task.Delay(10, token);
        }

        TimeSpan untilNextRound = sinceRestart.Elapsed;
        await stopFeed.CancelAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await EndsAsync(feed, token), Is.True);
            Assert.That(requestsWhileWaiting, Is.Zero);
            Assert.That(server.Requests, Is.Positive, "the round from the head started");
            Assert.That(untilNextRound, Is.LessThan(bound));
        }
    }

    /// <summary>Whether <paramref name="task"/> ends, by completing or by cancellation, within a few seconds of a stop request.</summary>
    /// <remarks>A range-sync loop checks its token between awaits, so a stop can end it either way.</remarks>
    internal static async Task<bool> EndsAsync(Task task, CancellationToken token)
    {
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5), token));
        return task.IsCompletedSuccessfully || task.IsCanceled;
    }

    private static Harness CreateHarness(
        ulong anchorSlot = AnchorSlot,
        ulong wallSlot = WallSlot,
        BeaconChainStore? store = null,
        IBeaconSyncPeer[]? peers = null,
        DataColumnSidecarPool? sidecarPool = null,
        BeaconDiscovery? discovery = null,
        GossipRouter? router = null,
        ILogManager? logManager = null,
        BeaconP2P? p2p = null,
        PeerManager? peerManager = null,
        bool filterPoolByHead = false,
        HeadSnapshotHolder? headSnapshots = null)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + wallSlot * Spec.SecondsPerSlot).AddSeconds(6);
        ManualTimestamper timestamper = new(now);
        SlotClock slotClock = new(Spec, timestamper);
        store ??= new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>());
        ScriptedImporter importer = new() { Head = CreateHead(TestItem.KeccakA, anchorSlot, finalizedEpoch: Spec.GetEpoch(anchorSlot)) };
        ScriptedEngine engine = new();
        StubPool pool = new(peers ?? [], filterPoolByHead);
        sidecarPool ??= new DataColumnSidecarPool();
        router ??= new GossipRouter(Spec, slotClock, LimboLogs.Instance);
        BeaconChainStatusHolder statusHolder = new(Spec, timestamper);
        ExecutionPayloadEnvelopePool envelopePool = new();
        BeaconSyncOrchestrator orchestrator = new(
            new BeaconChainConfig(),
            Spec,
            store,
            new ScriptedFactory(importer),
            engine,
            pool,
            new RangeSync(pool, LimboLogs.Instance, sidecarPool, Spec, RangeSyncTests.ClockAtGenesis(Spec), discovery),
            slotClock,
            router,
            statusHolder,
            logManager ?? LimboLogs.Instance,
            p2p: p2p,
            peerManager: peerManager,
            discovery: discovery,
            columnPool: sidecarPool,
            envelopePool: envelopePool,
            headSnapshots: headSnapshots);

        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(anchorSlot);
        orchestrator.PtcReader = (_, slot) => importer.Ptc(slot);
        orchestrator.Initialize(importer, new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot);
        return new Harness(orchestrator, importer, engine, pool, router, statusHolder, timestamper, envelopePool);
    }

    private static HeadView CreateHead(Hash256 root, ulong slot, ulong finalizedEpoch, Hash256? execHash = null) => new(
        root,
        slot,
        execHash ?? TestItem.KeccakG,
        TestItem.KeccakH,
        TestItem.KeccakE,
        new CheckpointRef(finalizedEpoch + 1, TestItem.KeccakC),
        new CheckpointRef(finalizedEpoch, TestItem.KeccakD));

    private sealed record Harness(
        BeaconSyncOrchestrator Orchestrator,
        ScriptedImporter Importer,
        ScriptedEngine Engine,
        StubPool Pool,
        GossipRouter Router,
        BeaconChainStatusHolder StatusHolder,
        ManualTimestamper Timestamper,
        ExecutionPayloadEnvelopePool EnvelopePool);

    private sealed class ScriptedFactory(IBlockImporter importer) : IBlockImporterFactory
    {
        public IBlockImporter Create(ForkedBeaconState anchorState, ForkedSignedBeaconBlock anchorBlock, Hash256 anchorRoot) => importer;
    }

    private sealed class ScriptedImporter : IBlockImporter
    {
        public HashSet<Hash256> Known { get; } = [];

        /// <summary>Block roots for which <see cref="Import"/> answers <see cref="BlockImportResult.DataUnavailable"/> instead of importing.</summary>
        public HashSet<Hash256> Unavailable { get; } = [];

        /// <summary>Block roots whose known parent's state the regeneration budget refuses, so <see cref="Import"/> answers <see cref="BlockImportResult.UnknownParent"/>.</summary>
        public HashSet<Hash256> RegenerationRefused { get; } = [];

        /// <summary>Block roots whose known parent's state cannot be had for good, so <see cref="Import"/> answers <see cref="BlockImportResult.UnknownParent"/> with no refusal cause.</summary>
        public HashSet<Hash256> RegenerationImpossible { get; } = [];

        /// <summary>Block roots this node's own fork-choice admission refuses, so <see cref="Import"/> answers <see cref="BlockImportResult.Invalid"/> for <see cref="ImportRefusal.LocalAdmission"/>.</summary>
        public HashSet<Hash256> AdmissionRefused { get; } = [];

        public ImportRefusal LastRefusal { get; private set; }

        /// <summary>Fulu block signatures that fail, so a copy of a block under another signature answers <see cref="BlockImportResult.Invalid"/>.</summary>
        public HashSet<BlsSignature> ForgedSignatures { get; } = [];

        /// <summary>Block roots whose proposer signature fails, so <see cref="Import"/> answers <see cref="BlockImportResult.Invalid"/>.</summary>
        public HashSet<Hash256> Forged { get; } = [];

        /// <summary>Block roots for which <see cref="Import"/> answers <see cref="BlockImportResult.EngineUnavailable"/>.</summary>
        public HashSet<Hash256> EngineDown { get; } = [];

        /// <summary>Block roots for which <see cref="Import"/> answers <see cref="BlockImportResult.FutureSlot"/> until a slot tick reaches their slot.</summary>
        public HashSet<Hash256> Early { get; } = [];

        /// <summary>Parent roots whose payload is unverified, so a child <see cref="Import"/> answers <see cref="BlockImportResult.ParentPayloadUnverified"/> until <see cref="ImportEnvelope"/> records it.</summary>
        public HashSet<Hash256> UnverifiedPayloads { get; } = [];

        /// <summary>The verdict <see cref="ImportEnvelope"/> answers; a recording verdict removes the root from <see cref="UnverifiedPayloads"/>.</summary>
        public ExecutionPayloadEnvelopeImportResult EnvelopeResult { get; set; } = ExecutionPayloadEnvelopeImportResult.Valid;

        /// <summary>When set, answers <see cref="ImportEnvelope"/> in place of <see cref="EnvelopeResult"/>.</summary>
        public Func<SignedExecutionPayloadEnvelope, ExecutionPayloadEnvelopeImportResult>? EnvelopeVerdict { get; set; }

        public List<Hash256> Envelopes { get; } = [];

        /// <summary>Every block and envelope import in call order, each by the block root it names.</summary>
        public List<(bool Envelope, Hash256 Root)> ImportOrder { get; } = [];

        /// <summary>Whether each block and envelope import ran on a thread-pool thread, in call order.</summary>
        public List<bool> ImportedOnPoolThread { get; } = [];

        public List<object> GossipOperations { get; } = [];

        /// <summary>The count of <see cref="Ticks"/> when each of <see cref="GossipOperations"/> arrived.</summary>
        public List<int> TicksAtGossipOperations { get; } = [];

        /// <summary>Runs as each slot tick arrives, after it is recorded.</summary>
        public Action<ulong>? OnTick { get; set; }

        private readonly HashSet<Hash256> _deferred = [];

        public List<(ulong Slot, Hash256 Root, bool VerifySignatures)> Imports { get; } = [];
        public List<ulong> Ticks { get; } = [];
        public List<(Hash256 Root, Hash256 ExecutionHash, string Status, Hash256? LatestValidHash)> ForkchoiceVerdicts { get; } = [];
        public List<CheckpointRef> Finalizations { get; } = [];
        public required HeadView Head { get; set; }
        public HeadView? HeadAfterInvalidation { get; set; }
        public bool ExpectedProposer { get; set; } = true;
        public int ComputeHeadCalls { get; private set; }

        public bool IsKnown(Hash256 blockRoot) => Known.Contains(blockRoot);

        public bool IsExpectedProposer(ForkedSignedBeaconBlock block) => ExpectedProposer;

        /// <summary>The roots imported as blocks this node requested, once per attempt.</summary>
        public List<Hash256> RequestedImports { get; } = [];

        /// <summary>The roots of <see cref="RequestedImports"/> fetched by root, once per attempt.</summary>
        public List<Hash256> ByRootImports { get; } = [];

        public BlockImportResult ImportRequested(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool fetchedByRoot = false)
        {
            RequestedImports.Add(blockRoot);
            if (fetchedByRoot) ByRootImports.Add(blockRoot);
            return Import(block, blockRoot, verifySignatures: true);
        }

        public BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures)
        {
            Imports.Add((block.Slot, blockRoot, verifySignatures));
            LastRefusal = ImportRefusal.None;
            ImportOrder.Add((false, blockRoot));
            ImportedOnPoolThread.Add(Thread.CurrentThread.IsThreadPoolThread);
            _deferred.Remove(blockRoot);
            if (Known.Contains(blockRoot)) return BlockImportResult.AlreadyKnown;
            if (!Known.Contains(block.ParentRoot)) return _deferred.Contains(block.ParentRoot) ? Defer(blockRoot) : BlockImportResult.UnknownParent;
            if (AdmissionRefused.Contains(blockRoot))
            {
                LastRefusal = ImportRefusal.LocalAdmission;
                return BlockImportResult.Invalid;
            }

            if (RegenerationImpossible.Contains(blockRoot)) return BlockImportResult.UnknownParent;
            if (RegenerationRefused.Contains(blockRoot))
            {
                LastRefusal = ImportRefusal.RegenerationBudget;
                return BlockImportResult.UnknownParent;
            }

            if (UnverifiedPayloads.Contains(block.ParentRoot)) return Defer(blockRoot);
            if (Unavailable.Contains(blockRoot)) return BlockImportResult.DataUnavailable;
            if (Forged.Contains(blockRoot)) return BlockImportResult.Invalid;
            if (block is ForkedSignedBeaconBlock.OfFulu { Block.Signature: var signature } && ForgedSignatures.Contains(signature)) return BlockImportResult.Invalid;
            if (EngineDown.Contains(blockRoot)) return BlockImportResult.EngineUnavailable;
            if (Early.Contains(blockRoot) && (Ticks.Count == 0 || Ticks[^1] < block.Slot)) return BlockImportResult.FutureSlot;
            Known.Add(blockRoot);
            OnImported?.Invoke(block, blockRoot);
            return BlockImportResult.Imported;
        }

        /// <summary>Runs on the import thread as each block imports, after it is known.</summary>
        public Action<ForkedSignedBeaconBlock, Hash256>? OnImported { get; set; }

        /// <summary>As the real importer: a signed block is deferred, and a child of a deferred block is deferred too.</summary>
        private BlockImportResult Defer(Hash256 blockRoot)
        {
            if (Forged.Contains(blockRoot)) return BlockImportResult.Invalid;
            _deferred.Add(blockRoot);
            return BlockImportResult.ParentPayloadUnverified;
        }

        public ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope)
        {
            Hash256 blockRoot = envelope.Message!.BeaconBlockRoot!;
            Envelopes.Add(blockRoot);
            ImportOrder.Add((true, blockRoot));
            ImportedOnPoolThread.Add(Thread.CurrentThread.IsThreadPoolThread);
            ExecutionPayloadEnvelopeImportResult result = EnvelopeVerdict?.Invoke(envelope) ?? EnvelopeResult;
            if (result is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic)
            {
                UnverifiedPayloads.Remove(blockRoot);
            }

            return result;
        }

        public void OnSlotTick(ulong slot)
        {
            Ticks.Add(slot);
            OnTick?.Invoke(slot);
        }

        public Action? OnComputeHead { get; set; }

        public HeadView ComputeHead()
        {
            ComputeHeadCalls++;
            OnComputeHead?.Invoke();
            return Head;
        }

        public void OnForkchoiceUpdated(Hash256 headRoot, Hash256 headExecutionHash, PayloadStatusV1 status)
        {
            ForkchoiceVerdicts.Add((headRoot, headExecutionHash, status.Status, status.LatestValidHash));
            if (status.Status == PayloadStatus.Invalid)
                Head = HeadAfterInvalidation ?? Head;
        }

        public void OnFinalized(CheckpointRef finalized) => Finalizations.Add(finalized);

        public bool OnGossipAggregate(SignedAggregateAndProof aggregate) => Consume(aggregate);

        public bool OnGossipAggregate(SignedAggregateAndProofGloas aggregate) => Consume(aggregate);

        /// <summary>Whether fork choice accepts each gossip slashing and payload attestation, as a verified signature would.</summary>
        public bool AcceptsGossipOperations { get; set; } = true;

        public bool OnGossipAttesterSlashing(AttesterSlashing slashing) => Consume(slashing);

        public bool OnGossipAttesterSlashing(AttesterSlashingGloas slashing) => Consume(slashing);

        public bool OnGossipPayloadAttestation(PayloadAttestationMessage message) => Consume(message);

        /// <summary>The committee of a slot as the head state answers it; <c>null</c> is a head state that cannot tell.</summary>
        public Func<ulong, ulong[]?> Ptc { get; set; } = static _ => EveryValidator;

        private bool Consume(object operation)
        {
            GossipOperations.Add(operation);
            TicksAtGossipOperations.Add(Ticks.Count);
            return AcceptsGossipOperations;
        }
    }

    private sealed class ScriptedEngine : IEngineDriver
    {
        public Queue<PayloadStatusV1> FcuResponses { get; } = new();
        public List<(Hash256 Head, Hash256 Safe, Hash256 Finalized)> FcuCalls { get; } = [];

        public SignedBeaconBlock? CurrentBlock { get; set; }

        public Action? OnCall { get; set; }

        public int FailingFcuCalls { get; set; }
        public bool IsAvailable { get; private set; } = true;
        public Exception? FcuFailure { get; set; }
        public Task<PayloadStatusV1>? FcuAnswer { get; set; }

        public Task<PayloadStatusV1>? PendingFcu { get; set; }

        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
        {
            FcuCalls.Add((headExecHash, safeExecHash, finalizedExecHash));
            OnCall?.Invoke();
            if (FailingFcuCalls > 0)
            {
                FailingFcuCalls--;
                IsAvailable = false;
                throw FcuFailure ?? new EngineUnavailableException("forkchoiceUpdatedV3", "engine unavailable");
            }

            IsAvailable = true;
            return PendingFcu ?? FcuAnswer ?? Task.FromResult(FcuResponses.Count > 0 ? FcuResponses.Dequeue() : PayloadStatusV1.Syncing);
        }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }

    private sealed class StubPool(IBeaconSyncPeer[] peers, bool filterByHead = false) : IBeaconSyncPeerPool
    {
        private readonly Lock _lock = new();
        private readonly List<ulong> _statusRefreshSlots = [];
        private ulong? _refreshedUpTo;

        public int GetBestPeersCalls { get; private set; }

        /// <summary>Offered first for any slot up to the latest status refresh, as <see cref="PeerManager"/> offers peers once a refresh shows them ahead or fails.</summary>
        public IBeaconSyncPeer[] OfferedAfterRefresh { get; set; } = [];

        public ulong[] StatusRefreshSlots
        {
            get
            {
                lock (_lock)
                {
                    return [.. _statusRefreshSlots];
                }
            }
        }

        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot)
        {
            GetBestPeersCalls++;
            IBeaconSyncPeer[] known = filterByHead ? [.. peers.Where(p => p.HeadSlot >= minHeadSlot)] : peers;
            lock (_lock)
            {
                return _refreshedUpTo >= minHeadSlot ? [.. OfferedAfterRefresh, .. known] : known;
            }
        }

        public void RefreshStatusesBehind(ulong slot, string reason)
        {
            lock (_lock)
            {
                _statusRefreshSlots.Add(slot);
                _refreshedUpTo = Math.Max(_refreshedUpTo ?? 0, slot);
            }
        }
    }

    private sealed class FakeTopic : ITopic
    {
        public event Action<byte[]>? OnMessage { add { } remove { } }

        public bool IsSubscribed { get; private set; }

        public void Subscribe() => IsSubscribed = true;

        public void Unsubscribe() => IsSubscribed = false;

        public void Publish(byte[] value) { }

        public void Publish(IMessage value) { }
    }
}
