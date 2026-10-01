// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;
using Snappier;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>
/// Execution payload envelopes through the orchestrator (gloas/fork-choice.md <c>on_execution_payload_envelope</c>): held until
/// their block imports, retried while their data or the engine is missing, recovered by root for a parked full child, and fetched
/// by range alongside range-synced Gloas blocks (gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange and ByRoot v1).
/// </summary>
public partial class BeaconSyncOrchestratorTests
{
    private const ulong EnvelopeBlockSlot = 150;

    [Test]
    public async Task A_blob_carrying_stored_parent_recovers_columns_and_imports_its_full_child([Values] bool anchor, [Values] bool delayedColumns)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        ulong wallSlot = ColumnSlot + 2;
        ForkedSignedBeaconBlock parent = GloasBlobBlock(ColumnSlot, AnchorRoot(), ColumnSlot);
        Hash256 root = parent.ComputeMessageRoot();
        ExecutionPayloadBid bid = ((ForkedSignedBeaconBlock.OfGloas)parent).Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), GloasCheckpointFiles.Spec);
        store.PutForkedBlock(root, parent);
        DataColumnSidecarPool sidecars = new();
        int columnRequests = 0;
        EnvelopeServingPeer peer = new("peer", wallSlot, byRoot: _ => [EnvelopeFor(root, ColumnSlot)],
            gloasColumnsByRoot: ids => delayedColumns && ++columnRequests == 1 ? []
                : [.. ids[0].Columns!.Select(c => DataColumnSidecarGloasTestFixture.BuildSidecar(c, ColumnSlot, root))]);
        Harness harness = CreateHarness(wallSlot: wallSlot, store: store, peers: [peer], sidecarPool: sidecars, discovery: discovery);
        if (anchor) harness.Orchestrator.Initialize(harness.Importer, parent, root);
        harness.Importer.Known.UnionWith([AnchorRoot(), root]);
        harness.Importer.UnverifiedPayloads.Add(root);
        harness.Importer.Head = CreateHead(root, ColumnSlot, finalizedEpoch: Spec.GetEpoch(ColumnSlot));
        GloasCustodySamplingAvailability availability = new(new DiscoveryNodeCustodySource(discovery), sidecars, RangeSyncTests.ClockAtGenesis(Spec), Spec);
        Func<SignedExecutionPayloadEnvelope, ExecutionPayloadEnvelopeImportResult> envelopeVerdict = _ => availability.IsDataAvailable(root, bid)
            ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        harness.Importer.EnvelopeVerdict = envelopeVerdict;
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(ColumnSlot + 1, root));

        Assert.That(await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None), Is.EqualTo(BlockImportResult.ParentPayloadUnverified));
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        if (delayedColumns) harness.Importer.EnvelopeVerdict = _ => ExecutionPayloadEnvelopeImportResult.EngineUnavailable;
        await harness.Orchestrator.ProcessSlotAsync(wallSlot, CancellationToken.None);
        harness.Timestamper.Set(SlotStart(wallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(wallSlot + 1, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        if (delayedColumns)
        {
            Assert.That(peer.ColumnRootRequests, Has.Count.EqualTo(2));
            harness.Importer.EnvelopeVerdict = envelopeVerdict;
        }
        await harness.Orchestrator.ProcessSlotAsync(wallSlot + 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.ColumnRootRequests, Has.Count.EqualTo(delayedColumns ? 2 : 1));
            Assert.That(peer.ColumnRootRequests[0][0].BlockRoot, Is.EqualTo(root));
            Assert.That(availability.IsDataAvailable(root, bid), Is.True);
            Assert.That(harness.Importer.Known.Contains(child.ComputeMessageRoot()), Is.True);
            Assert.That(harness.EnvelopePool.TryGet(root, out _), Is.True);
        }
    }

    [Test]
    public async Task Gossip_envelope_reaches_the_importer_through_the_worker()
    {
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(Sepolia.GenesisTime + (FirstGloasSlot + 1) * Sepolia.SecondsPerSlot).AddSeconds(6));
        GossipRouter router = new(Sepolia, new SlotClock(Sepolia, timestamper), LimboLogs.Instance);
        Harness harness = CreateHarness(router: router);
        harness.Orchestrator.RouteGossipEvents();
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, FirstGloasSlot + 1);

        MessageValidity validity = router.HandleExecutionPayloadEnvelope(Snappy.CompressToArray(SignedExecutionPayloadEnvelope.Encode(envelope)));
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(validity, Is.EqualTo(MessageValidity.Ignored), "fixture: the envelope names a block the router does not hold, so it is consumed");
            Assert.That(harness.Importer.Envelopes, Is.EqualTo(new[] { TestItem.KeccakA }));
        }
    }

    /// <summary>gloas/p2p-interface.md <c>execution_payload</c>: an envelope for a block not yet seen MAY be queued until the block is retrieved.</summary>
    [Test]
    public async Task Envelope_before_its_block_imports_once_the_block_imports_and_is_not_redelivered()
    {
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EnvelopeVerdict = e => harness.Importer.Known.Contains(e.Message!.BeaconBlockRoot!) ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.UnknownBlock;
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        Hash256 root = block.ComputeMessageRoot();
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(root, EnvelopeBlockSlot);

        ExecutionPayloadEnvelopeImportResult? early = await harness.Orchestrator.ImportEnvelopeAsync(envelope, CancellationToken.None);
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, EnvelopeBlockSlot, builderIndex: 1), CancellationToken.None);
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.UnknownBlock));
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(new[] { (true, root), (true, root), (false, root), (true, root) }), "both held, the first imported right after its block, then neither again");
            Assert.That(harness.EnvelopePool.TryGet(root, out _), Is.True);
            Assert.That(harness.Router.IsEnvelopeSeen(root, envelope.Message!.BuilderIndex), Is.True);
        }
    }

    /// <summary>
    /// A full child parked on its parent's unverified payload asks one peer for that parent's envelope by root and imports within the
    /// same call. gloas/fork-choice.md <c>get_forkchoice_store</c> starts with <c>payloads = {}</c>, so a Gloas anchor's first full child
    /// recovers the anchor's envelope the same way.
    /// </summary>
    [TestCase(false, TestName = "Parked_full_child_triggers_one_envelope_by_root_request_and_imports_in_the_same_pass")]
    [TestCase(true, TestName = "Gloas_anchor_first_full_child_recovers_the_anchor_envelope_by_root")]
    public async Task Parked_full_child_recovers_its_parents_envelope_by_root(bool parentIsAnchor)
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        Hash256 parentRoot = parentIsAnchor ? anchorRoot : parent.ComputeMessageRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot + 1, parentRoot));
        Hash256 childRoot = child.ComputeMessageRoot();
        EnvelopeServingPeer peer = new("peer", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], EnvelopeBlockSlot)]);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.UnionWith([anchorRoot, parentRoot]);
        harness.Importer.UnverifiedPayloads.Add(parentRoot);

        await harness.Orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.RootRequests, Is.EqualTo(new[] { new[] { parentRoot } }), "one request, for the parent only");
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(new[] { (false, childRoot), (true, parentRoot), (false, childRoot) }));
            Assert.That(harness.Importer.Known, Does.Contain(childRoot));
            Assert.That(harness.Orchestrator.SyncTip, Is.EqualTo((childRoot, child.Slot)));
            Assert.That(harness.EnvelopePool.TryGet(parentRoot, out _), Is.True);
        }
    }

    [Test]
    public async Task Parent_envelope_is_requested_by_root_at_most_once_per_slot()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        int requestsInFirstSlot = peer.RootRequests.Count;
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestsInFirstSlot, Is.EqualTo(1), "the slot tick retries the parked child without asking again");
            Assert.That(peer.RootRequests, Has.Count.EqualTo(2), "the next slot asks again");
        }
    }

    /// <summary>gloas/fork-choice.md on_block: a full child can recover its parent's verified payload beyond the first three peers.</summary>
    [Test]
    public async Task Parent_envelope_recovery_reaches_the_fourth_peer_after_empty_replies()
    {
        Hash256 root = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, root));
        EnvelopeServingPeer[] silent = [.. Enumerable.Range(0, 3).Select(i => new EnvelopeServingPeer($"silent-{i}", WallSlot))];
        EnvelopeServingPeer serving = new("serving", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], AnchorSlot)]);
        Harness harness = CreateHarness(peers: [.. silent, serving]);
        harness.Importer.Known.Add(root);
        harness.Importer.UnverifiedPayloads.Add(root);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);
        Assert.That(serving.RootRequests, Is.Empty);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(silent.Select(static p => p.RootRequests.Count), Is.All.EqualTo(1));
            Assert.That(silent.SelectMany(static p => p.Reports), Is.Empty);
            Assert.That(serving.RootRequests, Has.Count.EqualTo(1));
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
        }
    }

    [Test]
    public async Task Envelope_by_root_for_another_block_penalizes_its_peer()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        EnvelopeServingPeer lying = new("lying", WallSlot, byRoot: _ => [EnvelopeFor(TestItem.KeccakF, EnvelopeBlockSlot)]);
        EnvelopeServingPeer honest = new("honest", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], EnvelopeBlockSlot)]);
        Harness harness = CreateHarness(peers: [lying, honest]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lying.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
            Assert.That(honest.Reports, Is.Empty);
            Assert.That(harness.Importer.Envelopes, Is.EqualTo(new[] { anchorRoot }), "the envelope for another block is never imported");
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
        }
    }

    /// <summary>
    /// Range sync fetches the envelopes of consecutive Gloas blocks with one ExecutionPayloadEnvelopesByRange request per
    /// <see cref="RangeSync.DefaultBatchSize"/> blocks and queues each envelope right after its block, so every full child finds its
    /// parent's payload recorded and none waits in the bounded retry set.
    /// </summary>
    [Test]
    public async Task Range_feed_requests_envelopes_once_per_run_and_imports_each_right_after_its_block()
    {
        const int Blocks = 20;
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, Blocks);
        Harness harness = CreateRangeHarness(chain, EnvelopesOf(chain), out EnvelopeServingPeer peer);
        harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.RangeRequests, Is.EqualTo(new[] { (AnchorSlot + 1, RangeSync.DefaultBatchSize), (AnchorSlot + 1 + RangeSync.DefaultBatchSize, (ulong)Blocks - RangeSync.DefaultBatchSize) }));
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(chain.SelectMany(static b => new[] { (false, b.ComputeMessageRoot()), (true, b.ComputeMessageRoot()) })));
            Assert.That(peer.RootRequests, Is.Empty, "no child waited for its parent's envelope");
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(chain[^1].Slot));
        }
    }

    /// <summary>gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange v1: <c>count</c> is at most <c>MAX_REQUEST_PAYLOADS</c> (128).</summary>
    [TestCase(127UL, true)]
    [TestCase(128UL, false)]
    [TestCase(200UL, false)]
    public async Task Range_feed_writes_out_a_run_before_it_spans_more_slots_than_one_request_may_ask_for(ulong slotStep, bool oneRequest)
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 2, slotStep: slotStep);
        Harness harness = CreateRangeHarness(chain, EnvelopesOf(chain), out EnvelopeServingPeer peer, wallSlot: chain[^1].Slot);

        await RunRangeRoundAsync(harness);

        Assert.That(peer.RangeRequests, Is.EqualTo(oneRequest
            ? new[] { (chain[0].Slot, slotStep + 1) }
            : new[] { (chain[0].Slot, 1UL), (chain[1].Slot, 1UL) }));
    }

    /// <summary>A block's envelope served twice keeps the first copy, so a later copy cannot displace the one already matched.</summary>
    [Test]
    public async Task Range_feed_imports_the_first_copy_of_an_envelope_served_twice()
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 2);
        SignedExecutionPayloadEnvelope first = EnvelopeFor(chain[0]);
        SignedExecutionPayloadEnvelope repeat = EnvelopeFor(chain[0]);
        Harness harness = CreateRangeHarness(chain, [first, repeat, EnvelopeFor(chain[1])], out EnvelopeServingPeer peer);
        List<SignedExecutionPayloadEnvelope> imported = [];
        harness.Importer.EnvelopeVerdict = e =>
        {
            imported.Add(e);
            return ExecutionPayloadEnvelopeImportResult.Valid;
        };

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(imported, Has.Count.EqualTo(2));
            Assert.That(imported[0], Is.SameAs(first));
            Assert.That(peer.Reports, Is.Empty, "a repeat is not a protocol violation");
        }
    }

    /// <summary>gloas/p2p-interface.md ExecutionPayloadEnvelopesByRange v1: a payload the next block does not build on is not on the chain.</summary>
    [Test]
    public async Task Range_feed_skips_an_envelope_the_next_block_does_not_build_on()
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 2, emptyAt: [1]);
        Harness harness = CreateRangeHarness(chain, EnvelopesOf(chain), out EnvelopeServingPeer peer);

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(new[] { (false, chain[0].ComputeMessageRoot()), (false, chain[1].ComputeMessageRoot()), (true, chain[1].ComputeMessageRoot()) }));
            Assert.That(peer.Reports, Is.Empty);
        }
    }

    public enum RangeEnvelopeFault
    {
        BlockOutsideTheRun,
        WrongSlot,
        WrongBuilderIndex,
        WrongBlockHash,
    }

    [Test]
    public async Task Range_feed_counts_an_envelope_that_matches_no_block_of_the_run_as_a_protocol_violation([Values] RangeEnvelopeFault fault)
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 2, emptyAt: [1]);
        SignedExecutionPayloadEnvelope bad = EnvelopeFor(chain[1]);
        switch (fault)
        {
            case RangeEnvelopeFault.BlockOutsideTheRun:
                bad.Message!.BeaconBlockRoot = TestItem.KeccakF;
                break;
            case RangeEnvelopeFault.WrongSlot:
                bad.Message!.Payload!.SlotNumber++;
                break;
            case RangeEnvelopeFault.WrongBuilderIndex:
                bad.Message!.BuilderIndex++;
                break;
            case RangeEnvelopeFault.WrongBlockHash:
                bad.Message!.Payload!.BlockHash = TestItem.KeccakF;
                break;
        }

        Harness harness = CreateRangeHarness(chain, [bad], out EnvelopeServingPeer peer, serveEveryEnvelope: true);

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Reports, Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }));
            Assert.That(harness.Importer.Envelopes, Is.Empty, "the envelope is discarded before import");
        }
    }

    [Test]
    public async Task Envelope_waiting_on_its_data_or_the_engine_is_retried_on_the_tick_and_dropped_past_finality(
        [Values(ExecutionPayloadEnvelopeImportResult.DataUnavailable, ExecutionPayloadEnvelopeImportResult.EngineUnavailable)] ExecutionPayloadEnvelopeImportResult waiting,
        [Values] bool finalizedPast)
    {
        Harness harness = CreateHarness();
        ulong finalizedEpoch = Spec.GetEpoch(EnvelopeBlockSlot) + 1;
        // At the finalized epoch's start slot, the boundary that is dropped.
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, finalizedEpoch * Spec.SlotsPerEpoch);
        harness.Importer.EnvelopeVerdict = _ => harness.Importer.Envelopes.Count == 1 ? waiting : ExecutionPayloadEnvelopeImportResult.Valid;
        if (finalizedPast)
        {
            harness.Importer.Head = CreateHead(TestItem.KeccakB, AnchorSlot, finalizedEpoch: finalizedEpoch);
        }

        await harness.Orchestrator.ImportEnvelopeAsync(envelope, CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(finalizedPast ? 1 : 2), "retried once, until it imports, unless finality passed its slot");
            Assert.That(harness.EnvelopePool.TryGet(TestItem.KeccakA, out _), Is.EqualTo(!finalizedPast));
        }
    }

    [Test]
    public async Task Envelope_waiting_on_its_data_expires_under_stalled_finality()
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.KeccakA, WallSlot), CancellationToken.None);

        int attemptsNextSlot = await TickAtAgeAsync(harness, 1, () => harness.Importer.Envelopes.Count);
        int attemptsWithinAge = await TickAtAgeAsync(harness, RetryAgeSlots, () => harness.Importer.Envelopes.Count);
        int attemptsPastAge = await TickAtAgeAsync(harness, RetryAgeSlots + 1, () => harness.Importer.Envelopes.Count);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attemptsNextSlot, Is.EqualTo(2));
            Assert.That(attemptsWithinAge, Is.EqualTo(3), "a retry that still waits stays queued once, from the slot it was first queued at");
            Assert.That(attemptsPastAge, Is.EqualTo(3), "an envelope older than the retry age is dropped although finality never passed it");
        }
    }

    /// <summary>A data-unavailable block that only finality would release otherwise holds its retry place, and its children's places, while finality stalls.</summary>
    [Test]
    public async Task Data_unavailable_block_expires_from_the_retry_set_under_stalled_finality()
    {
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(WallSlot, anchorRoot));
        harness.Importer.Unavailable.Add(block.ComputeMessageRoot());
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);

        int attemptsWithinAge = await TickAtAgeAsync(harness, RetryAgeSlots, () => harness.Importer.Imports.Count);
        int attemptsPastAge = await TickAtAgeAsync(harness, RetryAgeSlots + 1, () => harness.Importer.Imports.Count);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attemptsWithinAge, Is.EqualTo(2));
            Assert.That(attemptsPastAge, Is.EqualTo(2), "the stuck block is dropped although finality never passed it");
        }
    }

    public static IEnumerable<ExecutionPayloadEnvelopeImportResult> EveryEnvelopeResultAndAnUnknownOne() =>
        [.. Enum.GetValues<ExecutionPayloadEnvelopeImportResult>(), (ExecutionPayloadEnvelopeImportResult)99];

    /// <summary>
    /// Only an envelope whose payload is recorded is served by ExecutionPayloadEnvelopesByRoot, and only one whose payload is recorded
    /// or already was marks its (block root, builder index) seen for gossip; a verdict added later is neither.
    /// </summary>
    [Test]
    public async Task Envelope_is_marked_seen_and_pooled_only_after_its_payload_is_recorded([ValueSource(nameof(EveryEnvelopeResultAndAnUnknownOne))] ExecutionPayloadEnvelopeImportResult result)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = result;
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);

        await harness.Orchestrator.ImportEnvelopeAsync(envelope, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Router.IsEnvelopeSeen(TestItem.KeccakA, envelope.Message!.BuilderIndex),
                Is.EqualTo(result is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic or ExecutionPayloadEnvelopeImportResult.AlreadyKnown));
            Assert.That(harness.EnvelopePool.TryGet(TestItem.KeccakA, out SignedExecutionPayloadEnvelope? served) ? served : null,
                Is.EqualTo(result is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic ? envelope : null));
        }
    }

    /// <summary>An envelope found invalid only when retried after waiting on its data still penalizes the peer that served it.</summary>
    [Test]
    public async Task Invalid_envelope_penalizes_only_the_req_resp_peer_that_served_it([Values] bool fromPeer, [Values] bool afterDataRetry)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeVerdict = _ => afterDataRetry && harness.Importer.Envelopes.Count == 1
            ? ExecutionPayloadEnvelopeImportResult.DataUnavailable
            : ExecutionPayloadEnvelopeImportResult.Invalid;
        EnvelopeServingPeer peer = new("peer", WallSlot);
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);

        harness.Orchestrator.WorkWriter.TryWrite(fromPeer ? new BeaconSyncOrchestrator.FetchedEnvelopeItem(envelope, peer) : new BeaconSyncOrchestrator.GossipEnvelopeItem(envelope));
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);
        await TickAtAgeAsync(harness, 1, () => 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(afterDataRetry ? 2 : 1), "fixture: an envelope waiting on its data is retried on the tick");
            Assert.That(peer.Reports, Is.EqualTo(fromPeer ? new[] { PeerFailureReason.ProtocolViolation } : []));
        }
    }

    /// <summary>A local fault in envelope import is logged and the envelope dropped; left to escape, it would stop the single worker every block goes through.</summary>
    [Test]
    public async Task Envelope_import_fault_is_dropped_without_stopping_the_worker([Values] bool withoutMessage)
    {
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EnvelopeVerdict = static _ => throw new InvalidOperationException("no execution verdict");
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(AnchorSlot + 1, anchorRoot));
        SignedExecutionPayloadEnvelope envelope = EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot);
        ulong builderIndex = envelope.Message!.BuilderIndex;
        if (withoutMessage)
        {
            envelope.Message = null;
        }

        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipEnvelopeItem(envelope));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(block));
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Known, Does.Contain(block.ComputeMessageRoot()), "the next item is still processed");
            Assert.That(harness.Importer.Envelopes, Has.Count.EqualTo(withoutMessage ? 0 : 1), "an envelope without a message never reaches the importer");
            Assert.That(harness.Router.IsEnvelopeSeen(TestItem.KeccakA, builderIndex), Is.False);
            Assert.That(harness.EnvelopePool.TryGet(TestItem.KeccakA, out _), Is.False);
        }
    }

    /// <summary>The module hands the orchestrator the envelope pool the req/resp protocols serve from.</summary>
    [Test]
    public async Task Orchestrator_from_the_module_pools_recorded_envelopes_where_by_root_serves_them()
    {
        using IContainer container = BeaconChainTestContainer.Builder().Build();
        BeaconSyncOrchestrator orchestrator = container.Resolve<BeaconSyncOrchestrator>();
        ExecutionPayloadEnvelopePool pool = container.Resolve<ExecutionPayloadEnvelopePool>();
        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        orchestrator.Initialize(new ScriptedImporter { Head = CreateHead(anchorRoot, AnchorSlot, finalizedEpoch: 0) }, new ForkedSignedBeaconBlock.OfFulu(anchorBlock), anchorRoot);

        await orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.KeccakA, EnvelopeBlockSlot), CancellationToken.None);

        Assert.That(pool.TryGet(TestItem.KeccakA, out _), Is.True);
    }

    /// <summary>
    /// A Gloas sidecar that arrives before its block carries no source peer to bound, so a flood can take a column's every candidate
    /// place with forgeries; the block's sampled columns must then come by DataColumnSidecarsByRoot (gloas/p2p-interface.md), at most
    /// once per block per slot.
    /// </summary>
    [Test]
    public async Task Block_whose_column_candidates_were_all_forged_becomes_available_from_peers()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        Hash256 anchorRoot = AnchorRoot();
        ulong columnSlot = ColumnSlot;
        ForkedSignedBeaconBlock block = GloasBlobBlock(EnvelopeBlockSlot, anchorRoot, columnSlot);
        ExecutionPayloadBid bid = ((ForkedSignedBeaconBlock.OfGloas)block).Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        Hash256 root = block.ComputeMessageRoot();
        DataColumnSidecarPool sidecars = new();
        IReadOnlyList<ulong> sampled = new DiscoveryNodeCustodySource(discovery).Current!.SampledColumns;
        for (int candidate = 0; candidate < DataColumnSidecarPool.MaxPendingGloasCandidatesPerKey; candidate++)
        {
            foreach (ulong column in sampled)
            {
                DataColumnSidecarGloas forged = DataColumnSidecarGloasTestFixture.BuildSidecar(column, columnSlot, root);
                forged.KzgProofs = [forged.KzgProofs![1], forged.KzgProofs[0]];
                sidecars.AddPendingGloas(forged, columnSlot);
            }
        }

        EnvelopeServingPeer peer = new("peer", WallSlot, gloasColumnsByRoot: ids => [.. ids[0].Columns!.Select(c => DataColumnSidecarGloasTestFixture.BuildSidecar(c, columnSlot, root))]);
        Harness harness = CreateHarness(peers: [peer], sidecarPool: sidecars, discovery: discovery);
        harness.Importer.Known.Add(anchorRoot);
        GloasCustodySamplingAvailability availability = new(new DiscoveryNodeCustodySource(discovery), sidecars, RangeSyncTests.ClockAtGenesis(Spec), Spec);

        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        bool availableOnImport = availability.IsDataAvailable(root, bid);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availableOnImport, Is.True, "fetched when the block imports, not a slot later");
            Assert.That(peer.ColumnRootRequests, Has.Count.EqualTo(1), "one request, for the one block");
            Assert.That(peer.ColumnRootRequests[0][0].BlockRoot, Is.EqualTo(root));
        }
    }

    [Test]
    public async Task Column_recovery_asks_when_its_block_imports_and_again_only_in_a_later_slot()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock block = GloasBlobBlock(EnvelopeBlockSlot, anchorRoot, ColumnSlot);
        ExecutionPayloadBid bid = ((ForkedSignedBeaconBlock.OfGloas)block).Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        Hash256 root = block.ComputeMessageRoot();
        DataColumnSidecarPool sidecars = new();
        int calls = 0;
        // The columns are not out yet on the first request.
        EnvelopeServingPeer peer = new("peer", WallSlot, gloasColumnsByRoot: ids => ++calls == 1
            ? []
            : [.. ids[0].Columns!.Select(c => DataColumnSidecarGloasTestFixture.BuildSidecar(c, ColumnSlot, root))]);
        Harness harness = CreateHarness(peers: [peer], sidecarPool: sidecars, discovery: discovery);
        harness.Importer.Known.Add(anchorRoot);
        GloasCustodySamplingAvailability availability = new(new DiscoveryNodeCustodySource(discovery), sidecars, RangeSyncTests.ClockAtGenesis(Spec), Spec);

        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        int requestsOnImport = peer.ColumnRootRequests.Count;
        // A tick queued behind the import runs in the slot the block imported in.
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        int requestsInImportSlot = peer.ColumnRootRequests.Count;
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestsOnImport, Is.EqualTo(1));
            Assert.That(requestsInImportSlot, Is.EqualTo(1), "at most once per block per slot");
            Assert.That(peer.ColumnRootRequests, Has.Count.EqualTo(2), "the next slot retries");
            Assert.That(availability.IsDataAvailable(root, bid), Is.True);
        }
    }

    /// <summary>Column recovery rotates through the peers across slots, so peers answering with nothing do not hide one that serves the columns.</summary>
    [Test]
    public async Task Column_recovery_asks_in_a_later_slot_the_peers_not_asked_yet()
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock block = GloasBlobBlock(EnvelopeBlockSlot, anchorRoot, ColumnSlot);
        ExecutionPayloadBid bid = ((ForkedSignedBeaconBlock.OfGloas)block).Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        Hash256 root = block.ComputeMessageRoot();
        DataColumnSidecarPool sidecars = new();
        EnvelopeServingPeer[] silent = [.. Enumerable.Range(0, 3).Select(i => new EnvelopeServingPeer($"silent-{i}", WallSlot, gloasColumnsByRoot: static _ => []))];
        EnvelopeServingPeer serving = new("serving", WallSlot, gloasColumnsByRoot: ids => [.. ids[0].Columns!.Select(c => DataColumnSidecarGloasTestFixture.BuildSidecar(c, ColumnSlot, root))]);
        Harness harness = CreateHarness(peers: [.. silent, serving], sidecarPool: sidecars, discovery: discovery);
        harness.Importer.Known.Add(anchorRoot);
        GloasCustodySamplingAvailability availability = new(new DiscoveryNodeCustodySource(discovery), sidecars, RangeSyncTests.ClockAtGenesis(Spec), Spec);

        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        bool availableOnImport = availability.IsDataAvailable(root, bid);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availableOnImport, Is.False);
            Assert.That(silent.Select(static p => p.ColumnRootRequests.Count), Is.All.EqualTo(1), "each silent peer is asked once");
            Assert.That(serving.ColumnRootRequests, Has.Count.EqualTo(1), "the next slot asks the peer the import did not");
            Assert.That(availability.IsDataAvailable(root, bid), Is.True);
        }
    }

    /// <summary>A range whose Gloas blocks are followed by a pre-Gloas one still reaches the worker in slot order.</summary>
    [Test]
    public async Task Range_feed_writes_out_buffered_gloas_blocks_before_a_later_pre_gloas_block()
    {
        List<ForkedSignedBeaconBlock.OfGloas> gloas = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 1);
        ForkedSignedBeaconBlock fulu = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(AnchorSlot + 2, gloas[0].ComputeMessageRoot()));
        EnvelopeServingPeer peer = new("peer", WallSlot, blocksByRange: (start, count) => [.. new[] { gloas[0], fulu }.Where(b => b.Slot >= start && b.Slot - start < count)]);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(AnchorRoot());

        await RunRangeRoundAsync(harness);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(new[] { AnchorSlot + 1, AnchorSlot + 2 }));
    }

    /// <summary>An envelope already held for the parent is imported before any peer is asked for another copy.</summary>
    [Test]
    public async Task Parked_full_child_takes_its_parents_envelope_from_those_held_before_asking_a_peer()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);
        // The first answer stands in for an importer that did not hold the block's state yet.
        harness.Importer.EnvelopeVerdict = _ => harness.Importer.Envelopes.Count == 1 ? ExecutionPayloadEnvelopeImportResult.UnknownBlock : ExecutionPayloadEnvelopeImportResult.Valid;
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(anchorRoot, AnchorSlot), CancellationToken.None);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.RootRequests, Is.Empty);
            Assert.That(harness.Importer.Known, Does.Contain(child.ComputeMessageRoot()));
        }
    }

    /// <summary>A child of a block that is itself parked waits on that block's import; only the envelope fork choice can record is asked for.</summary>
    [Test]
    public async Task Child_of_a_parked_block_asks_for_no_envelope_of_its_own_parent()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot + 1, parent.ComputeMessageRoot()));
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);

        await harness.Orchestrator.ImportBlockAsync(parent, CancellationToken.None);
        BlockImportResult childResult = await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(childResult, Is.EqualTo(BlockImportResult.ParentPayloadUnverified), "fixture: the child is deferred behind its parked parent");
            Assert.That(peer.RootRequests, Is.EqualTo(new[] { new[] { anchorRoot } }));
        }
    }

    /// <summary>An envelope held for a retry waits on its data or the engine, which another copy from a peer would not change.</summary>
    [Test]
    public async Task Parent_envelope_waiting_on_its_data_is_not_requested_by_root()
    {
        Hash256 anchorRoot = AnchorRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.UnverifiedPayloads.Add(anchorRoot);
        harness.Importer.EnvelopeResult = ExecutionPayloadEnvelopeImportResult.DataUnavailable;
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(anchorRoot, AnchorSlot), CancellationToken.None);

        await harness.Orchestrator.ImportBlockAsync(child, CancellationToken.None);

        Assert.That(peer.RootRequests, Is.Empty);
    }

    /// <summary>
    /// A gossip block's fetched parent that parks and then imports once its own parent's envelope is recovered is reported imported,
    /// so the gossip block after it in the fetched chain imports too instead of being given up.
    /// </summary>
    [Test]
    public async Task Gossip_block_imports_after_its_fetched_parent_recovers_its_parents_envelope()
    {
        const ulong NearWallSlot = WallSlot - 5;
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(NearWallSlot);
        ForkedSignedBeaconBlock full = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(NearWallSlot + 1, anchorRoot));
        Hash256 fullRoot = full.ComputeMessageRoot();
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(NearWallSlot + 2, fullRoot));
        ForkedSignedBeaconBlock gossip = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(NearWallSlot + 3, parent.ComputeMessageRoot()));
        EnvelopeServingPeer peer = new("peer", WallSlot, byRoot: roots => [EnvelopeFor(roots[0], NearWallSlot + 1)], blocksByRoot: _ => [parent]);
        Harness harness = CreateHarness(anchorSlot: NearWallSlot, peers: [peer]);
        harness.Importer.Known.UnionWith([anchorRoot, fullRoot]);
        harness.Importer.UnverifiedPayloads.Add(fullRoot);

        await harness.Orchestrator.ProcessGossipBlockAsync(gossip, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Known, Does.Contain(parent.ComputeMessageRoot()));
            Assert.That(harness.Importer.Known, Does.Contain(gossip.ComputeMessageRoot()));
        }
    }

    public enum HeldEnvelopeFlood
    {
        OneBlockFloods,
        OtherBlocksFillFirst,
        OtherBlocksFillAfterTheFirst,
    }

    /// <summary>Envelopes held for a block not imported yet keep their places first come, at most four for one block and 128 in all.</summary>
    [TestCase(HeldEnvelopeFlood.OneBlockFloods, 4)]
    [TestCase(HeldEnvelopeFlood.OtherBlocksFillFirst, 0)]
    [TestCase(HeldEnvelopeFlood.OtherBlocksFillAfterTheFirst, 1)]
    public async Task Envelopes_held_for_a_block_not_imported_yet_are_bounded_first_come(HeldEnvelopeFlood flood, int expectedImports)
    {
        const int PerBlock = 4;
        const int Total = 128;
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        // Every held envelope is tried, since none records the payload.
        harness.Importer.EnvelopeVerdict = e => harness.Importer.Known.Contains(e.Message!.BeaconBlockRoot!) ? ExecutionPayloadEnvelopeImportResult.Invalid : ExecutionPayloadEnvelopeImportResult.UnknownBlock;
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(EnvelopeBlockSlot, anchorRoot));
        Hash256 root = block.ComputeMessageRoot();
        int held = 0;
        if (flood == HeldEnvelopeFlood.OtherBlocksFillAfterTheFirst)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, EnvelopeBlockSlot, builderIndex: 0), CancellationToken.None);
            held = 1;
        }

        if (flood != HeldEnvelopeFlood.OneBlockFloods)
        {
            for (int i = held; i < Total; i++)
            {
                await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.Keccaks[i], EnvelopeBlockSlot), CancellationToken.None);
            }
        }

        for (int i = held; i <= PerBlock; i++)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, EnvelopeBlockSlot, builderIndex: (ulong)i), CancellationToken.None);
        }

        int beforeBlock = harness.Importer.Envelopes.Count;
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);

        Assert.That(harness.Importer.Envelopes.Count - beforeBlock, Is.EqualTo(expectedImports));
    }

    /// <summary>
    /// An envelope's slot and block root are its own unverified claims, so forged envelopes that fill every held place must give them up
    /// at finality or past the retry age; otherwise every real envelope that arrives before its block is refused for the process lifetime.
    /// </summary>
    [Test]
    public async Task Envelopes_held_for_blocks_that_never_import_give_up_their_places([Values] bool finalized)
    {
        const int Total = 128;
        Harness harness = CreateHarness();
        Hash256 anchorRoot = AnchorRoot();
        harness.Importer.Known.Add(anchorRoot);
        harness.Importer.EnvelopeVerdict = e => harness.Importer.Known.Contains(e.Message!.BeaconBlockRoot!) ? ExecutionPayloadEnvelopeImportResult.Valid : ExecutionPayloadEnvelopeImportResult.UnknownBlock;
        for (int i = 0; i < Total; i++)
        {
            await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(TestItem.Keccaks[i], EnvelopeBlockSlot), CancellationToken.None);
        }

        if (finalized)
        {
            harness.Importer.Head = CreateHead(TestItem.KeccakB, AnchorSlot, finalizedEpoch: Spec.GetEpoch(EnvelopeBlockSlot) + 1);
        }

        ulong age = finalized ? 1 : RetryAgeSlots + 1;
        await TickAtAgeAsync(harness, age, () => 0);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(WallSlot + age, anchorRoot));
        Hash256 root = block.ComputeMessageRoot();
        await harness.Orchestrator.ImportEnvelopeAsync(EnvelopeFor(root, block.Slot), CancellationToken.None);
        await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.ImportOrder.TakeLast(3), Is.EqualTo(new[] { (true, root), (false, root), (true, root) }), "held, then imported right after its block");
            Assert.That(harness.EnvelopePool.TryGet(root, out _), Is.True);
        }
    }

    /// <summary>ExecutionPayloadEnvelopesByRange is asked of at most three peers per run, however many serve no envelopes.</summary>
    [Test]
    public async Task Range_feed_asks_at_most_three_peers_for_the_envelopes_of_a_run()
    {
        const int Peers = 5;
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 2);
        EnvelopeServingPeer[] peers = [.. Enumerable.Range(0, Peers).Select(i => new EnvelopeServingPeer($"bare{i}", WallSlot, blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]))];
        Harness harness = CreateHarness(peers: peers);
        harness.Importer.Known.Add(AnchorRoot());

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(chain.Select(static b => b.Slot)), "fixture: the run is written out");
            Assert.That(peers.Sum(static p => p.RangeRequests.Count), Is.EqualTo(3));
        }
    }

    [Test]
    public async Task Range_envelope_recovery_reaches_the_fourth_peer_after_empty_replies()
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 3);
        SignedExecutionPayloadEnvelope[] envelopes = EnvelopesOf(chain);
        EnvelopeServingPeer[] silent = [.. Enumerable.Range(0, 3).Select(i => new EnvelopeServingPeer($"silent-{i}", WallSlot,
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]))];
        EnvelopeServingPeer serving = new("serving", WallSlot, byRange: (_, _) => envelopes,
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]);
        Harness harness = CreateHarness(peers: [.. silent, serving]);
        harness.Importer.Known.Add(AnchorRoot());
        harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));

        await harness.Orchestrator.FeedRangeSyncRoundAsync(CancellationToken.None);
        Assert.That(serving.RangeRequests, Is.Empty);
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serving.RangeRequests, Has.Count.EqualTo(1));
            Assert.That(silent.SelectMany(static p => p.Reports), Is.Empty);
            Assert.That(harness.Importer.ImportOrder, Does.Contain((true, chain[0].ComputeMessageRoot())));
        }
    }

    /// <summary>
    /// Networking BeaconBlocksByRange, which envelopes by range follow: a reply may stop early or be empty, but skipping an
    /// on-chain payload before one it serves is a violation. The next peer is asked for what is missing in every case.
    /// </summary>
    [TestCase(new int[0], false)]
    [TestCase(new[] { 0 }, false)]
    [TestCase(new[] { 1 }, true)]
    public async Task Range_feed_asks_the_next_peer_for_envelopes_the_first_did_not_serve(int[] servedByFirst, bool skipIsPenalized)
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 3);
        SignedExecutionPayloadEnvelope[] envelopes = EnvelopesOf(chain);
        EnvelopeServingPeer bare = new("bare", WallSlot,
            byRange: (_, _) => [.. servedByFirst.Select(i => envelopes[i])],
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]);
        Harness harness = CreateRangeHarness(chain, envelopes, out EnvelopeServingPeer serving, extraPeer: bare);
        harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bare.RangeRequests, Has.Count.EqualTo(1));
            Assert.That(bare.Reports, skipIsPenalized ? Is.EqualTo(new[] { PeerFailureReason.ProtocolViolation }) : Is.Empty);
            Assert.That(serving.Reports, Is.Empty);
            Assert.That(serving.RangeRequests, Has.Count.EqualTo(1));
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(chain.SelectMany(static b => new[] { (false, b.ComputeMessageRoot()), (true, b.ComputeMessageRoot()) })));
        }
    }

    /// <summary>fulu/p2p-interface.md Status v2: a peer whose <c>earliest_available_slot</c> is past the start of the range cannot serve it, and one at the start can.</summary>
    [TestCase(0UL, true)]
    [TestCase(1UL, false)]
    public async Task Range_feed_asks_for_envelopes_only_peers_that_serve_from_the_start_of_the_run(ulong earliestPastStart, bool candidateIsAsked)
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 2);
        SignedExecutionPayloadEnvelope[] envelopes = EnvelopesOf(chain);
        EnvelopeServingPeer Serving(string id, ulong earliestAvailableSlot) => new(
            id,
            WallSlot,
            byRange: (start, count) => [.. envelopes.Where(e => e.Message!.Payload!.SlotNumber >= start && e.Message.Payload.SlotNumber - start < count)],
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)],
            earliestAvailableSlot: earliestAvailableSlot);
        EnvelopeServingPeer candidate = Serving("candidate", chain[0].Slot + earliestPastStart);
        EnvelopeServingPeer fallback = Serving("fallback", 0);
        Harness harness = CreateHarness(peers: [candidate, fallback]);
        harness.Importer.Known.Add(AnchorRoot());
        harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(candidate.RangeRequests, Has.Count.EqualTo(candidateIsAsked ? 1 : 0));
            Assert.That(fallback.RangeRequests, Has.Count.EqualTo(candidateIsAsked ? 0 : 1));
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(chain.SelectMany(static b => new[] { (false, b.ComputeMessageRoot()), (true, b.ComputeMessageRoot()) })));
        }
    }

    /// <summary>The envelopes of a run are asked of the peers that reach its first slot, so a peer whose head is inside the run, short of its last slot, is asked before the one ahead.</summary>
    [Test]
    public async Task Range_feed_asks_for_envelopes_the_peers_that_reach_the_start_of_the_run()
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 3);
        SignedExecutionPayloadEnvelope[] envelopes = EnvelopesOf(chain);
        EnvelopeServingPeer Serving(string id, ulong headSlot) => new(
            id,
            headSlot,
            byRange: (start, count) => [.. envelopes.Where(e => e.Message!.Payload!.SlotNumber >= start && e.Message.Payload.SlotNumber - start < count)],
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]);
        EnvelopeServingPeer inside = Serving("inside", chain[1].Slot);
        EnvelopeServingPeer ahead = Serving("ahead", WallSlot);
        Harness harness = CreateHarness(peers: [inside, ahead], filterPoolByHead: true);
        harness.Importer.Known.Add(AnchorRoot());
        harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inside.RangeRequests, Has.Count.EqualTo(1));
            Assert.That(ahead.RangeRequests, Is.Empty, "the first peer served every envelope");
        }
    }

    /// <summary>fulu/p2p-interface.md Status v2: when no peer serves from the start of the run, one whose <c>earliest_available_slot</c> is inside the run is still asked, so the run does not go without envelopes.</summary>
    [Test]
    public async Task Range_feed_asks_for_envelopes_a_peer_serving_from_inside_the_run_when_none_serves_its_start()
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 2, 2);
        SignedExecutionPayloadEnvelope[] envelopes = EnvelopesOf(chain);
        EnvelopeServingPeer Serving(string id, ulong headSlot, ulong earliestAvailableSlot) => new(
            id,
            headSlot,
            byRange: (start, count) => [.. envelopes.Where(e => e.Message!.Payload!.SlotNumber >= start && e.Message.Payload.SlotNumber - start < count)],
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)],
            earliestAvailableSlot: earliestAvailableSlot);
        // Serves the blocks from before the run but its head is short of the run, so only the late-starting peer reaches it.
        EnvelopeServingPeer behind = Serving("behind", AnchorSlot + 1, 0);
        EnvelopeServingPeer beyond = Serving("beyond", WallSlot, chain[^1].Slot + 1);
        EnvelopeServingPeer late = Serving("late", WallSlot, chain[^1].Slot);
        Harness harness = CreateHarness(peers: [behind, beyond, late], filterPoolByHead: true);
        harness.Importer.Known.Add(AnchorRoot());
        harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(behind.RangeRequests, Is.Empty);
            Assert.That(beyond.RangeRequests, Is.Empty, "it serves nothing of the run");
            Assert.That(late.RangeRequests, Is.EqualTo(new[] { (chain[0].Slot, 2UL) }));
            Assert.That(harness.Importer.ImportOrder, Is.EqualTo(chain.SelectMany(static b => new[] { (false, b.ComputeMessageRoot()), (true, b.ComputeMessageRoot()) })));
        }
    }

    /// <summary>Once one peer has served every envelope the run builds on, asking more peers only costs them requests.</summary>
    [Test]
    public async Task Range_feed_stops_asking_peers_once_every_on_chain_envelope_is_found()
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = BuildGloasRun(AnchorRoot(), AnchorSlot + 1, 2);
        SignedExecutionPayloadEnvelope[] envelopes = EnvelopesOf(chain);
        EnvelopeServingPeer serving = new(
            "serving",
            WallSlot,
            byRange: (start, count) => [.. envelopes.Where(e => e.Message!.Payload!.SlotNumber >= start && e.Message.Payload.SlotNumber - start < count)],
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]);
        EnvelopeServingPeer second = new("second", WallSlot, blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]);
        Harness harness = CreateHarness(peers: [serving, second]);
        harness.Importer.Known.Add(AnchorRoot());
        harness.Importer.UnverifiedPayloads.UnionWith(chain.Select(static b => b.ComputeMessageRoot()));

        await RunRangeRoundAsync(harness);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(serving.RangeRequests, Has.Count.EqualTo(1));
            Assert.That(second.RangeRequests, Is.Empty, "the first peer already served every on-chain envelope");
        }
    }

    /// <summary>The newest blocks are the ones whose envelopes are due next, so a full set of blocks recovering columns gives way oldest first.</summary>
    [Test]
    public async Task Column_recovery_keeps_the_newest_blocks_when_full()
    {
        const int Capacity = 32;
        await using BeaconDiscovery discovery = CreateDiscovery();
        Hash256 anchorRoot = AnchorRoot();
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer], discovery: discovery);
        harness.Importer.Known.Add(anchorRoot);
        List<Hash256> roots = [];
        for (int i = 0; i <= Capacity; i++)
        {
            ForkedSignedBeaconBlock block = GloasBlobBlock(EnvelopeBlockSlot + (ulong)i, anchorRoot, ColumnSlot + (ulong)i);
            roots.Add(block.ComputeMessageRoot());
            await harness.Orchestrator.ImportBlockAsync(block, CancellationToken.None);
        }

        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        int requestsOnImport = peer.ColumnRootRequests.Count;
        harness.Timestamper.Set(SlotStart(WallSlot + 1));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + 1, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);

        Assert.That(peer.ColumnRootRequests.Skip(requestsOnImport).Select(static r => r[0].BlockRoot), Is.EquivalentTo(roots.Skip(1)));
    }

    [Test]
    public async Task Column_recovery_stops_once_its_block_is_finalized_or_past_its_age([Values] bool finalized)
    {
        await using BeaconDiscovery discovery = CreateDiscovery();
        Hash256 anchorRoot = AnchorRoot();
        EnvelopeServingPeer peer = new("peer", WallSlot);
        Harness harness = CreateHarness(peers: [peer], discovery: discovery);
        harness.Importer.Known.Add(anchorRoot);
        await harness.Orchestrator.ImportBlockAsync(GloasBlobBlock(EnvelopeBlockSlot, anchorRoot, ColumnSlot), CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);
        await harness.Orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);

        ulong nextTick = finalized ? WallSlot + 1 : WallSlot + RetryAgeSlots + 1;
        if (finalized)
        {
            harness.Importer.Head = CreateHead(TestItem.KeccakB, AnchorSlot, finalizedEpoch: Spec.GetEpoch(ColumnSlot) + 1);
        }

        harness.Timestamper.Set(SlotStart(nextTick));
        await harness.Orchestrator.ProcessSlotAsync(nextTick, CancellationToken.None);
        await harness.Orchestrator.SettleColumnFetchesAsync(CancellationToken.None);

        Assert.That(peer.ColumnRootRequests, Has.Count.EqualTo(1));
    }

    /// <summary>The first slot of the data availability window of a clock at genesis, which starts at the Fulu fork.</summary>
    private static ulong ColumnSlot => Spec.FuluForkEpoch * Spec.SlotsPerEpoch;

    /// <summary>A Gloas block at <paramref name="slot"/> whose bid, for <paramref name="bidSlot"/>, commits the fixture blobs.</summary>
    private static ForkedSignedBeaconBlock GloasBlobBlock(ulong slot, Hash256 parentRoot, ulong bidSlot)
    {
        SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
        ExecutionPayloadBid bid = block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        bid.BlobKzgCommitments = DataColumnSidecarGloasTestFixture.Commitments();
        bid.Slot = bidSlot;
        return new ForkedSignedBeaconBlock.OfGloas(block);
    }

    /// <summary>Resolves the node identity and its column custody as discovery's start does, without binding a socket.</summary>
    private static BeaconDiscovery CreateDiscovery()
    {
        BeaconDiscovery discovery = new(new BeaconChainConfig { Discv5Port = 0 }, Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), Timestamper.Default, LimboLogs.Instance);
        discovery.CreateDiscv5Services(IPAddress.Loopback);
        return discovery;
    }

    private static ulong RetryAgeSlots => 2 * Spec.SlotsPerEpoch;

    private static Hash256 AnchorRoot() => TestChain.BuildLinkedChain(AnchorSlot).AnchorRoot;

    private static DateTime SlotStart(ulong slot) => DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + slot * Spec.SecondsPerSlot).AddSeconds(6);

    /// <summary>Ticks the slot <paramref name="age"/> slots after the wall slot and returns the import attempts counted so far.</summary>
    private static async Task<int> TickAtAgeAsync(Harness harness, ulong age, Func<int> attempts)
    {
        harness.Timestamper.Set(SlotStart(WallSlot + age));
        await harness.Orchestrator.ProcessSlotAsync(WallSlot + age, CancellationToken.None);
        return attempts();
    }

    private static SignedExecutionPayloadEnvelope EnvelopeFor(Hash256 blockRoot, ulong slot, ulong builderIndex = 0, Hash256? blockHash = null) => new()
    {
        Message = new ExecutionPayloadEnvelope
        {
            Payload = new ExecutionPayloadGloas { SlotNumber = slot, BlockHash = blockHash ?? Hash256.Zero },
            ExecutionRequests = new ExecutionRequestsGloas(),
            BuilderIndex = builderIndex,
            BeaconBlockRoot = blockRoot,
            ParentBeaconBlockRoot = Hash256.Zero,
        },
        Signature = new BlsSignature(new byte[BlsSignature.Length]),
    };

    private static SignedExecutionPayloadEnvelope EnvelopeFor(ForkedSignedBeaconBlock.OfGloas block)
    {
        ExecutionPayloadBid bid = block.Block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
        return EnvelopeFor(block.ComputeMessageRoot(), block.Slot, bid.BuilderIndex, bid.BlockHash);
    }

    private static SignedExecutionPayloadEnvelope[] EnvelopesOf(List<ForkedSignedBeaconBlock.OfGloas> chain) => [.. chain.Select(EnvelopeFor)];

    /// <summary>Linked Gloas blocks from <paramref name="firstSlot"/>, each building on its parent's payload unless its index is in <paramref name="emptyAt"/>.</summary>
    private static List<ForkedSignedBeaconBlock.OfGloas> BuildGloasRun(Hash256 anchorRoot, ulong firstSlot, int count, ulong slotStep = 1, int[]? emptyAt = null)
    {
        List<ForkedSignedBeaconBlock.OfGloas> chain = [];
        Hash256 parentRoot = anchorRoot;
        Hash256 parentPayloadHash = Keccak.Compute("anchor payload");
        Hash256 grandparentPayloadHash = Keccak.Compute("before the anchor payload");
        for (int i = 0; i < count; i++)
        {
            ulong slot = firstSlot + (ulong)i * slotStep;
            SignedBeaconBlockGloas block = CreateMinimalGloasBlock(slot, parentRoot);
            ExecutionPayloadBid bid = block.Message!.Body!.SignedExecutionPayloadBid!.Message!;
            bid.BuilderIndex = 7;
            bid.BlockHash = Keccak.Compute($"payload {slot}");
            bid.ParentBlockHash = emptyAt?.Contains(i) == true ? grandparentPayloadHash : parentPayloadHash;
            chain.Add(new ForkedSignedBeaconBlock.OfGloas(block));
            parentRoot = chain[^1].ComputeMessageRoot();
            grandparentPayloadHash = bid.ParentBlockHash;
            parentPayloadHash = bid.BlockHash;
        }

        return chain;
    }

    /// <summary>
    /// A harness over a peer serving <paramref name="chain"/> and <paramref name="envelopes"/> by range, each in the requested window
    /// unless <paramref name="serveEveryEnvelope"/>, after <paramref name="extraPeer"/> when given; the anchor is known.
    /// </summary>
    private static Harness CreateRangeHarness(
        List<ForkedSignedBeaconBlock.OfGloas> chain,
        SignedExecutionPayloadEnvelope[] envelopes,
        out EnvelopeServingPeer peer,
        ulong wallSlot = WallSlot,
        bool serveEveryEnvelope = false,
        IBeaconSyncPeer? extraPeer = null)
    {
        peer = new EnvelopeServingPeer(
            "peer",
            wallSlot,
            byRange: (start, count) => [.. envelopes.Where(e => serveEveryEnvelope || (e.Message!.Payload!.SlotNumber >= start && e.Message.Payload.SlotNumber - start < count))],
            blocksByRange: (start, count) => [.. chain.Where(b => b.Slot >= start && b.Slot - start < count)]);
        Harness harness = CreateHarness(wallSlot: wallSlot, peers: extraPeer is null ? [peer] : [extraPeer, peer]);
        harness.Importer.Known.Add(AnchorRoot());
        return harness;
    }

    private static async Task RunRangeRoundAsync(Harness harness)
    {
        await harness.Orchestrator.FeedRangeSyncRoundAsync(CancellationToken.None);
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);
    }
}
