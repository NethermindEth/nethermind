// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
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
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.Sync;

public partial class BeaconSyncOrchestratorTests
{
    private const ulong AnchorSlot = 100;
    private const ulong WallSlot = 200;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

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

    [Test]
    public async Task Head_step_invalidates_payload_recomputes_head_and_retries_fcu_once_on_invalid()
    {
        Harness harness = CreateHarness();
        HeadView badHead = CreateHead(TestItem.KeccakA, 103, finalizedEpoch: 3, execHash: TestItem.KeccakB);
        HeadView goodHead = CreateHead(TestItem.KeccakC, 102, finalizedEpoch: 3, execHash: TestItem.KeccakD);
        harness.Importer.Head = badHead;
        harness.Importer.HeadAfterInvalidation = goodHead;
        harness.Engine.FcuResponses.Enqueue(PayloadStatusV1.Invalid(TestItem.KeccakF));
        harness.Engine.FcuResponses.Enqueue(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestItem.KeccakD });

        await harness.Orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.InvalidatedPayloads, Is.EqualTo((List<(Hash256, Hash256?)>)[(badHead.HeadRoot, TestItem.KeccakF)]), "INVALID propagated with the latest valid hash");
            Assert.That(harness.Importer.ComputeHeadCalls, Is.EqualTo(2), "head recomputed after invalidation");
            Assert.That(harness.Engine.FcuCalls, Is.EqualTo((List<(Hash256, Hash256, Hash256)>)
            [
                (badHead.HeadExecutionHash!, badHead.JustifiedExecutionHash!, badHead.FinalizedExecutionHash!),
                (goodHead.HeadExecutionHash!, goodHead.JustifiedExecutionHash!, goodHead.FinalizedExecutionHash!),
            ]), "FCU retried exactly once with the recomputed head");
            Assert.That(harness.StatusHolder.CurrentStatus.HeadRoot, Is.EqualTo(goodHead.HeadRoot), "status advertises the recovered head");
            Assert.That(harness.StatusHolder.JustifiedRoot, Is.EqualTo(goodHead.Justified.Root), "status carries the recovered head's justified root");
            Assert.That(harness.StatusHolder.ExecutionInSync, Is.True, "the retried FCU returned VALID");
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
        harness.Router.Start(id => topics[id] = new FakeTopic(), harness.Orchestrator.CurrentGossipDigest);
        harness.Orchestrator.GossipStarted = true;

        await harness.Orchestrator.ProcessSlotAsync(preRotationSlot, CancellationToken.None);
        byte[] digestBeforeBoundary = harness.Orchestrator.CurrentGossipDigest;
        await harness.Orchestrator.ProcessSlotAsync(Bpo2Epoch * Spec.SlotsPerEpoch, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(digestBeforeBoundary, Is.EqualTo(bpo1Digest), "no rotation before the boundary");
            Assert.That(harness.Orchestrator.CurrentGossipDigest, Is.EqualTo(bpo2Digest), "digest rotated at the BPO epoch");
            Assert.That(topics.Keys, Does.Contain(GossipTopics.Topic(bpo2Digest, GossipTopics.BeaconBlock)), "router re-subscribed on the new digest");
            Assert.That(harness.Importer.Ticks, Does.Contain(preRotationSlot).And.Contain(Bpo2Epoch * Spec.SlotsPerEpoch), "fork-choice ticked per slot");
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(2), "head FCU per slot tick");
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

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[150, 151]), "parent imported, then the queued child — nothing else");
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
    public async Task Replay_imports_canonical_store_blocks_without_network_and_stops_at_a_linkage_break()
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

        await harness.Orchestrator.ReplayStoredBlocksAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, 102, 103, 104, 105]), "all linked canonical blocks replayed");
            Assert.That(harness.Importer.Imports.Select(static i => i.VerifySignatures), Is.All.False, "store replays skip signature verification");
            Assert.That(harness.Pool.GetBestPeersCalls, Is.Zero, "the network was not touched");
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(105UL), "sync tip resumes at the replayed head");
            Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(1), "a head step follows the replay");
        }
    }

    private static Harness CreateHarness(ulong anchorSlot = AnchorSlot, ulong wallSlot = WallSlot, BeaconChainStore? store = null, IBeaconSyncPeer[]? peers = null)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + wallSlot * Spec.SecondsPerSlot).AddSeconds(6);
        ManualTimestamper timestamper = new(now);
        SlotClock slotClock = new(Spec, timestamper);
        store ??= new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>());
        ScriptedImporter importer = new() { Head = CreateHead(TestItem.KeccakA, anchorSlot, finalizedEpoch: Spec.GetEpoch(anchorSlot)) };
        ScriptedEngine engine = new();
        StubPool pool = new(peers ?? []);
        GossipRouter router = new(Spec, slotClock, LimboLogs.Instance);
        BeaconChainStatusHolder statusHolder = new(Spec, timestamper);
        BeaconSyncOrchestrator orchestrator = new(
            new BeaconChainConfig(),
            Spec,
            store,
            new ScriptedFactory(importer),
            engine,
            pool,
            new RangeSync(pool, LimboLogs.Instance, new DataColumnSidecarPool(), Spec),
            slotClock,
            router,
            statusHolder,
            LimboLogs.Instance);

        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(anchorSlot);
        orchestrator.Initialize(importer, anchorBlock, anchorRoot);
        return new Harness(orchestrator, importer, engine, pool, router, statusHolder, timestamper);
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
        ManualTimestamper Timestamper);

    private sealed class ScriptedFactory(IBlockImporter importer) : IBlockImporterFactory
    {
        public IBlockImporter Create(BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot) => importer;
    }

    private sealed class ScriptedImporter : IBlockImporter
    {
        public HashSet<Hash256> Known { get; } = [];

        /// <summary>Block roots for which <see cref="Import"/> answers <see cref="BlockImportResult.DataUnavailable"/> instead of importing.</summary>
        public HashSet<Hash256> Unavailable { get; } = [];

        /// <summary>Block roots whose proposer signature fails, so <see cref="Import"/> answers <see cref="BlockImportResult.Invalid"/>.</summary>
        public HashSet<Hash256> Forged { get; } = [];

        /// <summary>Block roots for which <see cref="Import"/> answers <see cref="BlockImportResult.EngineUnavailable"/>.</summary>
        public HashSet<Hash256> EngineDown { get; } = [];

        /// <summary>Parent roots whose payload is unverified, so a child <see cref="Import"/> answers <see cref="BlockImportResult.ParentPayloadUnverified"/> until <see cref="ImportEnvelope"/> records it.</summary>
        public HashSet<Hash256> UnverifiedPayloads { get; } = [];

        /// <summary>The verdict <see cref="ImportEnvelope"/> answers; a recording verdict removes the root from <see cref="UnverifiedPayloads"/>.</summary>
        public ExecutionPayloadEnvelopeImportResult EnvelopeResult { get; set; } = ExecutionPayloadEnvelopeImportResult.Valid;

        public List<Hash256> Envelopes { get; } = [];

        public List<object> GossipOperations { get; } = [];

        public List<(ulong Slot, Hash256 Root, bool VerifySignatures)> Imports { get; } = [];
        public List<ulong> Ticks { get; } = [];
        public List<(Hash256 Root, Hash256? LatestValidHash)> InvalidatedPayloads { get; } = [];
        public List<CheckpointRef> Finalizations { get; } = [];
        public required HeadView Head { get; set; }
        public HeadView? HeadAfterInvalidation { get; set; }
        public bool ExpectedProposer { get; set; } = true;
        public int ComputeHeadCalls { get; private set; }

        public bool IsKnown(Hash256 blockRoot) => Known.Contains(blockRoot);

        public bool IsExpectedProposer(ForkedSignedBeaconBlock block) => ExpectedProposer;

        public BlockImportResult Import(ForkedSignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures)
        {
            Imports.Add((block.Slot, blockRoot, verifySignatures));
            if (Known.Contains(blockRoot)) return BlockImportResult.AlreadyKnown;
            if (!Known.Contains(block.ParentRoot)) return BlockImportResult.UnknownParent;
            if (UnverifiedPayloads.Contains(block.ParentRoot)) return BlockImportResult.ParentPayloadUnverified;
            if (Unavailable.Contains(blockRoot)) return BlockImportResult.DataUnavailable;
            if (Forged.Contains(blockRoot)) return BlockImportResult.Invalid;
            if (EngineDown.Contains(blockRoot)) return BlockImportResult.EngineUnavailable;
            Known.Add(blockRoot);
            return BlockImportResult.Imported;
        }

        public ExecutionPayloadEnvelopeImportResult ImportEnvelope(SignedExecutionPayloadEnvelope envelope)
        {
            Hash256 blockRoot = envelope.Message!.BeaconBlockRoot!;
            Envelopes.Add(blockRoot);
            if (EnvelopeResult is ExecutionPayloadEnvelopeImportResult.Valid or ExecutionPayloadEnvelopeImportResult.Optimistic)
            {
                UnverifiedPayloads.Remove(blockRoot);
            }

            return EnvelopeResult;
        }

        public void OnSlotTick(ulong slot) => Ticks.Add(slot);

        public HeadView ComputeHead()
        {
            ComputeHeadCalls++;
            return Head;
        }

        public void OnInvalidExecutionPayload(Hash256 blockRoot, Hash256? latestValidHash)
        {
            InvalidatedPayloads.Add((blockRoot, latestValidHash));
            Head = HeadAfterInvalidation ?? Head;
        }

        public void OnFinalized(CheckpointRef finalized) => Finalizations.Add(finalized);

        public void OnGossipAggregate(SignedAggregateAndProof aggregate) { }

        public void OnGossipAggregate(SignedAggregateAndProofGloas aggregate) => GossipOperations.Add(aggregate);

        public void OnGossipAttesterSlashing(AttesterSlashing slashing) { }

        public void OnGossipAttesterSlashing(AttesterSlashingGloas slashing) => GossipOperations.Add(slashing);
    }

    private sealed class ScriptedEngine : IEngineDriver
    {
        public Queue<PayloadStatusV1> FcuResponses { get; } = new();
        public List<(Hash256 Head, Hash256 Safe, Hash256 Finalized)> FcuCalls { get; } = [];

        public SignedBeaconBlock? CurrentBlock { get; set; }

        public bool HasAnsweredNewPayload => false;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
        {
            FcuCalls.Add((headExecHash, safeExecHash, finalizedExecHash));
            return Task.FromResult(FcuResponses.Count > 0 ? FcuResponses.Dequeue() : PayloadStatusV1.Syncing);
        }

        public ExecutionStatus NotifyNewPayload(BeaconBlockBody body) => ExecutionStatus.Valid;
    }

    private sealed class StubPool(IBeaconSyncPeer[] peers) : IBeaconSyncPeerPool
    {
        public int GetBestPeersCalls { get; private set; }

        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot)
        {
            GetBestPeersCalls++;
            return peers;
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
