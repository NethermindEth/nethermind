// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Net;
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
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;
using FakeTopic = Nethermind.BeaconChain.Test.P2P.Gossip.GossipDigestWindowTests.RecordingTopic;

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

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[2])));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[0])));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[1])));
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, 102, 103]), "import order");
        Assert.That(harness.Importer.Imports.Select(static i => i.VerifySignatures), Is.All.True, "network blocks verify signatures");
        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "one head FCU per drained batch");
        Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(103UL), "sync tip follows imports");
        Assert.That(harness.StatusHolder.CurrentStatus.HeadRoot, Is.EqualTo(harness.Importer.Head.HeadRoot), "status holder refreshed by the head step");
    }

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

        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports, Has.Count.EqualTo(chain.Length), "fixture: every block imports in one worker pass");
            Assert.That(harness.Engine.FcuCalls.Skip(fcusBefore).Select(static call => call.Head),
                Is.EqualTo(new ulong[] { 102, 104, 106, 108, 110 }.Select(ExecutionHashOf)), "the head of each second of imports, in order");
        }

        static Hash256 ExecutionHashOf(ulong slot) => Keccak.Compute(BitConverter.GetBytes(slot));
    }

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(false, true)]
    [CancelAfter(10_000)]
    public async Task Gossip_progress_supersedes_a_range_round_on_an_old_fork_without_blaming_its_peer(bool honorsCancellation, bool delayPropagation, CancellationToken token)
    {
        (_, Hash256 anchorRoot, _) = TestChain.BuildLinkedChain(AnchorSlot);
        ForkedSignedBeaconBlock oldTip = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(101, anchorRoot));
        ForkedSignedBeaconBlock newTip = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(102, anchorRoot));
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(103, newTip.ComputeMessageRoot()));
        TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource propagationBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releasePropagation = new();
        CancellationTokenRegistration propagationGate = default;
        CancellationToken requestToken = default;
        TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.Id.Returns("canonical");
        peer.HeadSlot.Returns(104UL);
        List<(ulong Start, ulong Count)> requests = [];
        List<PeerFailureReason> failures = [];
        peer.RequestBlocksByRangeAsync(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            requestToken = call.ArgAt<CancellationToken>(2);
            requests.Add((call.ArgAt<ulong>(0), call.ArgAt<ulong>(1)));
            requested.TrySetResult();
            await reply.Task.WaitAsync(honorsCancellation ? call.ArgAt<CancellationToken>(2) : token);
            return (IReadOnlyList<ForkedSignedBeaconBlock>)[newTip, child];
        });
        peer.When(p => p.ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string>())).Do(call =>
        {
            failures.Add(call.ArgAt<PeerFailureReason>(0));
            reported.TrySetResult();
        });
        Harness harness = CreateHarness(wallSlot: 104, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.OnImported = (block, root) => harness.Importer.Head = CreateHead(root, block.Slot, Spec.GetEpoch(AnchorSlot));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(oldTip));
        await harness.Orchestrator.ProcessQueuedAsync(token);
        Assert.That(harness.Orchestrator.SyncTip.Root, Is.EqualTo(oldTip.ComputeMessageRoot()));
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task round = harness.Orchestrator.FeedRangeSyncRoundAsync(stop.Token);
        try
        {
            await requested.Task.WaitAsync(token);
            Assert.That(requests, Is.EqualTo(new[] { (102UL, 3UL) }), "the live round captured the old fork");
            if (delayPropagation)
            {
                CancellationTokenSource restart = (CancellationTokenSource)typeof(BeaconSyncOrchestrator)
                    .GetField("_rangeSyncRestart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(harness.Orchestrator)!;
                // Registered after the linked round token, so LIFO callbacks hold its propagation.
                propagationGate = restart.Token.Register(() =>
                {
                    propagationBlocked.TrySetResult();
                    releasePropagation.Wait(TimeSpan.FromSeconds(5));
                });
            }
            harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(newTip));
            harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(child));
            await harness.Orchestrator.ProcessQueuedAsync(token);
            Assert.That(harness.Orchestrator.SyncTip.Root, Is.EqualTo(child.ComputeMessageRoot()));
            Assert.That(harness.Importer.Head.HeadRoot, Is.EqualTo(child.ComputeMessageRoot()));
            if (delayPropagation)
            {
                await propagationBlocked.Task.WaitAsync(token);
                Assert.That(requestToken.IsCancellationRequested, Is.False, "the parent generation ended before its linked request token observes cancellation");
            }
            reply.TrySetResult();
            await Task.WhenAny(round, reported.Task).WaitAsync(token);
            Assert.That(failures, Is.Empty, "the canonical peer must not be blamed for disagreeing with the superseded fork");
            Assert.That(await EndsAsync(round, token), Is.True, "gossip progress retires the old round rather than retrying its stale anchor");
        }
        finally
        {
            releasePropagation.Set();
            propagationGate.Dispose();
            await stop.CancelAsync();
            reply.TrySetResult();
            try
            {
                await round;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [CancelAfter(10_000)]
    public async Task Higher_gossip_only_retires_the_range_for_tip_continuity_or_the_selected_head(bool selectedHead, bool linked, CancellationToken token)
    {
        (_, Hash256 anchorRoot, _) = TestChain.BuildLinkedChain(AnchorSlot);
        ForkedSignedBeaconBlock tip = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(101, anchorRoot));
        ForkedSignedBeaconBlock gossip = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(103, linked ? tip.ComputeMessageRoot() : anchorRoot));
        ForkedSignedBeaconBlock canonical = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(102, tip.ComputeMessageRoot()));
        TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.HeadSlot.Returns(104UL);
        peer.RequestBlocksByRangeAsync(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            requestToken = call.ArgAt<CancellationToken>(2);
            requested.TrySetResult();
            await reply.Task.WaitAsync(requestToken);
            return (IReadOnlyList<ForkedSignedBeaconBlock>)[];
        });
        Harness harness = CreateHarness(wallSlot: 104, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.OnImported = (block, root) =>
        {
            if (root != gossip.ComputeMessageRoot() || selectedHead || linked)
                harness.Importer.Head = CreateHead(root, block.Slot, Spec.GetEpoch(AnchorSlot));
        };
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(tip));
        await harness.Orchestrator.ProcessQueuedAsync(token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task round = harness.Orchestrator.FeedRangeSyncRoundAsync(stop.Token);
        try
        {
            await requested.Task.WaitAsync(token);
            int headSteps = harness.Importer.ComputeHeadCalls;
            harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(gossip));
            await harness.Orchestrator.ProcessQueuedAsync(token);
            bool superseded = selectedHead || linked;
            if (superseded) Assert.That(await EndsAsync(round, token), Is.True);
            Assert.That(harness.Orchestrator.SyncTip.Root, Is.EqualTo((superseded ? gossip : tip).ComputeMessageRoot()));
            Assert.That(requestToken.IsCancellationRequested, Is.EqualTo(superseded));
            Assert.That(harness.Importer.ComputeHeadCalls, Is.EqualTo(headSteps + 1), "the scheduled head step remains the only fork-choice computation");
            if (!superseded)
            {
                harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(canonical, peer, requestToken));
                await harness.Orchestrator.ProcessQueuedAsync(token);
                Assert.That(harness.Importer.Known, Does.Contain(canonical.ComputeMessageRoot()), "the active canonical round's queued blocks remain importable");
                Assert.That(harness.Orchestrator.SyncTip.Root, Is.EqualTo(canonical.ComputeMessageRoot()));
                Assert.That(requestToken.IsCancellationRequested, Is.False);
            }
        }
        finally
        {
            stop.Cancel();
            reply.TrySetResult();
            try { await round; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    public enum RangeRejection
    {
        AtImport,
        OnRetry,
        FutureOnRetry,
        HeldChildOnRetry,
    }

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
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

        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Ticks.Skip(ticksBefore), Is.EqualTo((ulong[])[WallSlot + 6]), "only the newest tick reaches fork choice");
        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(fcusBefore + 1), "one forkchoiceUpdated for the whole backlog");
    }

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
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(lines, Has.Length.EqualTo(1));
        Assert.That(lines[0], Does.Match(@"slot 120 \(\+20 slots, 2\.0 blocks/s, 0 ms/block of which newPayload \d+ ms\), 80 behind wall slot 200"));
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
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

    public enum ExecutionSend
    {
        Unchanged,
        Resend,
        Changed,
        PayloadFlip,
        Anchor,
        FailedSend,
        FailedHeadUnavailable,
        FailedHeadThrown,
        AnchorUnavailable,
        AnchorThrown,
        Invalid,
    }

    [Test]
    public async Task Execution_head_sends_retry_and_cache_only_answered_noninvalid_states([Values] ExecutionSend scenario)
    {
        Harness harness = CreateHarness();
        bool anchor = scenario is ExecutionSend.Anchor or ExecutionSend.AnchorUnavailable or ExecutionSend.AnchorThrown;
        bool headFailure = scenario is ExecutionSend.FailedHeadUnavailable or ExecutionSend.FailedHeadThrown;
        if (scenario is ExecutionSend.AnchorThrown or ExecutionSend.FailedHeadThrown)
            harness.Engine.FcuFailure = new InvalidOperationException("storage failure");
        if (anchor)
        {
            if (scenario != ExecutionSend.Anchor) harness.Engine.FailingFcuCalls = 1;
            PayloadStatusV1? kick = await harness.Orchestrator.KickExecutionAsync(TestItem.KeccakA);
            Hash256 anchorExecutionHash = harness.Engine.FcuCalls.Single().Head;
            harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot, finalizedEpoch: 0, execHash: anchorExecutionHash) with { JustifiedExecutionHash = null, FinalizedExecutionHash = null };
            await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                if (scenario == ExecutionSend.Anchor)
                    Assert.That(harness.Engine.FcuCalls, Is.EqualTo((List<(Hash256, Hash256, Hash256)>)[(anchorExecutionHash, anchorExecutionHash, anchorExecutionHash)]));
                else
                {
                    Assert.That(kick, Is.Null);
                    Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(2));
                }
            }
            return;
        }
        HeadView initial = CreateHead(TestItem.KeccakA, scenario == ExecutionSend.Invalid ? 103UL : 100UL, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        harness.Importer.Head = initial;
        if (headFailure) harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakB });
        if (scenario == ExecutionSend.FailedSend) harness.Engine.FailingFcuCalls = 1;
        if (scenario == ExecutionSend.Invalid)
        {
            harness.Importer.HeadAfterInvalidation = initial;
            harness.Engine.FcuResponses.Enqueue(PayloadStatusV1.Invalid(TestItem.KeccakF));
            harness.Engine.FcuResponses.Enqueue(PayloadStatusV1.Invalid(TestItem.KeccakF));
        }
        int steps = headFailure || scenario is ExecutionSend.PayloadFlip or ExecutionSend.FailedSend ? 3 : 2;
        for (int step = 0; step < steps; step++)
        {
            if (scenario is ExecutionSend.PayloadFlip or ExecutionSend.FailedSend ||
                step == 1 && scenario is (ExecutionSend.Unchanged or ExecutionSend.Resend or ExecutionSend.Changed))
                harness.Timestamper.Add(TimeSpan.FromSeconds(scenario == ExecutionSend.Resend ? 61 : 1));
            if (step == 1)
            {
                if (scenario == ExecutionSend.PayloadFlip) harness.Importer.Head = initial with { HeadExecutionHash = TestItem.KeccakD };
                else if (headFailure || scenario == ExecutionSend.Changed)
                    harness.Importer.Head = CreateHead(TestItem.KeccakC, 101, finalizedEpoch: 3, execHash: TestItem.KeccakD);
                if (headFailure) harness.Engine.FailingFcuCalls = 1;
            }
            else if (step == 2) harness.Importer.Head = initial;
            await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
            if (headFailure && step == 1)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(harness.StatusHolder.ExecutionInSync, Is.False);
                    Assert.That(Metrics.BeaconChainElInSync, Is.Zero);
                    Assert.That(harness.StatusHolder.CurrentStatus.HeadRoot, Is.EqualTo(TestItem.KeccakC));
                }
            }
        }
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        if (scenario == ExecutionSend.PayloadFlip)
            Assert.That(harness.Engine.FcuCalls.Select(static call => call.Head), Is.EqualTo(new[] { TestItem.KeccakB, TestItem.KeccakD, TestItem.KeccakB }));
        else
        {
            int expectedCalls = scenario == ExecutionSend.Unchanged ? 1 : headFailure || scenario == ExecutionSend.Invalid ? 3 : 2;
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(expectedCalls));
        }
        if (headFailure) Assert.That(harness.StatusHolder.ExecutionInSync, Is.False, "the fresh SYNCING answer is used, not the cached VALID");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_run_cancelled_before_or_during_the_engine_kick_builds_no_cache([Values] bool duringKick, CancellationToken testToken)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        PeerBandTests.Node node = PeerBandTests.CreateNode();
        await using BeaconP2P p2p = node.P2P;
        Harness harness = CreateHarness(discovery: discovery, p2p: p2p, peerManager: new PeerManager(p2p, node.Config, node.StatusHolder, LimboLogs.Instance));
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        int cacheBuilds = 0;
        if (duringKick) harness.Engine.OnCall = cts.Cancel;
        else cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => harness.Orchestrator.RunAsync(new ForkedBeaconState.OfFulu(new BeaconStateFulu()), new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot, cts.Token, () => cacheBuilds++));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(duringKick ? 1 : 0));
        Assert.That(cacheBuilds, Is.Zero);
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
    public async Task Finalized_checkpoint_advance_triggers_on_finalized_exactly_once()
    {
        Harness harness = CreateHarness();
        harness.Importer.Head = CreateHead(TestItem.KeccakA, 100, finalizedEpoch: 4);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None); // baseline head

        CheckpointRef advanced = new(5, TestItem.KeccakB);
        harness.Importer.Head = harness.Importer.Head with { Finalized = advanced };
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);
        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None); // same epoch again

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Finalizations, Is.EqualTo((List<CheckpointRef>)[advanced]), "OnFinalized fired once per advance");
        Assert.That(harness.StatusHolder.CurrentStatus.FinalizedEpoch, Is.EqualTo(5UL), "status advertises the new finality");
    }

    [Test]
    public async Task Slot_tick_advances_fork_choice_runs_fcu_and_rotates_gossip_digest_at_bpo_boundary()
    {
        const ulong Bpo2Epoch = 419_072;
        ulong preRotationSlot = (Bpo2Epoch - 1) * Spec.SlotsPerEpoch + 2;
        Harness harness = CreateHarness(anchorSlot: preRotationSlot - 10, wallSlot: preRotationSlot);
        byte[] bpo1Digest = ForkDigest.Compute(Spec, Bpo2Epoch - 1);
        byte[] bpo2Digest = ForkDigest.Compute(Spec, Bpo2Epoch);

        Dictionary<string, FakeTopic> topics = [];
        harness.Orchestrator.StartGossip(id => topics[id] = new FakeTopic(recordMessages: false));

        await harness.Orchestrator.ProcessSlotAsync(preRotationSlot, CancellationToken.None);
        byte[] digestBeforeBoundary = harness.Orchestrator.CurrentGossipDigest;
        await harness.Orchestrator.ProcessSlotAsync(Bpo2Epoch * Spec.SlotsPerEpoch, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(digestBeforeBoundary, Is.EqualTo(bpo1Digest), "no rotation before the boundary");
        Assert.That(harness.Orchestrator.CurrentGossipDigest, Is.EqualTo(bpo2Digest), "digest rotated at the BPO epoch");
        Assert.That(topics.Keys, Does.Contain(GossipTopics.Topic(bpo2Digest, GossipTopics.BeaconBlock)), "router re-subscribed on the new digest");
        Assert.That(harness.Importer.Ticks, Does.Contain(preRotationSlot).And.Contain(Bpo2Epoch * Spec.SlotsPerEpoch), "fork-choice ticked per slot");
        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "the head FCU is sent once; the unchanged head is not re-sent on the next tick");
    }

    [Test]
    public async Task Gossip_blocks_failing_validation_are_refused()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150, 151);
        harness.Importer.Known.Add(anchorRoot);
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

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

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[160]),
            "only the proposer check needs import to resolve earlier parent and timing checks");
        harness.Importer.Imports.Clear();

        await orchestrator.ImportBlockAsync(new ForkedSignedBeaconBlock.OfFulu(chain[0]), CancellationToken.None);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[150, 151]), "parent imported, then the queued child - nothing else");
    }

    [Test]
    public async Task Gossip_block_with_unavailable_data_retries_only_while_ahead_of_finality([Values] bool finalityPassesBlock)
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

        if (finalityPassesBlock)
        {
            harness.Importer.Head = harness.Importer.Head with { Finalized = new CheckpointRef(6, TestItem.KeccakB) };
        }
        harness.Importer.Unavailable.Remove(blockRoot);
        await orchestrator.ProcessSlotAsync(finalityPassesBlock ? 200UL : 151UL, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Known.Contains(blockRoot), Is.EqualTo(!finalityPassesBlock), "only a block ahead of finality imports once its data arrives");
        if (finalityPassesBlock)
            Assert.That(harness.Importer.Imports.Count(i => i.Root == blockRoot), Is.EqualTo(1), "the pruned entry must not be retried again on a later tick");
    }

    public enum BlockSource
    {
        Gossip,
        RangeSync,
        ByRootBackfill,
        GossipThenFetched,
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    [TestCase(true, true, true)]
    [CancelAfter(10_000)]
    public async Task Failed_queued_range_copy_ends_only_the_rejected_held_chain(bool restoreOldChain, bool alternateCopy, bool rejectAlternate, CancellationToken token)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 102, 103);
        SignedBeaconBlock oldBlock = new()
        {
            Message = TestChain.CreateBlock(101, anchorRoot).Message,
            Signature = new BlsSignature(Enumerable.Repeat((byte)0x22, 96).ToArray())
        };
        ForkedSignedBeaconBlock old = new ForkedSignedBeaconBlock.OfFulu(oldBlock);
        ForkedSignedBeaconBlock replacement = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        Hash256 oldRoot = old.ComputeMessageRoot();
        Hash256 replacementRoot = replacement.ComputeMessageRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfFulu(restoreOldChain ? TestChain.CreateBlock(103, oldRoot) : chain[1]);
        BlsSignature alternateSignature = new(Enumerable.Repeat((byte)0x11, 96).ToArray());
        ForkedSignedBeaconBlock alternate = new ForkedSignedBeaconBlock.OfFulu(new SignedBeaconBlock
        {
            Message = oldBlock.Message,
            Signature = alternateSignature
        });
        IBeaconSyncPeer oldPeer = Substitute.For<IBeaconSyncPeer>();
        IBeaconSyncPeer alternatePeer = Substitute.For<IBeaconSyncPeer>();
        IBeaconSyncPeer currentPeer = Substitute.For<IBeaconSyncPeer>();
        currentPeer.Id.Returns("current");
        currentPeer.HeadSlot.Returns(WallSlot);
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        currentPeer.RequestBlocksByRangeAsync(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            requestStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
            return (IReadOnlyList<ForkedSignedBeaconBlock>)[];
        });
        Harness harness = CreateHarness(peers: [currentPeer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.UnionWith([oldRoot, replacementRoot]);

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(old, oldPeer));
        await harness.Orchestrator.ProcessQueuedAsync(token);
        Assert.That(harness.Orchestrator.RangeHeldSlot, Is.EqualTo(old.Slot), "the old copy first owns the hold");
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(replacement, currentPeer));
        await harness.Orchestrator.ProcessQueuedAsync(token);
        Assert.That(harness.Orchestrator.RangeHeldSlot, Is.EqualTo(replacement.Slot), "B replaces A before the child arrives");
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(child, currentPeer));
        await harness.Orchestrator.ProcessQueuedAsync(token);
        Assert.That(harness.Orchestrator.RangeHeldSlot, Is.EqualTo(child.Slot), "the child holds its own parent's chain");
        if (alternateCopy)
        {
            await harness.Orchestrator.ImportBlockAsync(alternate, token, fetchedByRoot: rejectAlternate, servedBy: rejectAlternate ? alternatePeer : null);
        }
        harness.Importer.RequestedImports.Clear();
        harness.Importer.ByRootImports.Clear();

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task round = harness.Orchestrator.FeedRangeSyncRoundAsync(stop.Token);
        try
        {
            await requestStarted.Task.WaitAsync(token);
            harness.Importer.Unavailable.Remove(oldRoot);
            harness.Importer.ForgedSignatures.Add(oldBlock.Signature!);
            if (rejectAlternate)
            {
                harness.Importer.ForgedSignatures.Add(alternateSignature);
            }
            harness.Importer.EngineDown.Add(oldRoot);
            BlockImportResult result = await harness.Orchestrator.ImportBlockAsync(old, token);
            bool alternateSurvives = alternateCopy && !rejectAlternate;
            bool shouldEnd = restoreOldChain && !alternateSurvives;
            bool ended = shouldEnd ? await EndsAsync(round, token) : await Task.WhenAny(round, Task.Delay(300, token)) == round;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(alternateSurvives ? BlockImportResult.EngineUnavailable : BlockImportResult.Invalid));
                Assert.That(harness.Orchestrator.PendingRetryBlockCount, Is.EqualTo(alternateSurvives ? 2 : 1), "B and any surviving alternate copy remain queued");
                Assert.That(harness.Importer.ByRootImports, Is.EqualTo(rejectAlternate ? new[] { oldRoot } : Array.Empty<Hash256>()), "the promoted fetched copy retains its by-root origin");
                Assert.That(harness.Orchestrator.RangeHeldSlot, Is.EqualTo(shouldEnd ? (ulong?)null : child.Slot), "only rejection of the last copy of the held parent releases its chain");
                Assert.That(harness.Orchestrator.PendingGossipBlockCount, Is.EqualTo(shouldEnd ? 0 : 1), "children survive only while their parent can still import");
                Assert.That(ended, Is.EqualTo(shouldEnd), "restart fetching when the restored parent is rejected, but preserve an unrelated or recoverable chain");
                oldPeer.Received(1).ReportFailure(PeerFailureReason.ProtocolViolation, Arg.Any<string>());
                alternatePeer.Received(rejectAlternate ? 1 : 0).ReportFailure(PeerFailureReason.ProtocolViolation, Arg.Any<string>());
                currentPeer.DidNotReceive().ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string>());
            }

            if (!shouldEnd)
            {
                harness.Importer.Unavailable.Remove(replacementRoot);
                harness.Importer.EngineDown.Remove(oldRoot);
                await harness.Orchestrator.ImportBlockAsync(alternateCopy ? alternate : replacement, token);
                Assert.That(harness.Importer.RequestedImports, Does.Contain(child.ComputeMessageRoot()), "the surviving child retains its range provenance");
            }
        }
        finally
        {
            await stop.CancelAsync();
            Assert.That(await EndsAsync(round, token), Is.True);
        }
    }

    public enum CopyArrival
    {
        ForgeryFirst,
        GenuineFetchedFirst,
        GenuineFromGossipThenFetched,
    }

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(seenAfterForged, Is.False, "a block failing its signature does not mark (slot, proposer)");
        Assert.That(harness.Importer.Known, Does.Contain(SszRoots.HashTreeRoot(real.Message)), "the real block is imported after the forged one");
        Assert.That(harness.Router.IsProposalSeen(150, real.Message.ProposerIndex), "the imported block marks (slot, proposer)");
        Assert.That(harness.Importer.Imports.Select(static i => i.Root), Does.Not.Contain(SszRoots.HashTreeRoot(equivocation.Message)), "a later block for the pair is ignored");
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(seenEarly, Is.True);
        Assert.That(heldSlot, Is.EqualTo(fromRange ? WallSlot + 1 : (ulong?)null));
        Assert.That(harness.Orchestrator.RangeHeldSlot, Is.Null);
        Assert.That(harness.Importer.Known, Does.Contain(root), "the retry set re-imports the block after its slot's tick");
        Assert.That(harness.Importer.Imports.Select(static i => i.Root), Is.EqualTo(new[] { root, root }));
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
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(verdicts, Is.All.EqualTo(MessageValidity.Ignored), "consumed by the router, not forwarded");
        Assert.That(harness.Importer.GossipOperations.Select(static o => o.GetType()), Is.EqualTo(new[] { typeof(SignedAggregateAndProofGloas), typeof(AttesterSlashingGloas) }));
    }

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
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        string[] lines = [.. logger.LogList.Where(static l => l.StartsWith("Import worker spent") && l.Contains(" on a gossip aggregate"))];
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.GossipOperations, Has.Count.EqualTo(1), "fixture: the aggregate reaches fork choice");
        Assert.That(lines, Has.Length.EqualTo(pastThreshold ? 1 : 0));
        if (pastThreshold)
            Assert.That(lines[0], Does.EndWith($" ms on a gossip aggregate for slot {WallSlot} with target epoch {target.Epoch} root {target.Root}"));
    }

    [Test]
    public async Task Head_step_that_holds_the_worker_past_the_threshold_is_logged([Values] bool pastThreshold)
    {
        Nethermind.Core.Test.TestLogger logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));
        harness.Orchestrator.SlowWorkItemThreshold = pastThreshold ? TimeSpan.Zero : TimeSpan.FromHours(1);
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 101);
        harness.Importer.Known.Add(anchorRoot);

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(new ForkedSignedBeaconBlock.OfFulu(chain[0])));
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.ComputeHeadCalls, Is.EqualTo(1), "fixture: one head step ends the pass");
        Assert.That(logger.LogList.Where(static l => l.StartsWith("Import worker spent") && l.EndsWith(" ms on the head step")).ToArray(), Has.Length.EqualTo(pastThreshold ? 1 : 0));
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
        await CompleteWorkerAsync(harness.Orchestrator, CancellationToken.None);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(new[] { WallSlot + 1 }), "no later gossip message is needed to release the held block");
    }

    [Test]
    public async Task Metrics_replay_imports_canonical_store_blocks_without_network_and_stops_at_a_linkage_break()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 101, 102, 103, 104, 105);
        TestChain.Persist(store, anchor, anchorRoot, chain);
        SignedBeaconBlock stale = TestChain.CreateBlock(106, TestItem.KeccakA);
        store.PutBlock(SszRoots.HashTreeRoot(stale.Message!), stale);
        store.SetCanonicalRoot(106, SszRoots.HashTreeRoot(stale.Message!));

        Harness harness = CreateHarness(store: store);
        harness.Importer.Known.Add(anchorRoot);

        ulong before = Metrics.BeaconChainBlocksImported;
        Metrics.BeaconChainLastBlockImportMs = -1;
        await harness.Orchestrator.ReplayStoredBlocksAsync(CancellationToken.None);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, 102, 103, 104, 105]), "all linked canonical blocks replayed");
        Assert.That(harness.Importer.Imports.Select(static i => i.VerifySignatures), Is.All.False, "store replays skip signature verification");
        Assert.That(Metrics.BeaconChainBlocksImported, Is.EqualTo(before + 5));
        Assert.That(Metrics.BeaconChainLastBlockImportMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(harness.Pool.GetBestPeersCalls, Is.Zero, "the network was not touched");
        Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(105UL), "sync tip resumes at the replayed head");
        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "a head step follows the replay");
    }

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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(NearHeadAnchorSlot + 2), "the stored blocks replayed");
        Assert.That(startedBeforeHost, Is.False, "gossip topics cannot be subscribed before the host starts");
        Assert.That(harness.Orchestrator.GossipStarted, Is.True, "the first head step after the host starts starts gossip");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Networking_starts_before_the_stored_block_replay(CancellationToken testToken)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, AnchorSlot + 1);
        TestChain.Persist(store, anchorBlock, anchorRoot, chain);
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

    [Test]
    public async Task A_head_left_behind_restarts_range_sync_at_most_once_an_epoch()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150, 151, 152, 153);
        harness.Importer.Known.Add(anchorRoot);
        harness.Orchestrator.GossipStarted = true;
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot, finalizedEpoch: Spec.GetEpoch(AnchorSlot));
        List<ulong> tipsAfterTicks = [];
        foreach ((SignedBeaconBlock block, ulong tick) in new[] { (chain[0], WallSlot), (chain[1], WallSlot + Spec.SlotsPerEpoch), (chain[2], WallSlot + 2 * Spec.SlotsPerEpoch - 1), (chain[3], WallSlot + 2 * Spec.SlotsPerEpoch) })
        {
            ForkedSignedBeaconBlock forked = new ForkedSignedBeaconBlock.OfFulu(block);
            await harness.Orchestrator.ImportBlockAsync(forked, CancellationToken.None, rangeItem: new BeaconSyncOrchestrator.RangeBlockItem(forked));
            await harness.Orchestrator.ProcessSlotAsync(tick, CancellationToken.None);
            tipsAfterTicks.Add(harness.Orchestrator.SyncTip.Slot);
        }

        Assert.That(tipsAfterTicks, Is.EqualTo(new[] { 150UL, AnchorSlot, 152UL, AnchorSlot }));
    }

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

    [Test]
    [CancelAfter(30_000)]
    public async Task Only_a_block_whose_data_never_arrived_is_warned_about(CancellationToken token)
    {
        TestLogRecorder logger = new();
        Harness harness = CreateHarness(logManager: new OneLoggerLogManager(new ILogger(logger)));
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(AnchorSlot, 150);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(chain[0]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.Unavailable.Add(block.ComputeMessageRoot());
        await harness.Orchestrator.ImportBlockAsync(block, token);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, token);
        int warningsWhileWaiting = logger.Messages.Count(static l => l.StartsWith("Dropping block"));

        ulong expirySlot = WallSlot + 2 * Spec.SlotsPerEpoch + 1;
        harness.Timestamper.Set(SlotStart(expirySlot));
        await harness.Orchestrator.ProcessSlotAsync(expirySlot, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(warningsWhileWaiting, Is.Zero);
        Assert.That(logger.Messages.Count(l => l.StartsWith($"Dropping block {block.ComputeMessageRoot()}")), Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_range_sync_restart_starts_the_next_round_without_waiting_out_the_slot(CancellationToken token)
    {
        RangeSyncTests.StubPeer server = new("server", WallSlot, static (_, _) => []);
        Harness harness = CreateHarness(wallSlot: AnchorSlot, peers: [server]);
        harness.Importer.Head = CreateHead(TestItem.KeccakA, AnchorSlot, finalizedEpoch: Spec.GetEpoch(AnchorSlot));
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(await EndsAsync(feed, token), Is.True);
        Assert.That(requestsWhileWaiting, Is.Zero);
        Assert.That(server.Requests, Is.Positive, "the round from the head started");
        Assert.That(untilNextRound, Is.LessThan(bound));
    }

    /// <summary>A stop may complete or cancel the range loop; both satisfy shutdown.</summary>
    internal static async Task<bool> EndsAsync(Task task, CancellationToken token)
    {
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5), token));
        return task.IsCompletedSuccessfully || task.IsCanceled;
    }

    private static (Harness Harness, IBeaconSyncPeer Peer, SignedBeaconBlock[] Chain) CreateBackfillChain(ulong anchorSlot, params ulong[] slots)
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] chain) = TestChain.BuildLinkedChain(anchorSlot, slots);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        Harness harness = CreateHarness(anchorSlot: anchorSlot, peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        return (harness, peer, chain);
    }

    private static async Task CompleteWorkerAsync(BeaconSyncOrchestrator orchestrator, CancellationToken token)
    {
        orchestrator.WorkWriter.Complete();
        await orchestrator.RunWorkerAsync(token);
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
        public HashSet<Hash256> Unavailable { get; } = [];
        public HashSet<Hash256> RegenerationRefused { get; } = [];
        public HashSet<Hash256> RegenerationImpossible { get; } = [];
        public HashSet<Hash256> AdmissionRefused { get; } = [];
        public ImportRefusal LastRefusal { get; private set; }

        bool IBlockImporter.RejectGossip => RejectGossip;

        private bool RejectGossip { get; set; }
        public HashSet<BlsSignature> ForgedSignatures { get; } = [];
        public HashSet<Hash256> Forged { get; } = [];
        public HashSet<Hash256> InvalidTransition { get; } = [];
        public HashSet<Hash256> EngineDown { get; } = [];
        public HashSet<Hash256> Early { get; } = [];
        public HashSet<Hash256> UnverifiedPayloads { get; } = [];
        public ExecutionPayloadEnvelopeImportResult EnvelopeResult { get; set; } = ExecutionPayloadEnvelopeImportResult.Valid;
        public Func<SignedExecutionPayloadEnvelope, ExecutionPayloadEnvelopeImportResult>? EnvelopeVerdict { get; set; }
        public List<Hash256> Envelopes { get; } = [];
        public List<(bool Envelope, Hash256 Root)> ImportOrder { get; } = [];
        public List<bool> ImportedOnPoolThread { get; } = [];
        public List<object> GossipOperations { get; } = [];
        public List<int> TicksAtGossipOperations { get; } = [];
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
        public List<Hash256> RequestedImports { get; } = [];
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
            RejectGossip = false;
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
                if (block is ForkedSignedBeaconBlock.OfFulu { Block.Signature: var forged } && ForgedSignatures.Contains(forged)) return BlockImportResult.Invalid;

                LastRefusal = ImportRefusal.RegenerationBudget;
                return BlockImportResult.UnknownParent;
            }

            if (UnverifiedPayloads.Contains(block.ParentRoot)) return Defer(blockRoot);
            if (InvalidTransition.Contains(blockRoot)) return BlockImportResult.Invalid;
            if (Unavailable.Contains(blockRoot)) return BlockImportResult.DataUnavailable;
            if (Forged.Contains(blockRoot) || !ExpectedProposer)
            {
                RejectGossip = true;
                return BlockImportResult.Invalid;
            }
            if (block is ForkedSignedBeaconBlock.OfFulu { Block.Signature: var signature } && ForgedSignatures.Contains(signature)) return BlockImportResult.Invalid;
            if (EngineDown.Contains(blockRoot)) return BlockImportResult.EngineUnavailable;
            if (Early.Contains(blockRoot) && (Ticks.Count == 0 || Ticks[^1] < block.Slot)) return BlockImportResult.FutureSlot;
            Known.Add(blockRoot);
            OnImported?.Invoke(block, blockRoot);
            return BlockImportResult.Imported;
        }

        public Action<ForkedSignedBeaconBlock, Hash256>? OnImported { get; set; }

        private BlockImportResult Defer(Hash256 blockRoot)
        {
            if (Forged.Contains(blockRoot))
            {
                RejectGossip = true;
                return BlockImportResult.Invalid;
            }
            if (AdmissionRefused.Contains(blockRoot))
            {
                LastRefusal = ImportRefusal.LocalAdmission;
                return BlockImportResult.Invalid;
            }

            _deferred.Add(blockRoot);
            return BlockImportResult.ParentPayloadUnverified;
        }

        public bool? EnvelopeSignature { get; set; } = true;
        public bool? VerifyEnvelopeSignature(SignedExecutionPayloadEnvelope envelope) => EnvelopeSignature;

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
        public bool? OnGossipAggregate(SignedAggregateAndProof aggregate) => Consume(aggregate);
        public bool? OnGossipAggregate(SignedAggregateAndProofGloas aggregate) => Consume(aggregate);
        public bool? AcceptsGossipOperations { get; set; } = true;
        public bool? OnGossipAttesterSlashing(AttesterSlashing slashing) => Consume(slashing);
        public bool? OnGossipAttesterSlashing(AttesterSlashingGloas slashing) => Consume(slashing);
        public bool? OnGossipPayloadAttestation(PayloadAttestationMessage message) => Consume(message);
        public Func<ulong, ulong[]?> Ptc { get; set; } = static _ => EveryValidator;

        private bool? Consume(object operation)
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
}
