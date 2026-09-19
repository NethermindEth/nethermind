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
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Sync;

public class BeaconSyncOrchestratorTests
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
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipBlockItem(chain[2]));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(chain[0]));
        harness.Orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.RangeBlockItem(chain[1]));
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

        await orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(equivocation, CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(belowFinality, CancellationToken.None);
        harness.Importer.ExpectedProposer = false;
        await orchestrator.ProcessGossipBlockAsync(wrongProposer, CancellationToken.None);
        harness.Importer.ExpectedProposer = true;

        Assert.That(harness.Importer.Imports, Is.Empty, "all gossip blocks were held or dropped before import");

        // Importing the parent through range sync drains only the valid queued child.
        await orchestrator.ImportBlockAsync(chain[0], CancellationToken.None);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[150, 151]), "parent imported, then the queued child — nothing else");
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

    private static Harness CreateHarness(ulong anchorSlot = AnchorSlot, ulong wallSlot = WallSlot, BeaconChainStore? store = null)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(Spec.GenesisTime + wallSlot * Spec.SecondsPerSlot).AddSeconds(6);
        ManualTimestamper timestamper = new(now);
        SlotClock slotClock = new(Spec, timestamper);
        store ??= new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>());
        ScriptedImporter importer = new() { Head = CreateHead(TestItem.KeccakA, anchorSlot, finalizedEpoch: Spec.GetEpoch(anchorSlot)) };
        ScriptedEngine engine = new();
        StubPool pool = new();
        GossipRouter router = new(Spec, slotClock, LimboLogs.Instance);
        BeaconChainStatusHolder statusHolder = new(Spec, timestamper);
        BeaconSyncOrchestrator orchestrator = new(
            new BeaconChainConfig(),
            Spec,
            store,
            new ScriptedFactory(importer),
            engine,
            pool,
            new RangeSync(pool, LimboLogs.Instance),
            slotClock,
            router,
            statusHolder,
            LimboLogs.Instance);

        (SignedBeaconBlock anchorBlock, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(anchorSlot);
        orchestrator.Initialize(importer, anchorBlock, anchorRoot);
        return new Harness(orchestrator, importer, engine, pool, router, statusHolder);
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
        BeaconChainStatusHolder StatusHolder);

    private sealed class ScriptedFactory(IBlockImporter importer) : IBlockImporterFactory
    {
        public IBlockImporter Create(BeaconStateFulu anchorState, SignedBeaconBlock anchorBlock, Hash256 anchorRoot) => importer;
    }

    private sealed class ScriptedImporter : IBlockImporter
    {
        public HashSet<Hash256> Known { get; } = [];
        public List<(ulong Slot, Hash256 Root, bool VerifySignatures)> Imports { get; } = [];
        public List<ulong> Ticks { get; } = [];
        public List<(Hash256 Root, Hash256? LatestValidHash)> InvalidatedPayloads { get; } = [];
        public List<CheckpointRef> Finalizations { get; } = [];
        public required HeadView Head { get; set; }
        public HeadView? HeadAfterInvalidation { get; set; }
        public bool ExpectedProposer { get; set; } = true;
        public int ComputeHeadCalls { get; private set; }

        public bool IsKnown(Hash256 blockRoot) => Known.Contains(blockRoot);

        public bool IsExpectedProposer(SignedBeaconBlock block) => ExpectedProposer;

        public BlockImportResult Import(SignedBeaconBlock block, Hash256 blockRoot, bool verifySignatures)
        {
            Imports.Add((block.Message!.Slot, blockRoot, verifySignatures));
            if (Known.Contains(blockRoot)) return BlockImportResult.AlreadyKnown;
            if (!Known.Contains(block.Message.ParentRoot!)) return BlockImportResult.UnknownParent;
            Known.Add(blockRoot);
            return BlockImportResult.Imported;
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

        public void OnGossipAttesterSlashing(AttesterSlashing slashing) { }
    }

    private sealed class ScriptedEngine : IEngineDriver
    {
        public Queue<PayloadStatusV1> FcuResponses { get; } = new();
        public List<(Hash256 Head, Hash256 Safe, Hash256 Finalized)> FcuCalls { get; } = [];

        public SignedBeaconBlock? CurrentBlock { get; set; }

        public PayloadStatusV1? LastNewPayloadStatus => null;

        public Task<PayloadStatusV1> ForkchoiceUpdated(Hash256 headExecHash, Hash256 safeExecHash, Hash256 finalizedExecHash)
        {
            FcuCalls.Add((headExecHash, safeExecHash, finalizedExecHash));
            return Task.FromResult(FcuResponses.Count > 0 ? FcuResponses.Dequeue() : PayloadStatusV1.Syncing);
        }

        public bool NotifyNewPayload(BeaconBlockBody body) => true;
    }

    private sealed class StubPool : IBeaconSyncPeerPool
    {
        public int GetBestPeersCalls { get; private set; }

        public IReadOnlyList<IBeaconSyncPeer> GetBestPeers(ulong minHeadSlot)
        {
            GetBestPeersCalls++;
            return [];
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
