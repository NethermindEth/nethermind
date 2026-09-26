// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
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
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Sync;

/// <summary>Gloas blocks through the orchestrator: routed to the importer, parked on an unverified parent payload, and re-driven by its envelope.</summary>
public partial class BeaconSyncOrchestratorTests
{
    [Test]
    public async Task Gossip_gloas_block_reaches_the_importer_as_a_gloas_block()
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        harness.Importer.Known.Add(anchorRoot);
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(150, anchorRoot));

        await harness.Orchestrator.ProcessGossipBlockAsync(block, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Root), Is.EqualTo((Hash256[])[block.ComputeMessageRoot()]));
            Assert.That(harness.Router.IsProposalSeen(150, block.ProposerIndex), Is.True, "an imported Gloas block marks its (slot, proposer) seen");
        }
    }

    /// <summary>Range sync feeds the whole Gloas chain instead of stopping at its first block and fetching it again every round.</summary>
    [Test]
    public async Task Range_round_imports_past_the_fork()
    {
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] fuluBlocks) = TestChain.BuildLinkedChain(AnchorSlot, 101, 102);
        List<ForkedSignedBeaconBlock> chain = [.. fuluBlocks.Select(static b => new ForkedSignedBeaconBlock.OfFulu(b))];
        Hash256 parentRoot = chain[^1].ComputeMessageRoot();
        for (ulong slot = 103; slot <= WallSlot; slot++)
        {
            ForkedSignedBeaconBlock gloas = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(slot, parentRoot));
            chain.Add(gloas);
            parentRoot = gloas.ComputeMessageRoot();
        }

        RangeSyncTests.StubPeer peer = new("peer", WallSlot, (startSlot, count) => [.. chain.Where(b => b.Slot >= startSlot && b.Slot < startSlot + count)]);
        Harness harness = CreateHarness(peers: [peer]);
        harness.Importer.Known.Add(anchorRoot);

        await harness.Orchestrator.FeedRangeSyncRoundAsync(CancellationToken.None);
        harness.Orchestrator.WorkWriter.Complete();
        await harness.Orchestrator.RunWorkerAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo(chain.Select(static b => b.Slot)));
            Assert.That(harness.Orchestrator.SyncTip.Slot, Is.EqualTo(WallSlot));
        }
    }

    /// <summary>
    /// A block parked on its parent's unverified payload is retried on the slot tick like any retriable result, and at
    /// once when that parent's envelope records the payload. An envelope that records nothing re-drives nothing.
    /// </summary>
    [TestCase(ExecutionPayloadEnvelopeImportResult.Valid, true)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Optimistic, true)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Invalid, false)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.DataUnavailable, false)]
    public async Task Block_waiting_on_its_parents_payload_is_retried_and_re_driven_by_the_envelope(ExecutionPayloadEnvelopeImportResult envelopeResult, bool reDriven)
    {
        Harness harness = CreateHarness();
        (SignedBeaconBlock _, Hash256 anchorRoot, SignedBeaconBlock[] _) = TestChain.BuildLinkedChain(AnchorSlot);
        ForkedSignedBeaconBlock parent = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(150, anchorRoot));
        Hash256 parentRoot = parent.ComputeMessageRoot();
        ForkedSignedBeaconBlock child = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(151, parentRoot));
        harness.Importer.Known.UnionWith([anchorRoot, parentRoot]);
        harness.Importer.UnverifiedPayloads.Add(parentRoot);
        harness.Importer.EnvelopeResult = envelopeResult;
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        await orchestrator.ProcessGossipBlockAsync(child, CancellationToken.None);
        await orchestrator.ProcessSlotAsync(WallSlot, CancellationToken.None);
        int attemptsBeforeEnvelope = harness.Importer.Imports.Count;
        await orchestrator.ImportEnvelopeAsync(new SignedExecutionPayloadEnvelope { Message = new ExecutionPayloadEnvelope { BeaconBlockRoot = parentRoot } }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attemptsBeforeEnvelope, Is.EqualTo(2), "the slot tick retries the parked block");
            Assert.That(harness.Router.IsProposalSeen(151, child.ProposerIndex), Is.True, "a parked block passed its proposer signature check, so a second block for its (slot, proposer) is a repeat");
            Assert.That(harness.Importer.Imports, Has.Count.EqualTo(reDriven ? 3 : 2), "the envelope re-drives the parked block only when it records the payload");
            Assert.That(harness.Importer.Known.Contains(child.ComputeMessageRoot()), Is.EqualTo(reDriven));
        }
    }

    /// <summary>A restart replays stored Gloas blocks; the Fulu-only store read would throw on the first of them and stop the node.</summary>
    [Test]
    public async Task Replay_reads_stored_gloas_blocks()
    {
        BeaconChainSpec storeSpec = Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures.SyntheticSpec(gloasForkEpoch: 4);
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>(), storeSpec);
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] fulu) = TestChain.BuildLinkedChain(AnchorSlot, 101);
        TestChain.Persist(store, anchor, anchorRoot, fulu);
        ForkedSignedBeaconBlock gloas = new ForkedSignedBeaconBlock.OfGloas(CreateMinimalGloasBlock(4 * storeSpec.SlotsPerEpoch, SszRoots.HashTreeRoot(fulu[0].Message!)));
        store.PutForkedBlock(gloas.ComputeMessageRoot(), gloas);
        store.SetCanonicalRoot(gloas.Slot, gloas.ComputeMessageRoot());

        Harness harness = CreateHarness(store: store);
        harness.Importer.Known.Add(anchorRoot);
        await harness.Orchestrator.ReplayStoredBlocksAsync(CancellationToken.None);

        Assert.That(harness.Importer.Imports.Select(static i => i.Slot), Is.EqualTo((ulong[])[101, gloas.Slot]));
    }

    /// <summary>An envelope that brings in no parked block still moves the execution head from the bid's parent hash to its block hash.</summary>
    [TestCase(ExecutionPayloadEnvelopeImportResult.Valid, 1)]
    [TestCase(ExecutionPayloadEnvelopeImportResult.Invalid, 0)]
    public async Task Envelope_that_re_drives_no_block_still_runs_a_head_step(ExecutionPayloadEnvelopeImportResult envelopeResult, int expectedFcus)
    {
        Harness harness = CreateHarness();
        harness.Importer.EnvelopeResult = envelopeResult;
        BeaconSyncOrchestrator orchestrator = harness.Orchestrator;

        await orchestrator.ImportEnvelopeAsync(new SignedExecutionPayloadEnvelope { Message = new ExecutionPayloadEnvelope { BeaconBlockRoot = TestItem.KeccakA } }, CancellationToken.None);
        orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.GossipAggregateItem(new SignedAggregateAndProof()));
        orchestrator.WorkWriter.Complete();
        await orchestrator.RunWorkerAsync(CancellationToken.None);

        Assert.That(harness.Engine.FcuCalls, Has.Count.EqualTo(expectedFcus));
    }

    /// <summary>
    /// A Fulu chain crossing into Gloas through the orchestrator and the real importer: a Fulu block past the anchor
    /// imports, then the first Gloas block on it, its full child waits for that block's envelope, and the envelope's
    /// import brings the child in.
    /// </summary>
    [Test]
    public async Task Fulu_chain_crosses_into_gloas_and_a_full_child_imports_once_its_parents_envelope_does()
    {
        SignedGloasChain chain = new();
        SignedGloasChain.FuluBlock lastFulu = chain.NextFulu(31);
        SignedGloasChain.Block first = chain.NextOnFulu(lastFulu, 32, full: false, 0xC1);
        SignedGloasChain.Block child = chain.Next(first, 33, full: true, 0xC2);
        (BeaconSyncOrchestrator orchestrator, BlockImporter importer, SignedGloasChain.EnvelopeEngine engine) = CreateGloasOrchestrator(chain);

        await orchestrator.ProcessGossipBlockAsync(lastFulu.Forked, CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(first.Forked, CancellationToken.None);
        await orchestrator.ProcessGossipBlockAsync(child.Forked, CancellationToken.None);
        bool childKnownBeforeEnvelope = importer.IsKnown(child.Root);
        ExecutionPayloadEnvelopeImportResult envelope = await orchestrator.ImportEnvelopeAsync(first.Envelope, CancellationToken.None);
        await orchestrator.RunHeadStepAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.IsKnown(lastFulu.Root), Is.True, "the Fulu block past the anchor imports");
            Assert.That(importer.IsKnown(first.Root), Is.True, "the first Gloas block imports across the fork");
            Assert.That(childKnownBeforeEnvelope, Is.False, "the full child waits for its parent's payload");
            Assert.That(envelope, Is.EqualTo(ExecutionPayloadEnvelopeImportResult.Valid));
            Assert.That(importer.IsKnown(child.Root), Is.True, "the envelope re-drives the parked child");
            Assert.That(orchestrator.SyncTip, Is.EqualTo((child.Root, 33UL)));
            Assert.That(engine.FcuCalls[^1].Head, Is.EqualTo(child.Bid.ParentBlockHash), "the head is the child, whose own payload has not arrived");
        }
    }

    /// <summary>
    /// The retry set is bounded, so blocks parked there must be ones their proposer signed. Forged children of a full
    /// parent, sent before its envelope to fill that set, must not stop the real child from importing once it arrives.
    /// </summary>
    [Test]
    public async Task Forged_children_of_a_full_parent_do_not_crowd_out_the_real_one()
    {
        const int RetrySetCapacity = 128;
        SignedGloasChain chain = new();
        SignedGloasChain.Block first = chain.Next(null, 32, full: false, 0xC1);
        SignedGloasChain.Block child = chain.Next(first, 33, full: true, 0xC2);
        (BeaconSyncOrchestrator orchestrator, BlockImporter importer, _) = CreateGloasOrchestrator(chain);
        await orchestrator.ProcessGossipBlockAsync(first.Forked, CancellationToken.None);

        byte[] childSsz = SignedBeaconBlockCodec.Encode(child.Forked, chain.Spec);
        for (int i = 0; i < RetrySetCapacity; i++)
        {
            ForkedSignedBeaconBlock.OfGloas forged = (ForkedSignedBeaconBlock.OfGloas)SignedBeaconBlockCodec.Decode(childSsz, chain.Spec);
            forged.Block.Message!.Body!.Graffiti = Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures.Hash((byte)(0x80 + i));
            await orchestrator.ProcessGossipBlockAsync(forged, CancellationToken.None);
        }

        await orchestrator.ProcessGossipBlockAsync(child.Forked, CancellationToken.None);
        await orchestrator.ImportEnvelopeAsync(first.Envelope, CancellationToken.None);

        Assert.That(importer.IsKnown(child.Root), Is.True);
    }

    /// <summary>An orchestrator on the real importer over <paramref name="chain"/>, its wall clock inside slot 34.</summary>
    private static (BeaconSyncOrchestrator Orchestrator, BlockImporter Importer, SignedGloasChain.EnvelopeEngine Engine) CreateGloasOrchestrator(SignedGloasChain chain)
    {
        BeaconChainSpec spec = chain.Spec;
        ManualTimestamper timestamper = new(DateTime.UnixEpoch.AddSeconds(spec.GenesisTime + 34 * spec.SecondsPerSlot).AddSeconds(6));
        SlotClock slotClock = new(spec, timestamper);
        BeaconChainStore store = chain.CreateStore();
        SignedGloasChain.EnvelopeEngine engine = new();
        StubPool pool = new([]);
        BlockImporter importer = chain.CreateImporter(engine, store: store);
        BeaconSyncOrchestrator orchestrator = new(
            new BeaconChainConfig(),
            spec,
            store,
            new ScriptedFactory(importer),
            engine,
            pool,
            new RangeSync(pool, LimboLogs.Instance, new DataColumnSidecarPool(), spec),
            slotClock,
            new GossipRouter(spec, slotClock, LimboLogs.Instance),
            new BeaconChainStatusHolder(spec, timestamper),
            LimboLogs.Instance);
        orchestrator.Initialize(importer, chain.AnchorBlock, chain.AnchorRoot);
        orchestrator.GossipStarted = true;
        return (orchestrator, importer, engine);
    }
}
